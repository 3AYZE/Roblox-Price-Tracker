using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using RobloxPriceTracker.Core;
using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{

static async Task TestCoordinatorSequenceResyncAsync()
{
    var path = TempStatePath();
    var logPath = Path.Combine(Path.GetTempPath(), $"rpt-log-{Guid.NewGuid():N}.txt");
    try
    {
        var repo = new JsonFileRepository(path);
        await repo.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var key = new ItemKey(CatalogItemType.Asset, 777);
        await repo.AddOrUpdateTrackedAssetAsync(Available(key, 10_000, now, "Sequence Sync Limited"), 5_000);
        var engine = new AlertEngine();
        var snapshot = await repo.LoadSnapshotAsync(key) ?? throw new Exception("snapshot missing");
        var external = engine.Evaluate(snapshot, Available(key, 9_500, now.AddSeconds(1), "Sequence Sync Limited"), 5, now.AddSeconds(1));
        AssertTrue(await repo.CommitDecisionAsync(external, 5));

        var provider = new FixedProvider(Available(key, 9_000, now.AddSeconds(2), "Sequence Sync Limited"));
        var coordinator = new TrackerCoordinator(repo, provider, engine, new RateLimitGovernor(), 0, new AppLogger(logPath));
        var summary = await coordinator.CheckOnceAsync(CancellationToken.None);
        AssertEqual(6L, summary.PollSequence);
        var final = await repo.LoadSnapshotAsync(key) ?? throw new Exception("final snapshot missing");
        AssertEqual(6L, final.Market.LastPollSequence);
    }
    finally
    {
        TryDeleteState(path);
        try { File.Delete(logPath); } catch { }
    }
}

static async Task TestNotificationDispatcherConcurrencyAsync()
{
    var path = TempStatePath();
    try
    {
        var repo = new JsonFileRepository(path);
        await repo.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var key = new ItemKey(CatalogItemType.Asset, 778);
        await repo.AddOrUpdateTrackedAssetAsync(Available(key, 10_000, now, "Dispatch Limited"), 9_000);
        var engine = new AlertEngine();
        var snapshot = await repo.LoadSnapshotAsync(key) ?? throw new Exception("snapshot missing");
        var baseline = engine.Evaluate(snapshot, Available(key, 10_000, now.AddSeconds(1), "Dispatch Limited"), 1, now.AddSeconds(1));
        AssertTrue(await repo.CommitDecisionAsync(baseline, 1));
        snapshot = await repo.LoadSnapshotAsync(key) ?? throw new Exception("baseline missing");
        var hit = engine.Evaluate(snapshot, Available(key, 8_900, now.AddSeconds(2), "Dispatch Limited"), 2, now.AddSeconds(2));
        AssertTrue(await repo.CommitDecisionAsync(hit, 2));

        var sink = new CountingNotificationSink();
        var dispatcher = new NotificationDispatcher(repo, sink);
        await Task.WhenAll(
            dispatcher.DispatchPendingAsync(CancellationToken.None),
            dispatcher.DispatchPendingAsync(CancellationToken.None));

        AssertEqual(1, sink.Count);
        AssertEqual(0, (await repo.GetPendingNotificationsAsync()).Count);
    }
    finally
    {
        TryDeleteState(path);
    }
}

static TrackerItemSnapshot NewSnapshot(long target, long? trackedLow, AlertState targetState, long? current = null)
{
    var key = new ItemKey(CatalogItemType.Asset, 123);
    var item = new TrackedItem(key, "Test Limited", true, DateTimeOffset.UtcNow);
    var market = MarketState.Empty(key) with
    {
        ObservedStatus = current is null ? MarketStatus.Unknown : MarketStatus.Available,
        CurrentLowestPrice = current,
        LastValidPrice = current,
        TrackedLow = trackedLow,
        LastPollSequence = 0
    };
    var rules = new AlertRule[]
    {
        new(1, key, AlertRuleType.TargetPrice, target, targetState, 100, true, null),
        new(2, key, AlertRuleType.NewTrackedLow, null, AlertState.Armed, 0, true, null)
    };
    return new TrackerItemSnapshot(item, market, rules);
}

static MarketObservation Available(ItemKey key, long price, DateTimeOffset at, string name = "Test Limited") =>
    new(key, name, MarketStatus.Available, price, true, at);

static AlertRule TargetRule(TrackerItemSnapshot snapshot) => snapshot.Rules.Single(x => x.RuleType == AlertRuleType.TargetPrice);
static string TempStatePath() => Path.Combine(Path.GetTempPath(), $"rpt-selftest-{Guid.NewGuid():N}.json");

static void TryDeleteState(string path)
{
    foreach (var suffix in new[] { string.Empty, ".bak", ".tmp" })
    {
        try { File.Delete(path + suffix); } catch { }
    }
}

static void AssertTrue(bool condition)
{
    if (!condition) throw new Exception("Expected condition to be true.");
}

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new Exception($"Expected '{expected}', got '{actual}'.");
    }
}
}
