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
            ("Meaningful new low alerts without a target", TestTrackedLowAlertAsync),
            ("Far target suppresses new-low notification noise", TestFarTargetSuppressesNewLowNotificationAsync),
            ("Falling price near target sends approaching alert", TestApproachingTargetAlertAsync),
            ("Tiny near-target price move stays quiet", TestApproachingTargetTinyDropSuppressedAsync),
            ("Approaching-target alert respects cooldown", TestApproachingTargetCooldownAsync),
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
            ("UGC discovery falls back from protocol route errors", TestUgcDiscoveryFallsBackFromProtocolErrorAsync),
            ("UGC discovery keeps valid rows from a partially bad detail batch", TestUgcDiscoveryRecoversPartialBadDetailBatchAsync),
            ("UGC Hunter accepts positive category-specific original prices", TestUgcHunterMinimumPrimaryPricePolicyAsync),
            ("UGC Limited emotes are supported and hydrated", TestUgcLimitedEmoteHydrationAsync),
            ("UGC observed original lows are separate per asset type", TestUgcObservedOriginalCategoryLowsAsync),
            ("UGC observed lows reject resale-only and catalog-only data", TestUgcObservedPrimaryRejectsResaleAndUnverifiedAsync),
            ("Emissive and dance name hints do not imply verified minimums", TestUgcAppearanceHintsAsync),
            ("Verified materials use separate category price baselines", TestUgcMaterialClassSeparationAsync),
            ("Catalog emissive metadata is distinct from name hints", TestUgcVerifiedEmissiveMetadataAsync),
            ("Animation bundle discovery ignores emotes, bodies and non-Limiteds", TestLimitedAnimationBundleDiscoveryParserAsync),
            ("Bundle catalog listing alone is never verified original stock", TestLimitedBundleCatalogIsNotOriginalProofAsync),
            ("Animation bundle original-stock prices require Marketplace verification", TestBundleMarketplacePrimaryVerificationAsync),
            ("Animation packs hydrate sparse Limited search metadata", TestAnimationBundleSparseSearchNeedsDetailVerificationAsync),
            ("Animation packs reject invalid non-Limited detail responses", TestAnimationBundleDetailRejectionDoesNotFallbackAsync),
            ("Animation pack search pagination deduplicates and stays bounded", TestAnimationBundlePagedDiscoveryDeduplicatesAsync),
            ("Animation packs with unconfirmed Limited status do not enter live scan", TestAnimationBundleUnconfirmedNeverDisplayedAsync),
            ("Sparse bundle collectibles require matching verified Marketplace ID", TestAnimationBundleMarketplaceRecoversSparseCollectibleAsync),
            ("Animation bundle HTTP 429 keeps cached results and skips further calls", TestAnimationBundleRateLimitPreservesCacheContractAsync),
            ("Official and UGC tabs never mix assets, bundles or unknown sources", TestOfficialAndUgcSourcesAreSeparateAsync),
            ("Official and UGC tabs refresh only their own discovery sources", TestLimitedSourceSpecificRefreshAsync),
            ("Thumbnail parser keeps only completed requested Roblox PNG images", TestThumbnailCompletedAndPendingStatesAsync),
            ("Thumbnail parser rejects untrusted or insecure image URLs", TestThumbnailRejectsUntrustedSourcesAsync),
            ("Thumbnail parser handles empty and incomplete responses safely", TestThumbnailMalformedOrMissingDataAsync),
            ("UGC scanner excludes extreme prices by default, but supports research override", TestUgcScanPriceVisibilityAsync),
            ("UGC scanner never labels catalog quotes as verified original costs", TestUgcPriceQuoteProvenanceAsync),
            ("Creator-price 65 to 1080 with weak resale evidence is excluded", TestUgcPriceIntegrityVerifiedInflationAsync),
            ("Other verified creator repricing is excluded without item-ID rules", TestUgcPriceIntegrityAcrossCategoriesAsync),
            ("Legitimate valuable resale market overrides creator repricing", TestUgcPriceIntegrityIndependentDemandProtectionAsync),
            ("Historical corrections and price stability avoid exclusion", TestUgcPriceIntegrityCorrectionsAsync),
            ("Unknown resale counts are never treated as zero", TestUgcPriceIntegrityUnknownVolumeAsync),
            ("New Limiteds and catalog-only prices remain unverified", TestUgcPriceIntegrityMissingHistoryAsync),
            ("High supply and resale asks alone never cause hard exclusion", TestUgcPriceIntegrityNoFalsePositiveAsync),
            ("Three independent comparable resale observations can corroborate repricing", TestUgcPriceIntegrityComparableDivergenceAsync),
            ("Price-integrity evidence ledger persists only verified primary observations", TestUgcIntegrityLedgerPersistenceAsync),
            ("Config rejects unsafe threshold values", TestUgcPriceIntegrityInvalidConfigAsync),
            ("UGC resale holding period prevents premature distortion exclusions", TestUgcHoldingPeriodPreventsFalseExclusionsAsync),
            ("Insufficient secondary-volume day coverage prevents false hard exclusions", TestUgcShortVolumeWindowNeverProvesNoDemandAsync),
            ("Concurrent price-integrity history reads and backups remain safe", TestUgcLedgerConcurrentReadersWritersAsync),
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
            ("Legacy migration cannot replace newer tracker backup", TestLegacyMigrationSkipsProtectedTrackerStateAsync),
            ("Startup recovers missing items without losing current targets", TestTrackerStartupRecoversMissingItemsAsync),
            ("Live rollback is repaired before any partial save", TestTrackerInSessionRollbackGuardAsync),
            ("Startup restores from immutable checkpoint after both rolling files regress", TestTrackerCheckpointRecoveryAsync),
            ("User-data snapshot captures JSON state", TestUserDataSnapshotCapturesJsonAsync),
            ("Permanent EXE version and SHA-256 verification", TestSingleExeReleaseIntegrityAsync),
            ("Scan diagnostics records success, failure and cancellation", TestScanDiagnosticsOutcomesAsync),
            ("Crash diagnostics redacts secrets and closes clean sessions", TestCrashDiagnosticsSessionAndRedactionAsync),
            ("Crash diagnostics flags abnormal previous session", TestCrashDiagnosticsAbnormalTerminationAsync)
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
