using System.Collections.Concurrent;
using RemoteDesk.Core.Interfaces;
using RemoteDesk.Core.Models;

namespace RemoteDesk.TestHost;

public sealed record InjectedMouseMove(int X, int Y, int ClientWidth, int ClientHeight);
public sealed record InjectedMouseButton(MouseButton Button, ButtonState State);
public sealed record InjectedMouseWheel(int DeltaX, int DeltaY);
public sealed record InjectedKey(ushort ScanCode, bool IsExtended, KeyState State);

public sealed class FakeInputInjector : IInputInjector
{
    public bool IsEnabled { get; set; } = true;
    public DisplayInfo? TargetDisplay { get; private set; }

    public ConcurrentQueue<InjectedMouseMove> MouseMoves { get; } = new();
    public ConcurrentQueue<InjectedMouseButton> MouseButtons { get; } = new();
    public ConcurrentQueue<InjectedMouseWheel> MouseWheels { get; } = new();
    public ConcurrentQueue<InjectedKey> KeyEvents { get; } = new();
    public ConcurrentQueue<string> UnicodeTexts { get; } = new();

    public void SetTargetDisplay(DisplayInfo display)
    {
        TargetDisplay = display;
    }

    public void InjectMouseMove(int targetX, int targetY, int clientWidth, int clientHeight)
    {
        if (!IsEnabled) return;
        MouseMoves.Enqueue(new InjectedMouseMove(targetX, targetY, clientWidth, clientHeight));
    }

    public void InjectMouseButton(MouseButton button, ButtonState state)
    {
        if (!IsEnabled) return;
        MouseButtons.Enqueue(new InjectedMouseButton(button, state));
    }

    public void InjectMouseWheel(int deltaX, int deltaY)
    {
        if (!IsEnabled) return;
        MouseWheels.Enqueue(new InjectedMouseWheel(deltaX, deltaY));
    }

    public void InjectKey(ushort scanCode, bool isExtended, KeyState state)
    {
        if (!IsEnabled) return;
        KeyEvents.Enqueue(new InjectedKey(scanCode, isExtended, state));
    }

    public void InjectUnicodeText(string text)
    {
        if (!IsEnabled) return;
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
