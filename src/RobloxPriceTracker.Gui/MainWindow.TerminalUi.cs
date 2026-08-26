using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RobloxPriceTracker.Gui;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<UgcHunterItem> _hunterRows = new();
    private ICollectionView? _hunterView;
    private RadioButton? _hunterNav;
    private RadioButton? _portfolioNav;
    private Grid? _hunterPage;
    private Grid? _portfolioPage;
    private DataGrid? _hunterGrid;
    private TextBox? _hunterSearchBox;
    private ComboBox? _hunterCategoryCombo;
    private ComboBox? _hunterOpportunityCombo;
    private TextBlock? _hunterRegimeText;
    private TextBlock? _hunterRegimeDetailText;
    private TextBlock? _hunterLiveCountText;
    private TextBlock? _hunterStrongCountText;
    private TextBlock? _hunterUpdatedText;
    private TextBlock? _hunterErrorText;
    private Image? _hunterInspectorImage;
    private TextBlock? _hunterInspectorTitle;
    private TextBlock? _hunterInspectorCreator;
    private TextBlock? _hunterInspectorPrice;
    private TextBlock? _hunterInspectorPhase;
    private TextBlock? _hunterInspectorOpportunity;
    private TextBlock? _hunterInspectorEntry;
    private TextBlock? _hunterInspectorRisk;
    private TextBlock? _hunterInspectorConfidence;
    private TextBlock? _hunterInspectorVelocity;
    private TextBlock? _hunterInspectorEta;
    private TextBlock? _hunterInspectorSupply;
    private TextBlock? _hunterInspectorForecast;
    private TextBlock? _hunterInspectorReasons;
    private TextBlock? _hunterInspectorRisks;
    private Button? _hunterOpenRobloxButton;
    private Button? _hunterTrackButton;
    private DispatcherTimer? _hunterTimer;
    private bool _hunterRefreshInProgress;
    private DateTimeOffset? _hunterLastRefreshUtc;

    private static readonly Brush TerminalGreen = new SolidColorBrush(Color.FromRgb(39, 211, 139));
    private static readonly Brush TerminalRed = new SolidColorBrush(Color.FromRgb(255, 93, 108));
    private static readonly Brush TerminalAmber = new SolidColorBrush(Color.FromRgb(239, 190, 80));
    private static readonly Brush TerminalBlue = new SolidColorBrush(Color.FromRgb(80, 145, 255));
    private static readonly Brush TerminalText = new SolidColorBrush(Color.FromRgb(226, 232, 240));
    private static readonly Brush TerminalMuted = new SolidColorBrush(Color.FromRgb(112, 126, 145));
    private static readonly Brush TerminalPanel = new SolidColorBrush(Color.FromRgb(13, 18, 24));
    private static readonly Brush TerminalPanelRaised = new SolidColorBrush(Color.FromRgb(16, 23, 31));
    private static readonly Brush TerminalBorder = new SolidColorBrush(Color.FromRgb(31, 42, 55));

    private void ConfigureTerminalUi()
    {
        DashboardNav.Content = "Market";
        WatchlistNav.Content = "Tracker";
        ReportsNav.Content = "Research";

        var navStack = DashboardNav.Parent as StackPanel;
        if (navStack is not null)
        {
            _hunterNav = CreateTerminalNav("UGC Hunter", HunterNav_Checked);
            _portfolioNav = CreateTerminalNav("Portfolio", PortfolioNav_Checked);

            var dashboardIndex = navStack.Children.IndexOf(DashboardNav);
            navStack.Children.Insert(Math.Min(dashboardIndex + 1, navStack.Children.Count), _hunterNav);
            var watchIndex = navStack.Children.IndexOf(WatchlistNav);
            navStack.Children.Insert(Math.Min(watchIndex + 1, navStack.Children.Count), _portfolioNav);
        }

        var contentHost = DashboardPage.Parent as Grid;
        if (contentHost is not null)
        {
            _hunterPage = BuildHunterPage();
            _portfolioPage = BuildPortfolioPage();
            Panel.SetZIndex(_hunterPage, 0);
            Panel.SetZIndex(_portfolioPage, 0);
            contentHost.Children.Add(_hunterPage);
            contentHost.Children.Add(_portfolioPage);
        }

        _hunterView = CollectionViewSource.GetDefaultView(_hunterRows);
        _hunterView.Filter = FilterHunterRow;
        if (_hunterGrid is not null) _hunterGrid.ItemsSource = _hunterView;

        _hunterTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _hunterTimer.Tick += async (_, _) =>
        {
            if (_hunterPage?.Visibility == Visibility.Visible)
            {
                await RefreshHunterAsync(silent: true);
            }
        };
        _hunterTimer.Start();

        ApplyTerminalPolish();
    }

    private RadioButton CreateTerminalNav(string text, RoutedEventHandler handler)
    {
        var button = new RadioButton
        {
            Content = text,
            GroupName = "Navigation",
            Style = (Style)FindResource("NavigationButtonStyle")
        };
        button.Checked += handler;
        return button;
    }

    private void ApplyTerminalPolish()
    {
        Title = "RPT Markets — Roblox Limited Market Terminal";
        PageSubtitleText.Text = "Live Roblox Limited market intelligence, resale tracking, and UGC opportunity scanning.";
        CheckNowButton.ToolTip = "F5 · Refresh tracked market data";

        if (DashboardWatchlistList.Columns.Count > 0) DashboardWatchlistList.RowHeight = 58;
        if (WatchlistGrid.Columns.Count > 0) WatchlistGrid.RowHeight = 58;
    }

    private Grid BuildHunterPage()
    {
        var root = new Grid
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(16, 14, 16, 16),
            Background = Brushes.Transparent
        };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(58) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.Children.Add(top);

        var marketStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        top.Children.Add(marketStack);
        marketStack.Children.Add(MakeSmallLabel("UGC MARKET"));
        _hunterRegimeText = new TextBlock
        {
            Text = "CALIBRATING",
            Foreground = TerminalAmber,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Margin = new Thickness(11, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        marketStack.Children.Add(_hunterRegimeText);
        _hunterRegimeDetailText = new TextBlock
        {
            Text = "Scanning buyable UGC Limited drops",
            Foreground = TerminalMuted,
            FontSize = 9,
            Margin = new Thickness(16, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        marketStack.Children.Add(_hunterRegimeDetailText);

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(toolbar, 1);
        top.Children.Add(toolbar);

        _hunterSearchBox = new TextBox { Width = 210, ToolTip = "Search drop or creator" };
        _hunterSearchBox.TextChanged += (_, _) => _hunterView?.Refresh();
        toolbar.Children.Add(_hunterSearchBox);

        _hunterCategoryCombo = new ComboBox { Width = 125, Margin = new Thickness(7, 0, 0, 0), SelectedIndex = 0 };
        foreach (var category in new[] { "All categories", "Hat", "Hair", "Face", "Neck", "Shoulder", "Front", "Back", "Waist" })
            _hunterCategoryCombo.Items.Add(category);
        _hunterCategoryCombo.SelectionChanged += (_, _) => _hunterView?.Refresh();
        toolbar.Children.Add(_hunterCategoryCombo);

        _hunterOpportunityCombo = new ComboBox { Width = 122, Margin = new Thickness(7, 0, 0, 0), SelectedIndex = 0 };
        foreach (var item in new[] { "All scores", "70+ potential", "80+ potential", "90+ potential" })
            _hunterOpportunityCombo.Items.Add(item);
        _hunterOpportunityCombo.SelectionChanged += (_, _) => _hunterView?.Refresh();
        toolbar.Children.Add(_hunterOpportunityCombo);

        var refresh = new Button
        {
            Content = "REFRESH SCAN",
            Style = (Style)FindResource("SecondaryButtonStyle"),
            Margin = new Thickness(7, 0, 0, 0)
        };
        refresh.Click += async (_, _) => await RefreshHunterAsync(silent: false);
        toolbar.Children.Add(refresh);

        var main = new Grid();
        Grid.SetRow(main, 2);
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(350) });
        root.Children.Add(main);

        var scannerPanel = MakePanel();
        main.Children.Add(scannerPanel);
        var scannerGrid = new Grid();
        scannerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        scannerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        scannerPanel.Child = scannerGrid;

        var scannerHeader = new Grid { Margin = new Thickness(12, 0, 12, 0) };
        scannerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scannerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        scannerGrid.Children.Add(scannerHeader);
        var scannerTitleStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        scannerHeader.Children.Add(scannerTitleStack);
        scannerTitleStack.Children.Add(new TextBlock { Text = "LIVE DROP SCANNER", Foreground = TerminalText, FontSize = 10.5, FontWeight = FontWeights.SemiBold });
        _hunterUpdatedText = new TextBlock { Text = "Waiting for first scan", Foreground = TerminalMuted, FontSize = 8.5, Margin = new Thickness(10, 1, 0, 0) };
        scannerTitleStack.Children.Add(_hunterUpdatedText);

        var counts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(counts, 1);
        scannerHeader.Children.Add(counts);
        counts.Children.Add(MakeSmallLabel("LIVE"));
        _hunterLiveCountText = MakeMetric("0", TerminalText, 11);
        _hunterLiveCountText.Margin = new Thickness(6, 0, 14, 0);
        counts.Children.Add(_hunterLiveCountText);
        counts.Children.Add(MakeSmallLabel("HIGH CONVICTION"));
        _hunterStrongCountText = MakeMetric("0", TerminalGreen, 11);
        _hunterStrongCountText.Margin = new Thickness(6, 0, 0, 0);
        counts.Children.Add(_hunterStrongCountText);

        _hunterGrid = new DataGrid
        {
            IsReadOnly = true,
            RowHeight = 58,
            ColumnHeaderHeight = 34,
            BorderThickness = new Thickness(0, 1, 0, 0),
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow
        };
        _hunterGrid.SelectionChanged += (_, _) => UpdateHunterInspector();
        _hunterGrid.MouseDoubleClick += (_, _) => OpenSelectedHunterOnRoblox();
        Grid.SetRow(_hunterGrid, 1);
        scannerGrid.Children.Add(_hunterGrid);
        AddHunterColumns(_hunterGrid);

        var inspector = MakePanel();
        Grid.SetColumn(inspector, 2);
        main.Children.Add(inspector);
        inspector.Child = BuildHunterInspector();

        _hunterErrorText = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            Foreground = TerminalRed,
            Background = new SolidColorBrush(Color.FromRgb(42, 18, 22)),
            FontSize = 9,
            TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(10, 7, 10, 7),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 360, 8)
        };
        Grid.SetRow(_hunterErrorText, 2);
        Panel.SetZIndex(_hunterErrorText, 8);
        root.Children.Add(_hunterErrorText);

        return root;
    }

    private UIElement BuildHunterInspector()
    {
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var stack = new StackPanel { Margin = new Thickness(15) };
        scroll.Content = stack;

        stack.Children.Add(MakeSmallLabel("SELECTED DROP"));
        var hero = new Grid { Margin = new Thickness(0, 11, 0, 0) };
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        stack.Children.Add(hero);

        var imageBorder = new Border
        {
            Width = 62,
            Height = 62,
            Background = TerminalPanelRaised,
            BorderBrush = TerminalBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4)
        };
        _hunterInspectorImage = new Image { Stretch = Stretch.UniformToFill };
        imageBorder.Child = _hunterInspectorImage;
        hero.Children.Add(imageBorder);

        var identity = new StackPanel { Margin = new Thickness(9, 2, 0, 0) };
        Grid.SetColumn(identity, 1);
        hero.Children.Add(identity);
        _hunterInspectorTitle = new TextBlock { Text = "Select a live drop", Foreground = TerminalText, FontWeight = FontWeights.SemiBold, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        identity.Children.Add(_hunterInspectorTitle);
        _hunterInspectorCreator = new TextBlock { Text = "UGC Hunter will explain the opportunity here.", Foreground = TerminalMuted, FontSize = 8.5, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        identity.Children.Add(_hunterInspectorCreator);
        _hunterInspectorPrice = new TextBlock { Text = "—", Foreground = TerminalText, FontWeight = FontWeights.SemiBold, FontSize = 16, Margin = new Thickness(0, 7, 0, 0) };
        identity.Children.Add(_hunterInspectorPrice);

        _hunterInspectorPhase = new TextBlock { Text = "NO DROP SELECTED", Foreground = TerminalBlue, FontSize = 8.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 13, 0, 0) };
        stack.Children.Add(_hunterInspectorPhase);

        var scoreGrid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        for (var i = 0; i < 4; i++) scoreGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        stack.Children.Add(scoreGrid);
        _hunterInspectorOpportunity = AddScore(scoreGrid, 0, "OPPORTUNITY", "—", TerminalGreen);
        _hunterInspectorEntry = AddScore(scoreGrid, 1, "ENTRY", "—", TerminalBlue);
        _hunterInspectorRisk = AddScore(scoreGrid, 2, "RISK", "—", TerminalRed);
        _hunterInspectorConfidence = AddScore(scoreGrid, 3, "CONF", "—", TerminalAmber);

        stack.Children.Add(MakeSeparator(15, 13));
        stack.Children.Add(MakeSmallLabel("LIVE MARKET"));
        _hunterInspectorVelocity = AddInspectorMetric(stack, "Velocity", "—");
        _hunterInspectorEta = AddInspectorMetric(stack, "Sellout ETA", "—");
        _hunterInspectorSupply = AddInspectorMetric(stack, "Supply", "—");

        stack.Children.Add(MakeSeparator(15, 13));
        stack.Children.Add(MakeSmallLabel("RESALE OUTLOOK"));
        _hunterInspectorForecast = new TextBlock { Text = "—", Foreground = TerminalText, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) };
        stack.Children.Add(_hunterInspectorForecast);
        stack.Children.Add(new TextBlock { Text = "Statistical scenario range, not a guaranteed resale price.", Foreground = TerminalMuted, FontSize = 8, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });

        stack.Children.Add(MakeSeparator(15, 13));
        stack.Children.Add(MakeSmallLabel("WHY IT SCORES"));
        _hunterInspectorReasons = new TextBlock { Text = "—", Foreground = new SolidColorBrush(Color.FromRgb(169, 182, 197)), FontSize = 9, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), LineHeight = 16 };
        stack.Children.Add(_hunterInspectorReasons);

        stack.Children.Add(MakeSmallLabel("RISKS"));
        _hunterInspectorRisks = new TextBlock { Text = "—", Foreground = new SolidColorBrush(Color.FromRgb(205, 157, 164)), FontSize = 9, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), LineHeight = 16 };
        stack.Children.Add(_hunterInspectorRisks);

        var actions = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(7) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        stack.Children.Add(actions);

        _hunterOpenRobloxButton = new Button { Content = "OPEN ROBLOX", Style = (Style)FindResource("SecondaryButtonStyle"), IsEnabled = false };
        _hunterOpenRobloxButton.Click += (_, _) => OpenSelectedHunterOnRoblox();
        actions.Children.Add(_hunterOpenRobloxButton);
        _hunterTrackButton = new Button { Content = "TRACK AFTER DROP", Style = (Style)FindResource("PrimaryButtonStyle"), IsEnabled = false };
        _hunterTrackButton.Click += async (_, _) => await TrackSelectedHunterAsync();
        Grid.SetColumn(_hunterTrackButton, 2);
        actions.Children.Add(_hunterTrackButton);

        return scroll;
    }

    private Grid BuildPortfolioPage()
    {
        var root = new Grid { Visibility = Visibility.Collapsed, Margin = new Thickness(16, 14, 16, 16) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(72) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid();
        root.Children.Add(header);
        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(title);
        title.Children.Add(new TextBlock { Text = "PORTFOLIO LAB", Foreground = TerminalText, FontSize = 18, FontWeight = FontWeights.SemiBold });
        title.Children.Add(new TextBlock { Text = "Paper-trade Hunter ideas before committing Robux. Position accounting will expand in the next portfolio pass.", Foreground = TerminalMuted, FontSize = 9, Margin = new Thickness(0, 4, 0, 0) });

        var content = new Grid();
        Grid.SetRow(content, 2);
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.7, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(content);

        var left = MakePanel();
        content.Children.Add(left);
        var leftStack = new StackPanel { Margin = new Thickness(18) };
        left.Child = leftStack;
        leftStack.Children.Add(MakeSmallLabel("PAPER TRADING WORKSPACE"));
        leftStack.Children.Add(new TextBlock { Text = "Validate the Hunter model before using real Robux", Foreground = TerminalText, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) });
        leftStack.Children.Add(new TextBlock { Text = "The production plan records simulated entries, forecast snapshots, sellout results, and later resale performance. This page is intentionally separated from automatic purchasing: Hunter analyzes opportunities but never buys for you.", Foreground = new SolidColorBrush(Color.FromRgb(145, 158, 174)), FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 9, 0, 0), LineHeight = 17 });

        var stages = new[]
        {
            ("01", "ENTRY SNAPSHOT", "Opportunity / Entry / Risk / Confidence frozen at the paper entry."),
            ("02", "LIVE FOLLOW-UP", "Sellout time, velocity decay, supply absorption, and resale discovery."),
            ("03", "BACKTEST", "Compare predicted range and direction against what actually happened."),
            ("04", "CALIBRATION", "Use accumulated error to improve future Hunter scoring weights.")
        };
        foreach (var stage in stages)
        {
            var row = new Grid { Margin = new Thickness(0, 15, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            leftStack.Children.Add(row);
            row.Children.Add(new TextBlock { Text = stage.Item1, Foreground = TerminalBlue, FontWeight = FontWeights.Bold, FontSize = 10, VerticalAlignment = VerticalAlignment.Top });
            var info = new StackPanel();
            Grid.SetColumn(info, 1);
            row.Children.Add(info);
            info.Children.Add(new TextBlock { Text = stage.Item2, Foreground = TerminalText, FontWeight = FontWeights.SemiBold, FontSize = 9.5 });
            info.Children.Add(new TextBlock { Text = stage.Item3, Foreground = TerminalMuted, FontSize = 8.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
        }

        var right = MakePanel();
        Grid.SetColumn(right, 2);
        content.Children.Add(right);
        var rightStack = new StackPanel { Margin = new Thickness(18) };
        right.Child = rightStack;
        rightStack.Children.Add(MakeSmallLabel("MODEL DISCIPLINE"));
        rightStack.Children.Add(new TextBlock { Text = "No automatic buying", Foreground = TerminalGreen, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) });
        rightStack.Children.Add(new TextBlock { Text = "The terminal keeps decision support separate from execution. Forecasts are measured against later outcomes so weak signals can be downgraded instead of presented as certainty.", Foreground = TerminalMuted, FontSize = 9.5, TextWrapping = TextWrapping.Wrap, LineHeight = 16, Margin = new Thickness(0, 7, 0, 0) });
        rightStack.Children.Add(MakeSeparator(16, 13));
        rightStack.Children.Add(MakeSmallLabel("PLANNED POSITION METRICS"));
        foreach (var metric in new[] { "Average entry", "Current floor", "Unrealized P/L", "ROI", "Liquidity", "Exit pressure", "Forecast accuracy" })
            AddInspectorMetric(rightStack, metric, "—");

        return root;
    }

    private void AddHunterColumns(DataGrid grid)
    {
        grid.Columns.Add(CreateHunterAssetColumn());
        grid.Columns.Add(HunterTextColumn("PRICE", nameof(UgcHunterItem.PriceText), 0.70, TerminalText, true));
        grid.Columns.Add(HunterTextColumn("LEFT", nameof(UgcHunterItem.RemainingText), 0.92, TerminalMuted));
        grid.Columns.Add(HunterTextColumn("VEL", nameof(UgcHunterItem.VelocityText), 0.72, TerminalGreen, true));
        grid.Columns.Add(HunterTextColumn("ETA", nameof(UgcHunterItem.EtaText), 0.62, TerminalAmber));
        grid.Columns.Add(HunterTextColumn("POT", nameof(UgcHunterItem.OpportunityText), 0.55, TerminalGreen, true));
        grid.Columns.Add(HunterTextColumn("ENTRY", nameof(UgcHunterItem.EntryText), 0.58, TerminalBlue, true));
        grid.Columns.Add(HunterTextColumn("RISK", nameof(UgcHunterItem.RiskText), 0.55, TerminalRed, true));
        grid.Columns.Add(HunterTextColumn("CONF", nameof(UgcHunterItem.ConfidenceText), 0.64, TerminalAmber));
        grid.Columns.Add(HunterTextColumn("PHASE", nameof(UgcHunterItem.Phase), 1.08, TerminalMuted));
    }

    private DataGridTemplateColumn CreateHunterAssetColumn()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panel.SetValue(StackPanel.VerticalAlignmentProperty, VerticalAlignment.Center);

        var image = new FrameworkElementFactory(typeof(Image));
        image.SetBinding(Image.SourceProperty, new Binding(nameof(UgcHunterItem.ThumbnailUrl)));
        image.SetValue(FrameworkElement.WidthProperty, 34d);
        image.SetValue(FrameworkElement.HeightProperty, 34d);
        image.SetValue(Image.StretchProperty, Stretch.UniformToFill);
        panel.AppendChild(image);

        var textStack = new FrameworkElementFactory(typeof(StackPanel));
        textStack.SetValue(FrameworkElement.MarginProperty, new Thickness(9, 0, 0, 0));
        textStack.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(UgcHunterItem.Name)));
        name.SetValue(TextBlock.ForegroundProperty, TerminalText);
        name.SetValue(TextBlock.FontSizeProperty, 10.7d);
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        textStack.AppendChild(name);
        var creator = new FrameworkElementFactory(typeof(TextBlock));
        creator.SetBinding(TextBlock.TextProperty, new Binding(nameof(UgcHunterItem.CreatorName)));
        creator.SetValue(TextBlock.ForegroundProperty, TerminalMuted);
        creator.SetValue(TextBlock.FontSizeProperty, 8.2d);
        creator.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 0));
        creator.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        textStack.AppendChild(creator);
        panel.AppendChild(textStack);

        return new DataGridTemplateColumn
        {
            Header = "ASSET",
            Width = new DataGridLength(2.15, DataGridLengthUnitType.Star),
            CellTemplate = new DataTemplate { VisualTree = panel }
        };
    }

    private static DataGridTemplateColumn HunterTextColumn(string header, string path, double width, Brush foreground, bool bold = false)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(path));
        text.SetValue(TextBlock.ForegroundProperty, foreground);
        text.SetValue(TextBlock.FontSizeProperty, 9.3d);
        text.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        if (bold) text.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        return new DataGridTemplateColumn
        {
            Header = header,
            Width = new DataGridLength(width, DataGridLengthUnitType.Star),
            CellTemplate = new DataTemplate { VisualTree = text }
        };
    }

    private bool FilterHunterRow(object item)
    {
        if (item is not UgcHunterItem row) return false;
        var query = _hunterSearchBox?.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(query) &&
            !row.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
            !row.CreatorName.Contains(query, StringComparison.OrdinalIgnoreCase) &&
            !row.AssetId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)) return false;

        var category = _hunterCategoryCombo?.SelectedItem as string;
        if (!string.IsNullOrWhiteSpace(category) && category != "All categories" && !row.Category.Equals(category, StringComparison.OrdinalIgnoreCase)) return false;

        var scoreIndex = _hunterOpportunityCombo?.SelectedIndex ?? 0;
        var minimum = scoreIndex switch { 1 => 70, 2 => 80, 3 => 90, _ => 0 };
        return row.OpportunityScore >= minimum;
    }

    private async void HunterNav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        ShowPage("Hunter");
        if (_hunterLastRefreshUtc is null || DateTimeOffset.UtcNow - _hunterLastRefreshUtc > TimeSpan.FromSeconds(45))
            await RefreshHunterAsync(silent: _hunterRows.Count > 0);
    }

    private void PortfolioNav_Checked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) ShowPage("Portfolio");
    }

    private void SetTerminalPageVisibility(string page)
    {
        if (_hunterPage is not null) _hunterPage.Visibility = page == "Hunter" ? Visibility.Visible : Visibility.Collapsed;
        if (_portfolioPage is not null) _portfolioPage.Visibility = page == "Portfolio" ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RefreshHunterAsync(bool silent)
    {
        if (_hunterRefreshInProgress) return;
        _hunterRefreshInProgress = true;
        if (_hunterUpdatedText is not null) _hunterUpdatedText.Text = "Scanning Roblox Marketplace…";
        if (_hunterErrorText is not null && !silent) _hunterErrorText.Visibility = Visibility.Collapsed;
        try
        {
            var snapshot = await _services.UgcHunterService.RefreshAsync();
            _hunterRows.Clear();
            foreach (var item in snapshot.Items) _hunterRows.Add(item);
            _hunterView?.Refresh();
            _hunterLastRefreshUtc = snapshot.ObservedAtUtc;

            if (_hunterRegimeText is not null)
            {
                _hunterRegimeText.Text = snapshot.Market.Regime;
                _hunterRegimeText.Foreground = snapshot.Market.Regime switch
                {
                    "VERY HOT" or "HOT" => TerminalGreen,
                    "COOLING" => TerminalAmber,
                    "WEAK" => TerminalRed,
                    _ => TerminalText
                };
            }
            if (_hunterRegimeDetailText is not null) _hunterRegimeDetailText.Text = snapshot.Market.Detail;
            if (_hunterLiveCountText is not null) _hunterLiveCountText.Text = snapshot.Market.LiveDrops.ToString("N0");
            if (_hunterStrongCountText is not null) _hunterStrongCountText.Text = snapshot.Market.StrongDrops.ToString("N0");
            if (_hunterUpdatedText is not null) _hunterUpdatedText.Text = $"Updated {snapshot.ObservedAtUtc.ToLocalTime():h:mm:ss tt}";

            if (_hunterGrid is not null && _hunterGrid.SelectedItem is null && _hunterRows.Count > 0)
                _hunterGrid.SelectedIndex = 0;
            UpdateHunterInspector();
        }
        catch (Exception ex)
        {
            _services.Logger.Error($"UGC Hunter refresh failed: {ex}");
            if (_hunterUpdatedText is not null) _hunterUpdatedText.Text = "Scan failed";
            if (_hunterErrorText is not null)
            {
                _hunterErrorText.Text = $"UGC Hunter could not refresh: {ex.Message}";
                _hunterErrorText.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _hunterRefreshInProgress = false;
        }
    }

    private void UpdateHunterInspector()
    {
        var item = _hunterGrid?.SelectedItem as UgcHunterItem;
        var enabled = item is not null;
        if (_hunterOpenRobloxButton is not null) _hunterOpenRobloxButton.IsEnabled = enabled;
        if (_hunterTrackButton is not null) _hunterTrackButton.IsEnabled = enabled;
        if (item is null) return;

        if (_hunterInspectorTitle is not null) _hunterInspectorTitle.Text = item.Name;
        if (_hunterInspectorCreator is not null) _hunterInspectorCreator.Text = $"{item.CreatorName} · {item.Category} · Asset {item.AssetId}";
        if (_hunterInspectorPrice is not null) _hunterInspectorPrice.Text = item.PriceText;
        if (_hunterInspectorPhase is not null)
        {
            _hunterInspectorPhase.Text = $"{item.Phase}  ·  ENTRY {item.EntryWindow}";
            _hunterInspectorPhase.Foreground = item.EntryScore >= 75 ? TerminalGreen : item.EntryScore >= 50 ? TerminalAmber : TerminalRed;
        }
        if (_hunterInspectorOpportunity is not null) _hunterInspectorOpportunity.Text = item.OpportunityText;
        if (_hunterInspectorEntry is not null) _hunterInspectorEntry.Text = item.EntryText;
        if (_hunterInspectorRisk is not null) _hunterInspectorRisk.Text = item.RiskText;
        if (_hunterInspectorConfidence is not null) _hunterInspectorConfidence.Text = item.ConfidenceText;
        if (_hunterInspectorVelocity is not null) _hunterInspectorVelocity.Text = $"{item.VelocityText} · {item.VelocityLabel} · {item.AccelerationText}";
        if (_hunterInspectorEta is not null) _hunterInspectorEta.Text = item.EtaText;
        if (_hunterInspectorSupply is not null) _hunterInspectorSupply.Text = $"{item.RemainingText} remaining / {item.SupplyText} total";
        if (_hunterInspectorForecast is not null) _hunterInspectorForecast.Text = $"{item.ForecastText}  ·  BASE {item.BaseValueText}";
        if (_hunterInspectorReasons is not null) _hunterInspectorReasons.Text = string.Join("\n", item.Reasons.Select(x => $"+ {x}"));
        if (_hunterInspectorRisks is not null) _hunterInspectorRisks.Text = string.Join("\n", item.Risks.Select(x => $"• {x}"));

        if (_hunterInspectorImage is not null)
        {
            try
            {
                _hunterInspectorImage.Source = string.IsNullOrWhiteSpace(item.ThumbnailUrl) ? null : new BitmapImage(new Uri(item.ThumbnailUrl));
            }
            catch { _hunterInspectorImage.Source = null; }
        }
    }

    private void OpenSelectedHunterOnRoblox()
    {
        if (_hunterGrid?.SelectedItem is not UgcHunterItem item) return;
        try { Process.Start(new ProcessStartInfo(item.RobloxUrl) { UseShellExecute = true }); }
        catch (Exception ex) { _services.Logger.Error($"Could not open Roblox catalog page: {ex.Message}"); }
    }

    private async Task TrackSelectedHunterAsync()
    {
        if (_hunterGrid?.SelectedItem is not UgcHunterItem) return;
        var dialog = new AddItemWindow(_services) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            await RefreshAllAsync();
            WatchlistNav.IsChecked = true;
            ShowPage("Watchlist");
        }
    }

    private static Border MakePanel() => new()
    {
        Background = TerminalPanel,
        BorderBrush = TerminalBorder,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4)
    };

    private static TextBlock MakeSmallLabel(string text) => new()
    {
        Text = text,
        Foreground = TerminalMuted,
        FontSize = 8.3,
        FontWeight = FontWeights.SemiBold,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static TextBlock MakeMetric(string text, Brush foreground, double size) => new()
    {
        Text = text,
        Foreground = foreground,
        FontSize = size,
        FontWeight = FontWeights.SemiBold,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static TextBlock AddScore(Grid grid, int column, string label, string value, Brush foreground)
    {
        var stack = new StackPanel();
        Grid.SetColumn(stack, column);
        grid.Children.Add(stack);
        stack.Children.Add(MakeSmallLabel(label));
        var text = new TextBlock { Text = value, Foreground = foreground, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0) };
        stack.Children.Add(text);
        return text;
    }

    private static TextBlock AddInspectorMetric(Panel panel, string label, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.Children.Add(row);
        row.Children.Add(new TextBlock { Text = label, Foreground = TerminalMuted, FontSize = 8.8, VerticalAlignment = VerticalAlignment.Center });
        var metric = new TextBlock { Text = value, Foreground = TerminalText, FontSize = 9.4, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        Grid.SetColumn(metric, 1);
        row.Children.Add(metric);
        return metric;
    }

    private static Border MakeSeparator(double top, double bottom) => new()
    {
        Height = 1,
        Background = TerminalBorder,
        Margin = new Thickness(0, top, 0, bottom)
    };
}
