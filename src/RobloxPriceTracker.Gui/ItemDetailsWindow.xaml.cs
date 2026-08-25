using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
            ItemMetaText.Text = $"Asset ID {_itemKey.Id} · Lowest reseller quote";
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

            RobloxResaleMarketData? resaleData = null;
            try
            {
                resaleData = await _services.ResaleDataService.GetAsync(_itemKey.Id);
            }
            catch (Exception ex)
            {
                _services.Logger.Error($"Could not load resale aggregates for {_itemKey}: {ex.Message}");
            }

            var forecast = _services.ForecastEngine.Calculate(_allHistory, resaleData, target);
            await _services.ForecastHistoryStore.RecordAsync(
                _itemKey,
                _snapshot.Market.LastPollSequence,
                forecast,
                _snapshot.Market.CurrentLowestPrice,
                _snapshot.Market.LastSuccessAtUtc ?? DateTimeOffset.UtcNow);
            var backtest = await _services.ForecastHistoryStore.GetStatsAsync(_itemKey, 50);
            ApplyForecast(forecast, resaleData, backtest);

            StatusText.Text = "Hover the chart for an exact quote. Forecasts are statistical estimates and are backtested automatically.";
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
            "1H" => now - TimeSpan.FromHours(1),
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
            RangeChangeText.Foreground = new SolidColorBrush(Color.FromRgb(167, 178, 192));
            return;
        }

        var prices = valid.Select(x => x.Price!.Value).ToArray();
        RangeLowText.Text = DisplayFormatting.Price(prices.Min());
        RangeHighText.Text = DisplayFormatting.Price(prices.Max());
        RangeChangeText.Text = DisplayFormatting.PercentageChange(valid[0].Price!.Value, valid[^1].Price!.Value);
        RangeChangeText.Foreground = new SolidColorBrush(valid[^1].Price!.Value switch
        {
            var last when last > valid[0].Price!.Value => Color.FromRgb(0, 192, 118),
            var last when last < valid[0].Price!.Value => Color.FromRgb(246, 70, 93),
            _ => Color.FromRgb(167, 178, 192)
        });
    }

    private void ApplyForecast(PriceForecastResult forecast, RobloxResaleMarketData? resaleData, ForecastBacktestStats backtest)
    {
        ForecastRapText.Text = resaleData?.RecentAveragePrice is > 0
            ? $"{resaleData.RecentAveragePrice.Value:N0} R$"
            : "—";
        ForecastSalesText.Text = resaleData is { IsAvailable: true, HasSalesSeries: true }
            ? $"{resaleData.SalesPerDay7d:0.#}/day"
            : "—";
        ForecastLiquidityText.Text = forecast.LiquidityScore is { } liquidity ? $"{liquidity:0}/100" : "—";
        ForecastAccuracyText.Text = backtest.EvaluatedForecasts switch
        {
            >= 5 => $"{backtest.AccuracyPercent:0}% accuracy · n={backtest.EvaluatedForecasts}",
            > 0 => $"Learning · n={backtest.EvaluatedForecasts}",
            _ => "Learning"
        };

        if (!forecast.IsAvailable || forecast.NextPrice is not > 0)
        {
            ForecastPriceText.Text = "—";
            ForecastFairValueText.Text = "—";
            ForecastRangeText.Text = forecast.Status;
            ForecastConfidenceText.Text = "—";
            ForecastDirectionText.Text = "INSUFFICIENT";
            ForecastDirectionText.Foreground = new SolidColorBrush(Color.FromRgb(132, 145, 162));
            ForecastPriceText.Foreground = new SolidColorBrush(Color.FromRgb(167, 178, 192));
            ForecastTarget1hText.Text = "—";
            ForecastTarget6hText.Text = "—";
            ForecastTarget24hText.Text = "—";
            ForecastEtaText.Text = "—";
            ForecastStatusText.Text = forecast.Status;
            return;
        }

        ForecastPriceText.Text = DisplayFormatting.Price(forecast.NextPrice);
        ForecastFairValueText.Text = DisplayFormatting.Price(forecast.FairValue);
        ForecastRangeText.Text = forecast.RangeLow is > 0 && forecast.RangeHigh is > 0
            ? $"Expected {DisplayFormatting.Price(forecast.RangeLow)} – {DisplayFormatting.Price(forecast.RangeHigh)}"
            : "—";
        ForecastConfidenceText.Text = $"{forecast.ConfidencePercent:0}%";
        ForecastDirectionText.Text = forecast.Direction switch
        {
            ForecastDirection.StrongBearish => "▼▼ STRONG BEARISH",
            ForecastDirection.Bearish => "▼ BEARISH",
            ForecastDirection.StrongBullish => "▲▲ STRONG BULLISH",
            ForecastDirection.Bullish => "▲ BULLISH",
            _ => "• NEUTRAL"
        };

        var directionColor = forecast.Direction switch
        {
            ForecastDirection.StrongBearish or ForecastDirection.Bearish => Color.FromRgb(246, 70, 93),
            ForecastDirection.StrongBullish or ForecastDirection.Bullish => Color.FromRgb(0, 192, 118),
            _ => Color.FromRgb(100, 168, 255)
        };
        var directionBrush = new SolidColorBrush(directionColor);
        ForecastDirectionText.Foreground = directionBrush;
        ForecastPriceText.Foreground = directionBrush;
        ForecastConfidenceText.Foreground = new SolidColorBrush(forecast.ConfidencePercent switch
        {
            >= 75 => Color.FromRgb(0, 192, 118),
            >= 50 => Color.FromRgb(240, 185, 11),
            _ => Color.FromRgb(246, 112, 93)
        });

        ForecastTarget1hText.Text = FormatProbability(forecast.TargetProbability1h);
        ForecastTarget6hText.Text = FormatProbability(forecast.TargetProbability6h);
        ForecastTarget24hText.Text = FormatProbability(forecast.TargetProbability24h);
        ForecastEtaText.Text = forecast.EstimatedHoursToTarget switch
        {
            0 => "Target already reached",
            > 0 and < 1 => $"~{forecast.EstimatedHoursToTarget.Value * 60:0} min",
            > 0 and < 48 => $"~{forecast.EstimatedHoursToTarget.Value:0.#} hr",
            > 0 => $"~{forecast.EstimatedHoursToTarget.Value / 24d:0.#} days",
            _ => "No current ETA"
        };
        ForecastStatusText.Text = backtest.EvaluatedForecasts >= 5
            ? $"MAPE {backtest.MeanAbsolutePercentError:0.#}% · range hit {backtest.RangeCoveragePercent:0}%"
            : forecast.SalesDataAvailable
                ? "RAP + daily volume active"
                : "Local quote model only";
    }

    private static string FormatProbability(double? probability) =>
        probability is { } value && !double.IsNaN(value) ? $"{value:0}%" : "—";

    private void SetStatusBadge(TrackerItemSnapshot snapshot, long? target)
    {
        var current = snapshot.Market.CurrentLowestPrice;
        var targetRule = snapshot.Rules.FirstOrDefault(x => x.RuleType == AlertRuleType.TargetPrice);
        var staleAfter = TimeSpan.FromSeconds(Math.Max(30, _services.Settings.NormalPollSeconds * 3));
        var stale = snapshot.Market.LastSuccessAtUtc is { } last && DateTimeOffset.UtcNow - last > staleAfter;

        if (stale)
        {
            SetBadge("Stale data", Color.FromRgb(52, 38, 18), Color.FromRgb(240, 185, 11));
            return;
        }
        if (targetRule is { State: AlertState.Triggered })
        {
            SetBadge("Target hit", Color.FromRgb(8, 42, 29), Color.FromRgb(0, 192, 118));
            return;
        }
        if (snapshot.Market.ObservedStatus == MarketStatus.Available && current is > 0 && target is > 0 && current.Value > target.Value && current.Value <= target.Value * 1.10)
        {
            SetBadge("Near target", Color.FromRgb(52, 42, 14), Color.FromRgb(240, 185, 11));
            return;
        }

        switch (snapshot.Market.ObservedStatus)
        {
            case MarketStatus.Available: SetBadge("Watching", Color.FromRgb(16, 38, 63), Color.FromRgb(100, 168, 255)); break;
            case MarketStatus.NoResellers: SetBadge("No sellers", Color.FromRgb(52, 38, 18), Color.FromRgb(240, 185, 11)); break;
            case MarketStatus.OffSale: SetBadge("Off sale", Color.FromRgb(35, 40, 51), Color.FromRgb(170, 178, 191)); break;
            default: SetBadge("Waiting", Color.FromRgb(31, 41, 54), Color.FromRgb(154, 165, 180)); break;
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

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
