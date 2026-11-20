using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using RemoteDesk.Core.Models;
using RemoteDesk.Viewer.ViewModels;

namespace RemoteDesk.Viewer;

public partial class MainWindow : Window
{
    private readonly ViewerViewModel _viewModel;

    [LibraryImport("user32.dll")]
    private static partial uint MapVirtualKeyExW(uint uCode, uint uMapType, nint dwhkl);

    [LibraryImport("user32.dll")]
    private static partial nint GetKeyboardLayout(uint idThread);

    private const uint MAPVK_VK_TO_VSC_EX = 4;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new ViewerViewModel(Dispatcher);
        DataContext = _viewModel;

        Closed += (s, e) => _viewModel.Dispose();
    }

    private void OnVideoMouseMove(object sender, MouseEventArgs e)
    {
        if (!_viewModel.IsLoopbackActive || _viewModel.Injector == null || _viewModel.VideoImage == null) return;

        var pos = e.GetPosition(VideoImageControl);
        var actualW = VideoImageControl.ActualWidth;
        var actualH = VideoImageControl.ActualHeight;

        if (actualW <= 0 || actualH <= 0) return;

        var pixelX = (int)(pos.X * _viewModel.VideoImage.PixelWidth / actualW);
        var pixelY = (int)(pos.Y * _viewModel.VideoImage.PixelHeight / actualH);

        _viewModel.Injector.InjectMouseMove(pixelX, pixelY, _viewModel.VideoImage.PixelWidth, _viewModel.VideoImage.PixelHeight);
    }

    private void OnVideoMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.IsLoopbackActive || _viewModel.Injector == null) return;

        var btn = MapMouseButton(e.ChangedButton);
        if (btn.HasValue)
        {
            _viewModel.Injector.InjectMouseButton(btn.Value, ButtonState.Down);
            e.Handled = true;
        }
    }

    private void OnVideoMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.IsLoopbackActive || _viewModel.Injector == null) return;

        var btn = MapMouseButton(e.ChangedButton);
        if (btn.HasValue)
        {
            _viewModel.Injector.InjectMouseButton(btn.Value, ButtonState.Up);
            e.Handled = true;
        }
    }

    private void OnVideoMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_viewModel.IsLoopbackActive || _viewModel.Injector == null) return;

        _viewModel.Injector.InjectMouseWheel(0, e.Delta);
        e.Handled = true;
    }

    private static RemoteDesk.Core.Models.MouseButton? MapMouseButton(System.Windows.Input.MouseButton button)
    {
        return button switch
        {
            System.Windows.Input.MouseButton.Left => RemoteDesk.Core.Models.MouseButton.Left,
            System.Windows.Input.MouseButton.Right => RemoteDesk.Core.Models.MouseButton.Right,
            System.Windows.Input.MouseButton.Middle => RemoteDesk.Core.Models.MouseButton.Middle,
            System.Windows.Input.MouseButton.XButton1 => RemoteDesk.Core.Models.MouseButton.XButton1,
            System.Windows.Input.MouseButton.XButton2 => RemoteDesk.Core.Models.MouseButton.XButton2,
            _ => null
        };
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (!_viewModel.IsLoopbackActive || _viewModel.Injector == null) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        var scanCode = GetScanCode(vk, out var isExtended);

        if (scanCode > 0)
        {
            _viewModel.Injector.InjectKey((ushort)scanCode, isExtended, KeyState.Down);
            e.Handled = true;
        }
    }

    private void OnWindowKeyUp(object sender, KeyEventArgs e)
    {
        if (!_viewModel.IsLoopbackActive || _viewModel.Injector == null) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        var scanCode = GetScanCode(vk, out var isExtended);

        if (scanCode > 0)
        {
            _viewModel.Injector.InjectKey((ushort)scanCode, isExtended, KeyState.Up);
            e.Handled = true;
        }
    }

    private void OnWindowTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!_viewModel.IsLoopbackActive || _viewModel.Injector == null) return;

        if (!string.IsNullOrEmpty(e.Text))
        {
            _viewModel.Injector.InjectUnicodeText(e.Text);
            e.Handled = true;
        }
    }

    private static uint GetScanCode(uint vk, out bool isExtended)
    {
        var hkl = GetKeyboardLayout(0);
        var scanCodeEx = MapVirtualKeyExW(vk, MAPVK_VK_TO_VSC_EX, hkl);

        isExtended = (scanCodeEx & 0xE000) != 0 || (scanCodeEx & 0xE100) != 0;
        return scanCodeEx & 0xFF;
    }
}
