namespace RobloxPriceTracker.Core;

public enum ProviderFailureKind
{
    None = 0,
    RateLimited = 1,
    Transient = 2,
    Protocol = 3
}

public sealed record ProviderFailure(
    ProviderFailureKind Kind,
    string Message,
    TimeSpan? RetryAfter = null,
    int? HttpStatusCode = null);

public sealed record ProviderFetchResult(
    IReadOnlyDictionary<ItemKey, MarketObservation> Observations,
    ProviderFailure? Failure)
{
    public bool IsCompleteSuccess => Failure is null || Failure.Kind == ProviderFailureKind.None;
}

public interface IMarketProvider
{
    string Name { get; }
    Task<ProviderFetchResult> FetchAsync(IReadOnlyCollection<ItemKey> items, CancellationToken cancellationToken);
}
