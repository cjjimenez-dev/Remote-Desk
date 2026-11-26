using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
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
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint hProcess, uint uExitCode);
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
        var exitCode = e.ApplicationExitCode;
        Log.Information("Application OnExit triggered (exitCode={ExitCode}).", exitCode);
        base.OnExit(e);

        // Give background Kestrel/SignalR disposal a short window on a foreground thread,
        // flush logs, attempt graceful Environment.Exit, and force-terminate with clean exitCode
        // if native COM/FFmpeg/msquic teardowns stall process exit.
        var procHandle = Process.GetCurrentProcess().Handle;

        var failsafeThread = new Thread(() =>
        {
            Thread.Sleep(1800);
            Log.CloseAndFlush();
            TerminateProcess(procHandle, unchecked((uint)exitCode));
        })
        {
            IsBackground = false,
            Name = "RemoteDesk.ExitFailsafe"
        };
        failsafeThread.Start();

        var exitThread = new Thread(() =>
        {
            Thread.Sleep(500);
            Log.CloseAndFlush();
            Environment.Exit(exitCode);
        })
        {
            IsBackground = false,
            Name = "RemoteDesk.GracefulExit"
        };
        exitThread.Start();
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

