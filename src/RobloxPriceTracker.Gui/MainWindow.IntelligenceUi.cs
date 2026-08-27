using RobloxPriceTracker.Core;

namespace RobloxPriceTracker.Gui;

public partial class MainWindow
{
    private RadioButton? _analyzerNav;
    private Grid? _analyzerPage;
    private TextBox? _analyzerInput;
    private TextBlock? _analyzerStatus;
    private TextBlock? _analyzerTitle;
    private TextBlock? _analyzerSubtitle;
    private TextBlock? _analyzerRecommendation;
    private TextBlock? _analyzerQuality;
    private TextBlock? _analyzerPrimary;
    private TextBlock? _analyzerResale;
    private TextBlock? _analyzerEconomics;
    private TextBlock? _analyzerEvidence;
    private TextBlock? _analyzerCreatorSummary;
    private TextBlock? _analyzerCreatorStats;
    private TextBlock? _analyzerCreatorItems;
    private Button? _analyzerOpenButton;
    private RobloxUgcItemIntelligenceService? _itemIntelligenceService;
    private RobloxUgcItemIntelligence? _lastIntelligence;
    private CancellationTokenSource? _analyzerCts;

    private void ConfigureIntelligenceUi()
    {
        _itemIntelligenceService = new RobloxUgcItemIntelligenceService(_services.HttpClient, _services.Logger, _services.ResaleDataService);

        if (DashboardNav.Parent is StackPanel navStack)
        {
            _analyzerNav = CreateTerminalNav("Analyzer", AnalyzerNav_Checked);
            var hunterIndex = _hunterNav is not null ? navStack.Children.IndexOf(_hunterNav) : -1;
            var insertAt = hunterIndex >= 0 ? hunterIndex + 1 : Math.Min(navStack.Children.IndexOf(DashboardNav) + 1, navStack.Children.Count);
            navStack.Children.Insert(Math.Clamp(insertAt, 0, navStack.Children.Count), _analyzerNav);
        }

        if (DashboardPage.Parent is Grid contentHost)
        {
            _analyzerPage = BuildAnalyzerPage();
            contentHost.Children.Add(_analyzerPage);
        }

        if (_hunterGrid is not null)
        {
            _hunterGrid.Columns.Insert(Math.Min(4, _hunterGrid.Columns.Count),
                HunterTextColumn("DATA", nameof(UgcHunterItem.DataQualityText), 0.82, TerminalBlue, true));
            _hunterGrid.SelectionChanged += (_, _) => UpdateHunterEvidenceQuality();
        }

        EnhanceHunterActions();
    }

    private void AnalyzerNav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        ShowPage("Analyzer");
        _analyzerInput?.Focus();
    }

    private void SetIntelligencePageVisibility(string page)
    {
        if (_analyzerPage is not null)
            _analyzerPage.Visibility = page == "Analyzer" ? Visibility.Visible : Visibility.Collapsed;
    }

    private Grid BuildAnalyzerPage()
    {
        var root = new Grid
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(16, 14, 16, 16),
            Background = Brushes.Transparent
        };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(72) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.Children.Add(header);

        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(titleStack);
        titleStack.Children.Add(new TextBlock
        {
            Text = "DIRECT ITEM ANALYZER",
            Foreground = TerminalText,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = "Paste any Roblox catalog URL or asset ID. Real Roblox data is kept separate from model estimates.",
            Foreground = TerminalMuted,
            FontSize = 8.8,
            Margin = new Thickness(0, 5, 0, 0)
        });

        var inputStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(inputStack, 1);
        header.Children.Add(inputStack);
        _analyzerInput = new TextBox
        {
            Width = 390,
            ToolTip = "Roblox catalog URL or asset ID"
        };
        _analyzerInput.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await AnalyzeCurrentInputAsync();
            }
        };
        inputStack.Children.Add(_analyzerInput);

        var analyzeButton = new Button
        {
            Content = "ANALYZE",
            Style = (Style)FindResource("PrimaryButtonStyle"),
            Margin = new Thickness(7, 0, 0, 0)
        };
        analyzeButton.Click += async (_, _) => await AnalyzeCurrentInputAsync();
        inputStack.Children.Add(analyzeButton);

        var body = new Grid();
        Grid.SetRow(body, 2);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.45, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.85, GridUnitType.Star) });
        root.Children.Add(body);

        var itemPanel = MakePanel();
        body.Children.Add(itemPanel);
        var itemScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        itemPanel.Child = itemScroll;
        var itemStack = new StackPanel { Margin = new Thickness(16) };
        itemScroll.Content = itemStack;

        _analyzerStatus = new TextBlock
        {
            Text = "READY",
            Foreground = TerminalBlue,
            FontSize = 8.5,
            FontWeight = FontWeights.SemiBold
        };
        itemStack.Children.Add(_analyzerStatus);
        _analyzerTitle = new TextBlock
        {
            Text = "Analyze a Limited or UGC item",
            Foreground = TerminalText,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        itemStack.Children.Add(_analyzerTitle);
        _analyzerSubtitle = new TextBlock
        {
            Text = "No item loaded",
            Foreground = TerminalMuted,
            FontSize = 9,
            Margin = new Thickness(0, 5, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        itemStack.Children.Add(_analyzerSubtitle);

        var verdictGrid = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        verdictGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        verdictGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        itemStack.Children.Add(verdictGrid);
        _analyzerRecommendation = AddScore(verdictGrid, 0, "RECOMMENDATION", "—", TerminalGreen);
        _analyzerQuality = AddScore(verdictGrid, 1, "DATA QUALITY", "—", TerminalBlue);

        itemStack.Children.Add(MakeSeparator(18, 14));
        _analyzerPrimary = AddAnalyzerBlock(itemStack, "PRIMARY MARKET");
        itemStack.Children.Add(MakeSeparator(16, 14));
        _analyzerResale = AddAnalyzerBlock(itemStack, "RESALE MARKET");
        itemStack.Children.Add(MakeSeparator(16, 14));
        _analyzerEconomics = AddAnalyzerBlock(itemStack, "ECONOMICS + MODEL");
        itemStack.Children.Add(MakeSeparator(16, 14));
        _analyzerEvidence = AddAnalyzerBlock(itemStack, "DATA EVIDENCE");

        _analyzerOpenButton = new Button
        {
            Content = "OPEN ROBLOX",
            Style = (Style)FindResource("SecondaryButtonStyle"),
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 18, 0, 0)
        };
        _analyzerOpenButton.Click += (_, _) =>
        {
            if (_lastIntelligence is not null) OpenRobloxAsset(_lastIntelligence.Catalog.AssetId);
        };
        itemStack.Children.Add(_analyzerOpenButton);

        var creatorPanel = MakePanel();
        Grid.SetColumn(creatorPanel, 2);
        body.Children.Add(creatorPanel);
        var creatorScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        creatorPanel.Child = creatorScroll;
        var creatorStack = new StackPanel { Margin = new Thickness(16) };
        creatorScroll.Content = creatorStack;
        creatorStack.Children.Add(MakeSmallLabel("CREATOR INTELLIGENCE"));
        _analyzerCreatorSummary = new TextBlock
        {
            Text = "Analyze an item to inspect its creator track record.",
            Foreground = TerminalText,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        creatorStack.Children.Add(_analyzerCreatorSummary);
        _analyzerCreatorStats = new TextBlock
        {
            Text = "—",
            Foreground = TerminalMuted,
            FontSize = 9,
            LineHeight = 17,
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        creatorStack.Children.Add(_analyzerCreatorStats);
        creatorStack.Children.Add(MakeSeparator(16, 13));
        creatorStack.Children.Add(MakeSmallLabel("RECENT LIMITED SAMPLE"));
        _analyzerCreatorItems = new TextBlock
        {
            Text = "—",
            Foreground = new SolidColorBrush(Color.FromRgb(169, 182, 197)),
            FontSize = 8.7,
            LineHeight = 17,
            Margin = new Thickness(0, 9, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        creatorStack.Children.Add(_analyzerCreatorItems);
        creatorStack.Children.Add(new TextBlock
        {
            Text = "Creator intelligence samples up to 24 recent Limiteds. Current floor profitability uses the same 50% reseller proceeds assumption as Hunter.",
            Foreground = TerminalMuted,
            FontSize = 7.8,
            Margin = new Thickness(0, 14, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        return root;
    }

    private static TextBlock AddAnalyzerBlock(Panel panel, string label)
    {
        panel.Children.Add(MakeSmallLabel(label));
        var text = new TextBlock
        {
            Text = "—",
            Foreground = TerminalText,
            FontSize = 10,
            LineHeight = 18,
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        panel.Children.Add(text);
        return text;
    }

    private async Task AnalyzeCurrentInputAsync()
    {
        if (_itemIntelligenceService is null || _analyzerInput is null) return;
        if (!ItemInputParser.TryParseAsset(_analyzerInput.Text ?? string.Empty, out var key, out var error))
        {
            SetAnalyzerStatus("INVALID INPUT", TerminalRed);
            if (_analyzerTitle is not null) _analyzerTitle.Text = error ?? "Enter a Roblox catalog URL or asset ID.";
            return;
        }

        _analyzerCts?.Cancel();
        _analyzerCts?.Dispose();
        _analyzerCts = new CancellationTokenSource();
        var token = _analyzerCts.Token;
        SetAnalyzerStatus("ANALYZING ROBLOX DATA…", TerminalAmber);
        if (_analyzerOpenButton is not null) _analyzerOpenButton.IsEnabled = false;

        try
        {
            var result = await _itemIntelligenceService.AnalyzeAsync(key.Id, token);
            _lastIntelligence = result;
            RenderAnalyzerResult(result);
            if (_analyzerOpenButton is not null) _analyzerOpenButton.IsEnabled = true;
            SetAnalyzerStatus($"UPDATED {result.ObservedAtUtc.ToLocalTime():h:mm:ss tt}", TerminalGreen);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            SetAnalyzerStatus("CANCELED", TerminalMuted);
        }
        catch (Exception ex)
        {
            _services.Logger.Error($"Direct item analyzer failed for {key.Id}: {ex}");
            SetAnalyzerStatus("ANALYSIS FAILED", TerminalRed);
            if (_analyzerTitle is not null) _analyzerTitle.Text = "Could not analyze this item";
            if (_analyzerSubtitle is not null) _analyzerSubtitle.Text = ex.Message;
        }
    }

    private void RenderAnalyzerResult(RobloxUgcItemIntelligence item)
    {
        var catalog = item.Catalog;
        var score = item.Score;
        var floor = item.CurrentFloor;

        if (_analyzerTitle is not null) _analyzerTitle.Text = catalog.Name;
        if (_analyzerSubtitle is not null)
            _analyzerSubtitle.Text = $"{catalog.CreatorName} · {catalog.CreatorType} {catalog.CreatorId} · Asset {catalog.AssetId} · {catalog.AssetType}";
        if (_analyzerRecommendation is not null)
        {
            _analyzerRecommendation.Text = item.Recommendation;
            _analyzerRecommendation.Foreground = item.Recommendation switch
            {
                "HIGH RESALE" or "STRONG" => TerminalGreen,
                "AVOID" => TerminalRed,
                "INSUFFICIENT DATA" => TerminalAmber,
                _ => TerminalBlue
            };
        }
        if (_analyzerQuality is not null)
        {
            _analyzerQuality.Text = item.Quality.Summary;
            _analyzerQuality.Foreground = item.Quality.Score switch
            {
                >= 85 => TerminalGreen,
                >= 70 => TerminalBlue,
                >= 50 => TerminalAmber,
                _ => TerminalRed
            };
        }

        if (_analyzerPrimary is not null)
        {
            _analyzerPrimary.Text =
                $"Price       {FormatRobux(item.OriginalPrice)}\n" +
                $"Sold        {FormatNumber(item.Sold)}\n" +
                $"Remaining   {FormatNumber(item.Remaining)}\n" +
                $"Supply      {FormatNumber(item.TotalSupply)}\n" +
                $"Buyable     {(item.Marketplace?.IsPrimaryPurchasable == true || catalog.IsShopPurchasable ? "YES" : "NO")}\n" +
                $"Source      {(item.Marketplace is { IsAvailable: true } ? "Roblox Marketplace Items" : "Catalog details")}";
        }

        if (_analyzerResale is not null)
        {
            _analyzerResale.Text =
                $"Floor       {FormatRobux(floor)}\n" +
                $"RAP         {(item.Rap is > 0 ? $"{item.Rap.Value:N0} R$" : "—")}\n" +
                $"Sellers     {(item.SellerCount > 0 ? item.SellerCount.ToString("N0") + (item.Resellers?.HasMoreListings == true ? "+" : string.Empty) : "—")}\n" +
                $"Resales 7d  {(item.Resale?.HasSalesSeries == true ? item.Sales7d.ToString("0.#") : "—")}\n" +
                $"Resales 30d {(item.Resale?.HasSalesSeries == true ? item.Sales30d.ToString("0.#") : "—")}";
        }

        if (_analyzerEconomics is not null)
        {
            var floorRoi = item.CurrentFloorNetRoi is { } roi ? roi.ToString("+0%;-0%;0%") : "—";
            _analyzerEconomics.Text = score is null
                ? "This item does not have enough positive-price Limited data for resale economics."
                : $"Break-even resale  {score.BreakEvenResale:N0} R$\n" +
                  $"Current floor ROI  {floorRoi}\n" +
                  $"Liquidity         {score.LiquidityLabel} · {score.LiquidityScore:0}/100\n" +
                  $"Demand            {score.DemandScore:0}/100\n" +
                  $"Profitability     {score.ProfitabilityScore:0}/100\n" +
                  $"Risk              {score.RiskScore:0}/100\n" +
                  $"Model bear/base/bull  {score.BearResale:N0} / {score.BaseResale:N0} / {score.BullResale:N0} R$\n" +
                  $"Model base net ROI    {score.BaseNetRoi:+0%;-0%;0%}";
        }

        if (_analyzerEvidence is not null)
        {
            var present = item.Quality.Evidence.Count == 0 ? "none" : string.Join(" · ", item.Quality.Evidence.Select(x => $"✓ {x}"));
            var missing = item.Quality.Missing.Count == 0 ? "none" : string.Join(" · ", item.Quality.Missing.Select(x => $"— {x}"));
            _analyzerEvidence.Text = $"Verified: {present}\nMissing: {missing}\nObserved: {item.ObservedAtUtc.ToLocalTime():g}";
        }

        RenderCreatorIntelligence(item.Creator);
    }

    private void RenderCreatorIntelligence(RobloxCreatorIntelligence? creator)
    {
        if (creator is null)
        {
            if (_analyzerCreatorSummary is not null) _analyzerCreatorSummary.Text = "Creator intelligence unavailable";
            if (_analyzerCreatorStats is not null) _analyzerCreatorStats.Text = "No usable creator ID was returned for this asset.";
            if (_analyzerCreatorItems is not null) _analyzerCreatorItems.Text = "—";
            return;
        }

        if (_analyzerCreatorSummary is not null)
        {
            _analyzerCreatorSummary.Text = $"{creator.CreatorName}\n{creator.TrackRecord} TRACK RECORD";
            _analyzerCreatorSummary.Foreground = creator.TrackRecord switch
            {
                "STRONG" => TerminalGreen,
                "WEAK" => TerminalRed,
                "MIXED" => TerminalAmber,
                _ => TerminalText
            };
        }

        if (_analyzerCreatorStats is not null)
        {
            var profitable = creator.ProfitableShare is { } share
                ? $"{creator.ProfitableAtFloorCount}/{creator.FloorSampleCount} ({share:P0})"
                : "—";
            var sellThrough = creator.AverageSellThrough is { } sold ? sold.ToString("P0") : "—";
            var multiple = creator.MedianGrossResaleMultiple is { } m ? $"{m:0.00}×" : "—";
            _analyzerCreatorStats.Text =
                $"Sampled Limiteds   {creator.SampleSize}\n" +
                $"Roblox verified    {creator.VerifiedCount}\n" +
                $"Active / sold out  {creator.ActiveCount} / {creator.SoldOutCount}\n" +
                $"Profitable floors  {profitable}\n" +
                $"Avg sell-through   {sellThrough}\n" +
                $"Median floor/cost  {multiple}" +
                (string.IsNullOrWhiteSpace(creator.Error) ? string.Empty : $"\nCreator data note  {creator.Error}");
        }

        if (_analyzerCreatorItems is not null)
        {
            _analyzerCreatorItems.Text = creator.Items.Count == 0
                ? "No recent Limited sample was returned."
                : string.Join("\n", creator.Items.Take(8).Select(x =>
                {
                    var sell = x.SellThrough is { } st ? st.ToString("P0") : "—";
                    var floor = FormatRobux(x.CurrentFloor);
                    var cost = x.OriginalPrice is > 0 ? $"{x.OriginalPrice:N0} R$" : "—";
                    return $"{x.Name}\n  {cost} → {floor} · sold {sell}";
                }));
        }
    }

    private void SetAnalyzerStatus(string text, Brush brush)
    {
        if (_analyzerStatus is null) return;
        _analyzerStatus.Text = text;
        _analyzerStatus.Foreground = brush;
    }

    private void EnhanceHunterActions()
    {
        if (_hunterOpenRobloxButton?.Parent is not Grid actions || _hunterTrackButton is null) return;

        actions.ColumnDefinitions.Clear();
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(7) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(7) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_hunterOpenRobloxButton, 0);
        Grid.SetColumn(_hunterTrackButton, 4);

        var analyze = new Button
        {
            Content = "ANALYZE",
            Style = (Style)FindResource("SecondaryButtonStyle"),
            ToolTip = "Open this item in the deeper Analyzer workspace"
        };
        analyze.Click += async (_, _) =>
        {
            if (_hunterGrid?.SelectedItem is not UgcHunterItem item || _analyzerInput is null) return;
            _analyzerInput.Text = item.RobloxUrl;
            if (_analyzerNav is not null) _analyzerNav.IsChecked = true;
            else ShowPage("Analyzer");
            await AnalyzeCurrentInputAsync();
        };
        Grid.SetColumn(analyze, 2);
        actions.Children.Add(analyze);
    }

    private void UpdateHunterEvidenceQuality()
    {
        if (_hunterGrid?.SelectedItem is not UgcHunterItem item || _hunterInspectorSupply is null) return;
        _hunterInspectorSupply.Text =
            $"{item.RemainingText} remaining / {item.SupplyText} total · {item.VerificationText}\n" +
            $"DATA {item.DataQualityText} · {item.FreshnessText}";
        _hunterInspectorSupply.ToolTip = item.EvidenceText;
    }

    private static string FormatRobux(long? value) => value is > 0 ? $"{value.Value:N0} R$" : "—";
    private static string FormatRobux(int? value) => value is > 0 ? $"{value.Value:N0} R$" : "—";
    private static string FormatNumber(long? value) => value is { } number ? number.ToString("N0") : "—";
}
