using System.Collections.Concurrent;
using RemoteDesk.Core.Interfaces;
using RemoteDesk.Core.Models;

namespace RemoteDesk.TestHost;

public sealed record InjectedMouseMove(int X, int Y, int ScreenWidth, int ScreenHeight);
public sealed record InjectedMouseButton(MouseButton Button, ButtonState State);
public sealed record InjectedMouseWheel(int DeltaX, int DeltaY);
public sealed record InjectedKey(ushort ScanCode, bool IsExtended, KeyState State);

public sealed class FakeInputInjector : IInputInjector
{
    public ConcurrentQueue<InjectedMouseMove> MouseMoves { get; } = new();
    public ConcurrentQueue<InjectedMouseButton> MouseButtons { get; } = new();
    public ConcurrentQueue<InjectedMouseWheel> MouseWheels { get; } = new();
    public ConcurrentQueue<InjectedKey> KeyEvents { get; } = new();
    public ConcurrentQueue<string> UnicodeTexts { get; } = new();

    public void InjectMouseMove(int absoluteX, int absoluteY, int screenWidth, int screenHeight)
    {
        MouseMoves.Enqueue(new InjectedMouseMove(absoluteX, absoluteY, screenWidth, screenHeight));
    }

    public void InjectMouseButton(MouseButton button, ButtonState state)
    {
        MouseButtons.Enqueue(new InjectedMouseButton(button, state));
    }

    public void InjectMouseWheel(int deltaX, int deltaY)
    {
        MouseWheels.Enqueue(new InjectedMouseWheel(deltaX, deltaY));
    }

    public void InjectKey(ushort scanCode, bool isExtended, KeyState state)
    {
        KeyEvents.Enqueue(new InjectedKey(scanCode, isExtended, state));
    }

    public void InjectUnicodeText(string text)
    {
        UnicodeTexts.Enqueue(text);
    }

    public void Clear()
    {
        MouseMoves.Clear();
        MouseButtons.Clear();
        MouseWheels.Clear();
        KeyEvents.Clear();
        UnicodeTexts.Clear();
    }
}
