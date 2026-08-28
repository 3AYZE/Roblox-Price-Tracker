using System.IO;
using System.Text.Json;
using RobloxPriceTracker.Infrastructure;

namespace RobloxPriceTracker.Gui;

public sealed class AppSettings
{
    public int NormalPollSeconds { get; set; } = 60;
    public int NearTargetPollSeconds { get; set; } = 25;
    public bool StartMonitoringOnLaunch { get; set; } = true;
    public bool PlayAlertSound { get; set; } = true;
    public bool ConfirmBeforeRemove { get; set; } = true;
    public bool ShowTrayNotifications { get; set; } = true;
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
    public bool StartMinimizedToTray { get; set; } = true;
    public int StartupDelaySeconds { get; set; } = 15;
    public bool AutoUpdateEnabled { get; set; } = true;
}

public sealed class AppSettingsStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public AppSettingsStore(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SafeJsonStore.LoadWithRecoveryAsync<AppSettings>(_path, Options, cancellationToken: cancellationToken).ConfigureAwait(false)
                   ?? new AppSettings();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SafeJsonStore.SaveWithBackupAsync(_path, settings, Options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
