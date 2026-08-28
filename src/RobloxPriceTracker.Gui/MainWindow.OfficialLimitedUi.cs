using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RobloxPriceTracker.Infrastructure;

namespace RobloxPriceTracker.Gui;

public partial class MainWindow
{
    private readonly ObservableCollection<OfficialLimitedRow> _officialRows = new();
    private ICollectionView? _officialMarketView;
    private ICollectionView? _officialHuntView;
    private RadioButton? _huntNav;
    private RadioButton? _officialMarketNav;
    private Grid? _huntPage;
    private Grid? _officialMarketPage;
    private DataGrid? _huntGrid;
    private DataGrid? _officialMarketGrid;
    private TextBox? _huntSearchBox;
    private TextBox? _officialSearchBox;
    private ComboBox? _huntStrategyCombo;
    private ComboBox? _officialFilterCombo;
    private TextBlock? _officialScanStatus;
    private TextBlock? _officialScanCount;
    private TextBlock? _officialEnrichedCount;
    private TextBlock? _officialDealCount;
    private TextBlock? _huntUpdatedText;
    private TextBlock? _officialWarningText;
    private Image? _huntInspectorImage;
    private TextBlock? _huntInspectorTitle;
    private TextBlock? _huntInspectorVerdict;
    private TextBlock? _huntInspectorPrice;
    private TextBlock? _huntInspectorScores;
    private TextBlock? _huntInspectorMarket;
    private TextBlock? _huntInspectorReason;
    private Button? _huntAnalyzeButton;
    private Button? _huntOpenButton;
    private RobloxOfficialLimitedMarketService? _officialLimitedService;
    private DispatcherTimer? _officialLimitedTimer;
    private DateTimeOffset? _officialLastRefreshUtc;
    private bool _officialRefreshInProgress;
    private CancellationTokenSource? _officialCts;

    private void ConfigureOfficialLimitedUi()
    {
        _officialLimitedService = new RobloxOfficialLimitedMarketService(
            _services.HttpClient,
            _services.Logger,
            _services.ResaleDataService,
            Path.Combine(_services.DataDirectory, "official-limited-history.json"));

        if (DashboardNav.Parent is StackPanel navStack)
        {
            _huntNav = CreateTerminalNav("Hunt", HuntNav_Checked);
            _officialMarketNav = CreateTerminalNav("Official Market", OfficialMarketNav_Checked);
            var dashboardIndex = navStack.Children.IndexOf(DashboardNav);
            var insertAt = Math.Clamp(dashboardIndex + 1, 0, navStack.Children.Count);
            navStack.Children.Insert(insertAt, _huntNav);
            navStack.Children.Insert(Math.Min(insertAt + 1, navStack.Children.Count), _officialMarketNav);
        }

        if (DashboardPage.Parent is Grid contentHost)
        {
            _huntPage = BuildOfficialHuntPage();
            _officialMarketPage = BuildOfficialMarketPage();
            contentHost.Children.Add(_huntPage);
            contentHost.Children.Add(_officialMarketPage);
        }

        var marketSource = new CollectionViewSource { Source = _officialRows };
        _officialMarketView = marketSource.View;
        _officialMarketView.Filter = FilterOfficialMarketRow;
        _officialMarketView.SortDescriptions.Add(new SortDescription(nameof(OfficialLimitedRow.HuntScore), ListSortDirection.Descending));
        if (_officialMarketGrid is not null) _officialMarketGrid.ItemsSource = _officialMarketView;

        var huntSource = new CollectionViewSource { Source = _officialRows };
        _officialHuntView = huntSource.View;
        _officialHuntView.Filter = FilterOfficialHuntRow;
        if (_huntGrid is not null) _huntGrid.ItemsSource = _officialHuntView;
        ApplyHuntSort();

        _officialLimitedTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _officialLimitedTimer.Tick += async (_, _) =>
        {
            if (_huntPage?.Visibility == Visibility.Visible || _officialMarketPage?.Visibility == Visibility.Visible)
                await RefreshOfficialLimitedAsync(silent: true);
        };
        _officialLimitedTimer.Start();
    }

    private async void HuntNav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        ShowPage("Hunt");
        if (_officialLastRefreshUtc is null || DateTimeOffset.UtcNow - _officialLastRefreshUtc > TimeSpan.FromMinutes(2))
            await RefreshOfficialLimitedAsync(silent: _officialRows.Count > 0);
    }

    private async void OfficialMarketNav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        ShowPage("OfficialMarket");
        if (_officialLastRefreshUtc is null || DateTimeOffset.UtcNow - _officialLastRefreshUtc > TimeSpan.FromMinutes(2))
            await RefreshOfficialLimitedAsync(silent: _officialRows.Count > 0);
    }

    private void SetOfficialLimitedPageVisibility(string page)
    {
        if (_huntPage is not null) _huntPage.Visibility = page == "Hunt" ? Visibility.Visible : Visibility.Collapsed;
        if (_officialMarketPage is not null) _officialMarketPage.Visibility = page == "OfficialMarket" ? Visibility.Visible : Visibility.Collapsed;
    }

    private Grid BuildOfficialHuntPage()
    {
        var root = MakeOfficialRoot();
        var header = BuildOfficialHeader("OFFICIAL LIMITED HUNT", "Only evidence-backed Roblox Limited opportunities make this board.", true);
        root.Children.Add(header);

        var body = new Grid();
        Grid.SetRow(body, 2);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        root.Children.Add(body);

        var panel = MakePanel();
        body.Children.Add(panel);
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.Child = grid;

        var toolbar = new Grid { Margin = new Thickness(12, 0, 12, 0) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(toolbar);
        var titleStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        toolbar.Children.Add(titleStack);
        titleStack.Children.Add(new TextBlock { Text = "BEST DEALS", Foreground = TerminalText, FontWeight = FontWeights.SemiBold, FontSize = 10.5 });
        _huntUpdatedText = new TextBlock { Text = "Waiting for scan", Foreground = TerminalMuted, FontSize = 8.5, Margin = new Thickness(10, 1, 0, 0) };
        titleStack.Children.Add(_huntUpdatedText);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(controls, 1);
        toolbar.Children.Add(controls);
        _huntSearchBox = new TextBox { Width = 180, ToolTip = "Search official Limited" };
        _huntSearchBox.TextChanged += (_, _) => _officialHuntView?.Refresh();
        controls.Children.Add(_huntSearchBox);
        _huntStrategyCombo = new ComboBox { Width = 126, Margin = new Thickness(7, 0, 0, 0), SelectedIndex = 0 };
        foreach (var value in new[] { "Best", "Fresh", "Value", "Fast Flip", "Stable", "High Upside" }) _huntStrategyCombo.Items.Add(value);
        _huntStrategyCombo.SelectionChanged += (_, _) => { ApplyHuntSort(); _officialHuntView?.Refresh(); };
        controls.Children.Add(_huntStrategyCombo);
        var refresh = MakeOfficialRefreshButton();
        controls.Children.Add(refresh);

        _huntGrid = CreateOfficialGrid();
        _huntGrid.SelectionChanged += (_, _) => UpdateOfficialHuntInspector();
        _huntGrid.MouseDoubleClick += async (_, _) => await AnalyzeSelectedOfficialAsync(_huntGrid);
        Grid.SetRow(_huntGrid, 1);
        grid.Children.Add(_huntGrid);
        AddHuntColumns(_huntGrid);

        var inspector = MakePanel();
        Grid.SetColumn(inspector, 2);
        inspector.Child = BuildOfficialHuntInspector();
        body.Children.Add(inspector);
        return root;
    }

    private Grid BuildOfficialMarketPage()
    {
        var root = MakeOfficialRoot();
        root.Children.Add(BuildOfficialHeader("ROBLOX OFFICIAL LIMITEDS", "Broad Roblox-published Limited browser. Double-click any row for the full Analyzer.", false));

        var panel = MakePanel();
        Grid.SetRow(panel, 2);
        root.Children.Add(panel);
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.Child = grid;

        var toolbar = new Grid { Margin = new Thickness(12, 0, 12, 0) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(toolbar);
        toolbar.Children.Add(new TextBlock { Text = "OFFICIAL MARKET", Foreground = TerminalText, FontWeight = FontWeights.SemiBold, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center });

        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(controls, 1);
        toolbar.Children.Add(controls);
        _officialSearchBox = new TextBox { Width = 220, ToolTip = "Search Roblox Limited" };
        _officialSearchBox.TextChanged += (_, _) => _officialMarketView?.Refresh();
        controls.Children.Add(_officialSearchBox);
        _officialFilterCombo = new ComboBox { Width = 132, Margin = new Thickness(7, 0, 0, 0), SelectedIndex = 0 };
        foreach (var value in new[] { "All official", "Hunt candidates", "Below RAP", "High liquidity", "Price drops", "Enriched only" }) _officialFilterCombo.Items.Add(value);
        _officialFilterCombo.SelectionChanged += (_, _) => _officialMarketView?.Refresh();
        controls.Children.Add(_officialFilterCombo);
        controls.Children.Add(MakeOfficialRefreshButton());

        _officialMarketGrid = CreateOfficialGrid();
        _officialMarketGrid.MouseDoubleClick += async (_, _) => await AnalyzeSelectedOfficialAsync(_officialMarketGrid);
        Grid.SetRow(_officialMarketGrid, 1);
        grid.Children.Add(_officialMarketGrid);
        AddOfficialMarketColumns(_officialMarketGrid);
        return root;
    }

    private Grid MakeOfficialRoot()
    {
        var root = new Grid { Visibility = Visibility.Collapsed, Margin = new Thickness(16, 14, 16, 16), Background = Brushes.Transparent };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(64) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        return root;
    }

    private Grid BuildOfficialHeader(string title, string subtitle, bool hunt)
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(stack);
        stack.Children.Add(new TextBlock { Text = title, Foreground = TerminalText, FontSize = 13, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(new TextBlock { Text = subtitle, Foreground = TerminalMuted, FontSize = 8.7, Margin = new Thickness(0, 5, 0, 0) });

        var metrics = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(metrics, 1);
        header.Children.Add(metrics);
        metrics.Children.Add(MakeSmallLabel("SCANNED"));
        _officialScanCount ??= MakeMetric("0", TerminalText, 11);
        var scanned = new TextBlock { Text = _officialScanCount.Text, Foreground = TerminalText, FontWeight = FontWeights.SemiBold, FontSize = 11, Margin = new Thickness(6, 0, 14, 0) };
        if (!hunt) _officialScanCount = scanned;
        metrics.Children.Add(scanned);
        metrics.Children.Add(MakeSmallLabel(hunt ? "DEALS" : "ENRICHED"));
        var metric = MakeMetric("0", hunt ? TerminalGreen : TerminalBlue, 11);
        metric.Margin = new Thickness(6, 0, 14, 0);
        if (hunt) _officialDealCount = metric; else _officialEnrichedCount = metric;
        metrics.Children.Add(metric);
        _officialScanStatus ??= new TextBlock { Text = "READY", Foreground = TerminalMuted, FontSize = 8.5, VerticalAlignment = VerticalAlignment.Center };
        metrics.Children.Add(_officialScanStatus);
        return header;
    }

    private Button MakeOfficialRefreshButton()
    {
        var button = new Button { Content = "REFRESH", Style = (Style)FindResource("SecondaryButtonStyle"), Margin = new Thickness(7, 0, 0, 0) };
        button.Click += async (_, _) => await RefreshOfficialLimitedAsync(silent: false);
        return button;
    }

    private DataGrid CreateOfficialGrid() => new()
    {
        IsReadOnly = true,
        RowHeight = 52,
        ColumnHeaderHeight = 34,
        BorderThickness = new Thickness(0, 1, 0, 0),
        SelectionMode = DataGridSelectionMode.Single,
        SelectionUnit = DataGridSelectionUnit.FullRow
    };

    private void AddHuntColumns(DataGrid grid)
    {
        grid.Columns.Add(OfficialColumn("ITEM", nameof(OfficialLimitedRow.Name), 2.25));
        grid.Columns.Add(OfficialColumn("FLOOR", nameof(OfficialLimitedRow.FloorText), 0.82));
        grid.Columns.Add(OfficialColumn("RAP", nameof(OfficialLimitedRow.RapText), 0.82));
        grid.Columns.Add(OfficialColumn("DISCOUNT", nameof(OfficialLimitedRow.DiscountText), 0.78));
        grid.Columns.Add(OfficialColumn("7D SALES", nameof(OfficialLimitedRow.Sales7dText), 0.72));
        grid.Columns.Add(OfficialColumn("HUNT", nameof(OfficialLimitedRow.HuntText), 0.62));
        grid.Columns.Add(OfficialColumn("STATUS", nameof(OfficialLimitedRow.Status), 0.92));
        grid.Columns.Add(OfficialColumn("TREND", nameof(OfficialLimitedRow.Trend), 0.72));
    }

    private void AddOfficialMarketColumns(DataGrid grid)
    {
        grid.Columns.Add(OfficialColumn("ITEM", nameof(OfficialLimitedRow.Name), 2.30));
        grid.Columns.Add(OfficialColumn("FLOOR", nameof(OfficialLimitedRow.FloorText), 0.82));
        grid.Columns.Add(OfficialColumn("RAP", nameof(OfficialLimitedRow.RapText), 0.82));
        grid.Columns.Add(OfficialColumn("DISCOUNT", nameof(OfficialLimitedRow.DiscountText), 0.78));
        grid.Columns.Add(OfficialColumn("7D SALES", nameof(OfficialLimitedRow.Sales7dText), 0.72));
        grid.Columns.Add(OfficialColumn("30D SALES", nameof(OfficialLimitedRow.Sales30dText), 0.76));
        grid.Columns.Add(OfficialColumn("HUNT", nameof(OfficialLimitedRow.HuntText), 0.60));
        grid.Columns.Add(OfficialColumn("EVIDENCE", nameof(OfficialLimitedRow.EvidenceScoreText), 0.72));
        grid.Columns.Add(OfficialColumn("STATUS", nameof(OfficialLimitedRow.Status), 0.92));
    }

    private static DataGridTextColumn OfficialColumn(string header, string path, double star) => new()
    {
        Header = header,
        Binding = new Binding(path),
        Width = new DataGridLength(star, DataGridLengthUnitType.Star),
        MinWidth = 64
    };

    private UIElement BuildOfficialHuntInspector()
    {
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var stack = new StackPanel { Margin = new Thickness(15) };
        scroll.Content = stack;
        stack.Children.Add(MakeSmallLabel("SELECTED OPPORTUNITY"));

        var hero = new Grid { Margin = new Thickness(0, 11, 0, 0) };
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        stack.Children.Add(hero);
        var imageBorder = new Border { Width = 64, Height = 64, Background = TerminalPanelRaised, BorderBrush = TerminalBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
        _huntInspectorImage = new Image { Stretch = Stretch.UniformToFill };
        imageBorder.Child = _huntInspectorImage;
        hero.Children.Add(imageBorder);
        var identity = new StackPanel { Margin = new Thickness(9, 2, 0, 0) };
        Grid.SetColumn(identity, 1);
        hero.Children.Add(identity);
        _huntInspectorTitle = new TextBlock { Text = "Select a deal", Foreground = TerminalText, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        identity.Children.Add(_huntInspectorTitle);
        _huntInspectorVerdict = new TextBlock { Text = "Hunt only shows evidence-backed candidates.", Foreground = TerminalMuted, FontSize = 8.5, Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap };
        identity.Children.Add(_huntInspectorVerdict);
        _huntInspectorPrice = new TextBlock { Text = "—", Foreground = TerminalGreen, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 7, 0, 0) };
        identity.Children.Add(_huntInspectorPrice);

        stack.Children.Add(MakeSeparator(16, 13));
        stack.Children.Add(MakeSmallLabel("HUNT MODEL"));
        _huntInspectorScores = OfficialInspectorBlock(stack);
        stack.Children.Add(MakeSeparator(16, 13));
        stack.Children.Add(MakeSmallLabel("MARKET EVIDENCE"));
        _huntInspectorMarket = OfficialInspectorBlock(stack);
        stack.Children.Add(MakeSeparator(16, 13));
        stack.Children.Add(MakeSmallLabel("WHY IT RANKS"));
        _huntInspectorReason = OfficialInspectorBlock(stack);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
        _huntAnalyzeButton = new Button { Content = "ANALYZE", Style = (Style)FindResource("PrimaryButtonStyle"), IsEnabled = false };
        _huntAnalyzeButton.Click += async (_, _) => await AnalyzeSelectedOfficialAsync(_huntGrid);
        buttons.Children.Add(_huntAnalyzeButton);
        _huntOpenButton = new Button { Content = "OPEN ROBLOX", Style = (Style)FindResource("SecondaryButtonStyle"), IsEnabled = false, Margin = new Thickness(7, 0, 0, 0) };
        _huntOpenButton.Click += (_, _) => OpenSelectedOfficialOnRoblox(_huntGrid);
        buttons.Children.Add(_huntOpenButton);
        stack.Children.Add(buttons);
        return scroll;
    }

    private static TextBlock OfficialInspectorBlock(Panel parent)
    {
        var text = new TextBlock { Text = "—", Foreground = TerminalText, FontSize = 9.2, LineHeight = 17, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        parent.Children.Add(text);
        return text;
    }

    private bool FilterOfficialMarketRow(object value)
    {
        if (value is not OfficialLimitedRow row) return false;
        var search = _officialSearchBox?.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(search) && !row.Name.Contains(search, StringComparison.OrdinalIgnoreCase) && !row.AssetId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase)) return false;
        return _officialFilterCombo?.SelectedItem?.ToString() switch
        {
            "Hunt candidates" => row.IsHuntCandidate,
            "Below RAP" => row.DiscountPct is > 0,
            "High liquidity" => row.Sales7d >= 14,
            "Price drops" => row.FloorChangePct is <= -.10,
            "Enriched only" => row.IsEnriched,
            _ => true
        };
    }

    private bool FilterOfficialHuntRow(object value)
    {
        if (value is not OfficialLimitedRow row || !row.IsHuntCandidate) return false;
        var search = _huntSearchBox?.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(search) && !row.Name.Contains(search, StringComparison.OrdinalIgnoreCase) && !row.AssetId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase)) return false;
        return _huntStrategyCombo?.SelectedItem?.ToString() != "Fresh" || row.Status is "NEW DEAL" or "PRICE DROP";
    }

    private void ApplyHuntSort()
    {
        if (_officialHuntView is null) return;
        var property = _huntStrategyCombo?.SelectedItem?.ToString() switch
        {
            "Value" => nameof(OfficialLimitedRow.ValueScore),
            "Fast Flip" => nameof(OfficialLimitedRow.FlipScore),
            "Stable" => nameof(OfficialLimitedRow.StabilityScore),
            "High Upside" => nameof(OfficialLimitedRow.UpsideScore),
            _ => nameof(OfficialLimitedRow.HuntScore)
        };
        _officialHuntView.SortDescriptions.Clear();
        _officialHuntView.SortDescriptions.Add(new SortDescription(property, ListSortDirection.Descending));
        _officialHuntView.SortDescriptions.Add(new SortDescription(nameof(OfficialLimitedRow.EvidenceScore), ListSortDirection.Descending));
    }

    private async Task RefreshOfficialLimitedAsync(bool silent)
    {
        if (_officialLimitedService is null || _officialRefreshInProgress) return;
        _officialRefreshInProgress = true;
        _officialCts?.Cancel();
        _officialCts?.Dispose();
        _officialCts = new CancellationTokenSource();
        var token = _officialCts.Token;
        if (!silent) SetOfficialStatus("SCANNING ROBLOX…", TerminalAmber);

        try
        {
            var scan = await _officialLimitedService.ScanAsync(token);
            var thumbnails = await _services.ThumbnailService.GetAssetThumbnailUrlsAsync(scan.Items.Select(x => x.AssetId), token);
            var selectedAsset = (_huntGrid?.SelectedItem as OfficialLimitedRow)?.AssetId;
            _officialRows.Clear();
            foreach (var item in scan.Items)
            {
                thumbnails.TryGetValue(item.AssetId, out var thumb);
                _officialRows.Add(new OfficialLimitedRow(item, thumb));
            }

            _officialLastRefreshUtc = scan.ObservedAtUtc;
            _officialMarketView?.Refresh();
            _officialHuntView?.Refresh();
            ApplyHuntSort();
            UpdateOfficialCounts(scan);
            if (_huntUpdatedText is not null) _huntUpdatedText.Text = $"Updated {scan.ObservedAtUtc.ToLocalTime():h:mm tt}";
            SetOfficialStatus(scan.Warning is null ? "LIVE" : "PARTIAL", scan.Warning is null ? TerminalGreen : TerminalAmber);
            SetOfficialWarning(scan.Warning);

            if (_huntGrid is not null)
            {
                _huntGrid.SelectedItem = selectedAsset is { } id ? _officialRows.FirstOrDefault(x => x.AssetId == id && x.IsHuntCandidate) : null;
                if (_huntGrid.SelectedItem is null && _officialHuntView?.Cast<object>().FirstOrDefault() is OfficialLimitedRow first) _huntGrid.SelectedItem = first;
            }
            UpdateOfficialHuntInspector();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _services.Logger.Error($"Official Limited scan failed: {ex}");
            SetOfficialStatus("SCAN FAILED", TerminalRed);
            SetOfficialWarning(ex.Message);
        }
        finally
        {
            _officialRefreshInProgress = false;
        }
    }

    private void UpdateOfficialCounts(RobloxOfficialLimitedScanResult scan)
    {
        var deals = _officialRows.Count(x => x.IsHuntCandidate);
        if (_officialScanCount is not null) _officialScanCount.Text = scan.DiscoveredCount.ToString("N0");
        if (_officialEnrichedCount is not null) _officialEnrichedCount.Text = scan.EnrichedCount.ToString("N0");
        if (_officialDealCount is not null) _officialDealCount.Text = deals.ToString("N0");
    }

    private void SetOfficialStatus(string text, Brush brush)
    {
        if (_officialScanStatus is null) return;
        _officialScanStatus.Text = text;
        _officialScanStatus.Foreground = brush;
    }

    private void SetOfficialWarning(string? warning)
    {
        if (_officialWarningText is null && _huntPage is not null)
        {
            _officialWarningText = new TextBlock { Visibility = Visibility.Collapsed, Foreground = TerminalAmber, FontSize = 8.5, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8) };
            Grid.SetRow(_officialWarningText, 2);
            Panel.SetZIndex(_officialWarningText, 9);
            _huntPage.Children.Add(_officialWarningText);
        }
        if (_officialWarningText is null) return;
        _officialWarningText.Text = warning ?? string.Empty;
        _officialWarningText.Visibility = string.IsNullOrWhiteSpace(warning) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateOfficialHuntInspector()
    {
        var row = _huntGrid?.SelectedItem as OfficialLimitedRow;
        if (row is null)
        {
            if (_huntInspectorTitle is not null) _huntInspectorTitle.Text = "Select a deal";
            if (_huntInspectorVerdict is not null) _huntInspectorVerdict.Text = "Hunt only shows evidence-backed candidates.";
            if (_huntInspectorPrice is not null) _huntInspectorPrice.Text = "—";
            if (_huntInspectorScores is not null) _huntInspectorScores.Text = "—";
            if (_huntInspectorMarket is not null) _huntInspectorMarket.Text = "—";
            if (_huntInspectorReason is not null) _huntInspectorReason.Text = "—";
            if (_huntInspectorImage is not null) _huntInspectorImage.Source = null;
            if (_huntAnalyzeButton is not null) _huntAnalyzeButton.IsEnabled = false;
            if (_huntOpenButton is not null) _huntOpenButton.IsEnabled = false;
            return;
        }

        if (_huntInspectorTitle is not null) _huntInspectorTitle.Text = row.Name;
        if (_huntInspectorVerdict is not null)
        {
            _huntInspectorVerdict.Text = $"{row.Status} · Asset {row.AssetId}";
            _huntInspectorVerdict.Foreground = row.HuntScore >= 80 ? TerminalGreen : row.HuntScore >= 70 ? TerminalBlue : TerminalAmber;
        }
        if (_huntInspectorPrice is not null) _huntInspectorPrice.Text = row.FloorText;
        if (_huntInspectorScores is not null)
            _huntInspectorScores.Text = $"Hunt       {row.HuntScore}/100\nValue      {row.ValueScore}/100\nFast Flip  {row.FlipScore}/100\nStability  {row.StabilityScore}/100\nEvidence   {row.EvidenceScore}/100\nRisk       {row.RiskScore}/100";
        if (_huntInspectorMarket is not null)
            _huntInspectorMarket.Text = $"Floor      {row.FloorText}\nRAP        {row.RapText}\nFair value {row.FairValueText}\nBuy zone   {row.BuyZoneText}\nDiscount   {row.DiscountText}\nSales 7d   {row.Sales7dText}\nSales 30d  {row.Sales30dText}\nTrend      {row.Trend}\n{row.EvidenceText}";
        if (_huntInspectorReason is not null) _huntInspectorReason.Text = row.ReasonText;
        if (_huntInspectorImage is not null) _huntInspectorImage.Source = MakeOfficialBitmap(row.ThumbnailUrl);
        if (_huntAnalyzeButton is not null) _huntAnalyzeButton.IsEnabled = true;
        if (_huntOpenButton is not null) _huntOpenButton.IsEnabled = true;
    }

    private async Task AnalyzeSelectedOfficialAsync(DataGrid? grid)
    {
        if (grid?.SelectedItem is not OfficialLimitedRow row || _analyzerInput is null) return;
        _analyzerInput.Text = row.AssetId.ToString();
        if (_analyzerNav is not null) _analyzerNav.IsChecked = true;
        ShowPage("Analyzer");
        await AnalyzeCurrentInputAsync();
    }

    private static BitmapImage? MakeOfficialBitmap(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = uri;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    private void OpenSelectedOfficialOnRoblox(DataGrid? grid)
    {
        if (grid?.SelectedItem is not OfficialLimitedRow row) return;
        try
        {
            Process.Start(new ProcessStartInfo($"https://www.roblox.com/catalog/{row.AssetId}") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _services.Logger.Info($"Could not open Roblox asset {row.AssetId}: {ex.Message}");
        }
    }
}

public sealed class OfficialLimitedRow
{
    public OfficialLimitedRow(RobloxOfficialLimitedMarketItem item, string? thumbnailUrl)
    {
        AssetId = item.AssetId;
        Name = item.Name;
        FloorPrice = item.FloorPrice;
        Rap = item.Rap;
        Sales7d = item.Sales7d;
        Sales30d = item.Sales30d;
        FairValue = item.FairValue;
        DiscountPct = item.DiscountPct;
        HuntScore = item.HuntScore;
        ValueScore = item.ValueScore;
        FlipScore = item.FlipScore;
        StabilityScore = item.StabilityScore;
        EvidenceScore = item.EvidenceScore;
        UpsideScore = item.UpsideScore;
        RiskScore = item.RiskScore;
        Status = item.Status;
        Trend = item.Trend;
        IsHuntCandidate = item.IsHuntCandidate;
        IsFresh = item.IsFresh;
        FloorChangePct = item.FloorChangePct;
        IsEnriched = item.IsEnriched;
        EvidenceText = item.EvidenceText;
        ReasonText = item.ReasonText;
        ThumbnailUrl = thumbnailUrl;
    }

    public long AssetId { get; }
    public string Name { get; }
    public long? FloorPrice { get; }
    public double? Rap { get; }
    public double Sales7d { get; }
    public double Sales30d { get; }
    public double? FairValue { get; }
    public double? DiscountPct { get; }
    public int HuntScore { get; }
    public int ValueScore { get; }
    public int FlipScore { get; }
    public int StabilityScore { get; }
    public int EvidenceScore { get; }
    public int UpsideScore { get; }
    public int RiskScore { get; }
    public string Status { get; }
    public string Trend { get; }
    public bool IsHuntCandidate { get; }
    public bool IsFresh { get; }
    public double? FloorChangePct { get; }
    public bool IsEnriched { get; }
    public string EvidenceText { get; }
    public string ReasonText { get; }
    public string? ThumbnailUrl { get; }
    public string FloorText => FloorPrice is > 0 ? $"{FloorPrice.Value:N0} R$" : "—";
    public string RapText => Rap is > 0 ? $"{Rap.Value:N0} R$" : "—";
    public string FairValueText => FairValue is > 0 ? $"{FairValue.Value:N0} R$" : "—";
    public string BuyZoneText => FairValue is > 0 ? $"≤ {Math.Floor(FairValue.Value * .88):N0} R$" : "—";
    public string DiscountText => DiscountPct is { } value ? value.ToString("+0%;-0%;0%") : "—";
    public string Sales7dText => IsEnriched ? Sales7d.ToString("0.#") : "—";
    public string Sales30dText => IsEnriched ? Sales30d.ToString("0.#") : "—";
    public string HuntText => IsEnriched ? HuntScore.ToString() : "—";
    public string EvidenceScoreText => IsEnriched ? EvidenceScore.ToString() : "PENDING";
}
