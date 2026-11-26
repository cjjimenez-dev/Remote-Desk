using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using RemoteDesk.Core.Models;
using RemoteDesk.Core.Security;
using RemoteDesk.Server.Signaling;
using RemoteDesk.Transport.WebRtc;
using Xunit;

namespace RemoteDesk.Core.Tests.Services;

[Collection("WebRtcSerialTests")]
public class SignalingDeclineFlowTests
{
    private static async Task<(WebApplication App, string SignalingUrl)> StartEphemeralSignalingServerAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
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
        return (app, $"{serverAddress}/signaling");
    }

    [Fact]
    public async Task RejectConnection_DeliversReasonAndAllowsImmediateClientDispose_WithoutDeadlock()
    {
        var (app, signalingUrl) = await StartEphemeralSignalingServerAsync();
        try
        {
            await using var hostSignaling = new SignalingClient(signalingUrl);
            var viewerSignaling = new SignalingClient(signalingUrl);

            await hostSignaling.ConnectAsync();
            await viewerSignaling.ConnectAsync();

            var hostId = "host-reject-01";
            (await hostSignaling.RegisterHostAsync(hostId, "HostPC")).Should().BeTrue();

            var rejectionHandledTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            hostSignaling.ConnectionRequested += async req =>
            {
                await hostSignaling.RejectConnectionAsync(req.ViewerConnectionId, "Rejected by user");
            };

            viewerSignaling.ConnectionRejected += reason =>
            {
                // Mimic non-blocking Viewer teardown triggered from inside SignalR's ConnectionRejected callback.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await viewerSignaling.DisposeAsync().ConfigureAwait(false);
                        rejectionHandledTcs.TrySetResult(reason);
                    }
                    catch (Exception ex)
                    {
                        rejectionHandledTcs.TrySetException(ex);
                    }
                });
            };

            using var viewerIdentity = new DeviceIdentity();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var sig = viewerIdentity.Sign($"{hostId}:{viewerIdentity.DeviceId}:{ts}");

            await viewerSignaling.RequestConnectionAsync(
                hostId,
                "ViewerPC",
                viewerIdentity.DeviceId,
                viewerIdentity.PublicKeyBytes,
                sig,
                ts);

            var completed = await Task.WhenAny(rejectionHandledTcs.Task, Task.Delay(5000));
            completed.Should().Be(rejectionHandledTcs.Task, "declining a connection and disposing SignalingClient must never deadlock");
            (await rejectionHandledTcs.Task).Should().Be("Rejected by user");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task CancelConnection_NotifiesHostAndClearsPendingSlot()
    {
        var (app, signalingUrl) = await StartEphemeralSignalingServerAsync();
        try
        {
            await using var hostSignaling = new SignalingClient(signalingUrl);
            await using var viewerSignaling = new SignalingClient(signalingUrl);

            await hostSignaling.ConnectAsync();
            await viewerSignaling.ConnectAsync();

            const string hostId = "host-cancel-01";
            (await hostSignaling.RegisterHostAsync(hostId, "HostPC")).Should().BeTrue();

            var requestedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelledTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            hostSignaling.ConnectionRequested += req => requestedTcs.TrySetResult(req.ViewerConnectionId);
            hostSignaling.ConnectionCancelled += viewerConnId => cancelledTcs.TrySetResult(viewerConnId);

            await viewerSignaling.RequestConnectionAsync(hostId, "ViewerPC");
            var viewerConnId = await requestedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await viewerSignaling.CancelConnectionAsync();
            var cancelledConnId = await cancelledTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancelledConnId.Should().Be(viewerConnId);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task ViewerDisconnectBeforeApproval_TriggersConnectionCancelledOnHost()
    {
        var (app, signalingUrl) = await StartEphemeralSignalingServerAsync();
        try
        {
            await using var hostSignaling = new SignalingClient(signalingUrl);
            var viewerSignaling = new SignalingClient(signalingUrl);

            await hostSignaling.ConnectAsync();
            await viewerSignaling.ConnectAsync();

            const string hostId = "host-drop-01";
            (await hostSignaling.RegisterHostAsync(hostId, "HostPC")).Should().BeTrue();

            var requestedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelledTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            hostSignaling.ConnectionRequested += req => requestedTcs.TrySetResult(req.ViewerConnectionId);
            hostSignaling.ConnectionCancelled += viewerConnId => cancelledTcs.TrySetResult(viewerConnId);

            await viewerSignaling.RequestConnectionAsync(hostId, "ViewerPC");
            var viewerConnId = await requestedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Viewer drops unexpectedly without clicking Cancel
            await viewerSignaling.DisposeAsync();

            var cancelledConnId = await cancelledTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancelledConnId.Should().Be(viewerConnId);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task NotifySessionEnded_RelaysSessionEndedByPeerImmediately()
    {
        var (app, signalingUrl) = await StartEphemeralSignalingServerAsync();
        try
        {
            await using var hostSignaling = new SignalingClient(signalingUrl);
            await using var viewerSignaling = new SignalingClient(signalingUrl);

            await hostSignaling.ConnectAsync();
            await viewerSignaling.ConnectAsync();

            const string hostId = "host-end-01";
            (await hostSignaling.RegisterHostAsync(hostId, "HostPC")).Should().BeTrue();

            var viewerAcceptedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sessionEndedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            string? capturedViewerConnId = null;

            hostSignaling.ConnectionRequested += async req =>
            {
                capturedViewerConnId = req.ViewerConnectionId;
                await hostSignaling.AcceptConnectionAsync(req.ViewerConnectionId, req.SessionId);
            };

            viewerSignaling.ConnectionAccepted += _ => viewerAcceptedTcs.TrySetResult(true);
            viewerSignaling.SessionEndedByPeer += reason => sessionEndedTcs.TrySetResult(reason);

            await viewerSignaling.RequestConnectionAsync(hostId, "ViewerPC");
            await viewerAcceptedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            capturedViewerConnId.Should().NotBeNullOrEmpty();
            await hostSignaling.NotifySessionEndedAsync(capturedViewerConnId!, "Remote partner ended the session.");

            var endReason = await sessionEndedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            endReason.Should().Be("Remote partner ended the session.");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
