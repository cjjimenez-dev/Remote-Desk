using System.Collections.Concurrent;

namespace RemoteDesk.Core.Security;

public sealed class RateLimiter
{
    public int MaxAttemptsBeforeLockout { get; set; } = 5;
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan WindowDuration { get; set; } = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, AttemptRecord> _records = new();

    public bool IsLockedOut(string key, out TimeSpan remainingLockout)
    {
        remainingLockout = TimeSpan.Zero;

        if (_records.TryGetValue(key, out var record))
        {
            var now = DateTimeOffset.UtcNow;
            if (record.LockedUntil.HasValue)
            {
                if (record.LockedUntil.Value > now)
                {
                    remainingLockout = record.LockedUntil.Value - now;
                    return true;
                }
                else
                {
                    // Lockout expired, reset attempts
                    _records.TryRemove(key, out _);
                    return false;
                }
            }
        }

        return false;
    }

    public void RecordFailedAttempt(string key)
    {
        var now = DateTimeOffset.UtcNow;

        _records.AddOrUpdate(key,
            _ => new AttemptRecord { Attempts = 1, FirstAttempt = now, LastAttempt = now },
            (_, existing) =>
            {
                if (now - existing.LastAttempt > WindowDuration)
                {
                    return new AttemptRecord { Attempts = 1, FirstAttempt = now, LastAttempt = now };
                }

                existing.Attempts++;
                existing.LastAttempt = now;

                if (existing.Attempts >= MaxAttemptsBeforeLockout)
                {
                    existing.LockedUntil = now.Add(LockoutDuration);
                }

                return existing;
            });
    }

    public void RecordSuccess(string key)
    {
        _records.TryRemove(key, out _);
    }

    public void Reset(string key)
    {
        _records.TryRemove(key, out _);
    }

    private sealed class AttemptRecord
    {
        public int Attempts { get; set; }
        public DateTimeOffset FirstAttempt { get; set; }
        public DateTimeOffset LastAttempt { get; set; }
        public DateTimeOffset? LockedUntil { get; set; }
    }
}
