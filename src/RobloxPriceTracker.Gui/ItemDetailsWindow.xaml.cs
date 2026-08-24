using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RobloxPriceTracker.Core;
using RobloxPriceTracker.Infrastructure;
using Color = System.Windows.Media.Color;

namespace RobloxPriceTracker.Gui;

public partial class ItemDetailsWindow : Window
{
    private readonly AppServices _services;
    private readonly ItemKey _itemKey;
    private readonly ObservableCollection<HistoryRow> _historyRows = new();
    private TrackerItemSnapshot? _snapshot;
    private IReadOnlyList<JsonFileRepository.PriceHistoryEntry> _allHistory = Array.Empty<JsonFileRepository.PriceHistoryEntry>();

    public ItemDetailsWindow(AppServices services, ItemKey itemKey)
    {
        _services = services;
        _itemKey = itemKey;
        InitializeComponent();
        DetailHistoryGrid.ItemsSource = _historyRows;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        try
        {
            _snapshot = await _services.Repository.LoadSnapshotAsync(_itemKey);
            if (_snapshot is null)
            {
                StatusText.Text = "This item is no longer active in the watchlist.";
                return;
            }

            ItemNameText.Text = _snapshot.Item.Name;
            ItemMetaText.Text = $"Asset ID {_itemKey.Id} · Lowest reseller monitoring";
            CurrentPriceText.Text = DisplayFormatting.Price(_snapshot.Market.CurrentLowestPrice);
            var target = _snapshot.Rules.FirstOrDefault(x => x.RuleType == AlertRuleType.TargetPrice)?.Threshold;
            TargetPriceText.Text = DisplayFormatting.Price(target);
            TrackedLowText.Text = DisplayFormatting.Price(_snapshot.Market.TrackedLow);
            LastCheckedText.Text = _snapshot.Market.LastSuccessAtUtc is { } checkedAt
                ? checkedAt.ToLocalTime().ToString("MMM d, h:mm tt")
                : "Never";
            TargetDistanceText.Text = FormatTargetDistance(_snapshot.Market.CurrentLowestPrice, target);
            SetStatusBadge(_snapshot, target);

            var thumbnailUrl = await _services.ThumbnailService.GetAssetThumbnailUrlAsync(_itemKey.Id);
            if (!string.IsNullOrWhiteSpace(thumbnailUrl))
            {
                try
                {
                    ItemThumbnail.Source = new BitmapImage(new Uri(thumbnailUrl));
                    ItemThumbnailFallback.Visibility = Visibility.Collapsed;
                }
                catch
                {
                    ItemThumbnailFallback.Visibility = Visibility.Visible;
                }
            }

            _allHistory = await _services.Repository.GetPriceHistoryAsync(_itemKey, 5000);
            ApplyRange();
            DetailPriceChart.SetTargetPrice(target);
            StatusText.Text = "Current data is stored locally and remains available after the application restarts.";
        }
        catch (Exception ex)
        {
            _services.Logger.Error(ex.ToString());
            StatusText.Text = ex.Message;
        }
    }

    private void DetailRangeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && _snapshot is not null)
        {
            ApplyRange();
        }
    }

    private void ApplyRange()
    {
        var range = (DetailRangeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "7D";
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? cutoff = range switch
        {
            "24H" => now - TimeSpan.FromHours(24),
            "7D" => now - TimeSpan.FromDays(7),
            "30D" => now - TimeSpan.FromDays(30),
            _ => null
        };

        var filtered = cutoff is { } since
            ? _allHistory.Where(x => x.ObservedAtUtc >= since).ToArray()
            : _allHistory.ToArray();

        _historyRows.Clear();
        foreach (var entry in filtered.Take(100))
        {
            _historyRows.Add(new HistoryRow(
                entry.ItemKey,
                _snapshot?.Item.Name ?? _itemKey.ToString(),
                DisplayFormatting.Price(entry.Price),
                FormatMarketStatus(entry.Status),
                entry.ObservedAtUtc));
        }

        DetailPriceChart.SetPoints(filtered);
        ObservationCountText.Text = $"{filtered.Length:N0} observation{(filtered.Length == 1 ? string.Empty : "s")} in range";

        var valid = filtered.Where(x => x.Price is > 0).OrderBy(x => x.ObservedAtUtc).ToArray();
        if (valid.Length == 0)
        {
            RangeLowText.Text = "—";
            RangeHighText.Text = "—";
            RangeChangeText.Text = "—";
            return;
        }

        var prices = valid.Select(x => x.Price!.Value).ToArray();
        RangeLowText.Text = DisplayFormatting.Price(prices.Min());
        RangeHighText.Text = DisplayFormatting.Price(prices.Max());
        RangeChangeText.Text = DisplayFormatting.PercentageChange(valid[0].Price!.Value, valid[^1].Price!.Value);
    }

    private void SetStatusBadge(TrackerItemSnapshot snapshot, long? target)
    {
        var current = snapshot.Market.CurrentLowestPrice;
        var targetRule = snapshot.Rules.FirstOrDefault(x => x.RuleType == AlertRuleType.TargetPrice);
        var staleAfter = TimeSpan.FromSeconds(Math.Max(30, _services.Settings.NormalPollSeconds * 3));
        var stale = snapshot.Market.LastSuccessAtUtc is { } last && DateTimeOffset.UtcNow - last > staleAfter;

        if (stale)
        {
            SetBadge("Stale data", Color.FromRgb(255, 247, 237), Color.FromRgb(154, 52, 18));
            return;
        }
        if (targetRule is { State: AlertState.Triggered })
        {
            SetBadge("Target hit", Color.FromRgb(236, 253, 245), Color.FromRgb(4, 120, 87));
            return;
        }
        if (snapshot.Market.ObservedStatus == MarketStatus.Available && current is > 0 && target is > 0 && current.Value > target.Value && current.Value <= target.Value * 1.10)
        {
            SetBadge("Near target", Color.FromRgb(255, 247, 237), Color.FromRgb(181, 71, 8));
            return;
        }

        switch (snapshot.Market.ObservedStatus)
        {
            case MarketStatus.Available: SetBadge("Watching", Color.FromRgb(239, 246, 255), Color.FromRgb(29, 78, 216)); break;
            case MarketStatus.NoResellers: SetBadge("No sellers", Color.FromRgb(255, 247, 237), Color.FromRgb(154, 52, 18)); break;
            case MarketStatus.OffSale: SetBadge("Off sale", Color.FromRgb(242, 244, 247), Color.FromRgb(71, 84, 103)); break;
            default: SetBadge("Waiting", Color.FromRgb(242, 244, 247), Color.FromRgb(102, 112, 133)); break;
        }
    }

    private void SetBadge(string text, Color background, Color foreground)
    {
        ItemStatusText.Text = text;
        ItemStatusText.Foreground = new SolidColorBrush(foreground);
        ItemStatusBadge.Background = new SolidColorBrush(background);
    }

    private static string FormatTargetDistance(long? current, long? target)
    {
        if (target is not > 0) return "Target unavailable";
        if (current is not > 0) return "Waiting for reseller price";
        var delta = current.Value - target.Value;
        if (delta == 0) return "At target";
        if (delta < 0) return $"{Math.Abs(delta):N0} R$ below target";
        var percent = delta / (double)target.Value * 100d;
        return $"{delta:N0} R$ above · {percent:0.#}%";
    }

    private static string FormatMarketStatus(MarketStatus status) => status switch
    {
        MarketStatus.Available => "Available",
        MarketStatus.NoResellers => "No sellers",
        MarketStatus.OffSale => "Off sale",
        MarketStatus.InvalidPrice => "Price issue",
        MarketStatus.Unsupported => "Unsupported",
        _ => "Unknown"
    };

    private void OpenRoblox_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo($"https://www.roblox.com/catalog/{_itemKey.Id}") { UseShellExecute = true }); }
        catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
