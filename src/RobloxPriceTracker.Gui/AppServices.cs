using System.IO;
using System.Net.Http;
using RobloxPriceTracker.Core;
using RobloxPriceTracker.Infrastructure;

namespace RobloxPriceTracker.Gui;

public sealed class AppServices : IDisposable
{
    private AppServices(
        string dataDirectory,
        JsonFileRepository repository,
        HttpClient httpClient,
        RobloxCatalogProvider provider,
        AlertEngine alertEngine,
        RateLimitGovernor rateGovernor,
        TrackerCoordinator coordinator,
        GuiNotificationSink notificationSink,
        NotificationDispatcher notificationDispatcher,
        AppLogger logger,
        AppSettingsStore settingsStore,
        AppSettings settings,
        RobloxThumbnailService thumbnailService,
        GitHubUpdateService updateService,
        RobloxResaleDataService resaleDataService,
        PriceForecastEngine forecastEngine,
        ForecastHistoryStore forecastHistoryStore,
        UgcHunterService ugcHunterService,
        PaperPortfolioStore paperPortfolioStore)
    {
        DataDirectory = dataDirectory;
        Repository = repository;
        HttpClient = httpClient;
        Provider = provider;
        AlertEngine = alertEngine;
        RateGovernor = rateGovernor;
        Coordinator = coordinator;
        NotificationSink = notificationSink;
        NotificationDispatcher = notificationDispatcher;
        Logger = logger;
        SettingsStore = settingsStore;
        Settings = settings;
        ThumbnailService = thumbnailService;
        UpdateService = updateService;
        ResaleDataService = resaleDataService;
        ForecastEngine = forecastEngine;
        ForecastHistoryStore = forecastHistoryStore;
        UgcHunterService = ugcHunterService;
        PaperPortfolioStore = paperPortfolioStore;
    }

    public string DataDirectory { get; }
    public JsonFileRepository Repository { get; }
    public HttpClient HttpClient { get; }
    public RobloxCatalogProvider Provider { get; }
    public AlertEngine AlertEngine { get; }
    public RateLimitGovernor RateGovernor { get; }
    public TrackerCoordinator Coordinator { get; }
    public GuiNotificationSink NotificationSink { get; }
    public NotificationDispatcher NotificationDispatcher { get; }
    public AppLogger Logger { get; }
    public AppSettingsStore SettingsStore { get; }
    public AppSettings Settings { get; }
    public RobloxThumbnailService ThumbnailService { get; }
    public GitHubUpdateService UpdateService { get; }
    public RobloxResaleDataService ResaleDataService { get; }
    public PriceForecastEngine ForecastEngine { get; }
    public ForecastHistoryStore ForecastHistoryStore { get; }
    public UgcHunterService UgcHunterService { get; }
    public PaperPortfolioStore PaperPortfolioStore { get; }

    public PollPlanner CreatePollPlanner() => new(
        TimeSpan.FromSeconds(Math.Max(10, Settings.NormalPollSeconds)),
        TimeSpan.FromSeconds(Math.Max(10, Settings.NearTargetPollSeconds)));

    public static async Task<AppServices> CreateAsync(CancellationToken cancellationToken = default)
    {
        var dataDir = Environment.GetEnvironmentVariable("RPT_DATA_DIR");
        if (string.IsNullOrWhiteSpace(dataDir))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            dataDir = Path.Combine(localAppData, "RobloxPriceTracker");
            var legacyDir = Path.Combine(localAppData, "RobloxPriceTrackerPrototype");

            Directory.CreateDirectory(dataDir);
            if (Directory.Exists(legacyDir))
            {
                foreach (var fileName in new[] { "tracker-state.json", "app-settings.json" })
                {
                    var oldPath = Path.Combine(legacyDir, fileName);
                    var newPath = Path.Combine(dataDir, fileName);
                    if (File.Exists(oldPath) && !File.Exists(newPath))
                    {
                        try { File.Copy(oldPath, newPath, overwrite: false); }
                        catch { }
                    }
                }
            }
        }

        Directory.CreateDirectory(dataDir);
        var repository = new JsonFileRepository(Path.Combine(dataDir, "tracker-state.json"));
        await repository.InitializeAsync(cancellationToken);

        var logger = new AppLogger(Path.Combine(dataDir, "logs", "app.log"));
        var settingsStore = new AppSettingsStore(Path.Combine(dataDir, "app-settings.json"));
        var settings = await settingsStore.LoadAsync(cancellationToken);

        var httpClient = RobloxCatalogProvider.CreateDefaultHttpClient();
        var batchSize = int.TryParse(Environment.GetEnvironmentVariable("RPT_BATCH_SIZE"), out var parsedBatch) ? parsedBatch : 40;
        var provider = new RobloxCatalogProvider(httpClient, new RobloxProviderOptions(batchSize));
        var alertEngine = new AlertEngine();
        var governor = new RateLimitGovernor();

        var persistedBackoff = await repository.GetProviderBackoffUntilAsync(provider.Name, cancellationToken);
        if (persistedBackoff is { } backoffUntil && backoffUntil > DateTimeOffset.UtcNow)
        {
            governor.ApplyMinimumDelay(backoffUntil - DateTimeOffset.UtcNow);
        }

        var sequence = await repository.GetMaxPollSequenceAsync(cancellationToken);
        var coordinator = new TrackerCoordinator(repository, provider, alertEngine, governor, sequence, logger);
        var notificationSink = new GuiNotificationSink();
        var dispatcher = new NotificationDispatcher(repository, notificationSink);
        var thumbnailService = new RobloxThumbnailService(httpClient);
        var updateService = new GitHubUpdateService(dataDir, logger);
        var resaleDataService = new RobloxResaleDataService(httpClient, logger);
        var forecastEngine = new PriceForecastEngine();
        var forecastHistoryStore = new ForecastHistoryStore(Path.Combine(dataDir, "forecast-history.json"));
        await forecastHistoryStore.InitializeAsync(cancellationToken);
        var ugcHunterService = new UgcHunterService(httpClient, thumbnailService, logger, dataDir);
        await ugcHunterService.InitializeAsync(cancellationToken);
        var paperPortfolioStore = new PaperPortfolioStore(dataDir);
        await paperPortfolioStore.InitializeAsync(cancellationToken);

        return new AppServices(
            dataDir,
            repository,
            httpClient,
            provider,
            alertEngine,
            governor,
            coordinator,
            notificationSink,
            dispatcher,
            logger,
            settingsStore,
            settings,
            thumbnailService,
            updateService,
            resaleDataService,
            forecastEngine,
            forecastHistoryStore,
            ugcHunterService,
            paperPortfolioStore);
    }

    public void Dispose()
    {
        UpdateService.Dispose();
        HttpClient.Dispose();
    }
}

public sealed class GuiNotificationEventArgs : EventArgs
{
    public GuiNotificationEventArgs(string title, string body)
    {
        Title = title;
        Body = body;
    }

    public string Title { get; }
    public string Body { get; }
}

public sealed class GuiNotificationSink : INotificationSink
{
    public event EventHandler<GuiNotificationEventArgs>? NotificationRaised;

    public Task SendAsync(string title, string body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NotificationRaised?.Invoke(this, new GuiNotificationEventArgs(title, body));
        return Task.CompletedTask;
    }
}
