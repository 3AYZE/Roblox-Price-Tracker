using RobloxPriceTracker.Core;
using RobloxPriceTracker.Infrastructure;

var exitCode = await ProgramMainAsync(args);
return exitCode;

static async Task<int> ProgramMainAsync(string[] args)
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
    {
        PrintHelp();
        return 0;
    }

    var dataDir = Environment.GetEnvironmentVariable("RPT_DATA_DIR");
    if (string.IsNullOrWhiteSpace(dataDir))
    {
        dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RobloxPriceTracker");
    }

    Directory.CreateDirectory(dataDir);
    var statePath = Path.Combine(dataDir, "tracker-state.json");
    var logPath = Path.Combine(dataDir, "logs", "app.log");
    var logger = new AppLogger(logPath);
    var repository = new JsonFileRepository(statePath);
    await repository.InitializeAsync();

    using var httpClient = RobloxCatalogProvider.CreateDefaultHttpClient();
    var batchSize = int.TryParse(Environment.GetEnvironmentVariable("RPT_BATCH_SIZE"), out var parsedBatch) ? parsedBatch : 40;
    var provider = new RobloxCatalogProvider(httpClient, new RobloxProviderOptions(batchSize));
    var engine = new AlertEngine();
    var governor = new RateLimitGovernor();
    var persistedBackoff = await repository.GetProviderBackoffUntilAsync(provider.Name);
    if (persistedBackoff is { } backoffUntil && backoffUntil > DateTimeOffset.UtcNow)
    {
        governor.ApplyMinimumDelay(backoffUntil - DateTimeOffset.UtcNow);
    }
    var dispatcher = new NotificationDispatcher(repository, new ConsoleNotificationSink());

    try
    {
        switch (args[0].ToLowerInvariant())
        {
            case "add":
                return await AddAsync(args, repository, provider, engine, dispatcher, logger);
            case "list":
                return await ListAsync(repository);
            case "check":
                return await CheckAsync(repository, provider, engine, governor, dispatcher, logger);
            case "watch":
                return await WatchAsync(repository, provider, engine, governor, dispatcher, logger);
            case "remove":
                return await RemoveAsync(args, repository);
            case "paths":
                Console.WriteLine($"Data directory: {dataDir}");
                Console.WriteLine($"State file:     {statePath}");
                Console.WriteLine($"Log:            {logPath}");
                return 0;
            default:
                Console.Error.WriteLine($"Unknown command: {args[0]}");
                PrintHelp();
                return 2;
        }
    }
    catch (OperationCanceledException)
    {
        return 0;
    }
    catch (Exception ex)
    {
        logger.Error(ex.ToString());
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        return 1;
    }
}

static async Task<int> AddAsync(
    string[] args,
    JsonFileRepository repository,
    IMarketProvider provider,
    AlertEngine engine,
    NotificationDispatcher dispatcher,
    AppLogger logger)
{
    if (args.Length != 3)
    {
        Console.Error.WriteLine("Usage: add <asset-id-or-catalog-url> <target-robux>");
        return 2;
    }

    if (!ItemInputParser.TryParseAsset(args[1], out var key, out var error))
    {
        Console.Error.WriteLine(error);
        return 2;
    }

    if (!long.TryParse(args[2].Replace(",", string.Empty, StringComparison.Ordinal), out var target))
    {
        Console.Error.WriteLine("Target must be a whole number of Robux.");
        return 2;
    }
    PriceValidation.ThrowIfInvalidTarget(target);

    var fetch = await provider.FetchAsync(new[] { key }, CancellationToken.None);
    if (fetch.Failure is not null)
    {
        Console.Error.WriteLine($"Roblox lookup failed: {fetch.Failure.Message}");
        return 1;
    }

    if (!fetch.Observations.TryGetValue(key, out var observation) || observation.Status == MarketStatus.MissingFromResponse)
    {
        Console.Error.WriteLine("Roblox did not return this asset. It may be invalid, deleted, moderated, or temporarily unavailable.");
        return 1;
    }

    if (!observation.IsResaleCapable || observation.Status == MarketStatus.Unsupported)
    {
        Console.Error.WriteLine("This asset is not confirmed as a resellable Limited/collectible. V1 intentionally refuses ambiguous non-resale items.");
        return 1;
    }

    await repository.AddOrUpdateTrackedAssetAsync(observation, target);

    var snapshot = await repository.LoadSnapshotAsync(key) ?? throw new InvalidOperationException("Newly added item could not be reloaded.");
    var sequence = await repository.GetMaxPollSequenceAsync() + 1;
    var decision = engine.Evaluate(snapshot, observation, sequence, TimeProvider.System.GetUtcNow());
    var committed = await repository.CommitDecisionAsync(decision, sequence);
    if (!committed)
    {
        throw new InvalidOperationException("The item changed concurrently while it was being added. Run the add command again.");
    }
    await dispatcher.DispatchPendingAsync();

    logger.Info($"Added/updated {snapshot.Item.ItemKey} ({observation.Name ?? snapshot.Item.Name}) with target {target:N0} R$.");
    Console.WriteLine($"Watching:      {observation.Name ?? snapshot.Item.Name}");
    Console.WriteLine($"Asset ID:      {key.Id}");
    Console.WriteLine($"Target:        {target:N0} R$");
    Console.WriteLine($"Market status: {observation.Status}");
    Console.WriteLine($"Current lowest:{(observation.LowestResalePrice is null ? " —" : $" {observation.LowestResalePrice:N0} R$")}");
    return 0;
}

static async Task<int> ListAsync(JsonFileRepository repository)
{
    var snapshots = await repository.LoadEnabledSnapshotsAsync();
    if (snapshots.Count == 0)
    {
        Console.WriteLine("No watched items.");
        return 0;
    }

    Console.WriteLine("WATCHLIST");
    Console.WriteLine(new string('-', 98));
    Console.WriteLine($"{"ID",-16} {"NAME",-32} {"CURRENT",12} {"TARGET",12} {"TRACKED LOW",12} {"STATUS",-12}");
    Console.WriteLine(new string('-', 98));

    foreach (var snapshot in snapshots)
    {
        var target = snapshot.Rules.FirstOrDefault(x => x.RuleType == AlertRuleType.TargetPrice)?.Threshold;
        Console.WriteLine(
            $"{snapshot.Item.ItemKey.Id,-16} {Truncate(snapshot.Item.Name, 32),-32} " +
            $"{FormatPrice(snapshot.Market.CurrentLowestPrice),12} {FormatPrice(target),12} {FormatPrice(snapshot.Market.TrackedLow),12} {snapshot.Market.ObservedStatus,-12}");
    }
    return 0;
}

static async Task<int> CheckAsync(
    JsonFileRepository repository,
    IMarketProvider provider,
    AlertEngine engine,
    RateLimitGovernor governor,
    NotificationDispatcher dispatcher,
    AppLogger logger)
{
    var maxSequence = await repository.GetMaxPollSequenceAsync();
    var coordinator = new TrackerCoordinator(repository, provider, engine, governor, maxSequence, logger);
    var summary = await coordinator.CheckOnceAsync(CancellationToken.None);
    await dispatcher.DispatchPendingAsync();
    PrintSummary(summary, governor);
    return summary.Failure?.Kind == ProviderFailureKind.Protocol ? 1 : 0;
}

static async Task<int> WatchAsync(
    JsonFileRepository repository,
    IMarketProvider provider,
    AlertEngine engine,
    RateLimitGovernor governor,
    NotificationDispatcher dispatcher,
    AppLogger logger)
{
    var maxSequence = await repository.GetMaxPollSequenceAsync();
    var coordinator = new TrackerCoordinator(repository, provider, engine, governor, maxSequence, logger);
    var planner = new PollPlanner();
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    Console.WriteLine("Monitoring started. Press Ctrl+C to stop.");
    while (!cts.IsCancellationRequested)
    {
        var summary = await coordinator.CheckOnceAsync(cts.Token);
        await dispatcher.DispatchPendingAsync(cts.Token);
        PrintSummary(summary, governor);

        var snapshots = await repository.LoadEnabledSnapshotsAsync(cts.Token);
        var delay = planner.GetNextDelay(snapshots, governor);
        Console.WriteLine($"Next check in approximately {delay.TotalSeconds:N0}s.");
        await Task.Delay(delay, cts.Token);
    }
    return 0;
}

static async Task<int> RemoveAsync(string[] args, JsonFileRepository repository)
{
    if (args.Length != 2)
    {
        Console.Error.WriteLine("Usage: remove <asset-id-or-catalog-url>");
        return 2;
    }

    if (!ItemInputParser.TryParseAsset(args[1], out var key, out var error))
    {
        Console.Error.WriteLine(error ?? "Invalid Roblox asset ID or catalog URL.");
        return 2;
    }

    await repository.DisableItemAsync(key);
    Console.WriteLine($"Stopped watching {key}.");
    return 0;
}

static void PrintSummary(CheckSummary summary, RateLimitGovernor governor)
{
    if (summary.SkippedForBackoff)
    {
        Console.WriteLine($"Check skipped: provider backoff active for ~{governor.RemainingDelay.TotalSeconds:N0}s.");
        return;
    }

    Console.WriteLine($"Poll {summary.PollSequence}: requested={summary.RequestedItems}, processed={summary.ProcessedItems}, missing={summary.MissingItems}, alerts={summary.AlertsCreated}");
    if (summary.Failure is not null)
    {
        Console.WriteLine($"Provider state: {summary.Failure.Kind} - {summary.Failure.Message}");
    }
}

static string FormatPrice(long? price) => price is null ? "—" : $"{price:N0}";
static string Truncate(string value, int length) => value.Length <= length ? value : value[..(length - 1)] + "…";

static void PrintHelp()
{
    Console.WriteLine("""
        Roblox Price Tracker - backend prototype

        Commands:
          add <asset-id-or-url> <target>   Add/update a resellable asset and target price.
          list                             Show current watchlist state.
          check                            Perform one batched Roblox market check.
          watch                            Monitor continuously until Ctrl+C.
          remove <asset-id-or-url>         Disable an item and its rules.
          paths                            Show database/log locations.

        Environment:
          RPT_DATA_DIR                     Override data directory.
          RPT_BATCH_SIZE                   Roblox request batch size (default 40, valid 1-100).

        V1 safety scope:
          - Read-only price monitoring.
          - No Roblox login, cookie, or .ROBLOSECURITY.
          - No purchasing/auto-buying.
          - Only confirmed resellable Limited/collectible assets.
        """);
}
