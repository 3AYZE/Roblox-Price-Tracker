using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace RobloxPriceTracker.Gui;

public partial class MainWindow : Window
{
    private DispatcherTimer? _updateTimer;
    private CancellationTokenSource? _updateCts;
    private bool _updateCheckRunning;
    private GitHubReleaseInfo? _stagedRelease;
    private string? _stagedUpdatePath;
    private Version? _deferredUpdateVersion;

    private void InitializeUpdateChecks()
    {
        _updateCts ??= new CancellationTokenSource();
        if (_updateTimer is null)
        {
            _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
            _updateTimer.Tick += async (_, _) =>
            {
                if (_services.Settings.AutoUpdateEnabled)
                {
                    await CheckForUpdatesAsync(userInitiated: false);
                }
            };
        }

        if (_services.Settings.AutoUpdateEnabled)
        {
            _updateTimer.Start();
            UpdateStatusText.Text = $"Automatic update checks enabled · v{CurrentVersionText()}";
            _ = DelayedInitialUpdateCheckAsync(_updateCts.Token);
        }
        else
        {
            _updateTimer.Stop();
            UpdateStatusText.Text = $"Automatic update checks disabled · v{CurrentVersionText()}";
        }
    }

    private void ApplyUpdateSetting()
    {
        if (_updateTimer is null)
        {
            InitializeUpdateChecks();
            return;
        }

        if (_services.Settings.AutoUpdateEnabled)
        {
            _updateTimer.Start();
            UpdateStatusText.Text = $"Automatic update checks enabled · v{CurrentVersionText()}";
            _ = CheckForUpdatesAsync(userInitiated: false);
        }
        else
        {
            _updateTimer.Stop();
            UpdateStatusText.Text = $"Automatic update checks disabled · v{CurrentVersionText()}";
        }
    }

    private void StopUpdateChecks()
    {
        _updateTimer?.Stop();
        _updateCts?.Cancel();
        _updateCts?.Dispose();
        _updateCts = null;
    }

    private async Task DelayedInitialUpdateCheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            if (_services.Settings.AutoUpdateEnabled)
            {
                await CheckForUpdatesAsync(userInitiated: false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesAsync(userInitiated: true);
    }

    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        if (_updateCheckRunning)
        {
            if (userInitiated) ShowBanner("Update check already running", "GitHub is already being checked for a newer release.");
            return;
        }

        _updateCheckRunning = true;
        CheckForUpdatesButton.IsEnabled = false;
        var cancellationToken = _updateCts?.Token ?? CancellationToken.None;
        try
        {
            UpdateStatusText.Text = "Checking GitHub Releases...";
            var result = await _services.UpdateService.CheckAsync(cancellationToken);
            if (!result.UpdateAvailable || result.Release is null)
            {
                UpdateStatusText.Text = result.Message;
                if (userInitiated) ShowBanner("Update check", result.Message);
                return;
            }

            var release = result.Release;
            if (_stagedRelease?.Version == release.Version && !string.IsNullOrWhiteSpace(_stagedUpdatePath) && File.Exists(_stagedUpdatePath))
            {
                UpdateStatusText.Text = $"v{VersionText(release.Version)} is verified and ready to install.";
                if (userInitiated) PromptToInstallStagedUpdate(release, _stagedUpdatePath);
                return;
            }

            UpdateStatusText.Text = $"Downloading v{VersionText(release.Version)}...";
            var stagedPath = await _services.UpdateService.DownloadAndVerifyAsync(release, cancellationToken);
            _stagedRelease = release;
            _stagedUpdatePath = stagedPath;
            UpdateStatusText.Text = $"v{VersionText(release.Version)} verified · restart to install";

            if (!IsVisible)
            {
                ShowTrayBalloon("Roblox Price Tracker update ready", $"v{VersionText(release.Version)} is verified and ready. Open the app to install it.");
                _deferredUpdateVersion = release.Version;
                return;
            }

            if (!userInitiated && _deferredUpdateVersion == release.Version)
            {
                return;
            }

            PromptToInstallStagedUpdate(release, stagedPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _services.Logger.Error($"Automatic update workflow failed: {ex}");
            UpdateStatusText.Text = "Update check/download failed. See the application log.";
            if (userInitiated) ShowBanner("Update failed", ex.Message);
        }
        finally
        {
            _updateCheckRunning = false;
            CheckForUpdatesButton.IsEnabled = true;
        }
    }

    private void PromptToInstallStagedUpdate(GitHubReleaseInfo release, string stagedPath)
    {
        var version = VersionText(release.Version);
        var result = MessageBox.Show(
            this,
            $"Roblox Price Tracker v{version} has been downloaded and SHA-256 verified.\n\nRestart now to install it?\n\nYour watchlist, history, alerts, and settings are stored separately and will be preserved.",
            $"Update v{version} ready",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (result != MessageBoxResult.Yes)
        {
            _deferredUpdateVersion = release.Version;
            UpdateStatusText.Text = $"v{version} ready · install on your next update check";
            return;
        }

        try
        {
            UpdateStatusText.Text = $"Installing v{version}...";
            _services.UpdateService.LaunchInstaller(stagedPath);
            _allowExit = true;
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _services.Logger.Error($"Update installer launch failed: {ex}");
            UpdateStatusText.Text = "The update is downloaded, but automatic replacement failed.";
            MessageBox.Show(this, ex.Message, "Update installation failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string CurrentVersionText() => VersionText(_services.UpdateService.CurrentVersion);

    private static string VersionText(Version version) => $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
}
