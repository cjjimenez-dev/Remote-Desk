using FluentAssertions;
using RemoteDesk.Core.Protocol;
using Xunit;

namespace RemoteDesk.Core.Tests;

public class ProtocolEnvelopeTests
{
    [Fact]
    public void EncodeAndDecode_ValidEnvelope_ReturnsOriginalData()
    {
        // Arrange
        var payload = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
        var envelope = new ProtocolEnvelope(
            channelId: DataChannels.Input,
            messageType: MessageType.MouseMove,
            payload: payload,
            flags: 0x0001
        );

        // Act
        var bytes = envelope.ToBytes();
        var success = ProtocolEnvelope.TryDecode(bytes, out var decoded, out var consumed);

        // Assert
        success.Should().BeTrue();
        consumed.Should().Be(bytes.Length);
        decoded.Should().NotBeNull();
        decoded.Version.Should().Be(ProtocolEnvelope.CurrentVersion);
        decoded.ChannelId.Should().Be(DataChannels.Input);
        decoded.MessageType.Should().Be(MessageType.MouseMove);
        decoded.Flags.Should().Be(0x0001);
        decoded.Payload.ToArray().Should().Equal(payload);
    }

    [Fact]
    public void TryDecode_InvalidMagic_ReturnsFalse()
    {
        // Arrange
        var buffer = new byte[ProtocolEnvelope.HeaderSize];
        buffer[0] = 0xFF; // Bad magic
        buffer[1] = 0xFF;

        // Act
        var success = ProtocolEnvelope.TryDecode(buffer, out var decoded, out var consumed);

        // Assert
        success.Should().BeFalse();
        decoded.Should().BeNull();
        consumed.Should().Be(0);
    }

    [Fact]
    public void TryDecode_IncompleteHeader_ReturnsFalse()
    {
        // Arrange
        var buffer = new byte[ProtocolEnvelope.HeaderSize - 1];

        // Act
        var success = ProtocolEnvelope.TryDecode(buffer, out var decoded, out var consumed);

        // Assert
        success.Should().BeFalse();
        decoded.Should().BeNull();
        consumed.Should().Be(0);
    }

    [Fact]
    public void TryDecode_IncompletePayload_ReturnsFalse()
    {
        // Arrange
        var payload = new byte[100];
        var envelope = new ProtocolEnvelope(DataChannels.Control, MessageType.HeartbeatPing, payload);
        var bytes = envelope.ToBytes();

        // Truncate payload by 1 byte
        var truncated = bytes.AsSpan(0, bytes.Length - 1);

        // Act
        var success = ProtocolEnvelope.TryDecode(truncated, out var decoded, out var consumed);

        // Assert
        success.Should().BeFalse();
        decoded.Should().BeNull();
        consumed.Should().Be(0);
    }
}
