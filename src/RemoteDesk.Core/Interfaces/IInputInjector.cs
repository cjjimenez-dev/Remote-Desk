using RemoteDesk.Core.Models;

namespace RemoteDesk.Core.Interfaces;

public interface IInputInjector
{
    bool IsEnabled { get; set; }

    void SetTargetDisplay(DisplayInfo display);
    void InjectMouseMove(int targetX, int targetY, int clientWidth, int clientHeight);
    void InjectMouseButton(MouseButton button, ButtonState state);
    void InjectMouseWheel(int deltaX, int deltaY);
    void InjectKey(ushort scanCode, bool isExtended, KeyState state);
    void InjectUnicodeText(string text);
}
