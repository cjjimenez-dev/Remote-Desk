using FluentAssertions;
using RemoteDesk.Core.Security;
using Xunit;

namespace RemoteDesk.Core.Tests.Security;

public class RateLimiterTests
{
    [Fact]
    public void RateLimiter_AllowsAttemptsUnderThreshold()
    {
        var limiter = new RateLimiter
        {
            MaxAttemptsBeforeLockout = 3,
            LockoutDuration = TimeSpan.FromMinutes(10)
        };

        var key = "viewer-device-1";

        limiter.RecordFailedAttempt(key);
        limiter.RecordFailedAttempt(key);

        var isLocked = limiter.IsLockedOut(key, out var remaining);
        isLocked.Should().BeFalse();
        remaining.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void RateLimiter_LocksOutWhenThresholdExceeded()
    {
        var limiter = new RateLimiter
        {
            MaxAttemptsBeforeLockout = 3,
            LockoutDuration = TimeSpan.FromMinutes(10)
        };

        var key = "viewer-device-2";

        limiter.RecordFailedAttempt(key);
        limiter.RecordFailedAttempt(key);
        limiter.RecordFailedAttempt(key); // 3rd attempt reaches limit

        var isLocked = limiter.IsLockedOut(key, out var remaining);
        isLocked.Should().BeTrue();
        remaining.Should().BeGreaterThan(TimeSpan.FromMinutes(9));
        remaining.Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void RateLimiter_RecordSuccess_ResetsAttempts()
    {
        var limiter = new RateLimiter
        {
            MaxAttemptsBeforeLockout = 3,
            LockoutDuration = TimeSpan.FromMinutes(10)
        };

        var key = "viewer-device-3";

        limiter.RecordFailedAttempt(key);
        limiter.RecordFailedAttempt(key);

        limiter.RecordSuccess(key);

        limiter.RecordFailedAttempt(key); // Fresh count: 1

        var isLocked = limiter.IsLockedOut(key, out _);
        isLocked.Should().BeFalse();
    }
}
