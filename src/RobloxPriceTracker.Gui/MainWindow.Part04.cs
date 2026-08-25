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

    private void WatchlistSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _watchlistView?.Refresh();
        UpdateWatchlistEmptyState();
    }

    private void WatchlistFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _watchlistView?.Refresh();
        UpdateWatchlistEmptyState();
    }

    private void WatchlistSortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyWatchlistSort();
    }

    private bool FilterWatchlistRow(object item)
    {
        if (item is not WatchlistRow row) return false;

        var search = WatchlistSearchBox?.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(search) &&
            !row.Name.Contains(search, StringComparison.OrdinalIgnoreCase) &&
            !row.AssetId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var filter = (WatchlistFilterCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
        return filter switch
        {
            "Near" => row.IsNearTarget && !row.IsTargetHit,
            "Hit" => row.IsTargetHit,
            "NoSellers" => row.Snapshot.Market.ObservedStatus == MarketStatus.NoResellers,
            "Issues" => row.IsIssue,
            _ => true
        };
    }

    private void ApplyWatchlistSort()
    {
        if (_watchlistView is null) return;
        _watchlistView.SortDescriptions.Clear();
        var sort = (WatchlistSortCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Added";
        switch (sort)
        {
            case "Name":
                _watchlistView.SortDescriptions.Add(new SortDescription(nameof(WatchlistRow.Name), ListSortDirection.Ascending));
                break;
            case "Target":
                _watchlistView.SortDescriptions.Add(new SortDescription(nameof(WatchlistRow.TargetDistanceSort), ListSortDirection.Ascending));
                break;
            case "Price":
                _watchlistView.SortDescriptions.Add(new SortDescription(nameof(WatchlistRow.CurrentPriceSort), ListSortDirection.Ascending));
                break;
            case "Checked":
                _watchlistView.SortDescriptions.Add(new SortDescription(nameof(WatchlistRow.LastCheckedSort), ListSortDirection.Descending));
                break;
            case "Change":
                _watchlistView.SortDescriptions.Add(new SortDescription(nameof(WatchlistRow.Change24hSort), ListSortDirection.Descending));
                break;
            case "ForecastConfidence":
                _watchlistView.SortDescriptions.Add(new SortDescription(nameof(WatchlistRow.ForecastConfidenceSort), ListSortDirection.Descending));
                break;
            case "ForecastPrice":
                _watchlistView.SortDescriptions.Add(new SortDescription(nameof(WatchlistRow.ForecastPriceSort), ListSortDirection.Ascending));
                break;
        }
    }

    private void UpdateWatchlistEmptyState()
    {
        if (WatchlistEmptyState is null) return;
        var visibleCount = _watchlistRows.Count(x => FilterWatchlistRow(x));
        var empty = visibleCount == 0;
        WatchlistEmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty) return;

        if (_watchlistRows.Count == 0)
        {
            WatchlistEmptyTitle.Text = "No items yet";
            WatchlistEmptyText.Text = "Add a Roblox Limited item to start monitoring its lowest reseller price.";
            WatchlistEmptyAddButton.Visibility = Visibility.Visible;
        }
        else
        {
            WatchlistEmptyTitle.Text = "No matching items";
            WatchlistEmptyText.Text = "Try a different search term or status filter.";
            WatchlistEmptyAddButton.Visibility = Visibility.Collapsed;
        }
    }

    private async void RefreshWatchlist_Click(object sender, RoutedEventArgs e) => await RefreshWatchlistAsync();
    private async void RefreshAlerts_Click(object sender, RoutedEventArgs e) => await RefreshAlertsAsync();
    private async void RefreshReports_Click(object sender, RoutedEventArgs e) => await LoadSelectedReportAsync();

    private void GoWatchlist_Click(object sender, RoutedEventArgs e)
    {
        WatchlistNav.IsChecked = true;
        ShowPage("Watchlist");
    }

    private void ViewAlertsButton_Click(object sender, RoutedEventArgs e)
    {
        AlertsNav.IsChecked = true;
        ShowPage("Alerts");
    }

    private async void ReportItemCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) await LoadSelectedReportAsync();
    }

    private async void ReportRangeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) await LoadSelectedReportAsync();
    }

    private async Task LoadSelectedReportAsync()
    {
        if (ReportItemCombo.SelectedItem is not WatchlistRow row)
        {
            ClearReport();
            return;
        }

        var history = await _services.Repository.GetPriceHistoryAsync(row.ItemKey, 5000);
        var cutoff = GetReportCutoff();
        var filtered = cutoff is { } since
            ? history.Where(x => x.ObservedAtUtc >= since).ToArray()
            : history.ToArray();
        _currentReportEntries = filtered;

        _historyRows.Clear();
        foreach (var entry in filtered.Take(500))
        {
            _historyRows.Add(new HistoryRow(
                entry.ItemKey,
                row.Name,
                DisplayFormatting.Price(entry.Price),
                FormatMarketStatus(entry.Status),
                entry.ObservedAtUtc));
        }

        var target = row.TargetValue;
        ReportPriceChart.SetTargetPrice(target);
        ReportPriceChart.SetPoints(filtered);
        ReportChartTitle.Text = $"{row.Name} — lowest reseller price";
        ReportCurrentText.Text = row.CurrentPrice;
        ReportTargetText.Text = row.TargetPrice;
        ReportObservationText.Text = $"{filtered.Length:N0} observation{(filtered.Length == 1 ? string.Empty : "s")} · Forecast {row.ForecastPrice} · {row.ForecastConfidence} confidence";

        var valid = filtered.Where(x => x.Price is > 0).OrderBy(x => x.ObservedAtUtc).ToArray();
        if (valid.Length == 0)
        {
            ReportLowText.Text = "—";
            ReportHighText.Text = "—";
            ReportChangeText.Text = "—";
            ReportChangeText.Foreground = new SolidColorBrush(Color.FromRgb(167, 178, 192));
            return;
        }

        var prices = valid.Select(x => x.Price!.Value).ToArray();
        ReportLowText.Text = DisplayFormatting.Price(prices.Min());
        ReportHighText.Text = DisplayFormatting.Price(prices.Max());
        ReportChangeText.Text = DisplayFormatting.PercentageChange(valid[0].Price!.Value, valid[^1].Price!.Value);
        ReportChangeText.Foreground = new SolidColorBrush(valid[^1].Price!.Value switch
        {
            var last when last > valid[0].Price!.Value => Color.FromRgb(0, 192, 118),
            var last when last < valid[0].Price!.Value => Color.FromRgb(246, 70, 93),
            _ => Color.FromRgb(167, 178, 192)
        });
    }

    private DateTimeOffset? GetReportCutoff()
    {
        var range = (ReportRangeCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "7D";
        var now = DateTimeOffset.UtcNow;
        return range switch
        {
            "1H" => now - TimeSpan.FromHours(1),
            "24H" => now - TimeSpan.FromHours(24),
            "7D" => now - TimeSpan.FromDays(7),
            "30D" => now - TimeSpan.FromDays(30),
            _ => null
        };
    }

    private void ClearReport()
    {
        _historyRows.Clear();
        _currentReportEntries = Array.Empty<JsonFileRepository.PriceHistoryEntry>();
        ReportPriceChart.SetTargetPrice(null);
        ReportPriceChart.SetPoints(Array.Empty<JsonFileRepository.PriceHistoryEntry>());
        ReportChartTitle.Text = "Price history";
        ReportCurrentText.Text = "—";
        ReportTargetText.Text = "—";
        ReportLowText.Text = "—";
        ReportHighText.Text = "—";
        ReportChangeText.Text = "—";
        ReportChangeText.Foreground = new SolidColorBrush(Color.FromRgb(167, 178, 192));
        ReportObservationText.Text = "0 observations";
    }
}
