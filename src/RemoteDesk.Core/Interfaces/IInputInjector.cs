using RemoteDesk.Core.Models;

namespace RemoteDesk.Core.Interfaces;

public interface IInputInjector
{
    void InjectMouseMove(int absoluteX, int absoluteY, int screenWidth, int screenHeight);
    void InjectMouseButton(MouseButton button, ButtonState state);
    void InjectMouseWheel(int deltaX, int deltaY);
    void InjectKey(ushort scanCode, bool isExtended, KeyState state);
    void InjectUnicodeText(string text);
}
