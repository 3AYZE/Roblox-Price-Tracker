using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
    private string? _deferredUpdateAssetUrl;

    private void InitializeUpdateChecks()
    {
        EnsureUpdateChannelUi();
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
            UpdateStatusText.Text = $"Automatic {UpdateChannelLabel()} update checks enabled · v{CurrentVersionText()}";
            _ = DelayedInitialUpdateCheckAsync(_updateCts.Token);
        }
        else
        {
            _updateTimer.Stop();
            UpdateStatusText.Text = $"Automatic update checks disabled · {UpdateChannelLabel()} channel · v{CurrentVersionText()}";
        }
    }

    private void ApplyUpdateSetting()
    {
        EnsureUpdateChannelUi();
        if (_updateTimer is null)
        {
            InitializeUpdateChecks();
            return;
        }

        if (_services.Settings.AutoUpdateEnabled)
        {
            _updateTimer.Start();
            UpdateStatusText.Text = $"Automatic {UpdateChannelLabel()} update checks enabled · v{CurrentVersionText()}";
            _ = CheckForUpdatesAsync(userInitiated: false);
        }
        else
        {
            _updateTimer.Stop();
            UpdateStatusText.Text = $"Automatic update checks disabled · {UpdateChannelLabel()} channel · v{CurrentVersionText()}";
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
            if (userInitiated) ShowBanner("Update check already running", "GitHub is already being checked for a newer build.");
            return;
        }

        _updateCheckRunning = true;
        CheckForUpdatesButton.IsEnabled = false;
        var cancellationToken = _updateCts?.Token ?? CancellationToken.None;
        try
        {
            UpdateStatusText.Text = IsLatestUpdateChannel()
                ? "Checking newest successful main build..."
                : "Checking GitHub Releases...";

            var result = IsLatestUpdateChannel()
                ? await _services.UpdateService.CheckMainLatestAsync(cancellationToken)
                : await _services.UpdateService.CheckAsync(cancellationToken);

            if (!result.UpdateAvailable || result.Release is null)
            {
                UpdateStatusText.Text = result.Message;
                if (userInitiated) ShowBanner("Update check", result.Message);
                return;
            }

            var release = result.Release;
            var assetIdentity = release.Executable.ApiUrl.AbsoluteUri;
            if (_stagedRelease?.Executable.ApiUrl == release.Executable.ApiUrl &&
                !string.IsNullOrWhiteSpace(_stagedUpdatePath) &&
                File.Exists(_stagedUpdatePath))
            {
                UpdateStatusText.Text = $"{UpdateDisplayName(release)} is verified and ready to install.";
                if (userInitiated) PromptToInstallStagedUpdate(release, _stagedUpdatePath);
                return;
            }

            UpdateStatusText.Text = $"Downloading {UpdateDisplayName(release)}...";
            var stagedPath = await _services.UpdateService.DownloadAndVerifyAsync(release, cancellationToken);
            _stagedRelease = release;
            _stagedUpdatePath = stagedPath;
            UpdateStatusText.Text = $"{UpdateDisplayName(release)} verified · restart to install";

            if (!IsVisible)
            {
                ShowTrayBalloon("Roblox Market Helper update ready", $"{UpdateDisplayName(release)} is verified and ready. Open the app to install it.");
                _deferredUpdateAssetUrl = assetIdentity;
                return;
            }

            if (!userInitiated && string.Equals(_deferredUpdateAssetUrl, assetIdentity, StringComparison.Ordinal))
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
        var displayName = UpdateDisplayName(release);
        var result = MessageBox.Show(
            this,
            $"{displayName} has been downloaded and SHA-256 verified.\n\nRestart now to install it?\n\nYour watchlist, history, alerts, and settings are stored separately and will be preserved.",
            $"{displayName} ready",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (result != MessageBoxResult.Yes)
        {
            _deferredUpdateAssetUrl = release.Executable.ApiUrl.AbsoluteUri;
            UpdateStatusText.Text = $"{displayName} ready · install on your next update check";
            return;
        }

        try
        {
            UpdateStatusText.Text = $"Installing {displayName}...";
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

    private static string UpdateDisplayName(GitHubReleaseInfo release) =>
        string.Equals(release.TagName, "main-latest", StringComparison.OrdinalIgnoreCase)
            ? release.ReleaseName
            : $"v{VersionText(release.Version)}";

    private static string VersionText(Version version) => $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
}
