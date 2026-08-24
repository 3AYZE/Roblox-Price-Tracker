using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using RobloxPriceTracker.Core;
using RobloxPriceTracker.Infrastructure;

sealed class FixedProvider : IMarketProvider
{
    private readonly MarketObservation _observation;

    public FixedProvider(MarketObservation observation)
    {
        _observation = observation;
    }

    public string Name => "FixedProvider";

    public Task<ProviderFetchResult> FetchAsync(IReadOnlyCollection<ItemKey> items, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyDictionary<ItemKey, MarketObservation> result = items.Contains(_observation.ItemKey)
            ? new Dictionary<ItemKey, MarketObservation> { [_observation.ItemKey] = _observation }
            : new Dictionary<ItemKey, MarketObservation>();
        return Task.FromResult(new ProviderFetchResult(result, null));
    }
}

sealed class CountingNotificationSink : INotificationSink
{
    private int _count;
    public int Count => Volatile.Read(ref _count);

    public async Task SendAsync(string title, string body, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _count);
        await Task.Delay(25, cancellationToken);
    }
}

sealed class DelegateHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

    public DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        _handler(request, cancellationToken);
}

sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow = new(2026, 8, 23, 0, 0, 0, TimeSpan.Zero);
    private long _timestamp;
    public override long TimestampFrequency => 1_000_000;
    public override DateTimeOffset GetUtcNow() => _utcNow;
    public override long GetTimestamp() => _timestamp;

    public void Advance(TimeSpan duration)
    {
        _utcNow += duration;
        _timestamp += checked((long)(duration.TotalSeconds * TimestampFrequency));
    }
}
