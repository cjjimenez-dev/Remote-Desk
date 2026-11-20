using System.Windows.Media.Imaging;
using RemoteDesk.Core.Models;

namespace RemoteDesk.Viewer.Rendering;

public interface IVideoRenderer : IDisposable
{
    WriteableBitmap? BitmapSource { get; }
    event Action? FrameRendered;
    void Render(CapturedFrame frame);
}
