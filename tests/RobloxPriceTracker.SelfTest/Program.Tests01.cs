using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using RobloxPriceTracker.Core;
using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
static Task TestCatalogParserAvailableAsync()
{
    const string json = """
        {"data":[{"id":123,"itemType":"Asset","name":"Example Limited","itemRestrictions":["Limited"],"lowestPrice":4500}]}
        """;
    using var doc = JsonDocument.Parse(json);
    var key = new ItemKey(CatalogItemType.Asset, 123);
    var result = RobloxCatalogParser.Parse(new[] { key }, doc.RootElement, DateTimeOffset.UtcNow);
    AssertEqual(MarketStatus.Available, result[key].Status);
    AssertEqual<long?>(4500L, result[key].LowestResalePrice);
    AssertTrue(result[key].IsResaleCapable);
    return Task.CompletedTask;
}

static Task TestNoResellersAsync()
{
    const string json = """
        {"data":[{"id":123,"itemType":"Asset","name":"Example Limited","itemRestrictions":["Limited"],"priceStatus":"No Resellers"}]}
        """;
    using var doc = JsonDocument.Parse(json);
    var key = new ItemKey(CatalogItemType.Asset, 123);
    var result = RobloxCatalogParser.Parse(new[] { key }, doc.RootElement, DateTimeOffset.UtcNow)[key];
    AssertEqual(MarketStatus.NoResellers, result.Status);
    AssertEqual<long?>(null, result.LowestResalePrice);
    AssertTrue(!result.HasActionablePrice);
    return Task.CompletedTask;
}

static Task TestPrimaryPriceNotFallbackAsync()
{
    const string json = """
        {"data":[{"id":124,"itemType":"Asset","name":"Ordinary Item","itemRestrictions":[],"price":5}]}
        """;
    using var doc = JsonDocument.Parse(json);
    var key = new ItemKey(CatalogItemType.Asset, 124);
    var result = RobloxCatalogParser.Parse(new[] { key }, doc.RootElement, DateTimeOffset.UtcNow)[key];
    AssertEqual(MarketStatus.Unsupported, result.Status);
    AssertEqual<long?>(null, result.LowestResalePrice);
    return Task.CompletedTask;
}

static Task TestZeroLowestPriceAsync()
{
    const string json = """
        {"data":[{"id":125,"itemType":"Asset","name":"Broken Limited","itemRestrictions":["Limited"],"lowestPrice":0}]}
        """;
    using var doc = JsonDocument.Parse(json);
    var key = new ItemKey(CatalogItemType.Asset, 125);
    var result = RobloxCatalogParser.Parse(new[] { key }, doc.RootElement, DateTimeOffset.UtcNow)[key];
    AssertEqual(MarketStatus.InvalidPrice, result.Status);
    AssertEqual<long?>(null, result.LowestResalePrice);
    return Task.CompletedTask;
}

static Task TestMissingBatchAsync()
{
    using var doc = JsonDocument.Parse("{\"data\":[]}");
    var key = new ItemKey(CatalogItemType.Asset, 999);
    var result = RobloxCatalogParser.Parse(new[] { key }, doc.RootElement, DateTimeOffset.UtcNow)[key];
    AssertEqual(MarketStatus.MissingFromResponse, result.Status);
    return Task.CompletedTask;
}

static Task TestTrackedLowBaselineAsync()
{
    var now = DateTimeOffset.UtcNow;
    var snapshot = NewSnapshot(target: 8_000, trackedLow: null, targetState: AlertState.Armed);
    var engine = new AlertEngine();
    var observation = Available(snapshot.Item.ItemKey, 10_000, now);
    var decision = engine.Evaluate(snapshot, observation, 1, now);
    AssertEqual<long?>(10_000L, decision.UpdatedSnapshot.Market.TrackedLow);
    AssertEqual(0, decision.Alerts.Count(x => x.EventType == AlertEventType.NewTrackedLow));
    return Task.CompletedTask;
}

static Task TestTrackedLowAlertAsync()
{
    var now = DateTimeOffset.UtcNow;
    var snapshot = NewSnapshot(target: 5_000, trackedLow: 10_000, targetState: AlertState.Armed, current: 10_000);
    snapshot = snapshot with { Rules = snapshot.Rules.Where(x => x.RuleType == AlertRuleType.NewTrackedLow).ToArray() };
    var engine = new AlertEngine();
    var decision = engine.Evaluate(snapshot, Available(snapshot.Item.ItemKey, 9_700, now), 2, now);
    AssertEqual(1, decision.Alerts.Count(x => x.EventType == AlertEventType.NewTrackedLow));
    AssertEqual<long?>(9_700L, decision.UpdatedSnapshot.Market.TrackedLow);
    return Task.CompletedTask;
}

static Task TestFarTargetSuppressesNewLowNotificationAsync()
{
    var now = DateTimeOffset.UtcNow;
    var snapshot = NewSnapshot(target: 5_000, trackedLow: 10_000, targetState: AlertState.Armed, current: 10_000);
    var engine = new AlertEngine();
    var decision = engine.Evaluate(snapshot, Available(snapshot.Item.ItemKey, 9_000, now), 2, now);
    AssertEqual(0, decision.Alerts.Count);
    AssertEqual<long?>(9_000L, decision.UpdatedSnapshot.Market.TrackedLow);
    return Task.CompletedTask;
}

static Task TestApproachingTargetAlertAsync()
{
    var now = DateTimeOffset.UtcNow;
    var snapshot = NewSnapshot(target: 8_000, trackedLow: 9_000, targetState: AlertState.Armed, current: 9_000);
    var engine = new AlertEngine();
    var decision = engine.Evaluate(snapshot, Available(snapshot.Item.ItemKey, 8_700, now), 2, now);
    AssertEqual(1, decision.Alerts.Count(x => x.EventType == AlertEventType.TargetApproaching));
    AssertEqual(1, decision.Alerts.Count(x => x.QueueNotification));
    AssertEqual(AlertState.Armed, TargetRule(decision.UpdatedSnapshot).State);
    return Task.CompletedTask;
}

static Task TestApproachingTargetTinyDropSuppressedAsync()
{
    var now = DateTimeOffset.UtcNow;
    var snapshot = NewSnapshot(target: 8_000, trackedLow: 8_500, targetState: AlertState.Armed, current: 8_500);
    var engine = new AlertEngine();
    var decision = engine.Evaluate(snapshot, Available(snapshot.Item.ItemKey, 8_400, now), 2, now);
    AssertEqual(0, decision.Alerts.Count(x => x.EventType == AlertEventType.TargetApproaching));
    AssertEqual<long?>(8_400L, decision.UpdatedSnapshot.Market.TrackedLow);
    return Task.CompletedTask;
}

static Task TestApproachingTargetCooldownAsync()
{
    var now = DateTimeOffset.UtcNow;
    var snapshot = NewSnapshot(target: 8_000, trackedLow: 9_000, targetState: AlertState.Armed, current: 9_000);
    var engine = new AlertEngine();

    var first = engine.Evaluate(snapshot, Available(snapshot.Item.ItemKey, 8_700, now), 2, now);
    AssertEqual(1, first.Alerts.Count(x => x.EventType == AlertEventType.TargetApproaching));

    var second = engine.Evaluate(first.UpdatedSnapshot, Available(snapshot.Item.ItemKey, 8_400, now.AddMinutes(10)), 3, now.AddMinutes(10));
    AssertEqual(0, second.Alerts.Count(x => x.EventType == AlertEventType.TargetApproaching));

    var third = engine.Evaluate(second.UpdatedSnapshot, Available(snapshot.Item.ItemKey, 8_100, now.AddMinutes(61)), 4, now.AddMinutes(61));
    AssertEqual(1, third.Alerts.Count(x => x.EventType == AlertEventType.TargetApproaching));
    return Task.CompletedTask;
}

static Task TestTargetDedupAndRearmAsync()
{
    var now = DateTimeOffset.UtcNow;
    var engine = new AlertEngine();
    var snapshot = NewSnapshot(target: 8_000, trackedLow: 9_000, targetState: AlertState.Armed, current: 9_000);

    var first = engine.Evaluate(snapshot, Available(snapshot.Item.ItemKey, 7_900, now), 2, now);
    AssertEqual(1, first.Alerts.Count(x => x.EventType == AlertEventType.TargetReached));
    AssertEqual(1, first.Alerts.Count);
    AssertEqual(1, first.Alerts.Count(x => x.QueueNotification));
    AssertEqual(AlertState.Triggered, TargetRule(first.UpdatedSnapshot).State);

    var second = engine.Evaluate(first.UpdatedSnapshot, Available(snapshot.Item.ItemKey, 7_500, now.AddMinutes(1)), 3, now.AddMinutes(1));
    AssertEqual(0, second.Alerts.Count(x => x.EventType == AlertEventType.TargetReached));

    var rearmPrice = AlertEngine.CalculateRearmPrice(8_000, 100);
    var third = engine.Evaluate(second.UpdatedSnapshot, Available(snapshot.Item.ItemKey, rearmPrice, now.AddMinutes(2)), 4, now.AddMinutes(2));
    AssertEqual(AlertState.Armed, TargetRule(third.UpdatedSnapshot).State);

    var fourth = engine.Evaluate(third.UpdatedSnapshot, Available(snapshot.Item.ItemKey, 7_999, now.AddMinutes(3)), 5, now.AddMinutes(3));
    AssertEqual(1, fourth.Alerts.Count(x => x.EventType == AlertEventType.TargetReached));
    return Task.CompletedTask;
}

static async Task TestRestartPersistenceAsync()
{
    var path = TempStatePath();
    try
    {
        var repo = new JsonFileRepository(path);
        await repo.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var key = new ItemKey(CatalogItemType.Asset, 222);
        var resolved = Available(key, 9_000, now, "Persistent Limited");
        await repo.AddOrUpdateTrackedAssetAsync(resolved, 8_000);

        var snapshot = await repo.LoadSnapshotAsync(key) ?? throw new Exception("snapshot missing");
        var engine = new AlertEngine();
        var hit = engine.Evaluate(snapshot, Available(key, 7_900, now.AddSeconds(1), "Persistent Limited"), 1, now.AddSeconds(1));
        await repo.CommitDecisionAsync(hit, 1);

        var restartedRepo = new JsonFileRepository(path);
        await restartedRepo.InitializeAsync();
        var reloaded = await restartedRepo.LoadSnapshotAsync(key) ?? throw new Exception("reloaded snapshot missing");
        AssertEqual(AlertState.Triggered, TargetRule(reloaded).State);

        var second = engine.Evaluate(reloaded, Available(key, 7_500, now.AddMinutes(1), "Persistent Limited"), 2, now.AddMinutes(1));
        AssertEqual(0, second.Alerts.Count(x => x.EventType == AlertEventType.TargetReached));
    }
    finally
    {
        TryDeleteState(path);
    }
}

static async Task TestDisableReAddAsync()
{
    var path = TempStatePath();
    try
    {
        var repo = new JsonFileRepository(path);
        await repo.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var key = new ItemKey(CatalogItemType.Asset, 277);
        var observation = Available(key, 9_000, now, "ReAdd Limited");
        await repo.AddOrUpdateTrackedAssetAsync(observation, 8_000);
        await repo.DisableItemAsync(key);
        await repo.AddOrUpdateTrackedAssetAsync(observation, 7_500);

        var snapshot = await repo.LoadSnapshotAsync(key) ?? throw new Exception("re-added snapshot missing");
        AssertEqual(2, snapshot.Rules.Count);
        AssertTrue(snapshot.Rules.All(x => x.Enabled && x.State == AlertState.Armed));
    }
    finally
    {
        TryDeleteState(path);
    }
}
}
