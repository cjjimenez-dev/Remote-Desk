using System;
using System.Linq;
using System.Windows;
using Serilog;

namespace RemoteDesk.Viewer.Services;

public enum AppTheme
{
    Light,
    Dark
}

public static class ThemeManager
{
    private static readonly ILogger Logger = Log.ForContext(typeof(ThemeManager));
    public static AppTheme CurrentTheme { get; private set; } = AppTheme.Light;
    public static event Action<AppTheme>? ThemeChanged;

    public static void ApplyTheme(AppTheme theme)
    {
        CurrentTheme = theme;
        try
        {
            var app = Application.Current;
            if (app == null) return;

            var themeFile = theme == AppTheme.Light ? "Themes/LightTheme.xaml" : "Themes/DarkTheme.xaml";
            var newDict = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/RemoteDesk.Viewer;component/{themeFile}", UriKind.Absolute)
            };

            // Remove existing theme dictionary if present
            var existingTheme = app.Resources.MergedDictionaries
                .FirstOrDefault(d => d.Source != null && d.Source.OriginalString.Contains("Theme.xaml", StringComparison.OrdinalIgnoreCase));

            if (existingTheme != null)
            {
                app.Resources.MergedDictionaries.Remove(existingTheme);
            }

            app.Resources.MergedDictionaries.Add(newDict);
            Logger.Information("Applied application theme: {Theme}", theme);
            ThemeChanged?.Invoke(theme);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to switch application theme to {Theme}", theme);
        }
    }
}
