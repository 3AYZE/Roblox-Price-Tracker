using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RobloxPriceTracker.Core;

namespace RobloxPriceTracker.Gui;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<UgcHunterItem> _hunterRows = new();
    private readonly ObservableCollection<PaperPositionRow> _paperRows = new();
    private ICollectionView? _hunterView;
    private RadioButton? _hunterNav;
    private RadioButton? _portfolioNav;
    private Grid? _hunterPage;
    private Grid? _portfolioPage;
    private DataGrid? _hunterGrid;
    private DataGrid? _portfolioGrid;
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
    private TextBlock? _portfolioCostText;
    private TextBlock? _portfolioValueText;
    private TextBlock? _portfolioPnlText;
    private TextBlock? _portfolioWinRateText;
    private TextBlock? _portfolioStatusText;
    private DispatcherTimer? _hunterTimer;
    private bool _hunterRefreshInProgress;
    private bool _portfolioRefreshInProgress;
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
            contentHost.Children.Add(_hunterPage);
            contentHost.Children.Add(_portfolioPage);
        }

        _hunterView = CollectionViewSource.GetDefaultView(_hunterRows);
        _hunterView.Filter = FilterHunterRow;
        if (_hunterGrid is not null) _hunterGrid.ItemsSource = _hunterView;
        if (_portfolioGrid is not null) _portfolioGrid.ItemsSource = _paperRows;

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
        DashboardWatchlistList.RowHeight = 58;
        WatchlistGrid.RowHeight = 58;
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
        foreach (var category in new[] { "All categories", "Hat", "Hair", "Face", "Neck", "Shoulder", "Front", "Back", "Waist", "T-Shirt", "Shirt", "Pants", "Jacket", "Sweater", "Shorts", "Shoes", "Dress / Skirt", "Eyebrow", "Eyelash", "Dynamic Head", "Face Makeup", "Lip Makeup", "Eye Makeup" })
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
        _hunterTrackButton = new Button { Content = "PAPER ENTRY", Style = (Style)FindResource("PrimaryButtonStyle"), IsEnabled = false, ToolTip = "Record a simulated 1-unit entry. No Robux is spent." };
        _hunterTrackButton.Click += async (_, _) => await TrackSelectedHunterAsync();
        Grid.SetColumn(_hunterTrackButton, 2);
        actions.Children.Add(_hunterTrackButton);

        return scroll;
    }

    private Grid BuildPortfolioPage()
    {
        var root = new Grid { Visibility = Visibility.Collapsed, Margin = new Thickness(16, 14, 16, 16) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(78) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.Children.Add(header);

        var metrics = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(metrics);
        metrics.Children.Add(MakeSmallLabel("PAPER COST"));
        _portfolioCostText = MakeMetric("0 R$", TerminalText, 14);
        _portfolioCostText.Margin = new Thickness(8, 0, 24, 0);
        metrics.Children.Add(_portfolioCostText);
        metrics.Children.Add(MakeSmallLabel("MARKET VALUE"));
        _portfolioValueText = MakeMetric("—", TerminalText, 14);
        _portfolioValueText.Margin = new Thickness(8, 0, 24, 0);
        metrics.Children.Add(_portfolioValueText);
        metrics.Children.Add(MakeSmallLabel("P/L"));
        _portfolioPnlText = MakeMetric("—", TerminalMuted, 14);
        _portfolioPnlText.Margin = new Thickness(8, 0, 24, 0);
        metrics.Children.Add(_portfolioPnlText);
        metrics.Children.Add(MakeSmallLabel("WIN RATE"));
        _portfolioWinRateText = MakeMetric("—", TerminalMuted, 14);
        _portfolioWinRateText.Margin = new Thickness(8, 0, 0, 0);
        metrics.Children.Add(_portfolioWinRateText);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        _portfolioStatusText = new TextBlock { Text = "Paper positions are local only", Foreground = TerminalMuted, FontSize = 8.5, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(_portfolioStatusText);
        var refresh = new Button { Content = "REFRESH FLOORS", Style = (Style)FindResource("SecondaryButtonStyle") };
        refresh.Click += async (_, _) => await RefreshPaperPortfolioAsync();
        actions.Children.Add(refresh);
        var open = new Button { Content = "OPEN ROBLOX", Style = (Style)FindResource("SecondaryButtonStyle"), Margin = new Thickness(7, 0, 0, 0) };
        open.Click += (_, _) => OpenSelectedPaperPosition();
        actions.Children.Add(open);
        var remove = new Button { Content = "REMOVE", Style = (Style)FindResource("SecondaryButtonStyle"), Margin = new Thickness(7, 0, 0, 0) };
        remove.Click += async (_, _) => await RemoveSelectedPaperPositionAsync();
        actions.Children.Add(remove);

        var panel = MakePanel();
        Grid.SetRow(panel, 2);
        root.Children.Add(panel);
        var panelGrid = new Grid();
        panelGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
        panelGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.Child = panelGrid;

        var title = new Grid { Margin = new Thickness(12, 0, 12, 0) };
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panelGrid.Children.Add(title);
        title.Children.Add(new TextBlock { Text = "PAPER POSITIONS", Foreground = TerminalText, FontSize = 10.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var note = new TextBlock { Text = "Hunter entry snapshots · actual resale floor when available", Foreground = TerminalMuted, FontSize = 8.3, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(note, 1);
        title.Children.Add(note);

        _portfolioGrid = new DataGrid
        {
            IsReadOnly = true,
            RowHeight = 56,
            ColumnHeaderHeight = 34,
            BorderThickness = new Thickness(0, 1, 0, 0),
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow
        };
        _portfolioGrid.MouseDoubleClick += (_, _) => OpenSelectedPaperPosition();
        Grid.SetRow(_portfolioGrid, 1);
        panelGrid.Children.Add(_portfolioGrid);
        AddPortfolioColumns(_portfolioGrid);

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

    private void AddPortfolioColumns(DataGrid grid)
    {
        grid.Columns.Add(HunterTextColumn("ASSET", nameof(PaperPositionRow.Name), 1.8, TerminalText, true));
        grid.Columns.Add(HunterTextColumn("QTY", nameof(PaperPositionRow.Quantity), 0.45, TerminalMuted));
        grid.Columns.Add(HunterTextColumn("ENTRY", nameof(PaperPositionRow.Entry), 0.72, TerminalText, true));
        grid.Columns.Add(HunterTextColumn("FLOOR", nameof(PaperPositionRow.Floor), 0.72, TerminalText, true));
        grid.Columns.Add(HunterTextColumn("VALUE", nameof(PaperPositionRow.Value), 0.78, TerminalMuted));
        grid.Columns.Add(CreatePaperProfitColumn());
        grid.Columns.Add(HunterTextColumn("FORECAST", nameof(PaperPositionRow.Forecast), 1.05, TerminalAmber));
        grid.Columns.Add(HunterTextColumn("MODEL", nameof(PaperPositionRow.Scores), 0.92, TerminalBlue));
        grid.Columns.Add(HunterTextColumn("ENTERED", nameof(PaperPositionRow.Entered), 0.96, TerminalMuted));
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

    private static DataGridTemplateColumn CreatePaperProfitColumn()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.VerticalAlignmentProperty, VerticalAlignment.Center);
        var pnl = new FrameworkElementFactory(typeof(TextBlock));
        pnl.SetBinding(TextBlock.TextProperty, new Binding(nameof(PaperPositionRow.ProfitLoss)));
        pnl.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(PaperPositionRow.ProfitForeground)));
        pnl.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        pnl.SetValue(TextBlock.FontSizeProperty, 9.4d);
        panel.AppendChild(pnl);
        var roi = new FrameworkElementFactory(typeof(TextBlock));
        roi.SetBinding(TextBlock.TextProperty, new Binding(nameof(PaperPositionRow.Roi)));
        roi.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(PaperPositionRow.ProfitForeground)));
        roi.SetValue(TextBlock.FontSizeProperty, 8.0d);
        roi.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 0));
        panel.AppendChild(roi);
        return new DataGridTemplateColumn
        {
            Header = "P/L",
            Width = new DataGridLength(0.82, DataGridLengthUnitType.Star),
            CellTemplate = new DataTemplate { VisualTree = panel }
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

    private async void PortfolioNav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        ShowPage("Portfolio");
        await RefreshPaperPortfolioAsync();
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
            var selectedAssetId = (_hunterGrid?.SelectedItem as UgcHunterItem)?.AssetId;
            var snapshot = await _services.UgcHunterService.RefreshAsync();
            _hunterRows.Clear();
            foreach (var item in snapshot.Items) _hunterRows.Add(item);
            _hunterView?.Refresh();
            _hunterLastRefreshUtc = snapshot.ObservedAtUtc;
            if (_hunterErrorText is not null) _hunterErrorText.Visibility = Visibility.Collapsed;

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

            if (_hunterGrid is not null)
            {
                var previous = selectedAssetId is { } id ? _hunterRows.FirstOrDefault(x => x.AssetId == id) : null;
                if (previous is not null) _hunterGrid.SelectedItem = previous;
                else if (_hunterRows.Count > 0) _hunterGrid.SelectedIndex = 0;
            }
            UpdateHunterInspector();
        }
        catch (Exception ex)
        {
            _services.Logger.Error($"UGC Hunter refresh failed: {ex}");
            var hasCachedRows = _hunterRows.Count > 0;
            if (_hunterUpdatedText is not null)
                _hunterUpdatedText.Text = hasCachedRows ? "Refresh failed · showing last scan" : "Scan failed";

            if (!hasCachedRows)
            {
                var status = "OFFLINE";
                var statusBrush = TerminalRed;
                var detail = "Roblox catalog search is temporarily unavailable";

                if (ex is RobloxUgcDiscoveryException discoveryFailure)
                {
                    switch (discoveryFailure.Kind)
                    {
                        case RobloxUgcDiscoveryFailureKind.RateLimited:
                            status = "RATE LIMITED";
                            statusBrush = TerminalAmber;
                            detail = "Roblox is rate limiting Hunter · wait a few seconds and refresh";
                            break;
                        case RobloxUgcDiscoveryFailureKind.Protocol:
                            status = "API ERROR";
                            detail = "Roblox catalog returned data Hunter could not use";
                            break;
                        case RobloxUgcDiscoveryFailureKind.Offline:
                            status = "OFFLINE";
                            detail = "Roblox catalog search is temporarily unavailable";
                            break;
                    }
                }

                if (_hunterRegimeText is not null)
                {
                    _hunterRegimeText.Text = status;
                    _hunterRegimeText.Foreground = statusBrush;
                }
                if (_hunterRegimeDetailText is not null)
                    _hunterRegimeDetailText.Text = detail;
            }

            if (_hunterErrorText is not null)
            {
                if (silent && hasCachedRows)
                {
                    _hunterErrorText.Visibility = Visibility.Collapsed;
                }
                else
                {
                    _hunterErrorText.Text = hasCachedRows
                        ? $"Live refresh failed. Showing the last successful scan. {ex.Message}"
                        : $"UGC Hunter could not refresh: {ex.Message}";
                    _hunterErrorText.Visibility = Visibility.Visible;
                }
            }
        }
        finally
        {
            _hunterRefreshInProgress = false;
        }
    }

    private async Task RefreshPaperPortfolioAsync()
    {
        if (_portfolioRefreshInProgress) return;
        _portfolioRefreshInProgress = true;
        if (_portfolioStatusText is not null) _portfolioStatusText.Text = "Refreshing resale floors…";
        try
        {
            var positions = await _services.PaperPortfolioStore.LoadAsync();
            if (positions.Count > 0)
            {
                var keys = positions.Select(x => new ItemKey(CatalogItemType.Asset, x.AssetId)).Distinct().ToArray();
                var result = await _services.Provider.FetchAsync(keys, CancellationToken.None);
                var now = DateTimeOffset.UtcNow;
                foreach (var position in positions)
                {
                    var key = new ItemKey(CatalogItemType.Asset, position.AssetId);
                    if (result.Observations.TryGetValue(key, out var observation))
                    {
                        await _services.PaperPortfolioStore.UpdateMarketAsync(position.Id, observation.LowestResalePrice is > 0 ? observation.LowestResalePrice : null, now);
                    }
                }
                positions = await _services.PaperPortfolioStore.LoadAsync();
            }

            _paperRows.Clear();
            foreach (var position in positions) _paperRows.Add(new PaperPositionRow { Position = position });
            UpdatePortfolioSummary();
            if (_portfolioStatusText is not null) _portfolioStatusText.Text = positions.Count == 0 ? "No paper positions yet" : $"Updated {DateTime.Now:h:mm:ss tt}";
        }
        catch (Exception ex)
        {
            _services.Logger.Error($"Paper portfolio refresh failed: {ex}");
            if (_portfolioStatusText is not null) _portfolioStatusText.Text = "Floor refresh failed";
        }
        finally
        {
            _portfolioRefreshInProgress = false;
        }
    }

    private void UpdatePortfolioSummary()
    {
        var positions = _paperRows.Select(x => x.Position).ToArray();
        var cost = positions.Sum(x => x.EntryPrice * x.Quantity);
        var priced = positions.Where(x => x.CurrentFloor is > 0).ToArray();
        var value = priced.Sum(x => x.CurrentFloor!.Value * x.Quantity);
        var pricedCost = priced.Sum(x => x.EntryPrice * x.Quantity);
        var pnl = value - pricedCost;
        var wins = priced.Count(x => x.CurrentFloor!.Value > x.EntryPrice);

        if (_portfolioCostText is not null) _portfolioCostText.Text = $"{cost:N0} R$";
        if (_portfolioValueText is not null) _portfolioValueText.Text = priced.Length > 0 ? $"{value:N0} R$" : "—";
        if (_portfolioPnlText is not null)
        {
            _portfolioPnlText.Text = priced.Length > 0 ? $"{pnl:+#,##0;-#,##0;0} R$" : "—";
            _portfolioPnlText.Foreground = priced.Length == 0 ? TerminalMuted : pnl >= 0 ? TerminalGreen : TerminalRed;
        }
        if (_portfolioWinRateText is not null)
        {
            _portfolioWinRateText.Text = priced.Length > 0 ? $"{(double)wins / priced.Length:P0}" : "—";
            _portfolioWinRateText.Foreground = priced.Length == 0 ? TerminalMuted : wins * 2 >= priced.Length ? TerminalGreen : TerminalAmber;
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
        OpenRobloxAsset(item.AssetId);
    }

    private async Task TrackSelectedHunterAsync()
    {
        if (_hunterGrid?.SelectedItem is not UgcHunterItem item) return;
        await _services.PaperPortfolioStore.AddAsync(item, 1);
        ShowBanner("Paper entry recorded", $"1 × {item.Name} at {item.Price:N0} R$. No Robux was spent.");
        await RefreshPaperPortfolioAsync();
    }

    private void OpenSelectedPaperPosition()
    {
        if (_portfolioGrid?.SelectedItem is not PaperPositionRow row) return;
        OpenRobloxAsset(row.Position.AssetId);
    }

    private async Task RemoveSelectedPaperPositionAsync()
    {
        if (_portfolioGrid?.SelectedItem is not PaperPositionRow row) return;
        await _services.PaperPortfolioStore.RemoveAsync(row.Position.Id);
        await RefreshPaperPortfolioAsync();
    }

    private void OpenRobloxAsset(long assetId)
    {
        try { Process.Start(new ProcessStartInfo($"https://www.roblox.com/catalog/{assetId}") { UseShellExecute = true }); }
        catch (Exception ex) { _services.Logger.Error($"Could not open Roblox catalog page: {ex.Message}"); }
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
