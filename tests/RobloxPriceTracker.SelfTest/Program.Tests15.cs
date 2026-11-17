using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
    private static readonly DateTimeOffset ReferenceTime = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static UgcPriceIntegrityPolicy.PrimaryPriceObservation Original(
        int price, DateTimeOffset observed, int type = 41, long supply = 200,
        long assetId = 1111, bool verified = true, long creatorId = 1234) =>
        new(assetId, creatorId, type, price, supply, observed, verified, "RobloxMarketplacePrimary");

    private static UgcPriceIntegrityPolicy.ResaleEvidence Market(
        long? ask = null, double? rap = null, double? completed = null,
        double? dailyMedian = null, int days = 0, int comparableCount = 0,
        double? comparableMedian = null, int volumeDays = 0) =>
        new(ask, rap, completed, dailyMedian, days, comparableCount, comparableMedian, volumeDays);

    // The numbers mirror the user-reported Angelic Valkyrie scenario, but the
    // observed 1080 R$ and low secondary-market volume are SYNTHETIC here;
    // the real asset is NOT hardcoded or deemed verified by this test.
    static Task TestUgcPriceIntegrityVerifiedInflationAsync()
    {
        var low = Original(65, ReferenceTime.AddDays(-45));
        var current = Original(1080, ReferenceTime, supply: 38);
        var result = UgcPriceIntegrityPolicy.Evaluate(current, [low],
            Market(ask: 500, rap: 65, completed: 1, dailyMedian: 145, days: 3, volumeDays: 20));
        AssertEqual(UgcPriceIntegrityPolicy.Excluded, result.Classification);
        AssertEqual(65, result.EarliestVerifiedPrimaryPrice);
        AssertTrue(result.CreatorPriceMultiple > 16);
        AssertTrue(result.ReasonCodes.Contains("LARGE_VERIFIED_REPRICING"));
        AssertTrue(result.ReasonCodes.Contains("CREATOR_ANCHOR_FALSE_BARGAIN"));
        AssertTrue(result.ReasonCodes.Contains("INSUFFICIENT_COMPLETED_RESALES"));
        AssertTrue(result.ReasonCodes.Contains("PRICE_RISE_DURING_INVENTORY_DECLINE"));
        return Task.CompletedTask;
    }

    static Task TestUgcPriceIntegrityAcrossCategoriesAsync()
    {
        foreach (var category in new[] { 8, 41, 42, 44, 46, 61, 67, 79 })
        {
            var before = Original(95, ReferenceTime.AddDays(-30), type: category, assetId: 8100 + category);
            var after = Original(495, ReferenceTime, type: category, assetId: 8100 + category, supply: 75);
            var result = UgcPriceIntegrityPolicy.Evaluate(after, [before], Market(ask: 180, completed: 0, volumeDays: 20));
            AssertEqual(UgcPriceIntegrityPolicy.Excluded, result.Classification);
        }
        return Task.CompletedTask;
    }

    static Task TestUgcPriceIntegrityIndependentDemandProtectionAsync()
    {
        var earlier = Original(95, ReferenceTime.AddDays(-35), type: 8);
        var current = Original(600, ReferenceTime, type: 8);
        var result = UgcPriceIntegrityPolicy.Evaluate(current, [earlier],
            Market(ask: 420, completed: 29, dailyMedian: 520, days: 12));
        AssertEqual(UgcPriceIntegrityPolicy.Passed, result.Classification);
        AssertTrue(result.ReasonCodes.Contains("INDEPENDENT_RESALE_SUPPORT"));
        return Task.CompletedTask;
    }

    static Task TestUgcPriceIntegrityCorrectionsAsync()
    {
        var early = Original(95, ReferenceTime.AddDays(-30));
        var raised = Original(490, ReferenceTime.AddDays(-5));
        var corrected = Original(95, ReferenceTime);
        var result = UgcPriceIntegrityPolicy.Evaluate(corrected, [early, raised],
            Market(ask: 90, completed: 1, dailyMedian: 80, days: 4));
        AssertEqual(UgcPriceIntegrityPolicy.Passed, result.Classification);

        var unchanged = UgcPriceIntegrityPolicy.Evaluate(Original(65, ReferenceTime),
            [Original(65, ReferenceTime.AddDays(-10))], Market(ask: 900, completed: 0));
        AssertEqual(UgcPriceIntegrityPolicy.Passed, unchanged.Classification);
        return Task.CompletedTask;
    }

    static Task TestUgcPriceIntegrityUnknownVolumeAsync()
    {
        var earlier = Original(65, ReferenceTime.AddDays(-45));
        var current = Original(1080, ReferenceTime);
        var noKnownCompletedTransactions = UgcPriceIntegrityPolicy.Evaluate(
            current, [earlier], Market(ask: 500, rap: 65));
        AssertEqual(UgcPriceIntegrityPolicy.Insufficient, noKnownCompletedTransactions.Classification);
        AssertTrue(noKnownCompletedTransactions.ReasonCodes.Contains("INDEPENDENT_CORROBORATION_MISSING"));
        return Task.CompletedTask;
    }

    static Task TestUgcPriceIntegrityMissingHistoryAsync()
    {
        var newItem = Original(199, ReferenceTime);
        var result = UgcPriceIntegrityPolicy.Evaluate(newItem, [],
            Market(ask: 100, completed: null));
        AssertEqual(UgcPriceIntegrityPolicy.Insufficient, result.Classification);
        AssertTrue(result.ReasonCodes.Contains("VERIFIED_CREATOR_HISTORY_MISSING"));

        var catalogOnly = UgcPriceIntegrityPolicy.Evaluate(
            Original(999, ReferenceTime, verified: false), [],
            Market(ask: 50, completed: 0));
        AssertEqual(UgcPriceIntegrityPolicy.Insufficient, catalogOnly.Classification);

        var tooRecent = UgcPriceIntegrityPolicy.Evaluate(
            Original(500, ReferenceTime),
            [Original(65, ReferenceTime.AddMinutes(-1))],
            Market(ask: 100, completed: 0));
        AssertEqual(UgcPriceIntegrityPolicy.Insufficient, tooRecent.Classification);
        return Task.CompletedTask;
    }

    static Task TestUgcPriceIntegrityNoFalsePositiveAsync()
    {
        var veryHighSupply = Original(3000, ReferenceTime, supply: 150000, type: 44);
        var noHistory = UgcPriceIntegrityPolicy.Evaluate(veryHighSupply, [],
            Market(ask: 300, rap: 10, completed: null));
        AssertEqual(UgcPriceIntegrityPolicy.Insufficient, noHistory.Classification);

        var largePriceButNoDiscount = UgcPriceIntegrityPolicy.Evaluate(
            Original(600, ReferenceTime), [Original(80, ReferenceTime.AddDays(-20))],
            Market(ask: 700, completed: 0));
        AssertEqual(UgcPriceIntegrityPolicy.Insufficient, largePriceButNoDiscount.Classification);

        var catalogOnlyPrior = UgcPriceIntegrityPolicy.Evaluate(
            Original(600, ReferenceTime),
            [Original(60, ReferenceTime.AddDays(-20), verified: false)],
            Market(ask: 200, completed: 0));
        AssertEqual(UgcPriceIntegrityPolicy.Insufficient, catalogOnlyPrior.Classification);
        return Task.CompletedTask;
    }

    static Task TestUgcPriceIntegrityComparableDivergenceAsync()
    {
        var earlier = Original(70, ReferenceTime.AddDays(-45));
        var current = Original(700, ReferenceTime);
        var comparable = UgcPriceIntegrityPolicy.Evaluate(current, [earlier],
            Market(ask: 200, completed: null, comparableCount: 4, comparableMedian: 130));
        AssertEqual(UgcPriceIntegrityPolicy.Excluded, comparable.Classification);
        AssertTrue(comparable.ReasonCodes.Contains("COMPARABLE_RESALE_DIVERGENCE"));
        var inadequate = UgcPriceIntegrityPolicy.Evaluate(current, [earlier],
            Market(ask: 200, completed: null, comparableCount: 2, comparableMedian: 130));
        AssertEqual(UgcPriceIntegrityPolicy.Insufficient, inadequate.Classification);
        return Task.CompletedTask;
    }

    static async Task TestUgcIntegrityLedgerPersistenceAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "rpt-price-integrity", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new UgcPriceIntegrityLedger(path);
            await store.InitializeAsync();
            store.Observe(Original(65, ReferenceTime.AddDays(-3)));
            store.Observe(Original(999, ReferenceTime.AddDays(-2), verified: false));
            store.Observe(Original(65, ReferenceTime.AddDays(-2)));
            store.Observe(Original(1080, ReferenceTime));
            await store.SaveIfChangedAsync();
            var restored = new UgcPriceIntegrityLedger(path);
            await restored.InitializeAsync();
            var records = restored.History(1111);
            AssertEqual(2, records.Count); // duplicate steady-state values were coalesced
            AssertEqual(65, records[0].PrimaryPrice);
            AssertEqual(1080, records[1].PrimaryPrice);
            AssertTrue(File.Exists(Path.Combine(path, "ugc-primary-price-evidence.json")));
            restored.Observe(Original(1200, ReferenceTime.AddHours(1)));
            await restored.SaveIfChangedAsync();
            AssertTrue(File.Exists(Path.Combine(path, "ugc-primary-price-evidence.json.bak")));
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    static Task TestUgcHoldingPeriodPreventsFalseExclusionsAsync()
    {
        var first = Original(65, ReferenceTime.AddDays(-5));
        var repriced = Original(1080, ReferenceTime, supply: 8);
        var decision = UgcPriceIntegrityPolicy.Evaluate(repriced, [first],
            Market(ask: 500, completed: 0, dailyMedian: null));
        AssertEqual(UgcPriceIntegrityPolicy.Insufficient, decision.Classification);
        AssertTrue(decision.ReasonCodes.Contains("RESALE_HOLDING_PERIOD_UNMATURED"));

        // Once that *observed* original-stock history has spanned enough time
        // (not just a newly created listing), the same multi-signal evidence
        // can trigger exclusion. No Roblox asset ID is hardcoded.
        var aged = UgcPriceIntegrityPolicy.Evaluate(repriced,
            [first with { ObservedAtUtc = ReferenceTime.AddDays(-45) }],
            Market(ask: 500, completed: 0, volumeDays: 20));
        AssertEqual(UgcPriceIntegrityPolicy.Excluded, aged.Classification);
        return Task.CompletedTask;
    }

    static Task TestUgcShortVolumeWindowNeverProvesNoDemandAsync()
    {
        var earlier = Original(65, ReferenceTime.AddDays(-60));
        var current = Original(1080, ReferenceTime);
        var onlyOneZeroDay = UgcPriceIntegrityPolicy.Evaluate(
            current, [earlier], Market(ask: 500, completed: 0, volumeDays: 1));
        AssertEqual(UgcPriceIntegrityPolicy.Insufficient, onlyOneZeroDay.Classification);
        AssertTrue(!onlyOneZeroDay.ReasonCodes.Contains("INSUFFICIENT_COMPLETED_RESALES"));

        var sustainedWeakDemand = UgcPriceIntegrityPolicy.Evaluate(
            current, [earlier], Market(ask: 500, completed: 0, volumeDays: 21));
        AssertEqual(UgcPriceIntegrityPolicy.Excluded, sustainedWeakDemand.Classification);
        return Task.CompletedTask;
    }

    static Task TestUgcPriceIntegrityInvalidConfigAsync()
    {
        var prior = Original(65, ReferenceTime.AddDays(-45));
        var now = Original(1080, ReferenceTime);
        var bad = new UgcPriceIntegrityPolicy.Configuration { MinCreatorPriceMultiple = 0 };
        AssertTrue(!bad.IsValid);
        var decision = UgcPriceIntegrityPolicy.Evaluate(now, [prior],
            Market(ask: 500, completed: 1, volumeDays: 20), bad);
        AssertEqual(UgcPriceIntegrityPolicy.Excluded, decision.Classification);
        return Task.CompletedTask;
    }
}
