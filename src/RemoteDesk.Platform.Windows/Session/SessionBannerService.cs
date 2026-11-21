using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using RemoteDesk.Core.Models;
using Serilog;

namespace RemoteDesk.Platform.Windows.Session;

public sealed class SessionBannerService : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<SessionBannerService>();

    private Thread? _staThread;
    private Window? _bannerWindow;
    private Action? _disconnectCallback;

    public void ShowBanner(string viewerName, string viewerDeviceId, SessionPermissions permissions, Action onDisconnect)
    {
        CloseBanner();

        _disconnectCallback = onDisconnect;
        var readySignal = new ManualResetEventSlim(false);

        _staThread = new Thread(() =>
        {
            try
            {
                var window = new Window
                {
                    Title = "RemoteDesk Active Session",
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = Brushes.Transparent,
                    Topmost = true,
                    ShowInTaskbar = false,
                    ResizeMode = ResizeMode.NoResize,
                    SizeToContent = SizeToContent.WidthAndHeight,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };

                // Position at top center of primary screen
                var screenWidth = SystemParameters.PrimaryScreenWidth;
                window.Left = (screenWidth - 460) / 2;
                window.Top = 16;

                // Border & Container
                var border = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(240, 24, 24, 27)), // zinc-900 with alpha
                    BorderBrush = new SolidColorBrush(Color.FromRgb(39, 39, 42)),      // zinc-800
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(14, 8, 14, 8),
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        BlurRadius = 15,
                        ShadowDepth = 3,
                        Opacity = 0.5,
                        Color = Colors.Black
                    }
                };

                // Allow dragging the banner
                border.MouseLeftButtonDown += (_, e) =>
                {
                    if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
                    {
                        window.DragMove();
                    }
                };

                var stack = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center
                };

                // Green pulse indicator
                var pulseDot = new Ellipse
                {
                    Width = 10,
                    Height = 10,
                    Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129)), // Emerald-500
                    Margin = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };

                // Session text block
                var textStack = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 16, 0)
                };

                var titleText = new TextBlock
                {
                    Text = $"RemoteDesk Active — Connected to {viewerName}",
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 13
                };

                var detailsText = new TextBlock
                {
                    Text = $"Device: {viewerDeviceId}  |  Perms: {permissions}",
                    Foreground = new SolidColorBrush(Color.FromRgb(161, 161, 170)), // zinc-400
                    FontSize = 11,
                    Margin = new Thickness(0, 2, 0, 0)
                };

                textStack.Children.Add(titleText);
                textStack.Children.Add(detailsText);

                // Disconnect button
                var disconnectBtn = new Button
                {
                    Content = "Disconnect",
                    Background = new SolidColorBrush(Color.FromRgb(220, 38, 38)), // Red-600
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.Bold,
                    FontSize = 11,
                    Padding = new Thickness(12, 4, 12, 4),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var btnBorder = new Style(typeof(Border));
                btnBorder.Setters.Add(new Setter(Border.CornerRadiusProperty, new CornerRadius(6)));
                disconnectBtn.Resources.Add(typeof(Border), btnBorder);

                disconnectBtn.Click += (_, _) =>
                {
                    Logger.Information("Host user clicked Disconnect on session banner.");
                    _disconnectCallback?.Invoke();
                };

                stack.Children.Add(pulseDot);
                stack.Children.Add(textStack);
                stack.Children.Add(disconnectBtn);

                border.Child = stack;
                window.Content = border;

                _bannerWindow = window;
                window.Closed += (_, _) => System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();

                window.Show();
                readySignal.Set();

                System.Windows.Threading.Dispatcher.Run();
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to display host session banner.");
                readySignal.Set();
            }
        });

        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.IsBackground = true;
        _staThread.Start();

        readySignal.Wait(TimeSpan.FromSeconds(2));
    }

    public void CloseBanner()
    {
        if (_bannerWindow != null)
        {
            try
            {
                _bannerWindow.Dispatcher.Invoke(() =>
                {
                    _bannerWindow.Close();
                });
            }
            catch
            {
                // Ignored during shutdown
            }
            _bannerWindow = null;
        }

        _staThread = null;
    }

    public void Dispose()
    {
        CloseBanner();
    }
}
