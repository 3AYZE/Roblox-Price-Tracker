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
            ("Notification dispatcher serializes concurrent delivery", TestNotificationDispatcherConcurrencyAsync)
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
