namespace RobloxPriceTracker.Gui;

/// <summary>
/// Display-only union of distinct marketplace product kinds. Asset and bundle IDs
/// must never be sent into the same analyzer, tracker, or paper-trading pipeline.
/// </summary>
public sealed class LiveDropRow : INotifyPropertyChanged
{
    private string? _thumbnailUrl;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string? ThumbnailUrl
    {
        get => _thumbnailUrl;
        set
        {
            if (string.Equals(_thumbnailUrl, value, StringComparison.Ordinal)) return;
            _thumbnailUrl = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThumbnailUrl)));
        }
    }
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string Creator { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Category { get; init; } = "";
    public string OriginalPrice { get; init; } = "—";
    public string CatalogQuote { get; init; } = "—";
    public string PriceDisplay { get; init; } = "—";
    public int? QuotedPrice { get; init; }
    public bool PrimaryVerified { get; init; }
    public string ResaleAsk { get; init; } = "—";
    public string Score { get; init; } = "—";
    public string Rap => Official?.RapText ?? "—";
    public string Sales7d => Official?.Sales7dText ?? "—";
    public long? OriginalUnitsLeft =>
        Ugc is { PrimaryMarketVerified: true } ? Ugc.UnitsAvailable :
        Bundle is { PrimaryMarketVerified: true } ? Bundle.OriginalUnitsAvailable : null;
    public string StockLeft => OriginalUnitsLeft is > 0 ? $"{OriginalUnitsLeft:N0} left" : "—";
    public long SortStockLow => OriginalUnitsLeft is > 0 ? OriginalUnitsLeft.Value : long.MaxValue;
    public long SortPriceLow => PriceNumber is > 0 ? PriceNumber.Value : long.MaxValue;
    public long SortPriceHigh => PriceNumber is > 0 ? -PriceNumber.Value : long.MaxValue;
    private long? PriceNumber => Official?.FloorPrice ?? QuotedPrice;
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
    private readonly Dictionary<long, string> _liveBundleImages = new();
    private ICollectionView? _liveDropView;
    private Grid? _liveDropPage;
    private DataGrid? _liveDropGrid;
    private ComboBox? _liveDropSort;
    private ComboBox? _ugcMaximumPrice;
    private ComboBox? _ugcStockFilter;
    private Image? _liveDropPreviewImage;
    private TextBlock? _liveDropPreviewPlaceholder;
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
        ApplyLiveDropSort();
        if (_liveDropGrid is not null) _liveDropGrid.ItemsSource = _liveDropView;
        SetLiveDropScope("UGC");

        _hunterRows.CollectionChanged += (_, _) => QueueLiveDropRebuild();
        _officialRows.CollectionChanged += (_, _) => QueueLiveDropRebuild();

        // Refresh only when the scanner is visible. No new background API polling.
        _liveDropTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        _liveDropTimer.Tick += async (_, _) =>
        {
            if (_liveDropPage?.Visibility != Visibility.Visible) return;
            // Expired stock is removed from the active view even if Roblox is down.
            UpdateLiveDropVisibleRows();
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
        if (_ugcMaximumPrice is not null)
            _ugcMaximumPrice.Visibility = scope == "UGC" ? Visibility.Visible : Visibility.Collapsed;
        if (_ugcStockFilter is not null)
            _ugcStockFilter.Visibility = scope == "UGC" ? Visibility.Visible : Visibility.Collapsed;

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
            _liveDropGrid.Columns.Add(CreateLiveDropAssetColumn());
            if (scope == "Official")
            {
                _liveDropGrid.Columns.Add(LiveDropColumn("RESALE FLOOR", nameof(LiveDropRow.ResaleAsk), 1.1));
                _liveDropGrid.Columns.Add(LiveDropColumn("RAP", nameof(LiveDropRow.Rap), 0.9));
                _liveDropGrid.Columns.Add(LiveDropColumn("7D SALES", nameof(LiveDropRow.Sales7d), 0.85));
            }
            else
            {
                _liveDropGrid.Columns.Add(LiveDropColumn("ORIGINAL PRICE", nameof(LiveDropRow.OriginalPrice), 1.12));
                _liveDropGrid.Columns.Add(LiveDropColumn("STOCK LEFT", nameof(LiveDropRow.StockLeft), 1.08));
            }

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
            var verified = _hunterRows.Count(x => UgcActiveStockPolicy.IsActiveSnapshot(
                x.PrimaryMarketVerified, x.UnitsAvailable, x.Price,
                _hunterLastRefreshUtc, DateTimeOffset.UtcNow));
            var bundles = _liveBundles.Count(x => UgcActiveStockPolicy.IsActiveSnapshot(
                x.PrimaryMarketVerified, x.OriginalUnitsAvailable, x.VerifiedOriginalPrice,
                _liveBundleScanUtc, DateTimeOffset.UtcNow));
            _liveDropStatus.Text = verified + bundles == 0
                ? "No verified in-stock UGC Limiteds found. Sold-out, unknown or expired stock is excluded."
                : $"Verified original-stock listings: {verified + bundles:N0} · " +
                  (last == default ? "Refresh to scan" : $"Last scan {last.ToLocalTime():h:mm tt}") +
                  " · Resale inventory excluded" +
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
            Text = "Only verified, in-stock original UGC Limiteds. Reseller inventory is excluded.",
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
        _liveDropSort = new ComboBox
        {
            Width = 138, Height = 30,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "Official: lowest resale ask. UGC: verified creator original-sale price."
        };
        foreach (var option in new[] { "Best matches", "Lowest price", "Highest price", "Least stock", "Name A–Z" })
            _liveDropSort.Items.Add(option);
        _liveDropSort.SelectedIndex = 0;
        _liveDropSort.SelectionChanged += (_, _) => ApplyLiveDropSort();
        toolbar.Children.Add(_liveDropSort);
        _ugcMaximumPrice = new ComboBox
        {
            Width = 140, Height = 30,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "Maximum verified original creator price. Default: 1,000 R$. Higher prices can be inspected by changing the filter."
        };
        foreach (var option in new[]
        {
            "≤ 500 R$", "≤ 1,000 R$", "≤ 2,500 R$",
            "≤ 5,000 R$", "Any verified price"
        })
            _ugcMaximumPrice.Items.Add(option);
        _ugcMaximumPrice.SelectedIndex = 1;
        _ugcMaximumPrice.SelectionChanged += (_, _) => UpdateLiveDropVisibleRows();
        toolbar.Children.Add(_ugcMaximumPrice);

        _ugcStockFilter = new ComboBox
        {
            Width = 123, Height = 30,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "Remaining ORIGINAL creator units only; not resellers"
        };
        foreach (var option in new[] { "All in stock", "≤ 10 left", "≤ 50 left", "≤ 100 left" })
            _ugcStockFilter.Items.Add(option);
        _ugcStockFilter.SelectedIndex = 0;
        _ugcStockFilter.SelectionChanged += (_, _) => UpdateLiveDropVisibleRows();
        toolbar.Children.Add(_ugcStockFilter);



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
            RowHeight = 62,
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
        var imageFrame = new Border
        {
            Width = 192, Height = 192,
            Background = TerminalPanel,
            BorderBrush = TerminalBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 12),
            ClipToBounds = true
        };
        var imageLayer = new Grid();
        _liveDropPreviewPlaceholder = new TextBlock
        {
            Text = "NO IMAGE",
            FontSize = 10,
            Foreground = TerminalMuted,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        imageLayer.Children.Add(_liveDropPreviewPlaceholder);
        _liveDropPreviewImage = new Image { Stretch = Stretch.Uniform };
        _liveDropPreviewImage.SetBinding(Image.SourceProperty,
            new Binding("SelectedItem.ThumbnailUrl") { Source = _liveDropGrid });
        imageLayer.Children.Add(_liveDropPreviewImage);
        imageFrame.Child = imageLayer;
        details.Children.Add(imageFrame);

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

    private static DataGridTemplateColumn CreateLiveDropAssetColumn()
    {
        var root = new FrameworkElementFactory(typeof(StackPanel));
        root.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        root.SetValue(FrameworkElement.MarginProperty, new Thickness(3, 2, 3, 2));

        var frame = new FrameworkElementFactory(typeof(Border));
        frame.SetValue(FrameworkElement.WidthProperty, 50d);
        frame.SetValue(FrameworkElement.HeightProperty, 50d);
        frame.SetValue(Border.BorderBrushProperty, TerminalBorder);
        frame.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        frame.SetValue(Border.BackgroundProperty, TerminalPanel);
        frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));

        var image = new FrameworkElementFactory(typeof(Image));
        image.SetValue(FrameworkElement.WidthProperty, 48d);
        image.SetValue(FrameworkElement.HeightProperty, 48d);
        image.SetValue(Image.StretchProperty, Stretch.Uniform);
        image.SetBinding(Image.SourceProperty, new Binding(nameof(LiveDropRow.ThumbnailUrl)));
        frame.AppendChild(image);
        root.AppendChild(frame);

        var identity = new FrameworkElementFactory(typeof(StackPanel));
        identity.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 0, 0, 0));
        identity.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(LiveDropRow.Name)));
        name.SetValue(TextBlock.ForegroundProperty, TerminalText);
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        name.SetValue(TextBlock.FontSizeProperty, 10.3d);
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        name.SetValue(FrameworkElement.MaxWidthProperty, 240d);
        identity.AppendChild(name);

        var subtitle = new FrameworkElementFactory(typeof(TextBlock));
        subtitle.SetBinding(TextBlock.TextProperty, new Binding(nameof(LiveDropRow.Identity)));
        subtitle.SetValue(TextBlock.ForegroundProperty, TerminalMuted);
        subtitle.SetValue(TextBlock.FontSizeProperty, 9d);
        subtitle.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 0));
        identity.AppendChild(subtitle);
        root.AppendChild(identity);

        return new DataGridTemplateColumn
        {
            Header = "ITEM",
            CellTemplate = new DataTemplate { VisualTree = root },
            Width = new DataGridLength(2.5, DataGridLengthUnitType.Star),
            MinWidth = 195
        };
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
            ThumbnailUrl = item.ThumbnailUrl,
            OriginalPrice = item.PrimaryMarketVerified ? $"{item.Price:N0} R$" : "—",
            CatalogQuote = item.PrimaryMarketVerified ? "—" : $"{item.Price:N0} R$",
            PriceDisplay = UgcScannerPriceVisibilityPolicy.DisplayQuote(
                item.PrimaryMarketVerified ? (int?)item.Price : null,
                item.PrimaryMarketVerified ? null : (int?)item.Price),
            QuotedPrice = item.Price, PrimaryVerified = item.PrimaryMarketVerified,
            ResaleAsk = item.CurrentResaleFloor is > 0 ? item.CurrentResaleText : "Unavailable",
            Score = item.PrimaryMarketVerified && item.Recommendation != "DATA REVIEW" &&
                    !item.IsExcludedPriceDistortion ? item.ResalePotentialText : "—",
            Status = item.IsExcludedPriceDistortion ? "EXCLUDED"
                : !item.PrimaryMarketVerified ? "CATALOG ONLY"
                : eligible ? "OPPORTUNITY"
                : item.Recommendation,
            Evidence = item.PrimaryMarketVerified
                ? $"Purchasable original stock verified · {item.MaterialText}"
                : "Catalog price is only a quote; original stock not verified and no buy recommendation",
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
        ThumbnailUrl = item.ThumbnailUrl,
        OriginalPrice = "—", // Official resale floors must not become creator prices.
        PriceDisplay = item.FloorText,
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

    private LiveDropRow FromBundle(RobloxAnimationBundleOffer item) => new()
    {
        Id = item.BundleId, Name = item.Name, Creator = item.CreatorName,
        Kind = "Bundle", Category = "Animation Pack",
        ThumbnailUrl = _liveBundleImages.TryGetValue(item.BundleId, out var image) ? image : null,
        OriginalPrice = item.OriginalPriceText,
        CatalogQuote = !item.PrimaryMarketVerified && item.CatalogQuotedPrice is > 0
            ? $"{item.CatalogQuotedPrice:N0} R$" : "—",
        PriceDisplay = UgcScannerPriceVisibilityPolicy.DisplayQuote(
            item.PrimaryMarketVerified ? item.VerifiedOriginalPrice : null,
            !item.PrimaryMarketVerified ? item.CatalogQuotedPrice : null),
        QuotedPrice = item.PrimaryMarketVerified ? item.VerifiedOriginalPrice : item.CatalogQuotedPrice,
        PrimaryVerified = item.PrimaryMarketVerified,
        ResaleAsk = item.LowestResaleAsk is > 0 ? item.ResaleAskText : "Unavailable",
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

        var max = _ugcMaximumPrice?.SelectedIndex switch
        {
            0 => 500,
            1 => 1000,
            2 => 2500,
            3 => 5000,
            4 => (int?)null,
            _ => UgcScannerPriceVisibilityPolicy.DefaultMaximumRobux
        };
        if (!UgcScannerPriceVisibilityPolicy.Include(row.QuotedPrice, row.PrimaryVerified,
            max, _ugcVerifiedOnly?.IsChecked == true))
            return false;
        // Price-distortion exclusions aren't normal shopping results.
        // Research-only Any price mode permits manual inspection.
        if (max is not null && row.Ugc?.IsExcludedPriceDistortion == true)
            return false;

        return filter switch
        {
            "Accessories" => row.Kind == "UGC" && !row.IsEmote,
            "Animation Packs" => row.IsBundle,
            "Emotes" => row.IsEmote,
            "Emissive / PBR" => row.IsVerifiedSpecialMaterial,
            _ => true
        };
    }

    private void ApplyLiveDropSort()
    {
        if (_liveDropView is null) return;
        var option = _liveDropSort?.SelectedItem?.ToString() ?? "Best matches";
        _liveDropView.SortDescriptions.Clear();
        switch (option)
        {
            case "Lowest price":
                _liveDropView.SortDescriptions.Add(new SortDescription(
                    nameof(LiveDropRow.SortPriceLow), ListSortDirection.Ascending));
                break;
            case "Highest price":
                _liveDropView.SortDescriptions.Add(new SortDescription(
                    nameof(LiveDropRow.SortPriceHigh), ListSortDirection.Ascending));
                break;
            case "Name A–Z":
                _liveDropView.SortDescriptions.Add(new SortDescription(
                    nameof(LiveDropRow.Name), ListSortDirection.Ascending));
                break;
            default:
                _liveDropView.SortDescriptions.Add(new SortDescription(
                    nameof(LiveDropRow.OpportunityRank), ListSortDirection.Descending));
                _liveDropView.SortDescriptions.Add(new SortDescription(
                    nameof(LiveDropRow.ScoreRank), ListSortDirection.Descending));
                break;
        }
    }

    private async Task LoadLiveBundleImagesAsync()
    {
        // Fetch at most the already-verified bounded bundle candidates and only
        // when the UGC scanner is active. Never block market results on image failure.
        var missing = _liveBundles.Select(x => x.BundleId)
            .Where(id => !_liveBundleImages.ContainsKey(id)).Distinct().ToArray();
        if (missing.Length == 0) return;
        try
        {
            var images = await _services.ThumbnailService.GetBundleThumbnailUrlsAsync(missing);
            foreach (var pair in images)
                _liveBundleImages[pair.Key] = pair.Value;
            foreach (var row in _liveDropRows.Where(x => x.IsBundle))
                if (images.TryGetValue(row.Id, out var url)) row.ThumbnailUrl = url;
            UpdateLiveDropInspector();
        }
        catch (Exception ex)
        {
            _services.Logger.Info($"Optional bundle thumbnails unavailable: {ex.Message}");
        }
    }

    private void UpdateLiveDropVisibleRows()
    {
        _liveDropView?.Refresh();
        if (_liveDropGrid is not null && _liveDropView is not null)
        {
            var visibleItems = _liveDropView.Cast<object>().ToArray();
            if (_liveDropGrid.SelectedItem is null ||
                !visibleItems.Contains(_liveDropGrid.SelectedItem))
                _liveDropGrid.SelectedItem = visibleItems.FirstOrDefault();
            UpdateLiveDropInspector();
        }
        if (_liveDropCount is not null)
        {
            var visible = _liveDropView?.Cast<object>().Count() ?? 0;
            var available = _liveDropRows.Count(x =>
                LiveDropScopePolicy.Accepts(_liveDropScope, x.Kind) &&
                IsLiveDropOriginalStockCurrent(x));
            _liveDropCount.Text = $"{visible:N0} shown / {available:N0} total";
        }
    }

    private void UpdateLiveDropInspector()
    {
        var row = _liveDropGrid?.SelectedItem as LiveDropRow;
        if (_liveDropInspectorTitle is not null) _liveDropInspectorTitle.Text = row?.Name ?? "Select an item";
        if (_liveDropPreviewPlaceholder is not null)
            _liveDropPreviewPlaceholder.Text = row is null ? "SELECT AN ITEM"
                : string.IsNullOrWhiteSpace(row.ThumbnailUrl) ? "IMAGE UNAVAILABLE" : "";
        if (_liveDropInspectorMeta is not null)
            _liveDropInspectorMeta.Text = row is null ? "" : $"{row.Category} · {row.Identity}\nCreator: {row.Creator}";
        if (_liveDropInspectorPrices is not null)
            _liveDropInspectorPrices.Text = row is null ? ""
                : row.Official is not null
                    ? $"Lowest resale ask: {row.ResaleAsk}\nRAP: {row.Rap}\n7-day sales: {row.Sales7d}\nScore: {row.Score}\n{row.Status}"
                    : $"Original price: {row.OriginalPrice}\nOriginal stock left: {row.StockLeft}\nResale seller ask: {row.ResaleAsk}\nMarket score: {row.Score}\n{row.Status}";
        if (_liveDropInspectorEvidence is not null)
            _liveDropInspectorEvidence.Text = row is null ? "" : $"EVIDENCE\n{row.Evidence}";
        if (_liveDropInspectorReason is not null)
            _liveDropInspectorReason.Text = row is null ? "" : $"DETAILS\n{row.Explanation}";
        if (_liveDropOpenButton is not null) _liveDropOpenButton.IsEnabled = row is not null;
        if (_liveDropAnalyzeButton is not null)
        {
            _liveDropAnalyzeButton.Visibility = row?.IsBundle == true ? Visibility.Collapsed : Visibility.Visible;
            _liveDropAnalyzeButton.IsEnabled = row is not null && !row.IsBundle;
        }
        if (_liveDropPaperButton is not null)
        {
            _liveDropPaperButton.Visibility = row?.Ugc is not null ? Visibility.Visible : Visibility.Collapsed;
            _liveDropPaperButton.IsEnabled = row?.Ugc is { PrimaryMarketVerified: true } && row.IsOpportunity;
        }
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
            var plan = LiveDropScopePolicy.DueSources(scope, force, now,
                _officialLastRefreshUtc, _hunterLastRefreshUtc, _liveBundleScanUtc);
            var scanUgc = plan.Ugc;
            var scanOfficial = plan.Official;
            var scanBundles = plan.Bundles;

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
            if (scope == "UGC" && _liveDropScope == "UGC")
                await LoadLiveBundleImagesAsync();
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
                    _ = Dispatcher.BeginInvoke(DispatcherPriority.Background,
                        new Action(async () => await RefreshLiveDropsAsync(force: false)));
            }
        }
    }
}
