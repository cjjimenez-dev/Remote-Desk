using MessagePack;
using RemoteDesk.Core.Models;

namespace RemoteDesk.Core.Protocol;

// ==================== CLIPBOARD ====================

[MessagePackObject]
public sealed class ClipboardTextMessage
{
    [Key(0)]
    public string Text { get; set; } = string.Empty;

    [Key(1)]
    public string ContentHash { get; set; } = string.Empty;

    [Key(2)]
    public long SequenceNumber { get; set; }
}

[MessagePackObject]
public sealed class ClipboardImageMessage
{
    [Key(0)]
    public byte[] ImageData { get; set; } = [];

    [Key(1)]
    public string Format { get; set; } = "image/png";

    [Key(2)]
    public int Width { get; set; }

    [Key(3)]
    public int Height { get; set; }

    [Key(4)]
    public string ContentHash { get; set; } = string.Empty;
}

// ==================== FILE TRANSFER ====================

[MessagePackObject]
public sealed class FileTransferRequestMessage
{
    [Key(0)]
    public string TransferId { get; set; } = Guid.NewGuid().ToString("N");

    [Key(1)]
    public string FileName { get; set; } = string.Empty;

    [Key(2)]
    public long FileSizeBytes { get; set; }

    [Key(3)]
    public string Sha256Checksum { get; set; } = string.Empty;
}

[MessagePackObject]
public sealed class FileTransferResponseMessage
{
    [Key(0)]
    public string TransferId { get; set; } = string.Empty;

    [Key(1)]
    public bool Accepted { get; set; }

    [Key(2)]
    public long ResumeOffsetBytes { get; set; }

    [Key(3)]
    public string? RejectReason { get; set; }
}

[MessagePackObject]
public sealed class FileChunkMessage
{
    [Key(0)]
    public string TransferId { get; set; } = string.Empty;

    [Key(1)]
    public long OffsetBytes { get; set; }

    [Key(2)]
    public byte[] Data { get; set; } = [];

    [Key(3)]
    public bool IsLastChunk { get; set; }
}

[MessagePackObject]
public sealed class FileTransferAckMessage
{
    [Key(0)]
    public string TransferId { get; set; } = string.Empty;

    [Key(1)]
    public long AcknowledgedOffsetBytes { get; set; }
}

[MessagePackObject]
public sealed class FileTransferCancelMessage
{
    [Key(0)]
    public string TransferId { get; set; } = string.Empty;

    [Key(1)]
    public string Reason { get; set; } = string.Empty;
}

// ==================== AUDIO ====================

[MessagePackObject]
public sealed class AudioPacketMessage
{
    [Key(0)]
    public long SequenceNumber { get; set; }

    [Key(1)]
    public long TimestampMs { get; set; }

    [Key(2)]
    public int SampleRate { get; set; } = 48000;

    [Key(3)]
    public int Channels { get; set; } = 2;

    [Key(4)]
    public byte[] AudioData { get; set; } = [];
}

[MessagePackObject]
public sealed class AudioControlMessage
{
    [Key(0)]
    public bool IsMuted { get; set; }

    [Key(1)]
    public float Volume { get; set; } = 1.0f; // 0.0 to 1.0
}

// ==================== CHAT ====================

[MessagePackObject]
public sealed class SessionChatMessage
{
    [Key(0)]
    public string MessageId { get; set; } = Guid.NewGuid().ToString("N");

    [Key(1)]
    public string SenderId { get; set; } = string.Empty;

    [Key(2)]
    public string SenderName { get; set; } = string.Empty;

    [Key(3)]
    public string Text { get; set; } = string.Empty;

    [Key(4)]
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}

// ==================== MULTI-MONITOR ====================

[MessagePackObject]
public sealed class DisplayListMessage
{
    [Key(0)]
    public List<DisplayInfo> Displays { get; set; } = [];

    [Key(1)]
    public int ActiveDisplayId { get; set; } = -1; // -1 = Combined/Virtual
}

[MessagePackObject]
public sealed class SwitchDisplayMessage
{
    [Key(0)]
    public int TargetDisplayId { get; set; } // -1 = Combined/Virtual
}

// ==================== ADAPTIVE QUALITY ====================

public enum QualityPreset : byte
{
    Speed = 0,    // Low latency, 30-60 FPS, lower bitrate
    Balanced = 1, // Standard default
    Quality = 2   // Crisp text, higher bitrate
}

[MessagePackObject]
public sealed class QualityMetricsReportMessage
{
    [Key(0)]
    public double RttMs { get; set; }

    [Key(1)]
    public double PacketLossPercent { get; set; }

    [Key(2)]
    public double CurrentFps { get; set; }

    [Key(3)]
    public int CurrentBitrateKbps { get; set; }

    [Key(4)]
    public double DecodeLatencyMs { get; set; }
}

[MessagePackObject]
public sealed class QualitySettingsUpdateMessage
{
    [Key(0)]
    public QualityPreset Preset { get; set; } = QualityPreset.Balanced;

    [Key(1)]
    public int TargetFps { get; set; } = 30;

    [Key(2)]
    public int TargetBitrateKbps { get; set; } = 4000;

    [Key(3)]
    public bool EnableStaticFrameSkip { get; set; } = true;
}
