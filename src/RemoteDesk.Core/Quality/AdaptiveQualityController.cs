using RemoteDesk.Core.Protocol;
using Serilog;

namespace RemoteDesk.Core.Quality;

public sealed class AdaptiveQualityController
{
    private readonly ILogger _logger = Log.ForContext<AdaptiveQualityController>();
    private readonly object _lock = new();

    private QualityPreset _currentPreset = QualityPreset.Balanced;
    private int _currentBitrateKbps = 4000;
    private int _targetFps = 30;
    private bool _enableStaticFrameSkip = true;

    private int _minBitrateKbps = 800;
    private int _maxBitrateKbps = 10000;
    private int _consecutiveHealthyReports = 0;

    public QualityPreset CurrentPreset
    {
        get { lock (_lock) return _currentPreset; }
    }

    public int CurrentBitrateKbps
    {
        get { lock (_lock) return _currentBitrateKbps; }
    }

    public int TargetFps
    {
        get { lock (_lock) return _targetFps; }
    }

    public bool EnableStaticFrameSkip
    {
        get { lock (_lock) return _enableStaticFrameSkip; }
    }

    public event Action<QualitySettingsUpdateMessage>? QualitySettingsChanged;

    public AdaptiveQualityController(QualityPreset initialPreset = QualityPreset.Balanced)
    {
        ApplyPreset(initialPreset);
    }

    public void ApplyPreset(QualityPreset preset)
    {
        lock (_lock)
        {
            _currentPreset = preset;
            switch (preset)
            {
                case QualityPreset.Speed:
                    _targetFps = 60;
                    _currentBitrateKbps = 2000;
                    _minBitrateKbps = 600;
                    _maxBitrateKbps = 4000;
                    _enableStaticFrameSkip = true;
                    break;

                case QualityPreset.Balanced:
                    _targetFps = 30;
                    _currentBitrateKbps = 4000;
                    _minBitrateKbps = 800;
                    _maxBitrateKbps = 8000;
                    _enableStaticFrameSkip = true;
                    break;

                case QualityPreset.Quality:
                    _targetFps = 30;
                    _currentBitrateKbps = 8000;
                    _minBitrateKbps = 2000;
                    _maxBitrateKbps = 15000;
                    _enableStaticFrameSkip = false; // prioritize continuous frame clarity
                    break;
            }

            _logger.Information("Applied quality preset {Preset}: Bitrate={Bitrate} Kbps, FPS={Fps}, FrameSkip={FrameSkip}",
                _currentPreset, _currentBitrateKbps, _targetFps, _enableStaticFrameSkip);

            NotifySettingsChanged();
        }
    }

    public QualitySettingsUpdateMessage ProcessMetrics(QualityMetricsReportMessage metrics)
    {
        lock (_lock)
        {
            bool changed = false;

            // 1. High packet loss (> 5%) or high RTT (> 200ms) indicates network congestion
            if (metrics.PacketLossPercent > 5.0 || metrics.RttMs > 200.0)
            {
                _consecutiveHealthyReports = 0;
                int reducedBitrate = (int)(_currentBitrateKbps * 0.80); // Drop 20%
                reducedBitrate = Math.Max(_minBitrateKbps, reducedBitrate);

                if (reducedBitrate != _currentBitrateKbps)
                {
                    _logger.Warning("Network congestion detected (Loss={Loss:F1}%, RTT={Rtt:F0}ms). Reducing bitrate: {Old} -> {New} Kbps",
                        metrics.PacketLossPercent, metrics.RttMs, _currentBitrateKbps, reducedBitrate);
                    _currentBitrateKbps = reducedBitrate;
                    changed = true;
                }

                // If loss is very high (> 12%), drop FPS target
                if (metrics.PacketLossPercent > 12.0 && _targetFps > 20)
                {
                    _targetFps = 20;
                    changed = true;
                }
            }
            // 2. Low loss (< 1%) and good RTT (< 100ms) -> Healthy connection
            else if (metrics.PacketLossPercent < 1.0 && metrics.RttMs < 100.0)
            {
                _consecutiveHealthyReports++;

                // Wait for 3 consecutive healthy reports before probing upward
                if (_consecutiveHealthyReports >= 3 && _currentBitrateKbps < _maxBitrateKbps)
                {
                    _consecutiveHealthyReports = 0;
                    int increasedBitrate = (int)(_currentBitrateKbps * 1.15); // +15% probe
                    increasedBitrate = Math.Min(_maxBitrateKbps, increasedBitrate);

                    if (increasedBitrate != _currentBitrateKbps)
                    {
                        _logger.Information("Network healthy. Probing higher bitrate: {Old} -> {New} Kbps",
                            _currentBitrateKbps, increasedBitrate);
                        _currentBitrateKbps = increasedBitrate;
                        changed = true;
                    }
                }
            }

            var update = GetCurrentSettings();
            if (changed)
            {
                NotifySettingsChanged();
            }

            return update;
        }
    }

    public QualitySettingsUpdateMessage GetCurrentSettings()
    {
        lock (_lock)
        {
            return new QualitySettingsUpdateMessage
            {
                Preset = _currentPreset,
                TargetBitrateKbps = _currentBitrateKbps,
                TargetFps = _targetFps,
                EnableStaticFrameSkip = _enableStaticFrameSkip
            };
        }
    }

    private void NotifySettingsChanged()
    {
        QualitySettingsChanged?.Invoke(GetCurrentSettings());
    }
}
