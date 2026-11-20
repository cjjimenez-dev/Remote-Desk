namespace RemoteDesk.Core.Models;

public enum ClipboardFormat
{
    Text,
    Rtf,
    Html,
    ImagePng,
    FileList
}

public sealed record ClipboardData(
    ClipboardFormat Format,
    byte[] Data,
    string? Text = null);
