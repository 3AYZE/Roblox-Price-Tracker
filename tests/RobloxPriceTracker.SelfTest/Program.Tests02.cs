using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using RobloxPriceTracker.Core;
using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{

static async Task TestNotificationOutboxAsync()
{
    var path = TempStatePath();
    try
    {
        var repo = new JsonFileRepository(path);
        await repo.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var key = new ItemKey(CatalogItemType.Asset, 333);
        await repo.AddOrUpdateTrackedAssetAsync(Available(key, 9_000, now, "Outbox Limited"), 8_000);
        var snapshot = await repo.LoadSnapshotAsync(key) ?? throw new Exception("snapshot missing");
        var engine = new AlertEngine();
        var baseline = engine.Evaluate(snapshot, Available(key, 9_000, now.AddSeconds(1), "Outbox Limited"), 1, now.AddSeconds(1));
        await repo.CommitDecisionAsync(baseline, 1);
        var afterBaseline = await repo.LoadSnapshotAsync(key) ?? throw new Exception("baseline snapshot missing");
        var decision = engine.Evaluate(afterBaseline, Available(key, 7_900, now.AddSeconds(2), "Outbox Limited"), 2, now.AddSeconds(2));
        AssertEqual(1, decision.Alerts.Count);
        AssertEqual(1, decision.Alerts.Count(x => x.QueueNotification));
        AssertEqual(AlertEventType.TargetReached, decision.Alerts[0].EventType);
        await repo.CommitDecisionAsync(decision, 2);

        var pending = await repo.GetPendingNotificationsAsync();
        AssertEqual(1, pending.Count);
        await repo.MarkNotificationDeliveredAsync(pending[0].Id, now.AddSeconds(2));
        AssertEqual(0, (await repo.GetPendingNotificationsAsync()).Count);
    }
    finally
    {
        TryDeleteState(path);
    }
}

static async Task TestOldSequenceGuardAsync()
{
    var path = TempStatePath();
    try
    {
        var repo = new JsonFileRepository(path);
        await repo.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var key = new ItemKey(CatalogItemType.Asset, 444);
        await repo.AddOrUpdateTrackedAssetAsync(Available(key, 10_000, now, "Sequence Limited"), 5_000);
        var engine = new AlertEngine();
        var snapshot = await repo.LoadSnapshotAsync(key) ?? throw new Exception("snapshot missing");

        var newer = engine.Evaluate(snapshot, Available(key, 9_000, now.AddSeconds(2), "Sequence Limited"), 2, now.AddSeconds(2));
        await repo.CommitDecisionAsync(newer, 2);

        var stale = engine.Evaluate(snapshot, Available(key, 12_000, now.AddSeconds(1), "Sequence Limited"), 1, now.AddSeconds(1));
        await repo.CommitDecisionAsync(stale, 1);

        var final = await repo.LoadSnapshotAsync(key) ?? throw new Exception("final snapshot missing");
        AssertEqual<long?>(9_000L, final.Market.CurrentLowestPrice);
        AssertEqual(2L, final.Market.LastPollSequence);
    }
    finally
    {
        TryDeleteState(path);
    }
}

static Task TestInputParserAsync()
{
    AssertTrue(ItemInputParser.TryParseAsset("https://www.roblox.com/catalog/123456/Test", out var good, out _));
    AssertEqual(123456L, good.Id);
    AssertTrue(!ItemInputParser.TryParseAsset("https://example.com/catalog/123456/Test", out _, out _));
    return Task.CompletedTask;
}

static Task TestRateLimitGovernorAsync()
{
    var time = new ManualTimeProvider();
    var governor = new RateLimitGovernor(time, randomSeed: 123);
    governor.OnFailure(new ProviderFailure(ProviderFailureKind.RateLimited, "429", TimeSpan.FromSeconds(60)));
    AssertTrue(governor.RemainingDelay > TimeSpan.FromSeconds(50));
    time.Advance(TimeSpan.FromMinutes(2));
    AssertTrue(governor.CanRequest);
    return Task.CompletedTask;
}

static async Task TestProviderRetryAfterAsync()
{
    using var client = new HttpClient(new DelegateHandler((_, _) =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
        return Task.FromResult(response);
    }));
    var provider = new RobloxCatalogProvider(client, new RobloxProviderOptions(1));
    var key = new ItemKey(CatalogItemType.Asset, 555);
    var result = await provider.FetchAsync(new[] { key }, CancellationToken.None);
    AssertEqual(ProviderFailureKind.RateLimited, result.Failure?.Kind ?? ProviderFailureKind.None);
    AssertEqual(TimeSpan.FromSeconds(42), result.Failure?.RetryAfter ?? TimeSpan.Zero);
}

static async Task TestProviderAnonymousCsrfRetryAsync()
{
    var call = 0;
    const string token = "anonymous-csrf-token";
    using var client = new HttpClient(new DelegateHandler((request, _) =>
    {
        call++;
        AssertTrue(request.Headers.TryGetValues("X-Requested-With", out var requestedWith) &&
                   requestedWith.Contains("XMLHttpRequest"));

        if (call == 1)
        {
            AssertTrue(!request.Headers.Contains("x-csrf-token"));
            var challenge = new HttpResponseMessage(HttpStatusCode.Forbidden);
            challenge.Headers.TryAddWithoutValidation("x-csrf-token", token);
            challenge.Content = new StringContent("{\"errors\":[{\"code\":0,\"message\":\"Token Validation Failed\"}]}");
            return Task.FromResult(challenge);
        }

        AssertTrue(request.Headers.TryGetValues("x-csrf-token", out var csrfValues) && csrfValues.Contains(token));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"data\":[{\"id\":556,\"itemType\":\"Asset\",\"name\":\"CSRF Limited\",\"itemRestrictions\":[\"Limited\"],\"lowestPrice\":1234}]}",
                System.Text.Encoding.UTF8,
                "application/json")
        });
    }));

    var provider = new RobloxCatalogProvider(client, new RobloxProviderOptions(1));
    var key = new ItemKey(CatalogItemType.Asset, 556);
    var result = await provider.FetchAsync(new[] { key }, CancellationToken.None);

    AssertEqual(2, call);
    AssertEqual<ProviderFailure?>(null, result.Failure);
    AssertEqual<long?>(1234L, result.Observations[key].LowestResalePrice);
}

static async Task TestProviderPartialFailureAsync()
{
    var call = 0;
    using var client = new HttpClient(new DelegateHandler((_, _) =>
    {
        call++;
        if (call == 1)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[{\"id\":601,\"itemType\":\"Asset\",\"name\":\"First\",\"itemRestrictions\":[\"Limited\"],\"lowestPrice\":1000}]}", System.Text.Encoding.UTF8, "application/json")
            });
        }

        var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
        return Task.FromResult(limited);
    }));

    var provider = new RobloxCatalogProvider(client, new RobloxProviderOptions(1));
    var first = new ItemKey(CatalogItemType.Asset, 601);
    var second = new ItemKey(CatalogItemType.Asset, 602);
    var result = await provider.FetchAsync(new[] { first, second }, CancellationToken.None);
    AssertTrue(result.Observations.ContainsKey(first));
    AssertTrue(!result.Observations.ContainsKey(second));
    AssertEqual(ProviderFailureKind.RateLimited, result.Failure?.Kind ?? ProviderFailureKind.None);
}
}
