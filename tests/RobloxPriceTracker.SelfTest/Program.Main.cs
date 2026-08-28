using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using RobloxPriceTracker.Core;
using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
    public static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("Catalog parser uses lowestPrice", TestCatalogParserAvailableAsync),
            ("NoResellers never becomes zero", TestNoResellersAsync),
            ("Primary price is never used as resale price", TestPrimaryPriceNotFallbackAsync),
            ("Zero lowestPrice is rejected", TestZeroLowestPriceAsync),
            ("Missing batch result is explicit", TestMissingBatchAsync),
            ("First price establishes tracked-low baseline", TestTrackedLowBaselineAsync),
            ("New tracked low fires only when lower", TestTrackedLowAlertAsync),
            ("Target alert deduplicates and rearms", TestTargetDedupAndRearmAsync),
            ("Restart preserves triggered target state", TestRestartPersistenceAsync),
            ("Disable and re-add rearms both rules", TestDisableReAddAsync),
            ("Notification outbox survives until delivered", TestNotificationOutboxAsync),
            ("Older poll sequence cannot overwrite newer state", TestOldSequenceGuardAsync),
            ("Roblox URL parser rejects foreign hosts", TestInputParserAsync),
            ("Rate-limit governor honors Retry-After", TestRateLimitGovernorAsync),
            ("Provider surfaces HTTP Retry-After", TestProviderRetryAfterAsync),
            ("Provider retries anonymous 403 CSRF challenge", TestProviderAnonymousCsrfRetryAsync),
            ("Provider preserves completed chunks on partial failure", TestProviderPartialFailureAsync),
            ("Coordinator resyncs sequence after external commit", TestCoordinatorSequenceResyncAsync),
            ("Notification dispatcher serializes concurrent delivery", TestNotificationDispatcherConcurrencyAsync),
            ("Resale parser reads RAP and daily volume", TestResaleDataParserAsync),
            ("Forecast waits for minimum history", TestForecastRequiresMinimumHistoryAsync),
            ("Forecast follows a clean downtrend", TestForecastFollowsCleanDowntrendAsync),
            ("Liquidity raises forecast confidence", TestLiquidityRaisesForecastConfidenceAsync),
            ("Forecast backtest store evaluates next quote", TestForecastBacktestStoreAsync),
            ("UGC resale break-even applies reseller share", TestUgcResaleBreakEvenAsync),
            ("Fast expensive sellout is not automatic buy", TestUgcFastExpensiveSelloutIsNotAutomaticBuyAsync),
            ("Profitable liquid UGC market ranks high", TestUgcProfitableLiquidMarketRanksHighAsync),
            ("Unprofitable live UGC resale is hard-gated", TestUgcUnprofitableLiveResaleHardGateAsync),
            ("UGC reseller book parser reads depth", TestUgcResellerBookParserAsync),
            ("UGC discovery accepts zero-price search rows", TestUgcDiscoveryAcceptsZeroPriceSearchRowAsync),
            ("UGC hydrated paid Shop Limited becomes candidate", TestUgcHydratedPaidShopLimitedBecomesCandidateAsync),
            ("UGC hydrated zero-price Limited is excluded", TestUgcHydratedZeroPriceLimitedIsExcludedAsync),
            ("UGC unavailable Limited is excluded", TestUgcHydratedUnavailableLimitedIsExcludedAsync),
            ("UGC hydrated experience-only Limited is excluded", TestUgcHydratedExperienceOnlyLimitedIsExcludedAsync),
            ("UGC discovery completes anonymous CSRF hydration", TestUgcDiscoveryRetriesAnonymousCsrfAndHydratesAsync),
            ("UGC discovery combines multiple Limited feeds", TestUgcDiscoveryCombinesMultipleLimitedFeedsAsync),
            ("UGC Hunter hides large primary-price increases", TestUgcHunterLargePrimaryPriceIncreasePolicyAsync),
            ("Marketplace item parser matches White Heart Aura reference", TestMarketplaceItemParserUsesWhiteHeartAuraReferenceAsync),
            ("White Heart Aura reference is unprofitable", TestWhiteHeartAuraReferenceIsUnprofitableAsync),
            ("UGC analyzer parses authoritative catalog detail", TestUgcAnalyzerCatalogParserAsync),
            ("UGC analyzer grades complete market evidence", TestUgcAnalyzerDataQualityAsync),
            ("Creator intelligence summarizes profitable track record", TestCreatorIntelligenceTrackRecordAsync),
            ("Official Limited parser keeps Roblox-published Limiteds", TestOfficialLimitedParserKeepsRobloxLimitedsAsync),
            ("Official Hunt ranks liquid discounts", TestOfficialHuntRanksLiquidDiscountAsync),
            ("Official Hunt rejects illiquid fake discounts", TestOfficialHuntRejectsFakeDeepDiscountWithoutLiquidityAsync),
            ("Official Hunt detects price drops", TestOfficialHuntDetectsPriceDropAsync),
            ("Safe JSON recovers last good backup", TestSafeJsonRecoversBackupAsync),
            ("Legacy JSON migration preserves current data", TestLegacyJsonMigrationPreservesCurrentAsync),
            ("User-data snapshot captures JSON state", TestUserDataSnapshotCapturesJsonAsync)
        };

        var failed = 0;
        foreach (var test in tests)
        {
            try
            {
                await test.Run();
                Console.WriteLine($"PASS  {test.Name}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"FAIL  {test.Name}");
                Console.WriteLine($"      {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed.");
        return failed == 0 ? 0 : 1;
    }
}
