using System.IO;
using System.Text.Json;

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
}

public sealed class AppSettingsStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public AppSettingsStore(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path))
            {
                return new AppSettings();
            }

            try
            {
                await using var stream = File.OpenRead(_path);
                return await JsonSerializer.DeserializeAsync<AppSettings>(stream, Options, cancellationToken) ?? new AppSettings();
            }
            catch
            {
                return new AppSettings();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var temp = _path + ".tmp";
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, settings, Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
