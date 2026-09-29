using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;

namespace RobloxPriceTracker.Infrastructure;

/// <summary>
/// All Roblox-facing services share this HTTP layer. Each host has a bounded,
/// independently adaptive request lane. HTTP 429 / Retry-After cools the entire
/// lane, while healthy responses slowly restore throughput. Calls to unrelated
/// hosts are never delayed. The response is owned by the original caller.
/// </summary>
public sealed class AdaptiveRobloxHttpHandler : DelegatingHandler
{
    private readonly ConcurrentDictionary<string, Lane> _lanes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string>? _diagnostics;

    public AdaptiveRobloxHttpHandler(HttpMessageHandler innerHandler, Action<string>? diagnostics = null)
        : base(innerHandler)
    {
        _diagnostics = diagnostics;
    }

    public static bool IsRobloxHost(string host) =>
        string.Equals(host, "roblox.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase);

    public static TimeSpan RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var delay = header?.Delta ?? (header?.Date is { } date
            ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(3));
        return TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds, 1500, 30000));
    }

    public IReadOnlyList<RobloxScanLaneMetrics> ReadMetrics() =>
        _lanes.OrderBy(pair => pair.Key)
            .Select(pair => pair.Value.Snapshot(pair.Key))
            .ToArray();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var host = request.RequestUri?.Host;
        if (host is null || !IsRobloxHost(host))
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // Separate lanes per Roblox service: catalog throttling must not stall
        // already-running resale, thumbnail, or marketplace requests.
        var lane = _lanes.GetOrAdd(host, key => new Lane(key.Contains("catalog.", StringComparison.OrdinalIgnoreCase) ? 2 : 3));
        using var lease = await lane.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var elapsed = Stopwatch.StartNew();
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            elapsed.Stop();
            lane.Record(response.StatusCode, RetryAfter(response), elapsed.Elapsed.TotalMilliseconds);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                _diagnostics?.Invoke($"Scan pacing: {host} returned 429; sharing its cooldown across in-flight scanners.");
            return response;
        }
        catch (HttpRequestException)
        {
            lane.Record(HttpStatusCode.ServiceUnavailable, TimeSpan.Zero, elapsed.Elapsed.TotalMilliseconds);
            throw;
        }
    }

    public sealed record RobloxScanLaneMetrics(
        string Host, long Requests, long RateLimits, double MeanLatencyMs,
        int Parallelism, int SpacingMs, int ActiveRequests);

    private sealed class Lane
    {
        private readonly object _sync = new();
        private readonly SemaphoreSlim _capacity;
        private readonly int _maximumParallelism;
        private readonly int _minimumSpacingMs;
        private DateTimeOffset _nextStart = DateTimeOffset.MinValue;
        private DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;
        private int _spacingMs;
        private int _parallelism = 1;
        private int _active;
        private int _healthyResponses;
        private long _requests;
        private long _rateLimits;
        private double _totalLatencyMs;

        public Lane(int maximumParallelism)
        {
            _maximumParallelism = maximumParallelism;
            _minimumSpacingMs = maximumParallelism == 2 ? 150 : 100;
            _spacingMs = _minimumSpacingMs;
            _capacity = new SemaphoreSlim(maximumParallelism, maximumParallelism);
        }

        public async Task<IDisposable> AcquireAsync(CancellationToken token)
        {
            await _capacity.WaitAsync(token).ConfigureAwait(false);
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    TimeSpan delay;
                    lock (_sync)
                    {
                        var now = DateTimeOffset.UtcNow;
                        var ready = _cooldownUntil > _nextStart ? _cooldownUntil : _nextStart;
                        if (_active < _parallelism && now >= ready)
                        {
                            _active++;
                            _nextStart = now.AddMilliseconds(_spacingMs);
                            return new Lease(this);
                        }

                        delay = _active >= _parallelism
                            ? TimeSpan.FromMilliseconds(35)
                            : ready - now;
                        if (delay < TimeSpan.FromMilliseconds(10))
                            delay = TimeSpan.FromMilliseconds(10);
                    }
                    await Task.Delay(delay, token).ConfigureAwait(false);
                }
            }
            catch
            {
                _capacity.Release();
                throw;
            }
        }

        public void Record(HttpStatusCode status, TimeSpan retry, double latencyMs)
        {
            lock (_sync)
            {
                _requests++;
                _totalLatencyMs += latencyMs;
                if (status == HttpStatusCode.TooManyRequests)
                {
                    _rateLimits++;
                    _healthyResponses = 0;
                    _parallelism = 1;
                    _spacingMs = Math.Min(2000, Math.Max(300, _spacingMs * 2));
                    var until = DateTimeOffset.UtcNow + retry + TimeSpan.FromMilliseconds(Random.Shared.Next(40, 180));
                    if (until > _cooldownUntil) _cooldownUntil = until;
                }
                else if ((int)status >= 500)
                {
                    _healthyResponses = 0;
                    _parallelism = 1;
                    _spacingMs = Math.Min(2000, Math.Max(250, _spacingMs * 2));
                    var until = DateTimeOffset.UtcNow.AddMilliseconds(700);
                    if (until > _cooldownUntil) _cooldownUntil = until;
                }
                else if ((int)status is >= 200 and < 500)
                {
                    if (++_healthyResponses >= 8)
                    {
                        _healthyResponses = 0;
                        _parallelism = Math.Min(_maximumParallelism, _parallelism + 1);
                        _spacingMs = Math.Max(_minimumSpacingMs, (int)(_spacingMs * .82));
                    }
                }
            }
        }

        public RobloxScanLaneMetrics Snapshot(string host)
        {
            lock (_sync)
                return new RobloxScanLaneMetrics(host, _requests, _rateLimits,
                    _requests == 0 ? 0 : _totalLatencyMs / _requests,
                    _parallelism, _spacingMs, _active);
        }

        private void Release()
        {
            lock (_sync) _active--;
            _capacity.Release();
        }

        private sealed class Lease : IDisposable
        {
            private Lane? _lane;
            public Lease(Lane lane) => _lane = lane;
            public void Dispose() => Interlocked.Exchange(ref _lane, null)?.Release();
        }
    }
}
