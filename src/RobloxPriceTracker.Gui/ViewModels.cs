using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using RobloxPriceTracker.Core;
using RobloxPriceTracker.Infrastructure;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace RobloxPriceTracker.Gui;

public sealed class WatchlistRow : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _thumbnailUrl = string.Empty;
    private string _currentPrice = "—";
    private string _targetPrice = "—";
    private string _trackedLow = "—";
    private string _targetDistance = "Target unavailable";
    private string _status = "Unknown";
    private string _lastChecked = "Never";
    private string _change24h = "—";
    private string _change24hAmount = "No 24H baseline";
    private IReadOnlyList<double> _sparklineValues = Array.Empty<double>();
    private Brush _statusBackground = Brush(21, 28, 37);
    private Brush _statusForeground = Brush(154, 165, 180);
    private Brush _priceForeground = Brush(230, 237, 243);
    private Brush _trendForeground = Brush(102, 115, 131);

    public ItemKey ItemKey { get; private set; }
    public TrackerItemSnapshot Snapshot { get; private set; } = null!;
    public long AssetId => ItemKey.Id;
    public string AssetIdText => $"Asset {AssetId}";
    public string Name { get => _name; private set => SetField(ref _name, value); }
    public string ThumbnailUrl { get => _thumbnailUrl; private set => SetField(ref _thumbnailUrl, value); }
    public string CurrentPrice { get => _currentPrice; private set => SetField(ref _currentPrice, value); }
    public string TargetPrice { get => _targetPrice; private set => SetField(ref _targetPrice, value); }
    public string TrackedLow { get => _trackedLow; private set => SetField(ref _trackedLow, value); }
    public string TargetDistance { get => _targetDistance; private set => SetField(ref _targetDistance, value); }
    public string Status { get => _status; private set => SetField(ref _status, value); }
    public string LastChecked { get => _lastChecked; private set => SetField(ref _lastChecked, value); }
    public string Change24h { get => _change24h; private set => SetField(ref _change24h, value); }
    public string Change24hAmount { get => _change24hAmount; private set => SetField(ref _change24hAmount, value); }
    public IReadOnlyList<double> SparklineValues { get => _sparklineValues; private set => SetField(ref _sparklineValues, value); }
    public Brush StatusBackground { get => _statusBackground; private set => SetField(ref _statusBackground, value); }
    public Brush StatusForeground { get => _statusForeground; private set => SetField(ref _statusForeground, value); }
    public Brush PriceForeground { get => _priceForeground; private set => SetField(ref _priceForeground, value); }
    public Brush TrendForeground { get => _trendForeground; private set => SetField(ref _trendForeground, value); }

    public long? TargetValue => Snapshot?.Rules.FirstOrDefault(x => x.RuleType == AlertRuleType.TargetPrice)?.Threshold;
    public long? CurrentPriceValue => Snapshot?.Market.CurrentLowestPrice;
    public long CurrentPriceSort => CurrentPriceValue ?? long.MaxValue;
    public double TargetDistanceSort { get; private set; } = double.MaxValue;
    public double Change24hSort { get; private set; } = double.MinValue;
    public long LastCheckedSort => Snapshot?.Market.LastSuccessAtUtc?.UtcDateTime.Ticks ?? 0;
    public bool IsNearTarget { get; private set; }
    public bool IsTargetHit { get; private set; }
    public bool IsStale { get; private set; }
    public bool IsIssue { get; private set; }

    public void Update(TrackerItemSnapshot snapshot, TimeSpan staleAfter)
    {
        Snapshot = snapshot;
        ItemKey = snapshot.Item.ItemKey;
        Name = snapshot.Item.Name;
        OnPropertyChanged(nameof(AssetId));
        OnPropertyChanged(nameof(AssetIdText));
        OnPropertyChanged(nameof(TargetValue));
        OnPropertyChanged(nameof(CurrentPriceValue));
        OnPropertyChanged(nameof(CurrentPriceSort));

        CurrentPrice = DisplayFormatting.Price(snapshot.Market.CurrentLowestPrice);
        TargetPrice = DisplayFormatting.Price(TargetValue);
        TrackedLow = DisplayFormatting.Price(snapshot.Market.TrackedLow);
        LastChecked = snapshot.Market.LastSuccessAtUtc is { } last ? FormatLocal(last) : "Never";

        IsStale = snapshot.Market.LastSuccessAtUtc is { } success && DateTimeOffset.UtcNow - success > staleAfter;
        IsTargetHit = snapshot.Rules.FirstOrDefault(x => x.RuleType == AlertRuleType.TargetPrice) is { State: AlertState.Triggered };
        IsNearTarget = false;
        IsIssue = false;
        TargetDistanceSort = double.MaxValue;
        TargetDistance = TargetValue is > 0 ? "Waiting for reseller price" : "Target unavailable";
        PriceForeground = Brush(230, 237, 243);

        var current = snapshot.Market.CurrentLowestPrice;
        var target = TargetValue;
        if (current is > 0 && target is > 0)
        {
            var delta = current.Value - target.Value;
            var percent = delta / (double)target.Value * 100d;
            TargetDistanceSort = percent;
            if (delta <= 0)
            {
                TargetDistance = delta == 0
                    ? "At target"
                    : $"{Math.Abs(delta):N0} R$ below target";
                PriceForeground = Brush(0, 192, 118);
            }
            else
            {
                TargetDistance = $"{delta:N0} R$ above · {percent:0.#}%";
                IsNearTarget = percent <= 10d;
                if (IsNearTarget)
                {
                    PriceForeground = Brush(240, 185, 11);
                }
            }
        }

        if (IsStale)
        {
            IsIssue = true;
            SetStatus("Stale data", 52, 38, 18, 240, 185, 11);
            RaiseComputedFlags();
            return;
        }

        if (IsTargetHit)
        {
            SetStatus("Target hit", 8, 42, 29, 0, 192, 118);
            RaiseComputedFlags();
            return;
        }

        switch (snapshot.Market.ObservedStatus)
        {
            case MarketStatus.Available when IsNearTarget:
                SetStatus("Near target", 52, 42, 14, 240, 185, 11);
                break;
            case MarketStatus.Available:
                SetStatus("Watching", 16, 38, 63, 100, 168, 255);
                break;
            case MarketStatus.NoResellers:
                IsIssue = true;
                SetStatus("No sellers", 52, 38, 18, 240, 185, 11);
                break;
            case MarketStatus.OffSale:
                IsIssue = true;
                SetStatus("Off sale", 35, 40, 51, 170, 178, 191);
                break;
            case MarketStatus.InvalidPrice:
                IsIssue = true;
                SetStatus("Price issue", 55, 22, 29, 246, 70, 93);
                break;
            case MarketStatus.Unsupported:
                IsIssue = true;
                SetStatus("Unsupported", 55, 22, 29, 246, 70, 93);
                break;
            default:
                IsIssue = true;
                SetStatus("Waiting", 31, 41, 54, 154, 165, 180);
                break;
        }

        RaiseComputedFlags();
    }

    public void UpdateTrend(IEnumerable<JsonFileRepository.PriceHistoryEntry> history)
    {
        var valid = history
            .Where(x => x.Price is > 0)
            .OrderBy(x => x.ObservedAtUtc)
            .ToArray();

        SparklineValues = valid.TakeLast(32).Select(x => (double)x.Price!.Value).ToArray();
        Change24h = "—";
        Change24hAmount = "No 24H baseline";
        Change24hSort = double.MinValue;
        TrendForeground = Brush(102, 115, 131);

        if (valid.Length == 0)
        {
            OnPropertyChanged(nameof(Change24hSort));
            return;
        }

        var recent = valid.Where(x => x.ObservedAtUtc >= DateTimeOffset.UtcNow - TimeSpan.FromHours(24)).ToArray();
        if (recent.Length < 2)
        {
            TrendForeground = valid.Length >= 2 && valid[^1].Price > valid[0].Price
                ? Brush(0, 192, 118)
                : valid.Length >= 2 && valid[^1].Price < valid[0].Price
                    ? Brush(246, 70, 93)
                    : Brush(100, 168, 255);
            OnPropertyChanged(nameof(Change24hSort));
            return;
        }

        var first = recent[0].Price!.Value;
        var last = CurrentPriceValue is > 0 ? CurrentPriceValue.Value : recent[^1].Price!.Value;
        var delta = last - first;
        var percent = first > 0 ? delta / (double)first * 100d : 0d;
        Change24hSort = percent;
        Change24h = percent switch
        {
            > 0.0001 => $"▲ {percent:0.#}%",
            < -0.0001 => $"▼ {Math.Abs(percent):0.#}%",
            _ => "0.0%"
        };
        Change24hAmount = delta switch
        {
            > 0 => $"+{delta:N0} R$",
            < 0 => $"-{Math.Abs(delta):N0} R$",
            _ => "0 R$"
        };
        TrendForeground = delta switch
        {
            > 0 => Brush(0, 192, 118),
            < 0 => Brush(246, 70, 93),
            _ => Brush(100, 168, 255)
        };
        OnPropertyChanged(nameof(Change24hSort));
    }

    public void SetThumbnail(string? thumbnailUrl)
    {
        ThumbnailUrl = thumbnailUrl ?? string.Empty;
    }

    private void RaiseComputedFlags()
    {
        OnPropertyChanged(nameof(TargetDistanceSort));
        OnPropertyChanged(nameof(Change24hSort));
        OnPropertyChanged(nameof(LastCheckedSort));
        OnPropertyChanged(nameof(IsNearTarget));
        OnPropertyChanged(nameof(IsTargetHit));
        OnPropertyChanged(nameof(IsStale));
        OnPropertyChanged(nameof(IsIssue));
    }

    private void SetStatus(string text, byte br, byte bg, byte bb, byte fr, byte fg, byte fb)
    {
        Status = text;
        StatusBackground = Brush(br, bg, bb);
        StatusForeground = Brush(fr, fg, fb);
    }

    private static string FormatLocal(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        return local.Date == DateTimeOffset.Now.Date
            ? local.ToString("h:mm tt")
            : local.ToString("MMM d, h:mm tt");
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }
}

public sealed record AlertRow(
    long Id,
    ItemKey ItemKey,
    string ItemName,
    string Event,
    string Price,
    string PreviousPrice,
    string Severity,
    DateTimeOffset CreatedAtUtc)
{
    public string Time => CreatedAtUtc.ToLocalTime().ToString("MMM d, h:mm:ss tt");
    public Brush SeverityBackground => Severity == "High"
        ? new SolidColorBrush(Color.FromRgb(8, 42, 29))
        : new SolidColorBrush(Color.FromRgb(16, 38, 63));
    public Brush SeverityForeground => Severity == "High"
        ? new SolidColorBrush(Color.FromRgb(0, 192, 118))
        : new SolidColorBrush(Color.FromRgb(100, 168, 255));
}

public sealed record HistoryRow(
    ItemKey ItemKey,
    string ItemName,
    string Price,
    string Status,
    DateTimeOffset ObservedAtUtc)
{
    public string Time => ObservedAtUtc.ToLocalTime().ToString("MMM d, h:mm:ss tt");
}

public sealed record RecentActivityRow(string Time, string Item, string Event, string Detail);

public static class DisplayFormatting
{
    public static string Price(long? price) => price is > 0 ? $"{price:N0} R$" : "—";

    public static string PercentageChange(long first, long last)
    {
        if (first <= 0)
        {
            return "—";
        }

        var change = (last - first) / (double)first * 100d;
        return change switch
        {
            > 0 => $"+{change:0.#}%",
            < 0 => $"{change:0.#}%",
            _ => "0%"
        };
    }
}
