using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RemoteDesk.Core.Interfaces;
using RemoteDesk.Core.Models;
using Serilog;

namespace RemoteDesk.Platform.Windows.Clipboard;

public sealed class WindowsClipboardService : IClipboardService
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int MaxPayloadSizeBytes = 10 * 1024 * 1024; // 10 MB limit

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(nint hwnd);

    private readonly ILogger _logger = Log.ForContext<WindowsClipboardService>();
    private readonly HashSet<string> _recentlyInjectedHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    private Thread? _staThread;
    private Dispatcher? _dispatcher;
    private HwndSource? _hwndSource;
    private readonly ManualResetEventSlim _startedEvent = new(false);
    private bool _isMonitoring;
    private bool _disposed;

    public event Action<ClipboardData>? RemoteClipboardChanged;

    public void StartMonitoring()
    {
        if (_isMonitoring || _disposed) return;

        _startedEvent.Reset();
        _staThread = new Thread(RunStaMessagePump)
        {
            Name = "RemoteDesk-Clipboard-STA",
            IsBackground = true
        };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();

        _startedEvent.Wait(TimeSpan.FromSeconds(5));
        _isMonitoring = true;
        _logger.Information("Windows clipboard monitoring started.");
    }

    public void StopMonitoring()
    {
        if (!_isMonitoring) return;
        _isMonitoring = false;

        if (_dispatcher != null && !_dispatcher.HasShutdownStarted)
        {
            _dispatcher.Invoke(() =>
            {
                if (_hwndSource != null)
                {
                    RemoveClipboardFormatListener(_hwndSource.Handle);
                    _hwndSource.Dispose();
                    _hwndSource = null;
                }
                _dispatcher.InvokeShutdown();
            });
        }

        _staThread?.Join(TimeSpan.FromSeconds(2));
        _staThread = null;
        _dispatcher = null;
        _logger.Information("Windows clipboard monitoring stopped.");
    }

    private void RunStaMessagePump()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;

        var parameters = new HwndSourceParameters("RemoteDeskClipboardHook")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0x800000 // WS_BORDER invisible
        };

        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(HwndHook);

        bool registered = AddClipboardFormatListener(_hwndSource.Handle);
        if (!registered)
        {
            int error = Marshal.GetLastWin32Error();
            _logger.Warning("Failed to register clipboard format listener. Win32Error: {Error}", error);
        }
        else
        {
            _logger.Debug("Registered AddClipboardFormatListener on HWND {Hwnd}", _hwndSource.Handle);
        }

        _startedEvent.Set();
        Dispatcher.Run();
    }

    private nint HwndHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            OnClipboardUpdateReceived();
            handled = true;
        }

        return nint.Zero;
    }

    private void OnClipboardUpdateReceived()
    {
        if (!_isMonitoring) return;

        try
        {
            ClipboardData? data = ReadClipboardWithRetry();
            if (data == null) return;

            string hash = ComputeHash(data);

            lock (_lock)
            {
                if (_recentlyInjectedHashes.Contains(hash))
                {
                    _logger.Debug("Ignoring clipboard update: matches recently injected remote hash {Hash}", hash);
                    _recentlyInjectedHashes.Remove(hash);
                    return;
                }
            }

            _logger.Debug("Local clipboard changed (Format={Format}, Hash={Hash}). Notifying remote peer.", data.Format, hash);
            RemoteClipboardChanged?.Invoke(data);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Error processing clipboard update.");
        }
    }

    public Task SetClipboardAsync(ClipboardData data, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (data.Data.Length > MaxPayloadSizeBytes)
        {
            _logger.Warning("Clipboard data size ({Size} bytes) exceeds limit ({Limit} bytes). Dropping.", data.Data.Length, MaxPayloadSizeBytes);
            return Task.CompletedTask;
        }

        string hash = ComputeHash(data);

        lock (_lock)
        {
            _recentlyInjectedHashes.Add(hash);
            // Limit memory of tracked hashes
            if (_recentlyInjectedHashes.Count > 100)
            {
                _recentlyInjectedHashes.Clear();
                _recentlyInjectedHashes.Add(hash);
            }
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void ApplyToClipboard()
        {
            try
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        if (data.Format == ClipboardFormat.Text && !string.IsNullOrEmpty(data.Text))
                        {
                            System.Windows.Clipboard.SetText(data.Text);
                            _logger.Debug("Set local text clipboard successfully: {Length} chars", data.Text.Length);
                            break;
                        }
                        else if (data.Format == ClipboardFormat.ImagePng && data.Data.Length > 0)
                        {
                            using var ms = new MemoryStream(data.Data);
                            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                            if (decoder.Frames.Count > 0)
                            {
                                System.Windows.Clipboard.SetImage(decoder.Frames[0]);
                                _logger.Debug("Set local image clipboard successfully: {Bytes} bytes", data.Data.Length);
                            }
                            break;
                        }
                    }
                    catch (COMException) when (attempt < 4)
                    {
                        Thread.Sleep(30);
                    }
                }
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to set clipboard data.");
                tcs.TrySetException(ex);
            }
        }

        if (_dispatcher != null && !_dispatcher.HasShutdownStarted)
        {
            _dispatcher.BeginInvoke((Action)ApplyToClipboard);
        }
        else
        {
            // Execute on separate STA thread if monitoring isn't active
            var thread = new Thread(ApplyToClipboard) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        return tcs.Task;
    }

    public Task<ClipboardData?> GetClipboardAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var tcs = new TaskCompletionSource<ClipboardData?>(TaskCreationOptions.RunContinuationsAsynchronously);

        void ReadFromClipboard()
        {
            try
            {
                var data = ReadClipboardWithRetry();
                tcs.TrySetResult(data);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }

        if (_dispatcher != null && !_dispatcher.HasShutdownStarted)
        {
            _dispatcher.BeginInvoke((Action)ReadFromClipboard);
        }
        else
        {
            var thread = new Thread(ReadFromClipboard) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        return tcs.Task;
    }

    private static ClipboardData? ReadClipboardWithRetry()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    string text = System.Windows.Clipboard.GetText();
                    if (!string.IsNullOrEmpty(text))
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(text);
                        if (bytes.Length > MaxPayloadSizeBytes) return null;
                        return new ClipboardData(ClipboardFormat.Text, bytes, text);
                    }
                }
                else if (System.Windows.Clipboard.ContainsImage())
                {
                    var image = System.Windows.Clipboard.GetImage();
                    if (image != null)
                    {
                        using var ms = new MemoryStream();
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(image));
                        encoder.Save(ms);
                        byte[] bytes = ms.ToArray();
                        if (bytes.Length > MaxPayloadSizeBytes) return null;
                        return new ClipboardData(ClipboardFormat.ImagePng, bytes);
                    }
                }

                return null;
            }
            catch (COMException) when (attempt < 4)
            {
                Thread.Sleep(30);
            }
        }

        return null;
    }

    public static string ComputeHash(ClipboardData data)
    {
        byte[] bytesToHash = data.Format == ClipboardFormat.Text && data.Text != null
            ? Encoding.UTF8.GetBytes(data.Text)
            : data.Data;

        byte[] hash = SHA256.HashData(bytesToHash);
        return Convert.ToHexString(hash);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopMonitoring();
        _startedEvent.Dispose();
    }
}
