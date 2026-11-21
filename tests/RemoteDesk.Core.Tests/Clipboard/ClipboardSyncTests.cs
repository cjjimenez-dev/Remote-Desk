using System.Text;
using FluentAssertions;
using RemoteDesk.Core.Models;
using RemoteDesk.Core.Protocol;
using RemoteDesk.Platform.Windows.Clipboard;
using Xunit;

namespace RemoteDesk.Core.Tests.Clipboard;

public class ClipboardSyncTests
{
    [Fact]
    public void ComputeHash_ForText_ProducesDeterministicSha256()
    {
        var text = "Hello from RemoteDesk!";
        var data1 = new ClipboardData(ClipboardFormat.Text, Encoding.UTF8.GetBytes(text), text);
        var data2 = new ClipboardData(ClipboardFormat.Text, Encoding.UTF8.GetBytes(text), text);

        string hash1 = WindowsClipboardService.ComputeHash(data1);
        string hash2 = WindowsClipboardService.ComputeHash(data2);

        hash1.Should().NotBeNullOrEmpty();
        hash1.Should().Be(hash2);
        hash1.Length.Should().Be(64); // 32 bytes hex = 64 chars
    }

    [Fact]
    public void ComputeHash_ForDifferentContent_ProducesDifferentHashes()
    {
        var data1 = new ClipboardData(ClipboardFormat.Text, Encoding.UTF8.GetBytes("Alpha"), "Alpha");
        var data2 = new ClipboardData(ClipboardFormat.Text, Encoding.UTF8.GetBytes("Beta"), "Beta");

        string hash1 = WindowsClipboardService.ComputeHash(data1);
        string hash2 = WindowsClipboardService.ComputeHash(data2);

        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void ClipboardTextMessage_SerializesAndDeserializesCorrectly()
    {
        var original = new ClipboardTextMessage
        {
            Text = "Remote copy-paste test",
            ContentHash = "abcdef123456",
            SequenceNumber = 42
        };

        byte[] serialized = ProtocolSerializer.Serialize(original);
        var deserialized = ProtocolSerializer.Deserialize<ClipboardTextMessage>(serialized);

        deserialized.Text.Should().Be(original.Text);
        deserialized.ContentHash.Should().Be(original.ContentHash);
        deserialized.SequenceNumber.Should().Be(original.SequenceNumber);
    }

    [Fact]
    public void ClipboardImageMessage_SerializesAndDeserializesCorrectly()
    {
        byte[] fakePng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
        var original = new ClipboardImageMessage
        {
            ImageData = fakePng,
            Format = "image/png",
            Width = 1920,
            Height = 1080,
            ContentHash = "hash12345"
        };

        byte[] serialized = ProtocolSerializer.Serialize(original);
        var deserialized = ProtocolSerializer.Deserialize<ClipboardImageMessage>(serialized);

        deserialized.ImageData.Should().Equal(fakePng);
        deserialized.Format.Should().Be("image/png");
        deserialized.Width.Should().Be(1920);
        deserialized.Height.Should().Be(1080);
        deserialized.ContentHash.Should().Be("hash12345");
    }
}
