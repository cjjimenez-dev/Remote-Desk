using FluentAssertions;
using RemoteDesk.Core.Models;
using RemoteDesk.Core.Protocol;
using Xunit;

namespace RemoteDesk.Core.Tests;

public class MessageSerializationTests
{
    [Fact]
    public void MouseMoveMessage_RoundtripSerialization_PreservesValues()
    {
        // Arrange
        var message = new MouseMoveMessage
        {
            X = 1920,
            Y = 1080,
            ScreenWidth = 3840,
            ScreenHeight = 2160
        };

        // Act
        var bytes = ProtocolSerializer.Serialize(message);
        var deserialized = ProtocolSerializer.Deserialize<MouseMoveMessage>(bytes);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized.X.Should().Be(1920);
        deserialized.Y.Should().Be(1080);
        deserialized.ScreenWidth.Should().Be(3840);
        deserialized.ScreenHeight.Should().Be(2160);
    }

    [Fact]
    public void KeyMessage_RoundtripSerialization_PreservesValues()
    {
        // Arrange
        var message = new KeyMessage
        {
            ScanCode = 0x1E, // 'A' key
            IsExtended = false,
            State = KeyState.Down
        };

        // Act
        var bytes = ProtocolSerializer.Serialize(message);
        var deserialized = ProtocolSerializer.Deserialize<KeyMessage>(bytes);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized.ScanCode.Should().Be(0x1E);
        deserialized.IsExtended.Should().BeFalse();
        deserialized.State.Should().Be(KeyState.Down);
    }
}
