using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RemoteDesk.Core.Models;

namespace RemoteDesk.Viewer.Rendering;

public sealed class WriteableBitmapVideoRenderer : IVideoRenderer
{
    private readonly Dispatcher _dispatcher;
    private WriteableBitmap? _bitmap;
    private int _width;
    private int _height;

    public WriteableBitmap? BitmapSource => _bitmap;
    public event Action? FrameRendered;

    public WriteableBitmapVideoRenderer(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void Render(CapturedFrame frame)
    {
        _dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            EnsureBitmap(frame.Width, frame.Height);

            if (_bitmap is null) return;

            _bitmap.Lock();
            try
            {
                var buffer = frame.Data.Span;
                var sourcePtr = MemoryMarshal.GetReference(buffer);
                unsafe
                {
                    var sourceAddress = (nint)System.Runtime.CompilerServices.Unsafe.AsPointer(ref sourcePtr);
                    var rect = new Int32Rect(0, 0, frame.Width, frame.Height);
                    _bitmap.WritePixels(rect, sourceAddress, frame.Data.Length, frame.Stride);
                }
            }
            finally
            {
                _bitmap.Unlock();
            }

            FrameRendered?.Invoke();
        });
    }

    public void RenderDecoded(DecodedFrame frame)
    {
        _dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            EnsureBitmap(frame.Width, frame.Height);

            if (_bitmap is null) return;

            _bitmap.Lock();
            try
            {
                var buffer = frame.Data.Span;
                var sourcePtr = MemoryMarshal.GetReference(buffer);
                unsafe
                {
                    var sourceAddress = (nint)System.Runtime.CompilerServices.Unsafe.AsPointer(ref sourcePtr);
                    var rect = new Int32Rect(0, 0, frame.Width, frame.Height);
                    _bitmap.WritePixels(rect, sourceAddress, frame.Data.Length, frame.Stride);
                }
            }
            finally
            {
                _bitmap.Unlock();
            }

            FrameRendered?.Invoke();
        });
    }

    private void EnsureBitmap(int width, int height)
    {
        if (_bitmap is not null && _width == width && _height == height)
        {
            return;
        }

        _width = width;
        _height = height;
        _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
    }

    public void Dispose()
    {
        _bitmap = null;
    }
}
