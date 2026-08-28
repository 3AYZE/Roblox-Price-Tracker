namespace RobloxPriceTracker.Gui;

internal static class CleanUiBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnLoaded));
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
            window.InitializeCleanUi();
    }
}

public partial class MainWindow
{
    private bool _cleanUiInitialized;
    private bool _cleanSyncingSidebar;
    private string _cleanCurrentPage = "Dashboard";
    private Border? _cleanWorkspaceBar;
    private StackPanel? _cleanWorkspaceTabs;
    private StackPanel? _cleanWorkspaceActions;
    private TextBlock? _cleanWorkspaceLabel;
    private Button? _cleanTrackAssetButton;
    private Border? _cleanHunterEmpty;
    private Border? _cleanPortfolioEmpty;
    private Border? _cleanHuntEmpty;
    private Border? _cleanOfficialEmpty;

    internal void InitializeCleanUi()
    {
        if (_cleanUiInitialized) return;
        _cleanUiInitialized = true;

        ConfigureCleanSidebar();
        BuildCleanWorkspaceBar();
        ApplyCleanTables();
        ConfigureCleanEmptyStates();
        ConfigureCleanInspectorBehavior();
        ConfigureCleanPageObservers();
        FindCleanHeaderActions();
        UpdateCleanEmptyStates();
        SyncCleanUiFromVisiblePage();
    }

    private void ConfigureCleanSidebar()
    {
        if (DashboardNav.Parent is not StackPanel navStack) return;

        DashboardNav.Checked -= DashboardNav_Checked;
        WatchlistNav.Checked -= WatchlistNav_Checked;
        AlertsNav.Checked -= AlertsNav_Checked;
        ReportsNav.Checked -= ReportsNav_Checked;
        SettingsNav.Checked -= SettingsNav_Checked;

        DashboardNav.Content = "Discover";
        WatchlistNav.Content = "My Items";
        ReportsNav.Content = "Research";
        SettingsNav.Content = "Settings";
        AlertsNav.Visibility = Visibility.Collapsed;

        DashboardNav.Checked += CleanDiscoverNav_Checked;
        WatchlistNav.Checked += CleanMyItemsNav_Checked;
        AlertsNav.Checked += CleanHiddenAlertsNav_Checked;
        ReportsNav.Checked += CleanResearchNav_Checked;
        SettingsNav.Checked += CleanSettingsNav_Checked;

        foreach (var label in navStack.Children.OfType<TextBlock>())
        {
            if (string.Equals(label.Text, "MARKET", StringComparison.OrdinalIgnoreCase))
                label.Text = "WORKSPACES";
            else if (string.Equals(label.Text, "SYSTEM", StringComparison.OrdinalIgnoreCase))
                label.Visibility = Visibility.Collapsed;
        }

        HideLegacyNavigation(_hunterNav, HunterNav_Checked, "Hunter");
        HideLegacyNavigation(_portfolioNav, PortfolioNav_Checked, "Portfolio");
        HideLegacyNavigation(_analyzerNav, AnalyzerNav_Checked, "Analyzer");
        HideLegacyNavigation(_huntNav, HuntNav_Checked, "Hunt");
        HideLegacyNavigation(_officialMarketNav, OfficialMarketNav_Checked, "OfficialMarket");

        foreach (var text in FindVisualChildren<TextBlock>(this))
        {
            if (text.Text.Contains("3AYZE", StringComparison.OrdinalIgnoreCase) &&
                text.Text.TrimStart().StartsWith("v0.", StringComparison.OrdinalIgnoreCase))
            {
                text.Text = "v0.11.0 · 3AYZE";
            }
        }
    }

    private void HideLegacyNavigation(RadioButton? nav, RoutedEventHandler oldHandler, string page)
    {
        if (nav is null) return;
        nav.Checked -= oldHandler;
        nav.Visibility = Visibility.Collapsed;
        nav.Checked += async (_, _) =>
        {
            if (IsLoaded && !_cleanSyncingSidebar)
                await NavigateCleanAsync(page);
        };
    }

    private async void CleanDiscoverNav_Checked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && !_cleanSyncingSidebar) await NavigateCleanAsync("Dashboard");
    }

    private async void CleanMyItemsNav_Checked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && !_cleanSyncingSidebar) await NavigateCleanAsync("Watchlist");
    }

    private async void CleanHiddenAlertsNav_Checked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && !_cleanSyncingSidebar) await NavigateCleanAsync("Alerts");
    }

    private async void CleanResearchNav_Checked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && !_cleanSyncingSidebar) await NavigateCleanAsync("Analyzer");
    }

    private async void CleanSettingsNav_Checked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && !_cleanSyncingSidebar) await NavigateCleanAsync("Settings");
    }

    private void BuildCleanWorkspaceBar()
    {
        if (DashboardPage.Parent is not Grid contentHost || contentHost.Parent is not Grid shell) return;

        _cleanWorkspaceBar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(14, 19, 26)),
            BorderBrush = TerminalBorder,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(18, 0, 18, 0)
        };
        Grid.SetRow(_cleanWorkspaceBar, 1);
        Panel.SetZIndex(_cleanWorkspaceBar, 30);
        shell.Children.Add(_cleanWorkspaceBar);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _cleanWorkspaceBar.Child = grid;

        _cleanWorkspaceLabel = new TextBlock
        {
            Text = "DISCOVER",
            Foreground = TerminalMuted,
            FontSize = 8.3,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 18, 0)
        };
        grid.Children.Add(_cleanWorkspaceLabel);

        _cleanWorkspaceTabs = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Grid.SetColumn(_cleanWorkspaceTabs, 1);
        grid.Children.Add(_cleanWorkspaceTabs);

        _cleanWorkspaceActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_cleanWorkspaceActions, 2);
        grid.Children.Add(_cleanWorkspaceActions);
    }

    private void RenderCleanWorkspaceBar(string page)
    {
        if (_cleanWorkspaceTabs is null || _cleanWorkspaceActions is null || _cleanWorkspaceLabel is null) return;
        _cleanWorkspaceTabs.Children.Clear();
        _cleanWorkspaceActions.Children.Clear();

        var section = CleanSectionFor(page);
        _cleanWorkspaceLabel.Text = section switch
        {
            "Discover" => "DISCOVER",
            "MyItems" => "MY ITEMS",
            "Research" => "RESEARCH",
            _ => "SETTINGS"
        };

        IReadOnlyList<(string Label, string Page)> tabs = section switch
        {
            "Discover" => new (string, string)[]
            {
                ("Overview", "Dashboard"),
                ("Official Hunt", "Hunt"),
                ("Official Market", "OfficialMarket"),
                ("UGC Hunter", "Hunter")
            },
            "MyItems" => new (string, string)[]
            {
                ("Tracker", "Watchlist"),
                ("Portfolio", "Portfolio"),
                ("Alerts", "Alerts")
            },
            "Research" => new (string, string)[]
            {
                ("Analyzer", "Analyzer"),
                ("History", "Reports")
            },
            _ => Array.Empty<(string Label, string Page)>()
        };

        foreach (var (label, target) in tabs)
            _cleanWorkspaceTabs.Children.Add(CreateCleanWorkspaceTab(label, target, target == page));

        if (section == "Settings")
        {
            _cleanWorkspaceActions.Children.Add(CreateCleanSmallAction("OPEN DATA FOLDER", (_, _) => OpenCleanDataFolder()));
            var backup = CreateCleanSmallAction("BACKUP NOW", async (_, _) => await CreateCleanManualBackupAsync());
            backup.Margin = new Thickness(7, 0, 0, 0);
            _cleanWorkspaceActions.Children.Add(backup);
        }
    }

    private Border CreateCleanWorkspaceTab(string label, string targetPage, bool active)
    {
        var border = new Border
        {
            BorderBrush = active ? TerminalBlue : Brushes.Transparent,
            BorderThickness = new Thickness(0, 0, 0, 2),
            Margin = new Thickness(0, 0, 4, 0)
        };
        var button = new Button
        {
            Content = label,
            Style = (Style)FindResource("LinkButtonStyle"),
            Foreground = active ? TerminalText : TerminalMuted,
            FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
            Height = 43,
            Padding = new Thickness(11, 0, 11, 0),
            VerticalAlignment = VerticalAlignment.Stretch
        };
        button.Click += async (_, _) => await NavigateCleanAsync(targetPage);
        border.Child = button;
        return border;
    }

    private Button CreateCleanSmallAction(string label, RoutedEventHandler handler)
    {
        var button = new Button
        {
            Content = label,
            Style = (Style)FindResource("SecondaryButtonStyle"),
            Padding = new Thickness(10, 4, 10, 4),
            FontSize = 8.2
        };
        button.Click += handler;
        return button;
    }

    private async Task NavigateCleanAsync(string page)
    {
        HideOfficialLimitedPages();

        switch (page)
        {
            case "Hunt":
                ShowPage("Hunt");
                ShowOfficialLimitedPage(hunt: true);
                SetOfficialPageHeader("Official Hunt", "Roblox Limited opportunities that currently pass the value, liquidity, and evidence checks.");
                if (_officialLastRefreshUtc is null || DateTimeOffset.UtcNow - _officialLastRefreshUtc > TimeSpan.FromMinutes(2))
                    await RefreshOfficialLimitedAsync(silent: _officialRows.Count > 0);
                break;
            case "OfficialMarket":
                ShowPage("OfficialMarket");
                ShowOfficialLimitedPage(hunt: false);
                SetOfficialPageHeader("Official Market", "Browse Roblox-published Limiteds. Double-click an item for the full Analyzer.");
                if (_officialLastRefreshUtc is null || DateTimeOffset.UtcNow - _officialLastRefreshUtc > TimeSpan.FromMinutes(2))
                    await RefreshOfficialLimitedAsync(silent: _officialRows.Count > 0);
                break;
            case "Hunter":
                ShowPage("Hunter");
                if (_hunterLastRefreshUtc is null || DateTimeOffset.UtcNow - _hunterLastRefreshUtc > TimeSpan.FromSeconds(45))
                    await RefreshHunterAsync(silent: _hunterRows.Count > 0);
                break;
            case "Portfolio":
                ShowPage("Portfolio");
                await RefreshPaperPortfolioAsync();
                break;
            case "Analyzer":
                ShowPage("Analyzer");
                _analyzerInput?.Focus();
                break;
            default:
                ShowPage(page);
                break;
        }

        SyncCleanUi(page);
    }

    private void ConfigureCleanPageObservers()
    {
        AttachCleanPageObserver(DashboardPage, "Dashboard");
        AttachCleanPageObserver(WatchlistPage, "Watchlist");
        AttachCleanPageObserver(AlertsPage, "Alerts");
        AttachCleanPageObserver(ReportsPage, "Reports");
        AttachCleanPageObserver(SettingsPage, "Settings");
        AttachCleanPageObserver(_hunterPage, "Hunter");
        AttachCleanPageObserver(_portfolioPage, "Portfolio");
        AttachCleanPageObserver(_analyzerPage, "Analyzer");
        AttachCleanPageObserver(_huntPage, "Hunt");
        AttachCleanPageObserver(_officialMarketPage, "OfficialMarket");
    }

    private void AttachCleanPageObserver(FrameworkElement? page, string key)
    {
        if (page is null) return;
        page.IsVisibleChanged += (_, _) =>
        {
            if (page.IsVisible) SyncCleanUi(key);
        };
    }

    private void SyncCleanUiFromVisiblePage()
    {
        var page = _huntPage?.IsVisible == true ? "Hunt"
            : _officialMarketPage?.IsVisible == true ? "OfficialMarket"
            : _hunterPage?.IsVisible == true ? "Hunter"
            : _portfolioPage?.IsVisible == true ? "Portfolio"
            : _analyzerPage?.IsVisible == true ? "Analyzer"
            : WatchlistPage.IsVisible ? "Watchlist"
            : AlertsPage.IsVisible ? "Alerts"
            : ReportsPage.IsVisible ? "Reports"
            : SettingsPage.IsVisible ? "Settings"
            : "Dashboard";
        SyncCleanUi(page);
    }

    private void SyncCleanUi(string page)
    {
        _cleanCurrentPage = page;
        SyncCleanSidebar(page);
        RenderCleanWorkspaceBar(page);
        ApplyCleanPageHeader(page);
        UpdateCleanHeaderActions(page);
        UpdateCleanEmptyStates();
    }

    private void SyncCleanSidebar(string page)
    {
        var target = CleanSectionFor(page) switch
        {
            "Discover" => DashboardNav,
            "MyItems" => WatchlistNav,
            "Research" => ReportsNav,
            _ => SettingsNav
        };

        if (target.IsChecked == true) return;
        _cleanSyncingSidebar = true;
        try { target.IsChecked = true; }
        finally { _cleanSyncingSidebar = false; }
    }

    private static string CleanSectionFor(string page) => page switch
    {
        "Dashboard" or "Hunt" or "OfficialMarket" or "Hunter" => "Discover",
        "Watchlist" or "Portfolio" or "Alerts" => "MyItems",
        "Analyzer" or "Reports" => "Research",
        _ => "Settings"
    };

    private void ApplyCleanPageHeader(string page)
    {
        (PageTitleText.Text, PageSubtitleText.Text) = page switch
        {
            "Dashboard" => ("Discover", "Find worthwhile Roblox Limited and UGC opportunities without mixing the two markets."),
            "Hunt" => ("Official Hunt", "Only Roblox Limiteds that currently pass the value, liquidity, and evidence checks."),
            "OfficialMarket" => ("Official Market", "Browse Roblox-published Limiteds with the essential market numbers."),
            "Hunter" => ("UGC Hunter", "Find active UGC Limited drops with resale potential and reasonable entry risk."),
            "Watchlist" => ("Tracker", "Your saved Limiteds, current prices, targets, and monitoring status."),
            "Portfolio" => ("Portfolio", "Paper positions used to validate ideas without spending Robux."),
            "Alerts" => ("Alerts", "Only price and target events that need attention."),
            "Analyzer" => ("Analyzer", "Deep-dive one Roblox item when you need the full evidence and model."),
            "Reports" => ("History", "Price history and forecast context for tracked items."),
            _ => ("Settings", "Monitoring, notifications, application behavior, and local data.")
        };
    }

    private void FindCleanHeaderActions()
    {
        _cleanTrackAssetButton = FindVisualChildren<Button>(this)
            .FirstOrDefault(button => string.Equals(button.Content?.ToString(), "+ TRACK ASSET", StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateCleanHeaderActions(string page)
    {
        var tracker = page == "Watchlist";
        MonitoringButton.Visibility = tracker ? Visibility.Visible : Visibility.Collapsed;
        CheckNowButton.Visibility = tracker ? Visibility.Visible : Visibility.Collapsed;
        if (_cleanTrackAssetButton is not null)
            _cleanTrackAssetButton.Visibility = tracker ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyCleanTables()
    {
        if (_hunterGrid is not null)
        {
            _hunterGrid.RowHeight = 58;
            _hunterGrid.Columns.Clear();
            _hunterGrid.Columns.Add(CreateHunterAssetColumn());
            _hunterGrid.Columns.Add(HunterTextColumn("PRICE", nameof(UgcHunterItem.PriceText), 0.72, TerminalText, true));
            _hunterGrid.Columns.Add(HunterTextColumn("SOLD", nameof(UgcHunterItem.PurchaseCountText), 0.68, TerminalMuted));
            _hunterGrid.Columns.Add(HunterTextColumn("LEFT", nameof(UgcHunterItem.RemainingText), 0.82, TerminalMuted));
            _hunterGrid.Columns.Add(HunterTextColumn("FLOOR", nameof(UgcHunterItem.CurrentResaleText), 0.76, TerminalGreen, true));
            _hunterGrid.Columns.Add(HunterTextColumn("RESALE", nameof(UgcHunterItem.ResalePotentialText), 0.62, TerminalGreen, true));
            _hunterGrid.Columns.Add(HunterTextColumn("RISK", nameof(UgcHunterItem.RiskText), 0.58, TerminalAmber, true));
            _hunterGrid.Columns.Add(HunterTextColumn("STATUS", nameof(UgcHunterItem.Recommendation), 0.90, TerminalBlue, true));
        }

        if (_portfolioGrid is not null)
        {
            _portfolioGrid.Columns.Clear();
            _portfolioGrid.Columns.Add(HunterTextColumn("ASSET", nameof(PaperPositionRow.Name), 2.0, TerminalText, true));
            _portfolioGrid.Columns.Add(HunterTextColumn("QTY", nameof(PaperPositionRow.Quantity), 0.45, TerminalMuted));
            _portfolioGrid.Columns.Add(HunterTextColumn("ENTRY", nameof(PaperPositionRow.Entry), 0.78, TerminalText, true));
            _portfolioGrid.Columns.Add(HunterTextColumn("FLOOR", nameof(PaperPositionRow.Floor), 0.78, TerminalText, true));
            _portfolioGrid.Columns.Add(CreatePaperProfitColumn());
            _portfolioGrid.Columns.Add(HunterTextColumn("ENTERED", nameof(PaperPositionRow.Entered), 1.0, TerminalMuted));
        }

        if (_huntGrid is not null)
        {
            _huntGrid.RowHeight = 58;
            _huntGrid.Columns.Clear();
            _huntGrid.Columns.Add(CreateCleanOfficialAssetColumn());
            _huntGrid.Columns.Add(OfficialColumn("FLOOR", nameof(OfficialLimitedRow.FloorText), 0.82));
            _huntGrid.Columns.Add(OfficialColumn("RAP", nameof(OfficialLimitedRow.RapText), 0.82));
            _huntGrid.Columns.Add(OfficialColumn("VALUE", nameof(OfficialLimitedRow.DiscountText), 0.72));
            _huntGrid.Columns.Add(OfficialColumn("7D SALES", nameof(OfficialLimitedRow.Sales7dText), 0.72));
            _huntGrid.Columns.Add(OfficialColumn("SCORE", nameof(OfficialLimitedRow.HuntText), 0.60));
            _huntGrid.Columns.Add(OfficialColumn("STATUS", nameof(OfficialLimitedRow.Status), 0.92));
        }

        if (_officialMarketGrid is not null)
        {
            _officialMarketGrid.RowHeight = 58;
            _officialMarketGrid.Columns.Clear();
            _officialMarketGrid.Columns.Add(CreateCleanOfficialAssetColumn());
            _officialMarketGrid.Columns.Add(OfficialColumn("FLOOR", nameof(OfficialLimitedRow.FloorText), 0.86));
            _officialMarketGrid.Columns.Add(OfficialColumn("RAP", nameof(OfficialLimitedRow.RapText), 0.86));
            _officialMarketGrid.Columns.Add(OfficialColumn("VALUE", nameof(OfficialLimitedRow.DiscountText), 0.76));
            _officialMarketGrid.Columns.Add(OfficialColumn("7D SALES", nameof(OfficialLimitedRow.Sales7dText), 0.74));
            _officialMarketGrid.Columns.Add(OfficialColumn("SCORE", nameof(OfficialLimitedRow.HuntText), 0.62));
            _officialMarketGrid.Columns.Add(OfficialColumn("STATUS", nameof(OfficialLimitedRow.Status), 0.94));
        }
    }

    private DataGridTemplateColumn CreateCleanOfficialAssetColumn()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panel.SetValue(StackPanel.VerticalAlignmentProperty, VerticalAlignment.Center);

        var image = new FrameworkElementFactory(typeof(Image));
        image.SetBinding(Image.SourceProperty, new Binding(nameof(OfficialLimitedRow.ThumbnailUrl)));
        image.SetValue(FrameworkElement.WidthProperty, 36d);
        image.SetValue(FrameworkElement.HeightProperty, 36d);
        image.SetValue(Image.StretchProperty, Stretch.UniformToFill);
        panel.AppendChild(image);

        var textStack = new FrameworkElementFactory(typeof(StackPanel));
        textStack.SetValue(FrameworkElement.MarginProperty, new Thickness(9, 0, 0, 0));
        textStack.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(OfficialLimitedRow.Name)));
        name.SetValue(TextBlock.ForegroundProperty, TerminalText);
        name.SetValue(TextBlock.FontSizeProperty, 10.7d);
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        textStack.AppendChild(name);
        var asset = new FrameworkElementFactory(typeof(TextBlock));
        asset.SetBinding(TextBlock.TextProperty, new Binding(nameof(OfficialLimitedRow.AssetId)) { StringFormat = "Asset {0}" });
        asset.SetValue(TextBlock.ForegroundProperty, TerminalMuted);
        asset.SetValue(TextBlock.FontSizeProperty, 8.1d);
        asset.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 0));
        textStack.AppendChild(asset);
        panel.AppendChild(textStack);

        return new DataGridTemplateColumn
        {
            Header = "ITEM",
            Width = new DataGridLength(2.25, DataGridLengthUnitType.Star),
            MinWidth = 190,
            CellTemplate = new DataTemplate { VisualTree = panel }
        };
    }

    private void ConfigureCleanInspectorBehavior()
    {
        if (_huntGrid is not null)
            _huntGrid.SelectionChanged += (_, _) => RenderCleanOfficialInspector();

        if (_hunterGrid is not null)
        {
            _hunterGrid.SelectionChanged += (_, _) => RenderCleanHunterInspector();
            _hunterGrid.PreviewMouseDoubleClick += CleanHunterDoubleClick;
        }
    }

    private void RenderCleanOfficialInspector()
    {
        if (_huntGrid?.SelectedItem is not OfficialLimitedRow row) return;
        var evidence = row.EvidenceScore >= 80 ? "HIGH" : row.EvidenceScore >= 60 ? "MEDIUM" : "LOW";
        if (_huntInspectorScores is not null)
            _huntInspectorScores.Text = $"Hunt score  {row.HuntScore}/100\nRisk        {row.RiskScore}/100\nEvidence    {evidence}";
        if (_huntInspectorMarket is not null)
            _huntInspectorMarket.Text = $"Floor       {row.FloorText}\nRAP         {row.RapText}\nBuy zone    {row.BuyZoneText}\n7d sales    {row.Sales7dText}\nTrend       {row.Trend}";
        if (_huntInspectorReason is not null)
            _huntInspectorReason.Text = row.ReasonText;
    }

    private void RenderCleanHunterInspector()
    {
        if (_hunterGrid?.SelectedItem is not UgcHunterItem item) return;
        if (_hunterInspectorForecast is not null)
            _hunterInspectorForecast.Text = $"FLOOR {item.CurrentResaleText} · RAP {item.RapText}\nRESALES {item.SalesText} · NET ROI {item.NetRoiText}";
        if (_hunterInspectorReasons is not null)
            _hunterInspectorReasons.Text = string.Join(" · ", item.Reasons.Take(2));
        if (_hunterInspectorRisks is not null)
            _hunterInspectorRisks.Text = string.Join(" · ", item.Risks.Take(2));
    }

    private async void CleanHunterDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_hunterGrid?.SelectedItem is not UgcHunterItem item || _analyzerInput is null) return;
        e.Handled = true;
        _analyzerInput.Text = item.AssetId.ToString(CultureInfo.InvariantCulture);
        await NavigateCleanAsync("Analyzer");
        await AnalyzeCurrentInputAsync();
    }

    private void ConfigureCleanEmptyStates()
    {
        _cleanHunterEmpty = AddCleanEmptyState(_hunterGrid, "No UGC opportunities loaded", "Open UGC Hunter to scan the current Roblox market.");
        _cleanPortfolioEmpty = AddCleanEmptyState(_portfolioGrid, "No paper positions", "Add a paper entry from UGC Hunter when you want to test an idea.");
        _cleanHuntEmpty = AddCleanEmptyState(_huntGrid, "No qualifying Official Hunt deals", "This is normal when no Roblox Limited currently passes the buy checks.");
        _cleanOfficialEmpty = AddCleanEmptyState(_officialMarketGrid, "No Official Market data loaded", "Open Official Market or press Refresh to scan Roblox Limiteds.");

        _hunterRows.CollectionChanged += (_, _) => UpdateCleanEmptyStates();
        _paperRows.CollectionChanged += (_, _) => UpdateCleanEmptyStates();
        _officialRows.CollectionChanged += (_, _) => UpdateCleanEmptyStates();

        if (_hunterSearchBox is not null) _hunterSearchBox.TextChanged += (_, _) => QueueCleanEmptyStateUpdate();
        if (_hunterCategoryCombo is not null) _hunterCategoryCombo.SelectionChanged += (_, _) => QueueCleanEmptyStateUpdate();
        if (_hunterOpportunityCombo is not null) _hunterOpportunityCombo.SelectionChanged += (_, _) => QueueCleanEmptyStateUpdate();
        if (_huntSearchBox is not null) _huntSearchBox.TextChanged += (_, _) => QueueCleanEmptyStateUpdate();
        if (_huntStrategyCombo is not null) _huntStrategyCombo.SelectionChanged += (_, _) => QueueCleanEmptyStateUpdate();
        if (_officialSearchBox is not null) _officialSearchBox.TextChanged += (_, _) => QueueCleanEmptyStateUpdate();
        if (_officialFilterCombo is not null) _officialFilterCombo.SelectionChanged += (_, _) => QueueCleanEmptyStateUpdate();
    }

    private void QueueCleanEmptyStateUpdate() => Dispatcher.BeginInvoke(new Action(UpdateCleanEmptyStates));

    private Border? AddCleanEmptyState(DataGrid? grid, string title, string detail)
    {
        if (grid?.Parent is not Grid parent) return null;
        var border = new Border
        {
            Background = TerminalPanel,
            IsHitTestVisible = false
        };
        Grid.SetRow(border, Grid.GetRow(grid));
        Panel.SetZIndex(border, 20);
        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = TerminalText,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        stack.Children.Add(new TextBlock
        {
            Text = detail,
            Foreground = TerminalMuted,
            FontSize = 8.8,
            Margin = new Thickness(0, 5, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420
        });
        border.Child = stack;
        parent.Children.Add(border);
        return border;
    }

    private void UpdateCleanEmptyStates()
    {
        if (_cleanHunterEmpty is not null)
            _cleanHunterEmpty.Visibility = _hunterView?.Cast<object>().Any() == true ? Visibility.Collapsed : Visibility.Visible;
        if (_cleanPortfolioEmpty is not null)
            _cleanPortfolioEmpty.Visibility = _paperRows.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_cleanHuntEmpty is not null)
            _cleanHuntEmpty.Visibility = _officialHuntView?.Cast<object>().Any() == true ? Visibility.Collapsed : Visibility.Visible;
        if (_cleanOfficialEmpty is not null)
            _cleanOfficialEmpty.Visibility = _officialMarketView?.Cast<object>().Any() == true ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpenCleanDataFolder()
    {
        try
        {
            Directory.CreateDirectory(_services.DataDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_services.DataDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowBanner("Could not open data folder", ex.Message);
        }
    }

    private async Task CreateCleanManualBackupAsync()
    {
        try
        {
            var path = await SafeJsonStore.CreateManualSnapshotAsync(_services.DataDirectory, _services.Logger);
            ShowBanner("Backup created", string.IsNullOrWhiteSpace(path) ? "There was no local JSON data to back up yet." : $"Saved to {path}");
        }
        catch (Exception ex)
        {
            _services.Logger.Error($"Manual data backup failed: {ex}");
            ShowBanner("Backup failed", ex.Message);
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }
}
