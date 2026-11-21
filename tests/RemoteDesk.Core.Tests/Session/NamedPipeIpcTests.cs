using FluentAssertions;
using MessagePack;
using RemoteDesk.Core.Models;
using RemoteDesk.Platform.Windows.Ipc;
using Xunit;

namespace RemoteDesk.Core.Tests.Session;

public class NamedPipeIpcTests
{
    [Fact]
    public async Task NamedPipeIpc_ClientAndServer_CommunicateBidirectionally()
    {
        var pipeName = $"RemoteDesk_TestPipe_{Guid.NewGuid():N}";
        await using var server = new NamedPipeIpcServer(pipeName);
        await using var client = new NamedPipeIpcClient(pipeName);

        var serverReceivedTcs = new TaskCompletionSource<IpcMessage>();
        var clientReceivedTcs = new TaskCompletionSource<IpcMessage>();

        server.MessageReceived += msg => serverReceivedTcs.TrySetResult(msg);
        client.MessageReceived += msg => clientReceivedTcs.TrySetResult(msg);

        server.Start();

        var connected = await client.ConnectAsync(timeoutMs: 5000);
        connected.Should().BeTrue("Client should connect to named pipe server");

        // 1. Client sends message to Server
        var clientMsg = new IpcMessage
        {
            Type = IpcMessageType.AgentReady,
            Payload = MessagePackSerializer.Serialize(new IpcStatusResponse
            {
                IsSessionActive = false,
                CurrentDesktop = "Default",
                ProcessId = 1234,
                SessionId = 1
            })
        };

        await client.SendMessageAsync(clientMsg);

        var receivedByServer = await serverReceivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        receivedByServer.Should().NotBeNull();
        receivedByServer.Type.Should().Be(IpcMessageType.AgentReady);

        var status = MessagePackSerializer.Deserialize<IpcStatusResponse>(receivedByServer.Payload!);
        status.ProcessId.Should().Be(1234);
        status.CurrentDesktop.Should().Be("Default");

        // 2. Server sends message to Client
        var serverMsg = new IpcMessage
        {
            Type = IpcMessageType.StartSession,
            Payload = MessagePackSerializer.Serialize(new IpcStartSessionRequest
            {
                SessionId = "sess-ipc-999",
                ViewerConnectionId = "viewer-conn-1",
                ViewerDeviceId = "111-222",
                ViewerName = "AdminOperator",
                Permissions = SessionPermissions.All
            })
        };

        await server.SendMessageAsync(serverMsg);

        var receivedByClient = await clientReceivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        receivedByClient.Should().NotBeNull();
        receivedByClient.Type.Should().Be(IpcMessageType.StartSession);

        var startReq = MessagePackSerializer.Deserialize<IpcStartSessionRequest>(receivedByClient.Payload!);
        startReq.SessionId.Should().Be("sess-ipc-999");
        startReq.ViewerName.Should().Be("AdminOperator");
        startReq.Permissions.Should().Be(SessionPermissions.All);
    }

    [Fact]
    public async Task NamedPipeIpc_PingPong_RoundTripSucceeds()
    {
        var pipeName = $"RemoteDesk_TestPingPong_{Guid.NewGuid():N}";
        await using var server = new NamedPipeIpcServer(pipeName);
        await using var client = new NamedPipeIpcClient(pipeName);

        var pongTcs = new TaskCompletionSource<string>();

        server.MessageReceived += async msg =>
        {
            if (msg.Type == IpcMessageType.Ping)
            {
                await server.SendMessageAsync(new IpcMessage
                {
                    Type = IpcMessageType.Pong,
                    CorrelationId = msg.CorrelationId
                });
            }
        };

        client.MessageReceived += msg =>
        {
            if (msg.Type == IpcMessageType.Pong)
            {
                pongTcs.TrySetResult(msg.CorrelationId);
            }
        };

        server.Start();
        await client.ConnectAsync(timeoutMs: 5000);

        var corrId = Guid.NewGuid().ToString("N");
        await client.SendMessageAsync(new IpcMessage
        {
            Type = IpcMessageType.Ping,
            CorrelationId = corrId
        });

        var receivedCorrId = await pongTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        receivedCorrId.Should().Be(corrId);
    }
}
