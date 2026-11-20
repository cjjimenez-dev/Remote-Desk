using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using RemoteDesk.Core.Models;
using RemoteDesk.Core.Security;
using RemoteDesk.Server.Signaling;
using RemoteDesk.Transport.WebRtc;
using SIPSorcery.Net;
using Xunit;

namespace RemoteDesk.Core.Tests;

[Collection("WebRtcSerialTests")]
public class TurnRelayAndPathIndicatorTests
{
    [Fact]
    public async Task SignalingHub_GetIceServers_ReturnsConfiguredServersWithValidCredentials()
    {
        Environment.SetEnvironmentVariable("TURN_HOST", "turn.test.remotedesk.net");
        Environment.SetEnvironmentVariable("TURN_SECRET", "super_secret_test_key_xyz");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSignalR();
        builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyHeader().AllowAnyMethod().SetIsOriginAllowed(_ => true).AllowCredentials()));

        var app = builder.Build();
        app.UseCors();
        app.MapHub<SignalingHub>("/signaling");

        await app.StartAsync();
        var signalingUrl = $"{app.Urls.First()}/signaling";

        try
        {
            await using var client = new SignalingClient(signalingUrl);
            await client.ConnectAsync();

            var iceServers = await client.GetIceServersAsync("device-123");
            iceServers.Should().NotBeNull();
            iceServers.Count.Should().BeGreaterThanOrEqualTo(6);

            var turnUdp = iceServers.FirstOrDefault(s => s.Urls.Any(u => u.Contains("transport=udp")));
            turnUdp.Should().NotBeNull();
            turnUdp!.Username.Should().EndWith(":device-123");
            turnUdp.Credential.Should().NotBeNullOrEmpty();
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task WebRtcTransport_ForcedRelayMode_SetsTurnRelayedPath()
    {
        await using var peer = new WebRtcTransport(isInitiator: true, forceRelay: true);
        peer.ConnectionPath.Should().Be(ConnectionPath.TurnRelayed);
    }

    [Fact]
    public async Task WebRtcTransport_DirectLocal_DetectsDirectLocalPath()
    {
        var config = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>()
        };

        // Standard direct loopback
        await using var peerA = new WebRtcTransport(isInitiator: true, config);
        await using var peerB = new WebRtcTransport(isInitiator: false, config);

        peerA.IceCandidateGenerated += (cand, mid, line) => peerB.AddIceCandidate(cand, mid, line);
        peerB.IceCandidateGenerated += (cand, mid, line) => peerA.AddIceCandidate(cand, mid, line);

        var pathAChangedTcs = new TaskCompletionSource<ConnectionPath>();
        peerA.ConnectionPathChanged += path => pathAChangedTcs.TrySetResult(path);

        var offer = await peerA.CreateOfferAsync();
        await peerB.SetRemoteDescriptionAsync(RTCSdpType.offer, offer);

        var answer = await peerB.CreateAnswerAsync();
        await peerA.SetRemoteDescriptionAsync(RTCSdpType.answer, answer);

        var completed = await Task.WhenAny(pathAChangedTcs.Task, Task.Delay(TimeSpan.FromSeconds(8)));
        completed.Should().Be(pathAChangedTcs.Task, "peer A should detect local connection path");

        var path = await pathAChangedTcs.Task;
        path.Should().Be(ConnectionPath.DirectLocal);
        peerA.ConnectionPath.Should().Be(ConnectionPath.DirectLocal);
    }
}
