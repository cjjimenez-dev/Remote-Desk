using SIPSorceryMedia.FFmpeg;
using Serilog;

namespace RemoteDesk.Platform.Windows;

public static class FFmpegBootstrapper
{
    private static readonly ILogger Logger = Log.ForContext(typeof(FFmpegBootstrapper));
    private static bool _isInitialized;
    private static readonly object Lock = new();

    public static bool Initialize()
    {
        if (_isInitialized) return true;

        lock (Lock)
        {
            if (_isInitialized) return true;

            var candidates = new List<string>
            {
                AppDomain.CurrentDomain.BaseDirectory,
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtimes", "win-x64", "native"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg")
            };

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
            {
                var wingetPackages = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
                if (Directory.Exists(wingetPackages))
                {
                    try
                    {
                        var matches = Directory.GetDirectories(wingetPackages, "*FFmpeg*", SearchOption.AllDirectories);
                        foreach (var match in matches)
                        {
                            var binDir = match.EndsWith("bin", StringComparison.OrdinalIgnoreCase) 
                                ? match 
                                : Path.Combine(match, "bin");

                            if (Directory.Exists(binDir) && File.Exists(Path.Combine(binDir, "avcodec-63.dll")))
                            {
                                candidates.Add(binDir);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug(ex, "Error scanning WinGet packages directory for FFmpeg.");
                    }
                }
            }

            foreach (var path in candidates)
            {
                if (Directory.Exists(path) && File.Exists(Path.Combine(path, "avcodec-63.dll")))
                {
                    try
                    {
                        Logger.Information("Initializing FFmpeg from {Path}", path);
                        FFmpegInit.Initialise(FfmpegLogLevelEnum.AV_LOG_WARNING, path);
                        _isInitialized = true;
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning(ex, "Failed initializing FFmpeg from candidate {Path}", path);
                    }
                }
            }

            // Fallback attempt from system PATH
            try
            {
                FFmpegInit.Initialise(FfmpegLogLevelEnum.AV_LOG_WARNING, null);
                _isInitialized = true;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Could not initialize FFmpeg from any standard search path.");
                return false;
            }
        }
    }
}
