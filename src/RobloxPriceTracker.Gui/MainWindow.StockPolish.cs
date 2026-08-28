namespace RobloxPriceTracker.Gui;

internal static class StockPolishBootstrap
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
        if (sender is not MainWindow window) return;
        window.Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(window.InitializeStockPolish));
    }
}

public partial class MainWindow
{
    private bool _stockPolishInitialized;
    private string? _stockReturnPage;
    private DateTimeOffset _stockReturnCapturedAtUtc;
    private bool _stockOverviewRefreshInProgress;
    private readonly ObservableCollection<StockSignalRow> _stockOfficialSignals = new();
    private readonly ObservableCollection<StockSignalRow> _stockUgcSignals = new();
    private TextBlock? _stockOfficialDealCount;
    private TextBlock? _stockUgcPickCount;
    private TextBlock? _stockBestScore;
    private TextBlock? _stockMarketSignal;
    private TextBlock? _stockBoardFreshness;
    private TextBlock? _stockOfficialEmpty;
    private TextBlock? _stockUgcEmpty;
    private Button? _stockAnalyzerBackButton;
    private TextBlock? _stockAnalyzerSignal;
    private TextBlock? _stockAnalyzerPrice;
    private TextBlock? _stockAnalyzerValue;
    private TextBlock? _stockAnalyzerConfidence;
    private TextBlock? _stockAnalyzerRisk;
    private TextBlock? _stockAnalyzerReason;

    internal void InitializeStockPolish()
    {
        if (_stockPolishInitialized) return;
        _stockPolishInitialized = true;

        AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(CaptureStockReturnContext), true);
        PreviewKeyDown += StockPolish_PreviewKeyDown;

        ReplaceDiscoverOverviewWithSignalBoard();
        InsertAnalyzerSignalStrip();

        _officialRows.CollectionChanged += (_, _) => UpdateStockSignalBoard();
        _hunterRows.CollectionChanged += (_, _) => UpdateStockSignalBoard();

        DashboardPage.IsVisibleChanged += async (_, _) =>
        {
            if (!DashboardPage.IsVisible) return;
            UpdateStockSignalBoard();
            await RefreshStockOverviewSourcesAsync();
        };

        if (_analyzerPage is not null)
        {
            _analyzerPage.IsVisibleChanged += (_, _) =>
            {
                if (!_analyzerPage.IsVisible) return;
                UpdateStockAnalyzerBackButton();
                UpdateStockAnalyzerSignal();
            };
        }

        if (_analyzerStatus is not null)
        {
            var descriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
            descriptor?.AddValueChanged(_analyzerStatus, (_, _) => UpdateStockAnalyzerSignal());
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(UpdateStockVersionText));
        UpdateStockSignalBoard();
        UpdateStockAnalyzerSignal();

        if (DashboardPage.IsVisible)
            _ = RefreshStockOverviewSourcesAsync();
    }

    private void CaptureStockReturnContext(object sender, MouseButtonEventArgs e)
    {
        if (_cleanCurrentPage == "Analyzer") return;
        if (e.OriginalSource is not DependencyObject source) return;

        var itemClick = FindStockAncestor<DataGridRow>(source) is not null ||
                        FindStockAncestor<ListBoxItem>(source) is not null;
        var button = FindStockAncestor<Button>(source);
        var analyzeClick = button?.Content?.ToString()?.Contains("ANALYZE", StringComparison.OrdinalIgnoreCase) == true;

        if (!itemClick && !analyzeClick) return;
        _stockReturnPage = _cleanCurrentPage;
        _stockReturnCapturedAtUtc = DateTimeOffset.UtcNow;
    }

    private async void StockPolish_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _cleanCurrentPage != "Analyzer" || !CanReturnFromAnalyzer()) return;
        e.Handled = true;
        await ReturnFromAnalyzerAsync();
    }

    private static T? FindStockAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        var current = start;
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private bool CanReturnFromAnalyzer() =>
        !string.IsNullOrWhiteSpace(_stockReturnPage) &&
        _stockReturnPage != "Analyzer" &&
        DateTimeOffset.UtcNow - _stockReturnCapturedAtUtc < TimeSpan.FromMinutes(10);

    private async Task ReturnFromAnalyzerAsync()
    {
        if (!CanReturnFromAnalyzer()) return;
        var target = _stockReturnPage!;
        _stockReturnPage = null;
        await NavigateCleanAsync(target);
    }

    private void ReplaceDiscoverOverviewWithSignalBoard()
    {
        var root = new Grid { Margin = new Thickness(18, 16, 18, 18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        root.Children.Add(BuildStockSummaryStrip());

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 2);
        root.Children.Add(body);

        var officialPanel = BuildStockOpportunityPanel(
            "OFFICIAL HUNT",
            "Roblox-published Limiteds currently passing the buy checks.",
            _stockOfficialSignals,
            out _stockOfficialEmpty,
            async () => await NavigateCleanAsync("Hunt"));
        body.Children.Add(officialPanel);

        var ugcPanel = BuildStockOpportunityPanel(
            "UGC HUNTER",
            "Active UGC Limited drops with the strongest resale setup.",
            _stockUgcSignals,
            out _stockUgcEmpty,
            async () => await NavigateCleanAsync("Hunter"));
        Grid.SetColumn(ugcPanel, 2);
        body.Children.Add(ugcPanel);

        DashboardPage.Content = root;
    }

    private Border BuildStockSummaryStrip()
    {
        var panel = MakePanel();
        panel.Padding = new Thickness(14, 11, 14, 11);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.28, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        panel.Child = grid;

        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock
        {
            Text = "MARKET SIGNAL BOARD",
            Foreground = TerminalText,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold
        });
        _stockBoardFreshness = new TextBlock
        {
            Text = "Loading market signals…",
            Foreground = TerminalMuted,
            FontSize = 8.3,
            Margin = new Thickness(0, 5, 0, 0)
        };
        title.Children.Add(_stockBoardFreshness);
        grid.Children.Add(title);

        _stockOfficialDealCount = AddStockSummaryMetric(grid, 1, "OFFICIAL DEALS", "0", TerminalGreen);
        _stockUgcPickCount = AddStockSummaryMetric(grid, 2, "UGC PICKS", "0", TerminalBlue);
        _stockBestScore = AddStockSummaryMetric(grid, 3, "BEST SCORE", "—", TerminalText);
        _stockMarketSignal = AddStockSummaryMetric(grid, 4, "MARKET CALL", "SCANNING", TerminalAmber);
        return panel;
    }

    private static TextBlock AddStockSummaryMetric(Grid grid, int column, string label, string value, Brush brush)
    {
        var border = new Border
        {
            BorderBrush = TerminalBorder,
            BorderThickness = new Thickness(1, 0, 0, 0),
            Padding = new Thickness(15, 0, 0, 0),
            Margin = new Thickness(7, 0, 0, 0)
        };
        Grid.SetColumn(border, column);
        grid.Children.Add(border);

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        border.Child = stack;
        stack.Children.Add(MakeSmallLabel(label));
        var text = new TextBlock
        {
            Text = value,
            Foreground = brush,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 0)
        };
        stack.Children.Add(text);
        return text;
    }

    private Border BuildStockOpportunityPanel(
        string title,
        string subtitle,
        ObservableCollection<StockSignalRow> source,
        out TextBlock emptyText,
        Func<Task> openPage)
    {
        var panel = MakePanel();
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(54) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
        panel.Child = root;

        var header = new StackPanel { Margin = new Thickness(13, 9, 13, 0) };
        header.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = TerminalText,
            FontSize = 10.7,
            FontWeight = FontWeights.SemiBold
        });
        header.Children.Add(new TextBlock
        {
            Text = subtitle,
            Foreground = TerminalMuted,
            FontSize = 8.2,
            Margin = new Thickness(0, 4, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        root.Children.Add(header);

        var columnHeader = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        columnHeader.Children.Add(StockColumnHeader("ITEM", 180));
        columnHeader.Children.Add(StockColumnHeader("PRICE", 72));
        columnHeader.Children.Add(StockColumnHeader("VALUE", 68));
        columnHeader.Children.Add(StockColumnHeader("SCORE", 52));
        columnHeader.Children.Add(StockColumnHeader("SIGNAL", 82));
        Grid.SetRow(columnHeader, 1);
        root.Children.Add(columnHeader);

        var list = new ListBox
        {
            ItemsSource = source,
            Background = Brushes.Transparent,
            BorderBrush = TerminalBorder,
            BorderThickness = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(4),
            ItemTemplate = BuildStockSignalRowTemplate()
        };
        list.MouseDoubleClick += async (_, _) =>
        {
            if (list.SelectedItem is not StockSignalRow row || _analyzerInput is null) return;
            _stockReturnPage = "Dashboard";
            _stockReturnCapturedAtUtc = DateTimeOffset.UtcNow;
            _analyzerInput.Text = row.AssetId.ToString(CultureInfo.InvariantCulture);
            await NavigateCleanAsync("Analyzer");
            await AnalyzeCurrentInputAsync();
        };
        Grid.SetRow(list, 2);
        root.Children.Add(list);

        emptyText = new TextBlock
        {
            Text = "No qualifying setups right now.",
            Foreground = TerminalMuted,
            FontSize = 9,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        Grid.SetRow(emptyText, 2);
        Panel.SetZIndex(emptyText, 3);
        root.Children.Add(emptyText);

        var footer = new Grid { Margin = new Thickness(12, 0, 12, 0) };
        var note = new TextBlock
        {
            Text = "Double-click a row for the full Analyzer.",
            Foreground = TerminalMuted,
            FontSize = 8.1,
            VerticalAlignment = VerticalAlignment.Center
        };
        footer.Children.Add(note);
        var open = new Button
        {
            Content = "OPEN BOARD",
            Style = (Style)FindResource("LinkButtonStyle"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        open.Click += async (_, _) => await openPage();
        footer.Children.Add(open);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        return panel;
    }

    private static TextBlock StockColumnHeader(string label, double width) => new()
    {
        Text = label,
        Width = width,
        Foreground = TerminalMuted,
        FontSize = 7.8,
        FontWeight = FontWeights.SemiBold,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static DataTemplate BuildStockSignalRowTemplate()
    {
        var root = new FrameworkElementFactory(typeof(StackPanel));
        root.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        root.SetValue(FrameworkElement.HeightProperty, 50d);
        root.SetValue(FrameworkElement.MarginProperty, new Thickness(5, 0, 5, 0));
        root.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        var itemPanel = new FrameworkElementFactory(typeof(StackPanel));
        itemPanel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        itemPanel.SetValue(FrameworkElement.WidthProperty, 180d);
        itemPanel.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        var image = new FrameworkElementFactory(typeof(Image));
        image.SetBinding(Image.SourceProperty, new Binding(nameof(StockSignalRow.ThumbnailUrl)));
        image.SetValue(FrameworkElement.WidthProperty, 32d);
        image.SetValue(FrameworkElement.HeightProperty, 32d);
        image.SetValue(Image.StretchProperty, Stretch.UniformToFill);
        itemPanel.AppendChild(image);

        var identity = new FrameworkElementFactory(typeof(StackPanel));
        identity.SetValue(FrameworkElement.WidthProperty, 138d);
        identity.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0));
        identity.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(StockSignalRow.Name)));
        name.SetValue(TextBlock.ForegroundProperty, TerminalText);
        name.SetValue(TextBlock.FontSizeProperty, 9.6d);
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        identity.AppendChild(name);
        var market = new FrameworkElementFactory(typeof(TextBlock));
        market.SetBinding(TextBlock.TextProperty, new Binding(nameof(StockSignalRow.Market)));
        market.SetValue(TextBlock.ForegroundProperty, TerminalMuted);
        market.SetValue(TextBlock.FontSizeProperty, 7.8d);
        market.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 0));
        identity.AppendChild(market);
        itemPanel.AppendChild(identity);
        root.AppendChild(itemPanel);

        root.AppendChild(StockCellFactory(nameof(StockSignalRow.PriceText), 72, TerminalText, true));
        root.AppendChild(StockCellFactory(nameof(StockSignalRow.ValueText), 68, TerminalGreen, false));
        root.AppendChild(StockCellFactory(nameof(StockSignalRow.ScoreText), 52, TerminalText, true));
        root.AppendChild(StockCellFactory(nameof(StockSignalRow.Signal), 82, TerminalBlue, true));
        return new DataTemplate { VisualTree = root };
    }

    private static FrameworkElementFactory StockCellFactory(string binding, double width, Brush foreground, bool bold)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(binding));
        text.SetValue(FrameworkElement.WidthProperty, width);
        text.SetValue(TextBlock.ForegroundProperty, foreground);
        text.SetValue(TextBlock.FontSizeProperty, 9d);
        text.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        if (bold) text.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        return text;
    }

    private async Task RefreshStockOverviewSourcesAsync()
    {
        if (_stockOverviewRefreshInProgress || !DashboardPage.IsVisible) return;
        _stockOverviewRefreshInProgress = true;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var needOfficial = _officialLastRefreshUtc is null || now - _officialLastRefreshUtc > TimeSpan.FromMinutes(5);
            var needUgc = _hunterLastRefreshUtc is null || now - _hunterLastRefreshUtc > TimeSpan.FromMinutes(3);

            if (needOfficial)
                await RefreshOfficialLimitedAsync(silent: _officialRows.Count > 0);

            if (needOfficial && needUgc)
                await Task.Delay(500);

            if (needUgc)
                await RefreshHunterAsync(silent: _hunterRows.Count > 0);
        }
        catch (Exception ex)
        {
            _services.Logger.Info($"Discover signal-board refresh kept cached data: {ex.Message}");
        }
        finally
        {
            _stockOverviewRefreshInProgress = false;
            UpdateStockSignalBoard();
        }
    }

    private void UpdateStockSignalBoard()
    {
        _stockOfficialSignals.Clear();
        foreach (var row in _officialRows
                     .Where(x => x.IsHuntCandidate)
                     .OrderByDescending(x => x.HuntScore)
                     .ThenByDescending(x => x.EvidenceScore)
                     .Take(5))
        {
            _stockOfficialSignals.Add(new StockSignalRow(
                row.AssetId,
                row.Name,
                row.ThumbnailUrl,
                "OFFICIAL",
                row.FloorText,
                row.DiscountText,
                row.HuntScore.ToString(CultureInfo.InvariantCulture),
                OfficialStockSignal(row)));
        }

        _stockUgcSignals.Clear();
        foreach (var item in _hunterRows
                     .Where(IsUgcBoardCandidate)
                     .OrderByDescending(x => x.ResalePotentialScore)
                     .ThenBy(x => x.RiskScore)
                     .Take(5))
        {
            _stockUgcSignals.Add(new StockSignalRow(
                item.AssetId,
                item.Name,
                item.ThumbnailUrl,
                "UGC",
                item.PriceText,
                item.NetRoiText,
                item.ResalePotentialText,
                UgcStockSignal(item)));
        }

        var officialDeals = _stockOfficialSignals.Count;
        var ugcPicks = _stockUgcSignals.Count;
        if (_stockOfficialDealCount is not null) _stockOfficialDealCount.Text = officialDeals.ToString(CultureInfo.InvariantCulture);
        if (_stockUgcPickCount is not null) _stockUgcPickCount.Text = ugcPicks.ToString(CultureInfo.InvariantCulture);
        if (_stockOfficialEmpty is not null) _stockOfficialEmpty.Visibility = officialDeals == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_stockUgcEmpty is not null) _stockUgcEmpty.Visibility = ugcPicks == 0 ? Visibility.Visible : Visibility.Collapsed;

        var bestOfficial = _officialRows.Where(x => x.IsHuntCandidate).Select(x => x.HuntScore).DefaultIfEmpty(0).Max();
        var bestUgc = _hunterRows.Where(IsUgcBoardCandidate).Select(x => (int)Math.Round(x.ResalePotentialScore)).DefaultIfEmpty(0).Max();
        var best = Math.Max(bestOfficial, bestUgc);
        if (_stockBestScore is not null) _stockBestScore.Text = best > 0 ? best.ToString(CultureInfo.InvariantCulture) : "—";

        var marketCall = best >= 85 ? "STRONG SETUP"
            : best >= 72 ? "BUY SETUPS"
            : best >= 58 ? "WATCH"
            : officialDeals + ugcPicks > 0 ? "SPECULATIVE" : "NO BUY SETUP";
        if (_stockMarketSignal is not null)
        {
            _stockMarketSignal.Text = marketCall;
            _stockMarketSignal.Foreground = marketCall switch
            {
                "STRONG SETUP" or "BUY SETUPS" => TerminalGreen,
                "WATCH" or "SPECULATIVE" => TerminalAmber,
                _ => TerminalMuted
            };
        }

        if (_stockBoardFreshness is not null)
        {
            var times = new List<DateTimeOffset>();
            if (_officialLastRefreshUtc is { } officialTime) times.Add(officialTime);
            if (_hunterLastRefreshUtc is { } ugcTime) times.Add(ugcTime);
            _stockBoardFreshness.Text = times.Count == 0
                ? "Scanning Official and UGC markets…"
                : $"Model signals · updated {times.Max().ToLocalTime():h:mm tt}";
        }
    }

    private static bool IsUgcBoardCandidate(UgcHunterItem item) =>
        item.Recommendation is "HIGH RESALE" or "STRONG" ||
        (item.Recommendation != "AVOID" && item.ResalePotentialScore >= 72 && item.EntryScore >= 55 && item.RiskScore < 70);

    private static string OfficialStockSignal(OfficialLimitedRow row) => row.Status switch
    {
        "BEST BUY" => "BEST BUY",
        "BUY ZONE" => "BUY ZONE",
        "PRICE DROP" => "BUY WATCH",
        "NEW DEAL" => "NEW DEAL",
        _ when row.HuntScore >= 80 => "STRONG",
        _ => "WATCH"
    };

    private static string UgcStockSignal(UgcHunterItem item) => item.Recommendation switch
    {
        "HIGH RESALE" => "STRONG",
        "STRONG" => "BUY WATCH",
        "SPECULATIVE" => "SPECULATIVE",
        "AVOID" => "AVOID",
        _ => item.ResalePotentialScore >= 78 && item.RiskScore < 55 ? "BUY WATCH" : "WATCH"
    };

    private void InsertAnalyzerSignalStrip()
    {
        if (_analyzerPage is null || _analyzerPage.RowDefinitions.Count < 3) return;

        foreach (UIElement child in _analyzerPage.Children)
        {
            var row = Grid.GetRow(child);
            if (row >= 2) Grid.SetRow(child, row + 1);
        }
        _analyzerPage.RowDefinitions.Insert(2, new RowDefinition { Height = new GridLength(86) });

        var panel = MakePanel();
        panel.Margin = new Thickness(0, 0, 0, 10);
        panel.Padding = new Thickness(12, 9, 12, 9);
        Grid.SetRow(panel, 2);
        Panel.SetZIndex(panel, 5);
        _analyzerPage.Children.Add(panel);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.45, GridUnitType.Star) });
        panel.Child = grid;

        _stockAnalyzerBackButton = new Button
        {
            Content = "← BACK",
            Style = (Style)FindResource("SecondaryButtonStyle"),
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 12, 0),
            Padding = new Thickness(10, 4, 10, 4),
            VerticalAlignment = VerticalAlignment.Center
        };
        _stockAnalyzerBackButton.Click += async (_, _) => await ReturnFromAnalyzerAsync();
        grid.Children.Add(_stockAnalyzerBackButton);

        _stockAnalyzerSignal = AddAnalyzerStockMetric(grid, 1, "MARKET CALL", "NO CALL", TerminalMuted);
        _stockAnalyzerPrice = AddAnalyzerStockMetric(grid, 2, "PRICE", "—", TerminalText);
        _stockAnalyzerValue = AddAnalyzerStockMetric(grid, 3, "VALUE", "—", TerminalGreen);
        _stockAnalyzerConfidence = AddAnalyzerStockMetric(grid, 4, "CONFIDENCE", "—", TerminalBlue);
        _stockAnalyzerRisk = AddAnalyzerStockMetric(grid, 5, "RISK", "—", TerminalAmber);

        var reasonStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        Grid.SetColumn(reasonStack, 6);
        grid.Children.Add(reasonStack);
        reasonStack.Children.Add(MakeSmallLabel("WHY"));
        _stockAnalyzerReason = new TextBlock
        {
            Text = "Analyze an item for a market call.",
            Foreground = TerminalMuted,
            FontSize = 8.6,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            MaxHeight = 38
        };
        reasonStack.Children.Add(_stockAnalyzerReason);
    }

    private static TextBlock AddAnalyzerStockMetric(Grid grid, int column, string label, string value, Brush brush)
    {
        var border = new Border
        {
            BorderBrush = TerminalBorder,
            BorderThickness = new Thickness(1, 0, 0, 0),
            Padding = new Thickness(13, 0, 0, 0),
            Margin = new Thickness(5, 0, 0, 0)
        };
        Grid.SetColumn(border, column);
        grid.Children.Add(border);
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        border.Child = stack;
        stack.Children.Add(MakeSmallLabel(label));
        var text = new TextBlock
        {
            Text = value,
            Foreground = brush,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        stack.Children.Add(text);
        return text;
    }

    private void UpdateStockAnalyzerBackButton()
    {
        if (_stockAnalyzerBackButton is null) return;
        if (!CanReturnFromAnalyzer())
        {
            _stockAnalyzerBackButton.Visibility = Visibility.Collapsed;
            return;
        }

        _stockAnalyzerBackButton.Content = $"← {StockPageLabel(_stockReturnPage!)}";
        _stockAnalyzerBackButton.Visibility = Visibility.Visible;
    }

    private static string StockPageLabel(string page) => page switch
    {
        "Dashboard" => "DISCOVER",
        "Hunt" => "OFFICIAL HUNT",
        "OfficialMarket" => "OFFICIAL MARKET",
        "Hunter" => "UGC HUNTER",
        "Watchlist" => "TRACKER",
        "Portfolio" => "PORTFOLIO",
        "Alerts" => "ALERTS",
        "Reports" => "HISTORY",
        _ => "BACK"
    };

    private void UpdateStockAnalyzerSignal()
    {
        UpdateStockAnalyzerBackButton();
        if (_stockAnalyzerSignal is null || _stockAnalyzerPrice is null || _stockAnalyzerValue is null ||
            _stockAnalyzerConfidence is null || _stockAnalyzerRisk is null || _stockAnalyzerReason is null)
            return;

        var item = _lastIntelligence;
        if (item is null)
        {
            SetStockAnalyzerMetric("NO CALL", "—", "—", "—", "—", "Analyze an item to see a buy/watch/avoid-style market signal.", TerminalMuted);
            return;
        }

        var official = _officialRows.FirstOrDefault(x => x.AssetId == item.Catalog.AssetId);
        if (official is not null)
        {
            var signal = official.IsHuntCandidate ? OfficialStockSignal(official)
                : official.HuntScore >= 60 ? "WATCH" : "PASS";
            var reason = string.IsNullOrWhiteSpace(official.ReasonText)
                ? "Official Limited score uses value, liquidity, stability, evidence, and risk."
                : official.ReasonText;
            SetStockAnalyzerMetric(
                signal,
                official.FloorText,
                official.DiscountText,
                StockConfidence(official.EvidenceScore),
                $"{official.RiskScore}/100",
                reason,
                StockSignalBrush(signal));
            return;
        }

        var ugc = _hunterRows.FirstOrDefault(x => x.AssetId == item.Catalog.AssetId);
        if (ugc is not null)
        {
            var signal = UgcStockSignal(ugc);
            var reason = ugc.Reasons.Count > 0 ? string.Join(" · ", ugc.Reasons.Take(2)) : "UGC resale model signal.";
            SetStockAnalyzerMetric(
                signal,
                ugc.CurrentResaleFloor is > 0 ? ugc.CurrentResaleText : ugc.PriceText,
                ugc.NetRoiText,
                $"{ugc.Confidence:0}%",
                $"{ugc.RiskScore:0}/100",
                reason,
                StockSignalBrush(signal));
            return;
        }

        var fallbackSignal = item.Recommendation switch
        {
            "HIGH RESALE" => "STRONG",
            "STRONG" => "BUY WATCH",
            "WATCH" => "WATCH",
            "SPECULATIVE" => "SPECULATIVE",
            "AVOID" => "AVOID",
            "INSUFFICIENT DATA" => "NO CALL",
            _ => "WATCH"
        };
        var fallbackPrice = item.CurrentFloor is > 0 ? $"{item.CurrentFloor.Value:N0} R$"
            : item.OriginalPrice is > 0 ? $"{item.OriginalPrice.Value:N0} R$" : "—";
        var fallbackValue = item.CurrentFloorNetRoi is { } roi ? roi.ToString("+0%;-0%;0%")
            : item.Score is not null ? item.Score.BaseNetRoi.ToString("+0%;-0%;0%") : "—";
        var fallbackRisk = item.Score is not null ? $"{item.Score.RiskScore:0}/100" : "—";
        var reasonText = item.Quality.Score < 45
            ? "Not enough verified market evidence for a reliable call."
            : item.Score?.Reasons.FirstOrDefault() ?? "Analyzer is using the available Roblox market evidence.";
        SetStockAnalyzerMetric(
            fallbackSignal,
            fallbackPrice,
            fallbackValue,
            $"{item.Quality.Score}%",
            fallbackRisk,
            reasonText,
            StockSignalBrush(fallbackSignal));
    }

    private void SetStockAnalyzerMetric(string signal, string price, string value, string confidence, string risk, string reason, Brush signalBrush)
    {
        if (_stockAnalyzerSignal is not null) { _stockAnalyzerSignal.Text = signal; _stockAnalyzerSignal.Foreground = signalBrush; }
        if (_stockAnalyzerPrice is not null) _stockAnalyzerPrice.Text = price;
        if (_stockAnalyzerValue is not null) _stockAnalyzerValue.Text = value;
        if (_stockAnalyzerConfidence is not null) _stockAnalyzerConfidence.Text = confidence;
        if (_stockAnalyzerRisk is not null) _stockAnalyzerRisk.Text = risk;
        if (_stockAnalyzerReason is not null) _stockAnalyzerReason.Text = reason;
    }

    private static string StockConfidence(int score) => score switch
    {
        >= 85 => "HIGH",
        >= 65 => "MEDIUM",
        _ => "LOW"
    };

    private static Brush StockSignalBrush(string signal) => signal switch
    {
        "BEST BUY" or "BUY ZONE" or "STRONG" or "BUY WATCH" or "NEW DEAL" => TerminalGreen,
        "WATCH" or "SPECULATIVE" => TerminalAmber,
        "AVOID" or "PASS" => TerminalRed,
        _ => TerminalMuted
    };

    private void UpdateStockVersionText()
    {
        foreach (var text in FindVisualChildren<TextBlock>(this))
        {
            if (text.Text.Contains("3AYZE", StringComparison.OrdinalIgnoreCase) &&
                text.Text.TrimStart().StartsWith("v0.", StringComparison.OrdinalIgnoreCase))
            {
                text.Text = "v0.11.1 · 3AYZE";
            }
        }
    }

    private sealed record StockSignalRow(
        long AssetId,
        string Name,
        string? ThumbnailUrl,
        string Market,
        string PriceText,
        string ValueText,
        string ScoreText,
        string Signal);
}
