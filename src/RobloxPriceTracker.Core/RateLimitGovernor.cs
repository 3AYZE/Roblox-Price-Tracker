namespace RobloxPriceTracker.Core;

public sealed class RateLimitGovernor
{
    private readonly TimeProvider _timeProvider;
    private readonly Random _random;
    private long _notBeforeTimestamp;
    private int _consecutiveFailures;

    public RateLimitGovernor(TimeProvider? timeProvider = null, int? randomSeed = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _random = randomSeed is null ? Random.Shared : new Random(randomSeed.Value);
    }

    public int ConsecutiveFailures => _consecutiveFailures;

    public TimeSpan RemainingDelay
    {
        get
        {
            var now = _timeProvider.GetTimestamp();
            var remainingTicks = _notBeforeTimestamp - now;
            if (remainingTicks <= 0)
            {
                return TimeSpan.Zero;
            }

            return TimeSpan.FromSeconds(remainingTicks / (double)_timeProvider.TimestampFrequency);
        }
    }

    public bool CanRequest => RemainingDelay <= TimeSpan.Zero;

    public void ApplyMinimumDelay(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        if (delay > RemainingDelay)
        {
            _notBeforeTimestamp = AddDelay(_timeProvider.GetTimestamp(), delay);
        }
    }

    public void OnSuccess()
    {
        if (_consecutiveFailures > 0)
        {
            _consecutiveFailures--;
        }

        if (_consecutiveFailures == 0)
        {
            _notBeforeTimestamp = 0;
        }
    }

    public void OnFailure(ProviderFailure failure)
    {
        _consecutiveFailures = Math.Min(_consecutiveFailures + 1, 12);

        var delay = failure.Kind switch
        {
            ProviderFailureKind.RateLimited when failure.RetryAfter is not null => failure.RetryAfter.Value,
            ProviderFailureKind.RateLimited => ExponentialBackoff(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10)),
            ProviderFailureKind.Transient => ExponentialBackoff(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(2)),
            ProviderFailureKind.Protocol => TimeSpan.FromMinutes(5),
            _ => TimeSpan.Zero
        };

        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        var jitterMultiplier = 1.00 + (_random.NextDouble() * 0.10);
        delay = TimeSpan.FromMilliseconds(Math.Max(1, delay.TotalMilliseconds * jitterMultiplier));
        _notBeforeTimestamp = AddDelay(_timeProvider.GetTimestamp(), delay);
    }

    private TimeSpan ExponentialBackoff(TimeSpan baseDelay, TimeSpan cap)
    {
        var exponent = Math.Max(0, _consecutiveFailures - 1);
        var multiplier = Math.Pow(2, Math.Min(exponent, 8));
        var delayMs = Math.Min(cap.TotalMilliseconds, baseDelay.TotalMilliseconds * multiplier);
        return TimeSpan.FromMilliseconds(delayMs);
    }

    private long AddDelay(long timestamp, TimeSpan delay)
    {
        var delta = checked((long)Math.Ceiling(delay.TotalSeconds * _timeProvider.TimestampFrequency));
        return checked(timestamp + delta);
    }
}
