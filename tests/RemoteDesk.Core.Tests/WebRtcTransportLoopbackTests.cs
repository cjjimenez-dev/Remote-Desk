using FluentAssertions;
using RemoteDesk.Core.Models;
using RemoteDesk.Core.Protocol;
using RemoteDesk.Transport.WebRtc;
using SIPSorcery.Net;
using Xunit;

namespace RemoteDesk.Core.Tests;

[Collection("WebRtcSerialTests")]
public class WebRtcTransportLoopbackTests
{
    [Fact]
    public async Task TwoPeers_CanConnect_AndExchangeMessagesAndVideo()
    {
        var config = new RTCConfiguration
        {
            // For fast local loopback in unit tests, we don't even need external STUN
            iceServers = new List<RTCIceServer>()
        };

        await using var peerA = new WebRtcTransport(isInitiator: true, config);
        await using var peerB = new WebRtcTransport(isInitiator: false, config);

        // Wire ICE candidates
        peerA.IceCandidateGenerated += (candidate, mid, line) =>
        {
            peerB.AddIceCandidate(candidate, mid, line);
        };

        peerB.IceCandidateGenerated += (candidate, mid, line) =>
        {
            peerA.AddIceCandidate(candidate, mid, line);
        };

        var connectedTcsA = new TaskCompletionSource<bool>();
        var connectedTcsB = new TaskCompletionSource<bool>();

        peerA.StateChanged += state =>
        {
            if (state == TransportState.Connected)
                connectedTcsA.TrySetResult(true);
        };

        peerB.StateChanged += state =>
        {
            if (state == TransportState.Connected)
                connectedTcsB.TrySetResult(true);
        };

        // SDP Exchange
        var offer = await peerA.CreateOfferAsync();
        await peerB.SetRemoteDescriptionAsync(RTCSdpType.offer, offer);

        var answer = await peerB.CreateAnswerAsync();
        await peerA.SetRemoteDescriptionAsync(RTCSdpType.answer, answer);

        // Wait for connection with timeout
        var delayTask = Task.Delay(TimeSpan.FromSeconds(10));
        var waitBoth = Task.WhenAll(connectedTcsA.Task, connectedTcsB.Task);

        var completed = await Task.WhenAny(waitBoth, delayTask);
        completed.Should().Be(waitBoth, "peers should connect via WebRTC loopback within 10 seconds");

        peerA.State.Should().Be(TransportState.Connected);
        peerB.State.Should().Be(TransportState.Connected);

        // Test Control Message Exchange (A -> B)
        var msgReceivedTcs = new TaskCompletionSource<(byte Channel, byte[] Payload)>();
        peerB.MessageReceived += (channel, payload) =>
        {
            msgReceivedTcs.TrySetResult((channel, payload.ToArray()));
        };

        // Give data channels a moment to open
        await Task.Delay(200);

        var testPayload = new byte[] { 10, 20, 30, 40, 50 };
        await peerA.SendMessageAsync(DataChannels.Control, testPayload);

        var msgCompleted = await Task.WhenAny(msgReceivedTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        msgCompleted.Should().Be(msgReceivedTcs.Task, "peer B should receive message from peer A");

        var received = await msgReceivedTcs.Task;
        received.Channel.Should().Be(DataChannels.Control);
        received.Payload.Should().Equal(testPayload);

        // Test Video Packet Exchange (B -> A) with a large (chunked) 50 KB frame
        var videoReceivedTcs = new TaskCompletionSource<byte[]>();
        peerA.VideoPacketReceived += packet =>
        {
            videoReceivedTcs.TrySetResult(packet.ToArray());
        };

        var testVideoFrame = new byte[50 * 1024];
        new Random(42).NextBytes(testVideoFrame);

        await peerB.SendVideoPacketAsync(testVideoFrame);

        var videoCompleted = await Task.WhenAny(videoReceivedTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        videoCompleted.Should().Be(videoReceivedTcs.Task, "peer A should receive video frame from peer B");

        var receivedVideo = await videoReceivedTcs.Task;
        receivedVideo.Should().Equal(testVideoFrame);
    }
}
