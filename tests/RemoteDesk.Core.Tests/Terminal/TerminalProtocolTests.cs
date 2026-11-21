using FluentAssertions;
using RemoteDesk.Core.Interfaces;
using RemoteDesk.Core.Protocol;
using RemoteDesk.Core.Services;
using RemoteDesk.Core.Tests.Mocks;
using Xunit;

namespace RemoteDesk.Core.Tests.Terminal;

public class TerminalProtocolTests
{
    [Fact]
    public void TerminalMessages_SerializationRoundTrip_Succeeds()
    {
        // 1. TerminalStartMessage
        var startMsg = new TerminalStartMessage
        {
            SessionId = "sess-12345",
            Shell = ShellType.PowerShell,
            Columns = 120,
            Rows = 30
        };
        var startBytes = ProtocolSerializer.Serialize(startMsg);
        var startDeser = ProtocolSerializer.Deserialize<TerminalStartMessage>(startBytes);
        startDeser.SessionId.Should().Be("sess-12345");
        startDeser.Shell.Should().Be(ShellType.PowerShell);
        startDeser.Columns.Should().Be(120);
        startDeser.Rows.Should().Be(30);

        // 2. TerminalInputMessage
        var inputMsg = new TerminalInputMessage
        {
            SessionId = "sess-12345",
            InputData = "dir\r\n"
        };
        var inBytes = ProtocolSerializer.Serialize(inputMsg);
        var inDeser = ProtocolSerializer.Deserialize<TerminalInputMessage>(inBytes);
        inDeser.SessionId.Should().Be("sess-12345");
        inDeser.InputData.Should().Be("dir\r\n");

        // 3. TerminalOutputMessage
        var outMsg = new TerminalOutputMessage
        {
            SessionId = "sess-12345",
            OutputData = "Directory: C:\\Users\r\n"
        };
        var outBytes = ProtocolSerializer.Serialize(outMsg);
        var outDeser = ProtocolSerializer.Deserialize<TerminalOutputMessage>(outBytes);
        outDeser.SessionId.Should().Be("sess-12345");
        outDeser.OutputData.Should().Be("Directory: C:\\Users\r\n");

        // 4. TerminalResizeMessage
        var resizeMsg = new TerminalResizeMessage
        {
            SessionId = "sess-12345",
            Columns = 140,
            Rows = 40
        };
        var resBytes = ProtocolSerializer.Serialize(resizeMsg);
        var resDeser = ProtocolSerializer.Deserialize<TerminalResizeMessage>(resBytes);
        resDeser.SessionId.Should().Be("sess-12345");
        resDeser.Columns.Should().Be(140);
        resDeser.Rows.Should().Be(40);

        // 5. TerminalCloseMessage
        var closeMsg = new TerminalCloseMessage
        {
            SessionId = "sess-12345",
            Reason = "User requested exit"
        };
        var closeBytes = ProtocolSerializer.Serialize(closeMsg);
        var closeDeser = ProtocolSerializer.Deserialize<TerminalCloseMessage>(closeBytes);
        closeDeser.SessionId.Should().Be("sess-12345");
        closeDeser.Reason.Should().Be("User requested exit");
    }

    [Fact]
    public async Task TerminalClientService_Workflow_EndToEnd_Succeeds()
    {
        var (viewerTransport, hostTransport) = InProcessTransportPair.CreateConnectedPair();

        using var client = new TerminalClientService(viewerTransport);

        TerminalStartMessage? receivedStart = null;
        TerminalInputMessage? receivedInput = null;
        TerminalResizeMessage? receivedResize = null;
        TerminalCloseMessage? receivedClose = null;

        hostTransport.MessageReceived += (channel, data) =>
        {
            if (channel != DataChannels.Terminal) return;
            if (!ProtocolEnvelope.TryDecode(data.Span, out var env, out _)) return;

            switch (env.MessageType)
            {
                case MessageType.TerminalStart:
                    receivedStart = ProtocolSerializer.Deserialize<TerminalStartMessage>(env.Payload);
                    break;
                case MessageType.TerminalInput:
                    receivedInput = ProtocolSerializer.Deserialize<TerminalInputMessage>(env.Payload);
                    break;
                case MessageType.TerminalResize:
                    receivedResize = ProtocolSerializer.Deserialize<TerminalResizeMessage>(env.Payload);
                    break;
                case MessageType.TerminalClose:
                    receivedClose = ProtocolSerializer.Deserialize<TerminalCloseMessage>(env.Payload);
                    break;
            }
        };

        // 1. Start Terminal
        var sessionId = await client.StartTerminalAsync(ShellType.Cmd, columns: 100, rows: 25);
        sessionId.Should().NotBeNullOrEmpty();
        client.IsActive.Should().BeTrue();
        await Task.Delay(50);

        receivedStart.Should().NotBeNull();
        receivedStart!.SessionId.Should().Be(sessionId);
        receivedStart.Shell.Should().Be(ShellType.Cmd);
        receivedStart.Columns.Should().Be(100);
        receivedStart.Rows.Should().Be(25);

        // 2. Send Input
        await client.SendInputAsync("echo Hello\r\n");
        await Task.Delay(50);

        receivedInput.Should().NotBeNull();
        receivedInput!.SessionId.Should().Be(sessionId);
        receivedInput.InputData.Should().Be("echo Hello\r\n");

        // 3. Receive Output
        string? receivedOutput = null;
        client.OutputReceived += text => receivedOutput = text;

        var outMsg = new TerminalOutputMessage
        {
            SessionId = sessionId,
            OutputData = "Hello\r\n"
        };
        var outEnv = new ProtocolEnvelope(DataChannels.Terminal, MessageType.TerminalOutput, ProtocolSerializer.Serialize(outMsg));
        await hostTransport.SendMessageAsync(DataChannels.Terminal, outEnv.ToBytes());
        await Task.Delay(50);

        receivedOutput.Should().Be("Hello\r\n");

        // 4. Resize
        await client.ResizeAsync(120, 35);
        await Task.Delay(50);

        receivedResize.Should().NotBeNull();
        receivedResize!.Columns.Should().Be(120);
        receivedResize.Rows.Should().Be(35);

        // 5. Close
        string? closedReason = null;
        client.Closed += reason => closedReason = reason;

        await client.CloseTerminalAsync("Normal exit");
        await Task.Delay(50);

        client.IsActive.Should().BeFalse();
        receivedClose.Should().NotBeNull();
        receivedClose!.Reason.Should().Be("Normal exit");
        closedReason.Should().Be("Normal exit");
    }
}
