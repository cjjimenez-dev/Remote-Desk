using System;
using System.IO;
using System.Windows;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace RemoteDesk.Viewer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RemoteDesk",
            "logs");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.Sink(new DailyFileLogSink(logDir))
            .CreateLogger();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        Log.CloseAndFlush();
    }

    private sealed class DailyFileLogSink : ILogEventSink
    {
        private readonly string _logDir;
        private readonly object _lock = new();

        public DailyFileLogSink(string logDir)
        {
            _logDir = logDir;
            try { Directory.CreateDirectory(_logDir); } catch { }
        }

        public void Emit(LogEvent logEvent)
        {
            try
            {
                var file = Path.Combine(_logDir, $"viewer-{DateTime.Now:yyyyMMdd}.log");
                var line = $"[{logEvent.Timestamp:HH:mm:ss} {logEvent.Level}] {logEvent.RenderMessage()}";
                if (logEvent.Exception != null)
                {
                    line += Environment.NewLine + logEvent.Exception;
                }
                lock (_lock)
                {
                    File.AppendAllText(file, line + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}

