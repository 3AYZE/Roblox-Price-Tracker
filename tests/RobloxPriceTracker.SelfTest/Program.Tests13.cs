using System.Text.Json;
using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
    static async Task TestScanDiagnosticsOutcomesAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "rpt-phase1-metrics", Guid.NewGuid().ToString("N"));
        var metrics = new ScanDiagnostics(root);
        try
        {
            var result = await metrics.MeasureAsync("ugc-discovery-verify",
                async token =>
                {
                    await Task.Delay(15, token);
                    return 42;
                });
            AssertEqual(42, result);

            var failed = false;
            try
            {
                await metrics.MeasureAsync<int>("ugc-resale-analysis",
                    _ => throw new InvalidOperationException("synthetic test failure"));
            }
            catch (InvalidOperationException) { failed = true; }
            AssertTrue(failed);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var cancelled = false;
            try
            {
                await metrics.MeasureAsync("official-full",
                    token => Task.FromResult(1), cancellation.Token);
            }
            catch (OperationCanceledException) { cancelled = true; }
            AssertTrue(cancelled);

            var lines = File.ReadAllLines(metrics.LogPath);
            AssertEqual(3, lines.Length);
            var records = lines
                .Select(line => JsonSerializer.Deserialize<ScanDiagnostics.ScanMeasurement>(line)!)
                .ToArray();
            AssertEqual("success", records[0].Outcome);
            AssertEqual("failed", records[1].Outcome);
            AssertEqual("cancelled", records[2].Outcome);
            AssertTrue(records.All(item => item.DurationMs >= 0));
            AssertTrue(records[0].DurationMs >= 5);
            AssertEqual("ugc-discovery-verify", records[0].Stage);
            AssertEqual("official-full", records[2].Stage);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    static Task TestCrashDiagnosticsSessionAndRedactionAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "rpt-phase1-crashes", Guid.NewGuid().ToString("N"));
        var diagnostics = new CrashDiagnostics(root);
        try
        {
            AssertTrue(!diagnostics.BeginSession());
            diagnostics.Record("self-test", new InvalidOperationException(
                "sample .ROBLOSECURITY:supersecret123 is not safe to share"), fatal: true);
            var log = File.ReadAllText(diagnostics.LogPath);
            AssertTrue(log.Contains("[FATAL]", StringComparison.Ordinal));
            AssertTrue(log.Contains("[REDACTED]", StringComparison.Ordinal));
            AssertTrue(!log.Contains("supersecret123", StringComparison.Ordinal));

            diagnostics.EndSession();
            var restart = new CrashDiagnostics(root);
            AssertTrue(!restart.BeginSession());
            restart.EndSession();
            AssertTrue(!File.Exists(Path.Combine(root, "logs", "session.active")));
            return Task.CompletedTask;
        }
        finally
        {
            diagnostics.EndSession();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    static Task TestCrashDiagnosticsAbnormalTerminationAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "rpt-phase1-unclean", Guid.NewGuid().ToString("N"));
        var first = new CrashDiagnostics(root);
        var restarted = new CrashDiagnostics(root);
        try
        {
            AssertTrue(!first.BeginSession());
            AssertTrue(restarted.BeginSession());
            AssertTrue(File.ReadAllText(restarted.LogPath).Contains("[UNCLEAN_EXIT]", StringComparison.Ordinal));
            restarted.EndSession();
            AssertTrue(!File.Exists(Path.Combine(root, "logs", "session.active")));
            return Task.CompletedTask;
        }
        finally
        {
            first.EndSession();
            restarted.EndSession();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
