using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Media;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using RobloxPriceTracker.Core;
using RobloxPriceTracker.Infrastructure;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace RobloxPriceTracker.Gui;

public partial class MainWindow : Window
{

    private readonly AppServices _services;
    private readonly ObservableCollection<WatchlistRow> _watchlistRows = new();
    private readonly ObservableCollection<WatchlistRow> _dashboardWatchRows = new();
    private readonly ObservableCollection<AlertRow> _alertRows = new();
    private readonly ObservableCollection<HistoryRow> _historyRows = new();
    private readonly ObservableCollection<RecentActivityRow> _activityRows = new();
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Windows.Forms.ToolStripMenuItem? _trayMonitoringMenuItem;
    private System.Windows.Forms.ToolStripMenuItem? _trayStartupMenuItem;
    private System.Drawing.Icon? _trayDrawingIcon;
    private ICollectionView? _watchlistView;
    private CancellationTokenSource? _monitoringCts;
    private Task? _monitoringTask;
    private DispatcherTimer? _notificationTimer;
    private DateTimeOffset? _lastCheckUtc;
    private DateTimeOffset? _nextCheckUtc;
    private IReadOnlyList<JsonFileRepository.PriceHistoryEntry> _currentReportEntries = Array.Empty<JsonFileRepository.PriceHistoryEntry>();
    private bool _initialized;
    private bool _allowExit;
    private bool _shownTrayCloseHint;
    private readonly bool _backgroundLaunch;
    private readonly bool _startupLaunch;
    private CancellationTokenSource? _startupDelayCts;

    public MainWindow(AppServices services, bool backgroundLaunch = false, bool startupLaunch = false)
    {
        _services = services;
        _backgroundLaunch = backgroundLaunch;
        _startupLaunch = startupLaunch;
        InitializeComponent();
        ConfigureStockUi();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await InitializeAsync();
    }

    internal async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        _services.NotificationSink.NotificationRaised += NotificationSink_NotificationRaised;
        Application.Current.SessionEnding += Application_SessionEnding;

        _watchlistView = CollectionViewSource.GetDefaultView(_watchlistRows);
        _watchlistView.Filter = FilterWatchlistRow;
        WatchlistGrid.ItemsSource = _watchlistView;
        AlertsGrid.ItemsSource = _alertRows;
        HistoryGrid.ItemsSource = _historyRows;
        DashboardActivityList.ItemsSource = _activityRows;
        DashboardWatchlistList.ItemsSource = _dashboardWatchRows;
        ReportItemCombo.ItemsSource = _watchlistRows;

        LoadSettingsIntoControls();
        InitializeTrayIcon();
        ShowPage("Dashboard");
        await _services.NotificationDispatcher.DispatchPendingAsync();
        await RefreshAllAsync(loadThumbnails: !_backgroundLaunch);

        if (_services.Settings.StartMonitoringOnLaunch)
        {
            if (_startupLaunch && _services.Settings.StartupDelaySeconds > 0)
            {
                UpdateMonitoringUi(false);
                ScheduleDelayedStartupMonitoring();
            }
            else
            {
                StartMonitoring();
            }
        }
        else
        {
            UpdateMonitoringUi(false);
        }

        InitializeUpdateChecks();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowExit && _services.Settings.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            Hide();
            if (!_shownTrayCloseHint)
            {
                _shownTrayCloseHint = true;
                ShowTrayBalloon("Roblox Price Tracker is still running", "Monitoring continues in the system tray. Use the tray menu to reopen or exit.");
            }
            return;
        }

        _services.NotificationSink.NotificationRaised -= NotificationSink_NotificationRaised;
        Application.Current.SessionEnding -= Application_SessionEnding;
        _startupDelayCts?.Cancel();
        _monitoringCts?.Cancel();
        _notificationTimer?.Stop();
        StopUpdateChecks();
        DisposeTrayIcon();
    }

    private void Application_SessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        _allowExit = true;
    }

    private void DashboardNav_Checked(object sender, RoutedEventArgs e) { if (IsLoaded) ShowPage("Dashboard"); }
    private void WatchlistNav_Checked(object sender, RoutedEventArgs e) { if (IsLoaded) ShowPage("Watchlist"); }
    private void AlertsNav_Checked(object sender, RoutedEventArgs e) { if (IsLoaded) ShowPage("Alerts"); }
    private void ReportsNav_Checked(object sender, RoutedEventArgs e) { if (IsLoaded) ShowPage("Reports"); }
    private void SettingsNav_Checked(object sender, RoutedEventArgs e) { if (IsLoaded) ShowPage("Settings"); }

    private void ShowPage(string page)
    {
        DashboardPage.Visibility = page == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
        WatchlistPage.Visibility = page == "Watchlist" ? Visibility.Visible : Visibility.Collapsed;
        AlertsPage.Visibility = page == "Alerts" ? Visibility.Visible : Visibility.Collapsed;
        ReportsPage.Visibility = page == "Reports" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;

        (PageTitleText.Text, PageSubtitleText.Text) = page switch
        {
            "Watchlist" => ("Watchlist", "Live quotes, 24H movement, target distance, and market signals."),
            "Alerts" => ("Alerts", "Event tape for target crossings and new observed lows."),
            "Reports" => ("Price History", "Interactive resale charts, range performance, and recorded observations."),
            "Settings" => ("Settings", "Monitoring cadence, background behavior, updates, data, and diagnostics."),
            _ => ("Market Overview", "Roblox resale quotes and target signals at a glance.")
        };
    }

    private async Task RefreshAllAsync(CancellationToken cancellationToken = default, bool loadThumbnails = true)
    {
        await RefreshWatchlistAsync(cancellationToken, loadThumbnails);
        await RefreshAlertsAsync(cancellationToken);
        await RefreshProviderHealthAsync(cancellationToken);
        UpdateDashboard();
        UpdateScheduleText();
    }

    private async Task RefreshWatchlistAsync(CancellationToken cancellationToken = default, bool loadThumbnails = true)
    {
        var snapshots = await _services.Repository.LoadEnabledSnapshotsAsync(cancellationToken);
        var staleAfter = TimeSpan.FromSeconds(Math.Max(30, _services.Settings.NormalPollSeconds * 3));
        var existing = _watchlistRows.ToDictionary(x => x.ItemKey);
        var seen = new HashSet<ItemKey>();

        foreach (var snapshot in snapshots)
        {
            seen.Add(snapshot.Item.ItemKey);
            if (!existing.TryGetValue(snapshot.Item.ItemKey, out var row))
            {
                row = new WatchlistRow();
                _watchlistRows.Add(row);
            }
            row.Update(snapshot, staleAfter);
        }

        foreach (var row in _watchlistRows.Where(x => !seen.Contains(x.ItemKey)).ToArray())
        {
            _watchlistRows.Remove(row);
        }

        if (loadThumbnails)
        {
            var thumbnails = await _services.ThumbnailService.GetAssetThumbnailUrlsAsync(_watchlistRows.Select(x => x.AssetId), cancellationToken);
            foreach (var row in _watchlistRows)
            {
                if (thumbnails.TryGetValue(row.AssetId, out var thumbnail))
                {
                    row.SetThumbnail(thumbnail);
                }
            }
        }

        foreach (var row in _watchlistRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var history = await _services.Repository.GetPriceHistoryAsync(row.ItemKey, 96);
                row.UpdateTrend(history);
            }
            catch (Exception ex)
            {
                _services.Logger.Error($"Could not calculate market trend for {row.ItemKey}: {ex.Message}");
            }
        }

        ApplyWatchlistSort();
        _watchlistView?.Refresh();
        UpdateWatchlistEmptyState();
        UpdateDashboardWatchlist();

        KpiWatchedItems.Text = _watchlistRows.Count.ToString("N0");
        KpiNearTarget.Text = _watchlistRows.Count(x => x.IsNearTarget && !x.IsTargetHit).ToString("N0");
        KpiTargetHits.Text = _watchlistRows.Count(x => x.IsTargetHit).ToString("N0");
        var issues = _watchlistRows.Count(x => x.IsIssue);
        HealthItemsNeedingAttentionText.Text = issues == 0 ? "No item issues" : $"{issues:N0} item{(issues == 1 ? string.Empty : "s")} need attention";

        if (ReportItemCombo.SelectedItem is null && _watchlistRows.Count > 0)
        {
            ReportItemCombo.SelectedIndex = 0;
        }
    }
}
