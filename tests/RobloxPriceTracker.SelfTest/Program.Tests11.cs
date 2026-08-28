using System.Text.Json;
using RobloxPriceTracker.Infrastructure;

internal static partial class Program
{
    static async Task TestSafeJsonRecoversBackupAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rpt-safe-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "state.json");
            var options = new JsonSerializerOptions { WriteIndented = true };
            await SafeJsonStore.SaveWithBackupAsync(path, new SafeJsonFixture("first", 1), options);
            await SafeJsonStore.SaveWithBackupAsync(path, new SafeJsonFixture("second", 2), options);
            await File.WriteAllTextAsync(path, "{not valid json");

            var recovered = await SafeJsonStore.LoadWithRecoveryAsync<SafeJsonFixture>(path, options);
            AssertTrue(recovered is not null);
            AssertEqual("first", recovered!.Name);
            AssertEqual(1, recovered.Value);
            AssertTrue(Directory.EnumerateFiles(directory, "state.json.corrupt-*").Any());
            AssertTrue(File.Exists(path));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    static Task TestLegacyJsonMigrationPreservesCurrentAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "rpt-migrate-" + Guid.NewGuid().ToString("N"));
        var legacy = Path.Combine(root, "legacy");
        var current = Path.Combine(root, "current");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(current);
        try
        {
            File.WriteAllText(Path.Combine(legacy, "app-settings.json"), "legacy-settings");
            File.WriteAllText(Path.Combine(legacy, "paper-portfolio.json"), "legacy-portfolio");
            File.WriteAllText(Path.Combine(current, "app-settings.json"), "current-settings");

            var migrated = SafeJsonStore.MigrateLegacyJsonFiles(legacy, current);
            AssertEqual("current-settings", File.ReadAllText(Path.Combine(current, "app-settings.json")));
            AssertEqual("legacy-portfolio", File.ReadAllText(Path.Combine(current, "paper-portfolio.json")));
            AssertEqual(1, migrated.Count);
            return Task.CompletedTask;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    static async Task TestUserDataSnapshotCapturesJsonAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rpt-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "tracker-state.json"), "{}");
            await File.WriteAllTextAsync(Path.Combine(directory, "paper-portfolio.json"), "[]");
            await File.WriteAllTextAsync(Path.Combine(directory, "ignored.tmp"), "temp");

            var snapshot = await SafeJsonStore.CreateManualSnapshotAsync(directory);
            AssertTrue(!string.IsNullOrWhiteSpace(snapshot));
            AssertTrue(File.Exists(Path.Combine(snapshot, "tracker-state.json")));
            AssertTrue(File.Exists(Path.Combine(snapshot, "paper-portfolio.json")));
            AssertTrue(!File.Exists(Path.Combine(snapshot, "ignored.tmp")));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private sealed record SafeJsonFixture(string Name, int Value);
}
