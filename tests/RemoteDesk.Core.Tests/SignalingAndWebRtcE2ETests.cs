using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using RemoteDesk.Core.Models;
using RemoteDesk.Core.Protocol;
using RemoteDesk.Server.Signaling;
using RemoteDesk.Transport.WebRtc;
using SIPSorcery.Net;
using Xunit;

namespace RemoteDesk.Core.Tests;

[Collection("WebRtcSerialTests")]
public class SignalingAndWebRtcE2ETests
{
    [Fact]
    public async Task SignalingAndWebRtc_FullHandshake_Succeeds()
    {
        // 1. Start ephemeral Signaling Server
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0"); // Dynamic free port
        builder.Services.AddSignalR();
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(p => p.AllowAnyHeader().AllowAnyMethod().SetIsOriginAllowed(_ => true).AllowCredentials());
        });

        var app = builder.Build();
        app.UseCors();
        app.MapHub<SignalingHub>("/signaling");

        await app.StartAsync();
        var serverAddress = app.Urls.First();
        var signalingUrl = $"{serverAddress}/signaling";

        try
        {
            // 2. Setup Host Signaling & WebRTC Transport
            await using var hostSignaling = new SignalingClient(signalingUrl);
            await using var viewerSignaling = new SignalingClient(signalingUrl);

            await hostSignaling.ConnectAsync();
            await viewerSignaling.ConnectAsync();

            var hostId = "host-456";
            var registered = await hostSignaling.RegisterHostAsync(hostId, "TestHostDevice");
            registered.Should().BeTrue();

            var hostTransportConnectedTcs = new TaskCompletionSource<bool>();
            var viewerTransportConnectedTcs = new TaskCompletionSource<bool>();
            var hostReceivedInputTcs = new TaskCompletionSource<MouseMoveMessage>();
            var viewerReceivedVideoTcs = new TaskCompletionSource<byte[]>();

            WebRtcTransport? hostTransport = null;
            WebRtcTransport? viewerTransport = null;

            var rtcConfig = new RTCConfiguration { iceServers = new List<RTCIceServer>() };

            // Host side signaling handlers
            hostSignaling.ConnectionRequested += async req =>
            {
                await hostSignaling.AcceptConnectionAsync(req.ViewerConnectionId, req.SessionId);

                hostTransport = new WebRtcTransport(isInitiator: false, rtcConfig);

                hostTransport.IceCandidateGenerated += (candidate, mid, line) =>
                {
                    _ = hostSignaling.SendIceCandidateAsync(req.ViewerConnectionId, candidate, mid, line);
                };

                hostTransport.StateChanged += state =>
                {
                    if (state == TransportState.Connected)
                        hostTransportConnectedTcs.TrySetResult(true);
                };

                hostTransport.MessageReceived += (channelId, data) =>
                {
                    if (channelId == DataChannels.Input && ProtocolEnvelope.TryDecode(data.Span, out var env, out _))
                    {
                        if (env.MessageType == MessageType.MouseMove)
                        {
                            var msg = ProtocolSerializer.Deserialize<MouseMoveMessage>(env.Payload);
                            hostReceivedInputTcs.TrySetResult(msg);
                        }
                    }
                };
            };

            hostSignaling.OfferReceived += async offer =>
            {
                if (hostTransport != null)
                {
                    await hostTransport.SetRemoteDescriptionAsync(RTCSdpType.offer, offer.Sdp);
                    var answerSdp = await hostTransport.CreateAnswerAsync();
                    await hostSignaling.SendAnswerAsync(offer.FromConnectionId, answerSdp);
                }
            };

            hostSignaling.IceCandidateReceived += ice =>
            {
                hostTransport?.AddIceCandidate(ice.Candidate, ice.SdpMid, (ushort)ice.SdpMLineIndex);
            };

            // Viewer side signaling handlers
            viewerSignaling.ConnectionAccepted += async res =>
            {
                viewerTransport = new WebRtcTransport(isInitiator: true, rtcConfig);

                viewerTransport.IceCandidateGenerated += (candidate, mid, line) =>
                {
                    _ = viewerSignaling.SendIceCandidateAsync(res.HostConnectionId, candidate, mid, line);
                };

                viewerTransport.StateChanged += state =>
                {
                    if (state == TransportState.Connected)
                        viewerTransportConnectedTcs.TrySetResult(true);
                };

                viewerTransport.VideoPacketReceived += packet =>
                {
                    viewerReceivedVideoTcs.TrySetResult(packet.ToArray());
                };

                var offerSdp = await viewerTransport.CreateOfferAsync();
                await viewerSignaling.SendOfferAsync(res.HostConnectionId, offerSdp);
            };

            viewerSignaling.AnswerReceived += async answer =>
            {
                if (viewerTransport != null)
                {
                    await viewerTransport.SetRemoteDescriptionAsync(RTCSdpType.answer, answer.Sdp);
                }
            };

            viewerSignaling.IceCandidateReceived += ice =>
            {
                viewerTransport?.AddIceCandidate(ice.Candidate, ice.SdpMid, (ushort)ice.SdpMLineIndex);
            };

            // 3. Initiate Connection Request from Viewer
            await viewerSignaling.RequestConnectionAsync(hostId, "TestViewerClient");

            // 4. Await P2P WebRTC Connection
            var bothConnected = Task.WhenAll(hostTransportConnectedTcs.Task, viewerTransportConnectedTcs.Task);
            var connectCompleted = await Task.WhenAny(bothConnected, Task.Delay(TimeSpan.FromSeconds(10)));
            connectCompleted.Should().Be(bothConnected, "both peers should establish WebRTC connection via SignalR signaling within 10s");

            // 5. Test Video Streaming: Host -> Viewer
            await Task.Delay(200); // Allow data channels to settle
            var testVideoPacket = new byte[25 * 1024];
            new Random(123).NextBytes(testVideoPacket);

            hostTransport.Should().NotBeNull();
            await hostTransport!.SendVideoPacketAsync(testVideoPacket);

            var videoCompleted = await Task.WhenAny(viewerReceivedVideoTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            videoCompleted.Should().Be(viewerReceivedVideoTcs.Task, "viewer should receive video packet sent by host");

            var receivedVideo = await viewerReceivedVideoTcs.Task;
            receivedVideo.Should().Equal(testVideoPacket);

            // 6. Test Remote Input Injection: Viewer -> Host
            var testMove = new MouseMoveMessage
            {
                X = 1024,
                Y = 768,
                ScreenWidth = 1920,
                ScreenHeight = 1080
            };
            var moveEnvelope = new ProtocolEnvelope(DataChannels.Input, MessageType.MouseMove, ProtocolSerializer.Serialize(testMove));

            viewerTransport.Should().NotBeNull();
            await viewerTransport!.SendMessageAsync(DataChannels.Input, moveEnvelope.ToBytes(), reliable: false);

            var inputCompleted = await Task.WhenAny(hostReceivedInputTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            inputCompleted.Should().Be(hostReceivedInputTcs.Task, "host should receive mouse move input sent by viewer");

            var receivedInput = await hostReceivedInputTcs.Task;
            receivedInput.X.Should().Be(1024);
            receivedInput.Y.Should().Be(768);
            receivedInput.ScreenWidth.Should().Be(1920);
            receivedInput.ScreenHeight.Should().Be(1080);

            // 7. Cleanup
            if (hostTransport != null) await hostTransport.DisposeAsync();
            if (viewerTransport != null) await viewerTransport.DisposeAsync();
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
