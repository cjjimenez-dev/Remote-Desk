using System.Buffers.Binary;

namespace RemoteDesk.Core.Protocol;

public sealed class ProtocolEnvelope
{
    public const ushort ExpectedMagic = 0x5244; // 'R', 'D' (0x52, 0x44)
    public const byte CurrentVersion = 1;
    public const int HeaderSize = 12;

    public byte Version { get; init; } = CurrentVersion;
    public byte ChannelId { get; init; }
    public MessageType MessageType { get; init; }
    public ushort Flags { get; init; }
    public ReadOnlyMemory<byte> Payload { get; init; }

    public ProtocolEnvelope()
    {
    }

    public ProtocolEnvelope(byte channelId, MessageType messageType, ReadOnlyMemory<byte> payload, ushort flags = 0)
    {
        Version = CurrentVersion;
        ChannelId = channelId;
        MessageType = messageType;
        Flags = flags;
        Payload = payload;
    }

    public byte[] ToBytes()
    {
        var result = new byte[HeaderSize + Payload.Length];
        WriteTo(result);
        return result;
    }

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < HeaderSize + Payload.Length)
        {
            throw new ArgumentException("Destination buffer is too small.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt16BigEndian(destination[0..2], ExpectedMagic);
        destination[2] = Version;
        destination[3] = ChannelId;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..6], (ushort)MessageType);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..8], Flags);
        BinaryPrimitives.WriteInt32LittleEndian(destination[8..12], Payload.Length);

        if (!Payload.IsEmpty)
        {
            Payload.Span.CopyTo(destination[HeaderSize..]);
        }
    }

    public static bool TryDecode(ReadOnlySpan<byte> source, out ProtocolEnvelope envelope, out int consumedBytes)
    {
        envelope = null!;
        consumedBytes = 0;

        if (source.Length < HeaderSize)
        {
            return false;
        }

        var magic = BinaryPrimitives.ReadUInt16BigEndian(source[0..2]);
        if (magic != ExpectedMagic)
        {
            return false;
        }

        var version = source[2];
        var channelId = source[3];
        var messageType = (MessageType)BinaryPrimitives.ReadUInt16LittleEndian(source[4..6]);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(source[6..8]);
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(source[8..12]);

        if (payloadLength < 0 || payloadLength > 100 * 1024 * 1024) // 100 MB max safety boundary
        {
            return false;
        }

        var totalSize = HeaderSize + payloadLength;
        if (source.Length < totalSize)
        {
            return false;
        }

        var payloadCopy = source.Slice(HeaderSize, payloadLength).ToArray();
        consumedBytes = totalSize;

        envelope = new ProtocolEnvelope
        {
            Version = version,
            ChannelId = channelId,
            MessageType = messageType,
            Flags = flags,
            Payload = payloadCopy
        };

        return true;
    }
}
