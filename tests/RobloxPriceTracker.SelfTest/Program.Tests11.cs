using System.Text.Json;
using RobloxPriceTracker.Core;
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
    static Task TestLegacyMigrationSkipsProtectedTrackerStateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "rpt-migrate-safe-" + Guid.NewGuid().ToString("N"));
        var legacy = Path.Combine(root, "legacy");
        var modern = Path.Combine(root, "modern");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(modern);
        try
        {
            File.WriteAllText(Path.Combine(legacy, "tracker-state.json"), "old-two-items");
            File.WriteAllText(Path.Combine(modern, "tracker-state.json.bak"), "newer-25-items");
            var migrated = SafeJsonStore.MigrateLegacyJsonFiles(legacy, modern);
            AssertEqual(0, migrated.Count);
            AssertTrue(!File.Exists(Path.Combine(modern, "tracker-state.json")));
            AssertEqual("newer-25-items", File.ReadAllText(Path.Combine(modern, "tracker-state.json.bak")));

            File.Delete(Path.Combine(modern, "tracker-state.json.bak"));
            Directory.CreateDirectory(Path.Combine(modern, "backups", "auto-20260927-073950"));
            File.WriteAllText(
                Path.Combine(modern, "backups", "auto-20260927-073950", "tracker-state.json"),
                "newer-25-items");
            migrated = SafeJsonStore.MigrateLegacyJsonFiles(legacy, modern);
            AssertEqual(0, migrated.Count);
            AssertTrue(!File.Exists(Path.Combine(modern, "tracker-state.json")));
            return Task.CompletedTask;
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    static async Task TestTrackerStartupRecoversMissingItemsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "rpt-rollback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "tracker-state.json");
        var options = TrackerTestJsonOptions();
        try
        {
            var repo = new JsonFileRepository(path);
            await repo.InitializeAsync();
            var now = DateTimeOffset.UtcNow;
            var a = new ItemKey(CatalogItemType.Asset, 7001);
            var b = new ItemKey(CatalogItemType.Asset, 7002);
            var c = new ItemKey(CatalogItemType.Asset, 7003);
            await repo.AddOrUpdateTrackedAssetAsync(Available(a, 5000, now, "First"), 5200);
            await repo.AddOrUpdateTrackedAssetAsync(Available(b, 2000, now, "Second"), 2500);
            await repo.AddOrUpdateTrackedAssetAsync(Available(c, 4000, now, "Missing"), 4000);
            // A later user update must survive when the older full backup is reconciled.
            await repo.AddOrUpdateTrackedAssetAsync(Available(a, 5000, now, "First"), 2200);
            var partial = JsonSerializer.Deserialize<JsonFileRepository.Store>(
                await File.ReadAllTextAsync(path), options)!;
            partial.Items.Remove(c.ToString());
            partial.MarketStates.Remove(c.ToString());
            partial.Rules.RemoveAll(rule => rule.ItemKey == c);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(partial, options));
            // The backup still contains all three items from the prior complete save.
            AssertEqual(3, JsonSerializer.Deserialize<JsonFileRepository.Store>(
                await File.ReadAllTextAsync(path + ".bak"), options)!.Items.Count);

            var restarted = new JsonFileRepository(path);
            await restarted.InitializeAsync();
            var restored = await restarted.LoadEnabledSnapshotsAsync();
            AssertEqual(3, restored.Count);
            AssertEqual<long?>(2200L, TargetRule(restored.Single(x => x.Item.ItemKey == a)).Threshold);
            AssertTrue(restored.Any(x => x.Item.ItemKey == c));
            AssertTrue(Directory.EnumerateFiles(root, "tracker-state.json.rollback-*").Any());
            AssertEqual(3, JsonSerializer.Deserialize<JsonFileRepository.Store>(
                await File.ReadAllTextAsync(path), options)!.Items.Count);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    static async Task TestTrackerInSessionRollbackGuardAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "rpt-liveguard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "tracker-state.json");
        var options = TrackerTestJsonOptions();
        try
        {
            var repo = new JsonFileRepository(path);
            await repo.InitializeAsync();
            var now = DateTimeOffset.UtcNow;
            var a = new ItemKey(CatalogItemType.Asset, 8001);
            var b = new ItemKey(CatalogItemType.Asset, 8002);
            await repo.AddOrUpdateTrackedAssetAsync(Available(a, 5000, now, "First"), 2500);
            await repo.AddOrUpdateTrackedAssetAsync(Available(b, 4000, now, "Second"), 2000);
            var partial = JsonSerializer.Deserialize<JsonFileRepository.Store>(
                await File.ReadAllTextAsync(path), options)!;
            partial.Items.Remove(b.ToString());
            partial.MarketStates.Remove(b.ToString());
            partial.Rules.RemoveAll(rule => rule.ItemKey == b);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(partial, options));

            await repo.UpdateProviderHealthAsync("test", "Healthy", 0, null, null);
            AssertEqual(2, (await repo.LoadEnabledSnapshotsAsync()).Count);
            AssertTrue(Directory.EnumerateFiles(root, "tracker-state.json.rollback-*").Any());
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    static async Task TestTrackerCheckpointRecoveryAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "rpt-checkpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "tracker-state.json");
        var options = TrackerTestJsonOptions();
        try
        {
            var repo = new JsonFileRepository(path);
            await repo.InitializeAsync();
            var now = DateTimeOffset.UtcNow;
            var a = new ItemKey(CatalogItemType.Asset, 9001);
            var b = new ItemKey(CatalogItemType.Asset, 9002);
            await repo.AddOrUpdateTrackedAssetAsync(Available(a, 1000, now, "One"), 800);
            await repo.AddOrUpdateTrackedAssetAsync(Available(b, 1500, now, "Two"), 1200);
            AssertTrue(Directory.EnumerateDirectories(Path.Combine(root, "backups"), "tracked-*").Count() >= 2);

            var partial = JsonSerializer.Deserialize<JsonFileRepository.Store>(
                await File.ReadAllTextAsync(path), options)!;
            partial.Items.Remove(b.ToString());
            partial.MarketStates.Remove(b.ToString());
            partial.Rules.RemoveAll(rule => rule.ItemKey == b);
            var invalid = JsonSerializer.Serialize(partial, options);
            await File.WriteAllTextAsync(path, invalid);
            await File.WriteAllTextAsync(path + ".bak", invalid);

            var restarted = new JsonFileRepository(path);
            await restarted.InitializeAsync();
            AssertEqual(2, (await restarted.LoadEnabledSnapshotsAsync()).Count);
            AssertTrue((await restarted.LoadSnapshotAsync(b)) is not null);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    private static JsonSerializerOptions TrackerTestJsonOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    static Task TestSingleExeReleaseIntegrityAsync()
    {
        AssertTrue(GitHubReleaseIntegrity.TryReadVersion("Roblox Price Tracker v0.13.9", out var version));
        AssertEqual(new Version(0, 13, 9), version);
        AssertTrue(!GitHubReleaseIntegrity.TryReadVersion("Latest", out _));
        AssertTrue(GitHubReleaseIntegrity.TryReadDigest("sha256:" + new string('a', 64), out var hash));
        AssertEqual(new string('a', 64), hash);
        AssertTrue(!GitHubReleaseIntegrity.TryReadDigest("sha256:" + new string('b', 63), out _));
        AssertTrue(!GitHubReleaseIntegrity.TryReadDigest("md5:" + new string('b', 64), out _));
        var current = new Version(0, 13, 9, 0);
        AssertTrue(GitHubReleaseIntegrity.NeedsDownload(current, new Version(0, 13, 10), hash, hash));
        AssertTrue(!GitHubReleaseIntegrity.NeedsDownload(current, new Version(0, 13, 8), hash, hash));
        AssertTrue(!GitHubReleaseIntegrity.NeedsDownload(current, current, hash, hash));
        AssertTrue(GitHubReleaseIntegrity.NeedsDownload(current, current, new string('b', 64), hash));
        AssertTrue(!GitHubReleaseIntegrity.NeedsDownload(current, current, null, hash));
        return Task.CompletedTask;
    }

}
