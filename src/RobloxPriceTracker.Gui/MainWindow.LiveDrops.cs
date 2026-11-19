namespace RobloxPriceTracker.Gui;

/// <summary>
/// Display-only union of distinct marketplace product kinds. Asset and bundle IDs
/// must never be sent into the same analyzer, tracker, or paper-trading pipeline.
/// </summary>
public sealed class LiveDropRow
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string Creator { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Category { get; init; } = "";
    public string OriginalPrice { get; init; } = "—";
    public string ResaleAsk { get; init; } = "—";
    public string Score { get; init; } = "—";
    public string Rap => Official?.RapText ?? "—";
    public string Sales7d => Official?.Sales7dText ?? "—";
    public string StockLeft => Ugc?.RemainingText ?? Bundle?.OriginalStockText ?? "—";
    public string Status { get; init; } = "RESEARCH";
    public string Evidence { get; init; } = "";
    public string Explanation { get; init; } = "";
    public string RobloxUrl { get; init; } = "";
    public bool IsOpportunity { get; init; }
    public bool IsEmote { get; init; }
    public bool IsVerifiedSpecialMaterial { get; init; }
    public int ScoreRank { get; init; }
    public int OpportunityRank => IsOpportunity ? 1 : 0;
    public UgcHunterItem? Ugc { get; init; }
    public OfficialLimitedRow? Official { get; init; }
    public RobloxAnimationBundleOffer? Bundle { get; init; }
    public string Identity => Bundle is null ? $"Asset {Id}" : $"Bundle {Id}";
    public bool IsBundle => Bundle is not null;
}

public partial class MainWindow
{
    private readonly ObservableCollection<LiveDropRow> _liveDropRows = new();
    private readonly List<RobloxAnimationBundleOffer> _liveBundles = new();
    private ICollectionView? _liveDropView;
    private Grid? _liveDropPage;
    private DataGrid? _liveDropGrid;
    private ComboBox? _liveDropTypeFilter;
    private TextBox? _liveDropSearch;
    private CheckBox? _liveDropOnlyOpportunities;
    private TextBlock? _liveDropStatus;
    private TextBlock? _liveDropCount;
    private TextBlock? _liveDropInspectorTitle;
    private TextBlock? _liveDropInspectorMeta;
    private TextBlock? _liveDropInspectorPrices;
    private TextBlock? _liveDropInspectorEvidence;
    private TextBlock? _liveDropInspectorReason;
    private Button? _liveDropOpenButton;
    private Button? _liveDropAnalyzeButton;
    private Button? _liveDropPaperButton;
    private RobloxAnimationBundleResearchService? _liveBundleResearch;
    private DateTimeOffset? _liveBundleScanUtc;
    private DispatcherTimer? _liveDropTimer;
    private bool _liveDropBusy;
    private bool _liveDropRebuildQueued;
    private bool _liveDropSwitchPending;
    private string _liveDropScope = "UGC";
    private TextBlock? _liveDropHeading;
    private string? _liveBundleScanError;

    private void ConfigureLiveDropUi()
    {
        if (DashboardPage.Parent is not Grid host) return;
        _liveBundleResearch = new RobloxAnimationBundleResearchService(_services.HttpClient, _services.Logger);
        _liveDropPage = BuildLiveDropPage();
        host.Children.Add(_liveDropPage);

        _liveDropView = CollectionViewSource.GetDefaultView(_liveDropRows);
        _liveDropView.Filter = FilterLiveDropRow;
        _liveDropView.SortDescriptions.Add(new SortDescription(nameof(LiveDropRow.OpportunityRank),
            ListSortDirection.Descending));
        _liveDropView.SortDescriptions.Add(new SortDescription(nameof(LiveDropRow.ScoreRank),
            ListSortDirection.Descending));
        if (_liveDropGrid is not null) _liveDropGrid.ItemsSource = _liveDropView;
        SetLiveDropScope("UGC");

        _hunterRows.CollectionChanged += (_, _) => QueueLiveDropRebuild();
        _officialRows.CollectionChanged += (_, _) => QueueLiveDropRebuild();

        // Refresh only when the scanner is visible. No new background API polling.
        _liveDropTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
        _liveDropTimer.Tick += async (_, _) =>
        {
            if (_liveDropPage?.Visibility == Visibility.Visible)
                await RefreshLiveDropsAsync(force: false);
        };
        _liveDropTimer.Start();
    }

    private void SetLiveDropPageVisibility(string page)
    {
        if (_liveDropPage is not null)
            _liveDropPage.Visibility = page is "UgcDrops" or "OfficialDrops"
                ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetLiveDropScope(string scope)
    {
        if (scope is not ("UGC" or "Official")) return;
        _liveDropScope = scope;
        if (_liveDropHeading is not null)
            _liveDropHeading.Text = scope == "Official" ? "OFFICIAL LIMITEDS" : "UGC LIMITEDS";
        if (_liveDropOnlyOpportunities is not null)
            _liveDropOnlyOpportunities.IsChecked = false;

        if (_liveDropSearch is not null)
        {
            _liveDropSearch.Text = "";
            _liveDropSearch.ToolTip = scope == "Official"
                ? "Search Roblox-published Limited name or asset ID"
                : "Search UGC item, creator, asset or bundle ID";
        }
        if (_liveDropTypeFilter is not null)
        {
            _liveDropTypeFilter.Items.Clear();
            var options = scope == "Official"
                ? new[] { "All Official", "Below RAP", "High liquidity", "Price drops" }
                : new[] { "All UGC", "Accessories", "Emotes", "Animation Packs", "Emissive / PBR" };
            foreach (var item in options) _liveDropTypeFilter.Items.Add(item);
            _liveDropTypeFilter.SelectedIndex = 0;
        }
        if (_liveDropGrid is not null)
        {
            _liveDropGrid.Columns.Clear();
            _liveDropGrid.Columns.Add(LiveDropColumn("ITEM", nameof(LiveDropRow.Name), 2.5));
            if (scope == "Official")
            {
                _liveDropGrid.Columns.Add(LiveDropColumn("RESALE FLOOR", nameof(LiveDropRow.ResaleAsk), 1.1));
                _liveDropGrid.Columns.Add(LiveDropColumn("RAP", nameof(LiveDropRow.Rap), 0.9));
                _liveDropGrid.Columns.Add(LiveDropColumn("7D SALES", nameof(LiveDropRow.Sales7d), 0.85));
            }
            else
            {
                _liveDropGrid.Columns.Add(LiveDropColumn("TYPE", nameof(LiveDropRow.Category), 1.1));
                _liveDropGrid.Columns.Add(LiveDropColumn("ORIGINAL", nameof(LiveDropRow.OriginalPrice), 0.95));
                _liveDropGrid.Columns.Add(LiveDropColumn("RESALE ASK", nameof(LiveDropRow.ResaleAsk), 1.05));
            }
            _liveDropGrid.Columns.Add(LiveDropColumn("SCORE", nameof(LiveDropRow.Score), 0.65));
            _liveDropGrid.Columns.Add(LiveDropColumn("STATUS", nameof(LiveDropRow.Status), 1.3));
            _liveDropGrid.SelectedItem = null;
        }
        UpdateLiveDropVisibleRows();
        if (_liveDropGrid is not null)
            _liveDropGrid.SelectedItem = _liveDropView?.Cast<object>().FirstOrDefault();
        UpdateLiveDropInspector();
        UpdateLiveDropScopeStatus();
    }

    private void UpdateLiveDropScopeStatus()
    {
        if (_liveDropStatus is null) return;
        if (_liveDropScope == "Official")
        {
            _liveDropStatus.Text = _officialLastRefreshUtc is { } last
                ? $"Roblox-published Limiteds · Updated {last.ToLocalTime():h:mm tt} · {_officialRows.Count:N0} items"
                : "Roblox-published Limiteds · Refresh to load resale prices, RAP and sales";
        }
        else
        {
            var last = new[] { _hunterLastRefreshUtc, _liveBundleScanUtc }
                .Where(x => x.HasValue).Select(x => x!.Value).DefaultIfEmpty().Max();
            _liveDropStatus.Text = $"UGC {_hunterRows.Count:N0} · Animation packs {_liveBundles.Count:N0} · " +
                (last == default ? "Refresh to scan" : $"Updated {last.ToLocalTime():h:mm tt}") +
                " · Bundles are research-only" +
                (string.IsNullOrWhiteSpace(_liveBundleScanError) ? "" : " · Animation scan incomplete");
        }
    }

    private Grid BuildLiveDropPage()
    {
        var root = new Grid
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(17, 13, 17, 16),
            Background = Brushes.Transparent
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        _liveDropHeading = new TextBlock
        {
            Text = "UGC LIMITEDS",
            Foreground = TerminalText,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        };
        heading.Children.Add(_liveDropHeading);
        _liveDropStatus = new TextBlock
        {
            Text = "UGC accessories, emotes and Limited animation packs. Original stock is not resale.",
            Foreground = TerminalMuted,
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 0)
        };
        heading.Children.Add(_liveDropStatus);
        root.Children.Add(heading);

        var toolbar = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 12),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(toolbar, 1);
        root.Children.Add(toolbar);

        _liveDropSearch = new TextBox
        {
            Width = 206,
            Height = 30,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Search item name, creator, or asset/bundle ID"
        };
        _liveDropSearch.TextChanged += (_, _) => UpdateLiveDropVisibleRows();
        toolbar.Children.Add(_liveDropSearch);

        _liveDropTypeFilter = new ComboBox
        {
            Width = 172,
            Height = 30,
            Margin = new Thickness(8, 0, 0, 0),
            SelectedIndex = 0
        };
        // Source-specific options are set by SetLiveDropScope. Official and UGC
        // products must never appear in the same result list.
        _liveDropTypeFilter.SelectionChanged += (_, _) => UpdateLiveDropVisibleRows();
        toolbar.Children.Add(_liveDropTypeFilter);

        _liveDropOnlyOpportunities = new CheckBox
        {
            Content = "Opportunities only",
            Foreground = TerminalText,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            ToolTip = "Requires independent investment evidence. Research-only bundles are excluded."
        };
        _liveDropOnlyOpportunities.Checked += (_, _) => UpdateLiveDropVisibleRows();
        _liveDropOnlyOpportunities.Unchecked += (_, _) => UpdateLiveDropVisibleRows();
        toolbar.Children.Add(_liveDropOnlyOpportunities);

        var refresh = new Button
        {
            Content = "REFRESH",
            Style = (Style)FindResource("SecondaryButtonStyle"),
            Margin = new Thickness(12, 0, 0, 0),
            Padding = new Thickness(12, 5, 12, 5)
        };
        refresh.Click += async (_, _) => await RefreshLiveDropsAsync(force: true);
        toolbar.Children.Add(refresh);

        _liveDropCount = new TextBlock
        {
            Text = "0 items",
            Foreground = TerminalMuted,
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        toolbar.Children.Add(_liveDropCount);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(315) });
        Grid.SetRow(body, 2);
        root.Children.Add(body);

        var tableBorder = MakePanel();
        body.Children.Add(tableBorder);
        _liveDropGrid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            EnableRowVirtualization = true,
            RowHeight = 40,
            Background = Brushes.Transparent,
            Foreground = TerminalText,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            SelectionMode = DataGridSelectionMode.Single
        };
        // Columns are configured separately for each market by SetLiveDropScope.
        _liveDropGrid.SelectionChanged += (_, _) => UpdateLiveDropInspector();
        _liveDropGrid.MouseDoubleClick += (_, _) => OpenLiveDropOnRoblox();
        tableBorder.Child = _liveDropGrid;

        var inspector = MakePanel();
        Grid.SetColumn(inspector, 2);
        body.Children.Add(inspector);
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        inspector.Child = scroll;
        var details = new StackPanel { Margin = new Thickness(15) };
        scroll.Content = details;
        details.Children.Add(new TextBlock
        {
            Text = "ITEM DETAILS",
            Foreground = TerminalMuted,
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        });
        _liveDropInspectorTitle = LiveDropDetail(details, 15, true);
        _liveDropInspectorTitle.Text = "Select an item";
        _liveDropInspectorMeta = LiveDropDetail(details, 10);
        _liveDropInspectorPrices = LiveDropDetail(details, 11);
        _liveDropInspectorEvidence = LiveDropDetail(details, 10);
        _liveDropInspectorReason = LiveDropDetail(details, 10);

        _liveDropOpenButton = new Button
        {
            Content = "OPEN ON ROBLOX",
            Style = (Style)FindResource("SecondaryButtonStyle"),
            IsEnabled = false,
            Margin = new Thickness(0, 16, 0, 0)
        };
        _liveDropOpenButton.Click += (_, _) => OpenLiveDropOnRoblox();
        details.Children.Add(_liveDropOpenButton);

        _liveDropAnalyzeButton = new Button
        {
            Content = "ANALYZE ASSET",
            Style = (Style)FindResource("SecondaryButtonStyle"),
            IsEnabled = false,
            Margin = new Thickness(0, 8, 0, 0)
        };
        _liveDropAnalyzeButton.Click += async (_, _) => await AnalyzeLiveDropAsync();
        details.Children.Add(_liveDropAnalyzeButton);

        _liveDropPaperButton = new Button
        {
            Content = "PAPER ENTRY",
            Style = (Style)FindResource("SecondaryButtonStyle"),
            IsEnabled = false,
            Margin = new Thickness(0, 8, 0, 0)
        };
        _liveDropPaperButton.Click += async (_, _) => await PaperEnterLiveDropAsync();
        details.Children.Add(_liveDropPaperButton);

        return root;
    }

    private static DataGridTextColumn LiveDropColumn(string title, string path, double proportion) => new()
    {
        Header = title,
        Binding = new Binding(path),
        Width = new DataGridLength(proportion, DataGridLengthUnitType.Star),
        MinWidth = title == "ITEM" ? 170 : 65
    };

    private static TextBlock LiveDropDetail(Panel panel, double size, bool bold = false)
    {
        var block = new TextBlock
        {
            Text = "—",
            Foreground = TerminalText,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            FontSize = size,
            LineHeight = Math.Max(size + 5, 16),
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        panel.Children.Add(block);
        return block;
    }

    private void QueueLiveDropRebuild()
    {
        if (_liveDropRebuildQueued) return;
        _liveDropRebuildQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _liveDropRebuildQueued = false;
            RebuildLiveDropRows();
        }));
    }

    private void RebuildLiveDropRows()
    {
        var prior = _liveDropGrid?.SelectedItem as LiveDropRow;
        var all = _hunterRows.Select(FromUgc)
            .Concat(_officialRows.Select(FromOfficial))
            .Concat(_liveBundles.Select(FromBundle))
            .ToArray();
        _liveDropRows.Clear();
        foreach (var row in all) _liveDropRows.Add(row);
        UpdateLiveDropVisibleRows();
        if (_liveDropGrid is not null)
        {
            if (prior is not null)
                _liveDropGrid.SelectedItem = all.FirstOrDefault(x => x.Id == prior.Id && x.Kind == prior.Kind);
            if (_liveDropGrid.SelectedItem is null)
                _liveDropGrid.SelectedItem = _liveDropView?.Cast<object>().FirstOrDefault();
        }
        UpdateLiveDropInspector();
    }

    private static LiveDropRow FromUgc(UgcHunterItem item)
    {
        // Reuse the same investment gates as the overview board; don't invent
        // a second and potentially contradictory definition of a qualified deal.
        var eligible = IsUgcBoardCandidate(item);
        return new LiveDropRow
        {
            Id = item.AssetId, Name = item.Name, Creator = item.CreatorName,
            Kind = "UGC",
            Category = item.AssetType == 61 ? "Emote"
                : item.IsEmissive == true ? "Emissive"
                : item.IsPbr == true ? "PBR"
                : item.Category,
            IsEmote = item.AssetType == 61,
            IsVerifiedSpecialMaterial = item.IsEmissive == true || item.IsPbr == true,
            OriginalPrice = item.PrimaryMarketVerified ? $"{item.Price:N0} R$" : "—",
            ResaleAsk = item.CurrentResaleText,
            Score = item.PrimaryMarketVerified && item.Recommendation != "DATA REVIEW" &&
                    !item.IsExcludedPriceDistortion ? item.ResalePotentialText : "—",
            Status = item.IsExcludedPriceDistortion ? "EXCLUDED"
                : !item.PrimaryMarketVerified ? "PRICE UNVERIFIED"
                : eligible ? "OPPORTUNITY"
                : item.Recommendation,
            Evidence = item.PrimaryMarketVerified
                ? $"Original stock verified · {item.MaterialText}"
                : "Original stock unverified · no automatic buy recommendation",
            Explanation = $"{item.PriceIntegrityText}\n{item.PriceIntegrityExplanation}\n" +
                string.Join("\n", item.Reasons.Take(2)),
            RobloxUrl = $"https://www.roblox.com/catalog/{item.AssetId}",
            Ugc = item, IsOpportunity = eligible,
            ScoreRank = eligible ? (int)item.ResalePotentialScore : 0
        };
    }

    private static LiveDropRow FromOfficial(OfficialLimitedRow item) => new()
    {
        Id = item.AssetId, Name = item.Name, Creator = "Roblox",
        Kind = "Official", Category = "Roblox Limited",
        OriginalPrice = "—", // Original creator price is NOT the resale floor.
        ResaleAsk = item.FloorText,
        Score = item.IsEnriched ? item.HuntText : "—",
        Status = item.IsHuntCandidate ? "OPPORTUNITY" : item.Status,
        Evidence = item.IsEnriched
            ? $"Resale floor {item.FloorText} · RAP {item.RapText} · 7d sales {item.Sales7dText}"
            : "Resale data incomplete",
        Explanation = item.ReasonText,
        RobloxUrl = $"https://www.roblox.com/catalog/{item.AssetId}",
        Official = item,
        IsOpportunity = item.IsHuntCandidate && item.IsEnriched,
        ScoreRank = item.IsHuntCandidate ? item.HuntScore : 0
    };

    private static LiveDropRow FromBundle(RobloxAnimationBundleOffer item) => new()
    {
        Id = item.BundleId, Name = item.Name, Creator = item.CreatorName,
        Kind = "Bundle", Category = "Animation Pack",
        OriginalPrice = item.OriginalPriceText,
        ResaleAsk = item.ResaleAskText,
        Score = "—", Status = "RESEARCH ONLY",
        Evidence = item.VerificationStatus,
        Explanation = "Limited animation bundle. Creator original-stock prices require Marketplace verification. " +
            "No modeled resale score or paper trading until independent resale economics are validated.",
        RobloxUrl = item.RobloxUrl,
        Bundle = item, IsOpportunity = false
    };

    private bool FilterLiveDropRow(object value)
    {
        if (value is not LiveDropRow row ||
            !LiveDropScopePolicy.Accepts(_liveDropScope, row.Kind)) return false;
        var query = _liveDropSearch?.Text.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(query) &&
            !row.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
            !row.Creator.Contains(query, StringComparison.OrdinalIgnoreCase) &&
            !row.Id.ToString(CultureInfo.InvariantCulture).Contains(query, StringComparison.Ordinal))
            return false;

        if (_liveDropOnlyOpportunities?.IsChecked == true && !row.IsOpportunity)
            return false;

        var filter = _liveDropTypeFilter?.SelectedItem?.ToString();
        if (_liveDropScope == "Official")
        {
            return filter switch
            {
                "Below RAP" => row.Official?.DiscountPct is > 0,
                "High liquidity" => row.Official?.Sales7d is >= 14,
                "Price drops" => row.Official?.FloorChangePct is <= -0.10,
                _ => true
            };
        }

        return filter switch
        {
            "Accessories" => row.Kind == "UGC" && !row.IsEmote,
            "Animation Packs" => row.IsBundle,
            "Emotes" => row.IsEmote,
            "Emissive / PBR" => row.IsVerifiedSpecialMaterial,
            _ => true
        };
    }

    private void UpdateLiveDropVisibleRows()
    {
        _liveDropView?.Refresh();
        if (_liveDropCount is not null)
        {
            var visible = _liveDropView?.Cast<object>().Count() ?? 0;
            var available = _liveDropRows.Count(x =>
                LiveDropScopePolicy.Accepts(_liveDropScope, x.Kind));
            _liveDropCount.Text = $"{visible:N0} shown / {available:N0} total";
        }
    }

    private void UpdateLiveDropInspector()
    {
        var row = _liveDropGrid?.SelectedItem as LiveDropRow;
        if (_liveDropInspectorTitle is not null) _liveDropInspectorTitle.Text = row?.Name ?? "Select an item";
        if (_liveDropInspectorMeta is not null)
            _liveDropInspectorMeta.Text = row is null ? "" : $"{row.Category} · {row.Identity}\nCreator: {row.Creator}";
        if (_liveDropInspectorPrices is not null)
            _liveDropInspectorPrices.Text = row is null ? ""
                : $"Original creator stock: {row.OriginalPrice}\nResale asking floor: {row.ResaleAsk}\nScore: {row.Score}\nStatus: {row.Status}";
        if (_liveDropInspectorEvidence is not null)
            _liveDropInspectorEvidence.Text = row is null ? "" : $"EVIDENCE\n{row.Evidence}";
        if (_liveDropInspectorReason is not null)
            _liveDropInspectorReason.Text = row is null ? "" : $"DETAILS\n{row.Explanation}";
        if (_liveDropOpenButton is not null) _liveDropOpenButton.IsEnabled = row is not null;
        if (_liveDropAnalyzeButton is not null) _liveDropAnalyzeButton.IsEnabled = row is not null && !row.IsBundle;
        if (_liveDropPaperButton is not null)
            _liveDropPaperButton.IsEnabled = row?.Ugc is { PrimaryMarketVerified: true } && row.IsOpportunity;
    }

    private void OpenLiveDropOnRoblox()
    {
        if (_liveDropGrid?.SelectedItem is not LiveDropRow row) return;
        try { Process.Start(new ProcessStartInfo(row.RobloxUrl) { UseShellExecute = true }); }
        catch (Exception ex) { _services.Logger.Info($"Unable to open {row.Identity}: {ex.Message}"); }
    }

    private async Task AnalyzeLiveDropAsync()
    {
        if (_liveDropGrid?.SelectedItem is not LiveDropRow { IsBundle: false } row ||
            _analyzerInput is null) return;
        _stockReturnPage = _liveDropScope == "Official" ? "OfficialDrops" : "UgcDrops";
        _stockReturnCapturedAtUtc = DateTimeOffset.UtcNow;
        _analyzerInput.Text = row.Id.ToString(CultureInfo.InvariantCulture);
        await NavigateCleanAsync("Analyzer");
        await AnalyzeCurrentInputAsync();
    }

    private async Task PaperEnterLiveDropAsync()
    {
        if (_liveDropGrid?.SelectedItem is not LiveDropRow row || !row.IsOpportunity ||
            row.Ugc is not { PrimaryMarketVerified: true } item ||
            item.IsExcludedPriceDistortion || item.Recommendation == "DATA REVIEW") return;
        await _services.PaperPortfolioStore.AddAsync(item, 1);
        ShowBanner("Paper entry recorded", $"1 × {item.Name} at {item.Price:N0} R$. No Robux spent.");
        await RefreshPaperPortfolioAsync();
    }

    private async Task OpenLiveDropTypeAsync(string filter)
    {
        // Existing Overview shortcuts lead to distinct screens, not a combined feed.
        await NavigateCleanAsync(filter == "Roblox Limiteds" ? "OfficialDrops" : "UgcDrops");
    }

    private async Task RefreshLiveDropsAsync(bool force)
    {
        if (_liveDropBusy)
        {
            // Switching tabs during a pending scan must still refresh the newly
            // selected source as soon as the previous request finishes.
            _liveDropSwitchPending = true;
            return;
        }
        _liveDropBusy = true;
        var scope = _liveDropScope;
        if (_liveDropStatus is not null)
            _liveDropStatus.Text = scope == "Official"
                ? "Refreshing Roblox Official Limiteds…"
                : "Refreshing UGC Limiteds and animation packs…";
        var errors = new List<string>();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var scanUgc = scope == "UGC" &&
                (force || _hunterLastRefreshUtc is null ||
                 now - _hunterLastRefreshUtc > TimeSpan.FromMinutes(3));
            var scanOfficial = scope == "Official" &&
                (force || _officialLastRefreshUtc is null ||
                 now - _officialLastRefreshUtc > TimeSpan.FromMinutes(5));
            var scanBundles = scope == "UGC" &&
                (force || _liveBundleScanUtc is null ||
                 now - _liveBundleScanUtc > TimeSpan.FromMinutes(5));

            if (scanUgc)
            {
                var last = _hunterLastRefreshUtc;
                await RefreshHunterAsync(silent: true);
                if (last == _hunterLastRefreshUtc) errors.Add("UGC data may be stale");
            }

            if (scanOfficial)
            {
                var last = _officialLastRefreshUtc;
                await RefreshOfficialLimitedAsync(silent: true);
                if (last == _officialLastRefreshUtc) errors.Add("Official data may be stale");
            }

            if (scanBundles && _liveBundleResearch is not null)
            {
                try
                {
                    if (scanUgc) await Task.Delay(350);
                    var found = await _liveBundleResearch.DiscoverAsync();
                    _liveBundles.Clear();
                    _liveBundles.AddRange(found.Where(x => x.LimitedVerified));
                    _liveBundleScanUtc = DateTimeOffset.UtcNow;
                    _liveBundleScanError = null;
                }
                catch (Exception ex)
                {
                    _liveBundleScanError = ex.Message;
                    _services.Logger.Info($"Animation pack scan incomplete; retaining cached bundles: {ex.Message}");
                }
            }

            if (scope == "UGC" && !string.IsNullOrWhiteSpace(_liveBundleScanError))
                errors.Add("Animation packs unavailable (cached results retained)");
            RebuildLiveDropRows();
            if (_liveDropScope == scope)
            {
                UpdateLiveDropScopeStatus();
                if (errors.Count > 0 && _liveDropStatus is not null)
                    _liveDropStatus.Text = "PARTIAL · " + string.Join("; ", errors) +
                        " · " + _liveDropStatus.Text;
            }
        }
        catch (Exception ex)
        {
            _services.Logger.Error($"Live drop refresh failed: {ex}");
            if (_liveDropScope == scope && _liveDropStatus is not null)
                _liveDropStatus.Text = $"Refresh interrupted; showing cached {scope} results. {ex.Message}";
        }
        finally
        {
            _liveDropBusy = false;
            if (_liveDropSwitchPending)
            {
                _liveDropSwitchPending = false;
                if (_liveDropPage?.IsVisible == true)
                    Dispatcher.BeginInvoke(DispatcherPriority.Background,
                        new Action(async () => await RefreshLiveDropsAsync(force: false)));
            }
        }
    }
}
