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

    private void StartMonitoring()
    {
        if (_startupDelayCts is not null)
        {
            var pending = _startupDelayCts;
            _startupDelayCts = null;
            pending.Cancel();
        }

        if (_monitoringCts is not null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _monitoringCts = cts;
        _monitoringTask = MonitorLoopGuardedAsync(cts);
        UpdateMonitoringUi(true);
    }

    private async Task StopMonitoringAsync()
    {
        var cts = _monitoringCts;
        var task = _monitoringTask;
        _monitoringCts = null;
        _monitoringTask = null;
        _nextCheckUtc = null;
        cts?.Cancel();
        UpdateMonitoringUi(false);
        UpdateScheduleText();

        if (task is not null)
        {
            try { await task; }
            catch (OperationCanceledException) { }
        }
    }

    private async Task MonitorLoopGuardedAsync(CancellationTokenSource cts)
    {
        try
        {
            await MonitorLoopAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _services.Logger.Error(ex.ToString());
            ShowBanner("Monitoring stopped", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_monitoringCts, cts))
            {
                _monitoringCts = null;
                _nextCheckUtc = null;
                UpdateMonitoringUi(false);
                UpdateScheduleText();
            }
            cts.Dispose();
        }
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await RunCheckAsync(false, cancellationToken);

            var snapshots = await _services.Repository.LoadEnabledSnapshotsAsync(cancellationToken);
            var delay = _services.CreatePollPlanner().GetNextDelay(snapshots, _services.RateGovernor);
            if (delay < TimeSpan.FromSeconds(10))
            {
                delay = TimeSpan.FromSeconds(10);
            }

            _nextCheckUtc = DateTimeOffset.UtcNow + delay;
            UpdateScheduleText();

            try
            {
                while (_nextCheckUtc is { } next && next > DateTimeOffset.UtcNow)
                {
                    var remaining = next - DateTimeOffset.UtcNow;
                    await Task.Delay(remaining > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : remaining, cancellationToken);
                    UpdateScheduleText();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private void UpdateMonitoringUi(bool monitoring)
    {
        MonitoringButton.Content = monitoring ? "Pause Monitoring" : "Start Monitoring";
        SidebarMonitorText.Text = monitoring ? "Monitoring" : "Paused";
        SidebarMonitorDot.Foreground = new SolidColorBrush(monitoring ? Color.FromRgb(18, 183, 106) : Color.FromRgb(152, 162, 179));
        FooterMonitorText.Text = monitoring ? "Monitoring ON" : "Monitoring PAUSED";
        FooterMonitorText.Foreground = new SolidColorBrush(monitoring ? Color.FromRgb(6, 118, 71) : Color.FromRgb(102, 112, 133));
        HealthMonitoringText.Text = monitoring ? "Active" : "Paused";
        _trayIcon?.UpdateMonitoring(monitoring);
    }

    private void UpdateScheduleText()
    {
        FooterLastCheckText.Text = _lastCheckUtc is { } last ? $"Last check: {last.ToLocalTime():h:mm:ss tt}" : "Last check: —";
        if (_nextCheckUtc is { } next && _monitoringCts is not null)
        {
            var seconds = Math.Max(0, Math.Ceiling((next - DateTimeOffset.UtcNow).TotalSeconds));
            FooterNextCheckText.Text = $"Next check: {seconds:N0}s";
            KpiNextRefresh.Text = $"Next check in {seconds:N0}s";
        }
        else
        {
            FooterNextCheckText.Text = "Next check: —";
            KpiNextRefresh.Text = "Next check not scheduled";
        }

        KpiLastRefresh.Text = _lastCheckUtc is { } refreshed ? refreshed.ToLocalTime().ToString("h:mm:ss tt") : "—";
    }

    private async void CheckNowButton_Click(object sender, RoutedEventArgs e) => await RunCheckAsync(true, CancellationToken.None);

    private async void MonitoringButton_Click(object sender, RoutedEventArgs e)
    {
        if (_startupDelayCts is not null)
        {
            CancelDelayedStartupMonitoring();
            return;
        }
        if (_monitoringCts is null) StartMonitoring();
        else await StopMonitoringAsync();
    }

    private async void AddItemButton_Click(object sender, RoutedEventArgs e)
    {
        var resumeMonitoring = _monitoringCts is not null;
        if (resumeMonitoring) await StopMonitoringAsync();

        try
        {
            var dialog = new AddItemWindow(_services) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                await RefreshAllAsync();
                WatchlistNav.IsChecked = true;
                ShowPage("Watchlist");
            }
        }
        finally
        {
            if (resumeMonitoring) StartMonitoring();
        }
    }

    private async void EditTargetButton_Click(object sender, RoutedEventArgs e)
    {
        if (WatchlistGrid.SelectedItem is not WatchlistRow row) return;

        var resumeMonitoring = _monitoringCts is not null;
        if (resumeMonitoring) await StopMonitoringAsync();

        try
        {
            var dialog = new AddItemWindow(_services, row.AssetId.ToString(), row.TargetValue) { Owner = this };
            if (dialog.ShowDialog() == true) await RefreshAllAsync();
        }
        finally
        {
            if (resumeMonitoring) StartMonitoring();
        }
    }

    private async void RemoveItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (WatchlistGrid.SelectedItem is not WatchlistRow row) return;

        if (_services.Settings.ConfirmBeforeRemove)
        {
            var result = MessageBox.Show(
                $"Stop watching {row.Name}?\n\nIts recorded history will remain in the local data file.",
                "Stop watching item",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;
        }

        await _services.Repository.DisableItemAsync(row.ItemKey);
        await RefreshAllAsync();
        WatchlistGrid.SelectedItem = null;
    }

    private async void ViewItemButton_Click(object sender, RoutedEventArgs e) => await OpenSelectedItemDetailsAsync();

    private async void WatchlistGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GetDoubleClickedWatchlistRow(WatchlistGrid, e) is { } row)
            await OpenItemDetailsAsync(row);
    }

    private async void DashboardWatchlistList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GetDoubleClickedWatchlistRow(DashboardWatchlistList, e) is { } row)
            await OpenItemDetailsAsync(row);
    }

    private static WatchlistRow? GetDoubleClickedWatchlistRow(DataGrid grid, MouseButtonEventArgs e)
    {
        var current = e.OriginalSource as DependencyObject;
        while (current is not null && current is not DataGridRow)
            current = VisualTreeHelper.GetParent(current);

        return current is DataGridRow dataGridRow && dataGridRow.Item is WatchlistRow row
            ? row
            : null;
    }

    private async Task OpenSelectedItemDetailsAsync()
    {
        if (WatchlistGrid.SelectedItem is not WatchlistRow row) return;
        await OpenItemDetailsAsync(row);
    }

    private async Task OpenItemDetailsAsync(WatchlistRow row)
    {
        var window = new ItemDetailsWindow(_services, row.ItemKey) { Owner = this };
        window.ShowDialog();
        await RefreshAllAsync();
    }

    private void OpenRobloxButton_Click(object sender, RoutedEventArgs e)
    {
        if (WatchlistGrid.SelectedItem is WatchlistRow row)
        {
            OpenUrl($"https://www.roblox.com/catalog/{row.AssetId}");
        }
    }

    private void WatchlistGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = WatchlistGrid.SelectedItem is WatchlistRow;
        ViewItemButton.IsEnabled = selected;
        EditTargetButton.IsEnabled = selected;
        OpenRobloxButton.IsEnabled = selected;
        RemoveItemButton.IsEnabled = selected;
        WatchlistSelectionHint.Text = selected && WatchlistGrid.SelectedItem is WatchlistRow row
            ? $"Selected: {row.Name} · {row.CurrentPrice} · {row.TargetDistance}"
            : "Select an item to view details or change its target.";
    }
}
