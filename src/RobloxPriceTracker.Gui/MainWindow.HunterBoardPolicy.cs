using System.Collections.Specialized;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using RobloxPriceTracker.Infrastructure;

namespace RobloxPriceTracker.Gui;

internal static class HunterBoardPolicyBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnMainWindowLoaded));
    }

    private static void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
            window.InitializeHunterBoardPolicy();
    }
}

public partial class MainWindow
{
    private readonly Dictionary<long, HunterPriceAnchorState> _hunterPriceAnchors = new();
    private readonly HashSet<long> _hunterPriceSpikeHidden = new();
    private bool _hunterBoardPolicyInitialized;
    private bool _hunterBoardPolicyRefreshScheduled;

    internal void InitializeHunterBoardPolicy()
    {
        if (_hunterBoardPolicyInitialized) return;
        _hunterBoardPolicyInitialized = true;

        LoadHunterPriceAnchors();
        _hunterRows.CollectionChanged += HunterRows_CollectionChanged;
        if (_hunterView is not null)
            _hunterView.Filter = FilterHunterBoardRow;

        foreach (var item in _hunterRows)
            ObserveHunterPrimaryPrice(item);
        ScheduleHunterBoardPolicyRefresh();
    }

    private bool FilterHunterBoardRow(object item)
    {
        if (!FilterHunterRow(item)) return false;
        return item is not UgcHunterItem row || !_hunterPriceSpikeHidden.Contains(row.AssetId);
    }

    private void HunterRows_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
            _hunterPriceSpikeHidden.Clear();

        if (e.NewItems is not null)
        {
            foreach (var value in e.NewItems)
            {
                if (value is UgcHunterItem item)
                    ObserveHunterPrimaryPrice(item);
            }
        }

        ScheduleHunterBoardPolicyRefresh();
    }

    private void ObserveHunterPrimaryPrice(UgcHunterItem item)
    {
        if (item.Price <= 0) return;
        var now = item.ObservedAtUtc == default ? DateTimeOffset.UtcNow : item.ObservedAtUtc;

        if (!_hunterPriceAnchors.TryGetValue(item.AssetId, out var anchor))
        {
            anchor = new HunterPriceAnchorState(item.AssetId, item.Price, item.Price, now, now);
        }
        else
        {
            var low = Math.Min(anchor.LowestObservedPrice, item.Price);
            anchor = anchor with
            {
                LowestObservedPrice = low,
                LastObservedPrice = item.Price,
                LastObservedAtUtc = now
            };
        }
        _hunterPriceAnchors[item.AssetId] = anchor;

        var wasHidden = _hunterPriceSpikeHidden.Contains(item.AssetId);
        var shouldHide = UgcHunterPrimaryPricePolicy.IsLargeIncrease(item.Price, anchor.LowestObservedPrice);
        if (shouldHide)
        {
            _hunterPriceSpikeHidden.Add(item.AssetId);
            if (!wasHidden)
            {
                _services.Logger.Info(
                    $"UGC Hunter hid asset {item.AssetId} after primary price rose from observed low {anchor.LowestObservedPrice:N0} R$ to {item.Price:N0} R$.");
            }
        }
        else
        {
            _hunterPriceSpikeHidden.Remove(item.AssetId);
        }
    }

    private void LoadHunterPriceAnchors()
    {
        var anchorPath = HunterPriceAnchorPath;
        try
        {
            if (File.Exists(anchorPath))
            {
                var saved = JsonSerializer.Deserialize<List<HunterPriceAnchorState>>(File.ReadAllText(anchorPath))
                    ?? new List<HunterPriceAnchorState>();
                foreach (var state in saved.Where(x => x.AssetId > 0 && x.LowestObservedPrice > 0))
                    _hunterPriceAnchors[state.AssetId] = state;
                return;
            }

            // Seed the new guard from Hunter's existing observation history so an already-observed
            // 95 -> 300 style repricing is caught immediately after upgrading.
            var legacyHistoryPath = Path.Combine(_services.DataDirectory, "ugc-hunter-history.json");
            if (!File.Exists(legacyHistoryPath)) return;
            var history = JsonSerializer.Deserialize<List<UgcHunterObservation>>(File.ReadAllText(legacyHistoryPath))
                ?? new List<UgcHunterObservation>();
            foreach (var group in history.Where(x => x.AssetId > 0 && x.Price > 0).GroupBy(x => x.AssetId))
            {
                var ordered = group.OrderBy(x => x.ObservedAtUtc).ToArray();
                _hunterPriceAnchors[group.Key] = new HunterPriceAnchorState(
                    group.Key,
                    ordered.Min(x => x.Price),
                    ordered[^1].Price,
                    ordered[0].ObservedAtUtc,
                    ordered[^1].ObservedAtUtc);
            }
        }
        catch (Exception ex)
        {
            _services.Logger.Error($"UGC Hunter price anchors could not be loaded: {ex.Message}");
        }
    }

    private void ScheduleHunterBoardPolicyRefresh()
    {
        if (_hunterBoardPolicyRefreshScheduled) return;
        _hunterBoardPolicyRefreshScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _hunterBoardPolicyRefreshScheduled = false;
            _hunterView?.Refresh();
            UpdateHunterVisibleCounts();
            PersistHunterPriceAnchors();
        }));
    }

    private void UpdateHunterVisibleCounts()
    {
        var visible = _hunterRows.Where(x => !_hunterPriceSpikeHidden.Contains(x.AssetId)).ToArray();
        if (_hunterLiveCountText is not null)
            _hunterLiveCountText.Text = visible.Length.ToString("N0");
        if (_hunterStrongCountText is not null)
        {
            var strong = visible.Count(x => x.ResalePotentialScore >= 75 && x.EntryScore >= 55 && x.Recommendation != "AVOID");
            _hunterStrongCountText.Text = strong.ToString("N0");
        }
    }

    private void PersistHunterPriceAnchors()
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(365);
            var states = _hunterPriceAnchors.Values
                .Where(x => x.LastObservedAtUtc >= cutoff)
                .OrderBy(x => x.AssetId)
                .ToArray();
            var path = HunterPriceAnchorPath;
            var temp = path + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(states));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _services.Logger.Error($"UGC Hunter price anchors could not be saved: {ex.Message}");
        }
    }

    private string HunterPriceAnchorPath => Path.Combine(_services.DataDirectory, "ugc-hunter-price-anchors.json");

    private sealed record HunterPriceAnchorState(
        long AssetId,
        int LowestObservedPrice,
        int LastObservedPrice,
        DateTimeOffset FirstObservedAtUtc,
        DateTimeOffset LastObservedAtUtc);
}
