using System.Windows.Navigation;

namespace RobloxPriceTracker.Gui;

/// <summary>
/// On-demand read-only research for collectible animation bundles. Bundle IDs are never
/// sent through the UGC asset scanner, resale analyzer, tracker, or paper portfolio.
/// </summary>
public sealed class AnimationBundleResearchWindow : Window
{
    private readonly RobloxAnimationBundleResearchService _research;
    private readonly RobloxThumbnailService _thumbnails;
    private readonly ObservableCollection<RobloxAnimationBundleOffer> _rows = [];
    private readonly ICollectionView _view;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DataGrid _grid;
    private readonly TextBox _search;
    private readonly Button _refresh;
    private readonly TextBlock _status;
    private bool _refreshing;

    public AnimationBundleResearchWindow(AppServices services)
    {
        _research = new RobloxAnimationBundleResearchService(services.HttpClient, services.Logger);
        _thumbnails = services.ThumbnailService;
        Title = "Animation Packs · Original-Price Research";
        Width = 1100;
        Height = 650;
        MinWidth = 850;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(13, 18, 24));
        Foreground = Brushes.WhiteSmoke;

        var root = new DockPanel { Margin = new Thickness(18) };
        Content = root;

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        header.Children.Add(new TextBlock
        {
            Text = "LIMITED ANIMATION BUNDLES",
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.WhiteSmoke
        });
        header.Children.Add(new TextBlock
        {
            Text = "BundleType.Animations (2) · official September 2026 animation-pack price floor: 300 R$.\n" +
                   "Only Marketplace-verified creator-stock quotes appear under ORIGINAL. Catalog search prices and resale asks never become original-sale evidence. Research only: no investment recommendations.",
            Foreground = Brushes.LightGray,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 8, 0, 0)
        });

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);
        _search = new TextBox { Width = 290, Height = 30, VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Filter bundle name, creator or numeric bundle ID" };
        toolbar.Children.Add(_search);
        _refresh = new Button { Content = "REFRESH BUNDLES", Width = 148, Margin = new Thickness(10, 0, 0, 0) };
        _refresh.Click += async (_, _) => await RefreshAsync();
        toolbar.Children.Add(_refresh);
        var open = new Button { Content = "OPEN ON ROBLOX", Width = 150, Margin = new Thickness(10, 0, 0, 0) };
        toolbar.Children.Add(open);

        _status = new TextBlock
        {
            Text = "Not scanned · click REFRESH BUNDLES",
            Margin = new Thickness(14, 7, 0, 0),
            Foreground = Brushes.LightGray,
            TextWrapping = TextWrapping.Wrap
        };
        toolbar.Children.Add(_status);

        _grid = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            RowHeight = 56,
            Background = new SolidColorBrush(Color.FromRgb(18, 26, 35)),
            Foreground = Brushes.WhiteSmoke,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            SelectionMode = DataGridSelectionMode.Single,
            HeadersVisibility = DataGridHeadersVisibility.Column
        };
        _grid.Columns.Add(CreateBundlePictureColumn());
        _grid.Columns.Add(Column("CREATOR", nameof(RobloxAnimationBundleOffer.CreatorName), 1.2));
        _grid.Columns.Add(Column("BUNDLE ID", nameof(RobloxAnimationBundleOffer.BundleId), 1.1));
        _grid.Columns.Add(Column("ORIGINAL R$", nameof(RobloxAnimationBundleOffer.OriginalPriceText), 1));
        _grid.Columns.Add(Column("ORIGINAL STOCK", nameof(RobloxAnimationBundleOffer.OriginalStockText), 1));
        _grid.Columns.Add(Column("RESALE ASK", nameof(RobloxAnimationBundleOffer.ResaleAskText), 1));
        _grid.Columns.Add(Column("EVIDENCE", nameof(RobloxAnimationBundleOffer.VerificationStatus), 2.1));
        root.Children.Add(_grid);

        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = Filter;
        _grid.ItemsSource = _view;
        // Subscribe only after all referenced controls have been initialized.
        _search.TextChanged += (_, _) => _view.Refresh();
        open.Click += (_, _) =>
        {
            if (_grid.SelectedItem is not RobloxAnimationBundleOffer selected) return;
            try { Process.Start(new ProcessStartInfo(selected.RobloxUrl) { UseShellExecute = true }); }
            catch (Exception ex) { _status.Text = $"Cannot open Roblox: {ex.Message}"; }
        };

        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) =>
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
    }

    private static DataGridTemplateColumn CreateBundlePictureColumn()
    {
        var row = new FrameworkElementFactory(typeof(StackPanel));
        row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var frame = new FrameworkElementFactory(typeof(Border));
        frame.SetValue(FrameworkElement.WidthProperty, 46d);
        frame.SetValue(FrameworkElement.HeightProperty, 46d);
        frame.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(28, 36, 46)));
        frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        var picture = new FrameworkElementFactory(typeof(Image));
        picture.SetBinding(Image.SourceProperty, new Binding(nameof(RobloxAnimationBundleOffer.ThumbnailUrl)));
        picture.SetValue(FrameworkElement.WidthProperty, 44d);
        picture.SetValue(FrameworkElement.HeightProperty, 44d);
        picture.SetValue(Image.StretchProperty, Stretch.Uniform);
        frame.AppendChild(picture);
        row.AppendChild(frame);

        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(RobloxAnimationBundleOffer.Name)));
        text.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetValue(TextBlock.MarginProperty, new Thickness(9, 0, 0, 0));
        text.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        row.AppendChild(text);
        return new DataGridTemplateColumn
        {
            Header = "BUNDLE",
            CellTemplate = new DataTemplate { VisualTree = row },
            Width = new DataGridLength(2.6, DataGridLengthUnitType.Star)
        };
    }

    private static DataGridTextColumn Column(string title, string path, double proportion) => new()
    {
        Header = title,
        Binding = new Binding(path),
        Width = new DataGridLength(proportion, DataGridLengthUnitType.Star)
    };

    private bool Filter(object value)
    {
        if (value is not RobloxAnimationBundleOffer item) return false;
        var text = _search.Text.Trim();
        return text.Length == 0 || item.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
            item.CreatorName.Contains(text, StringComparison.OrdinalIgnoreCase) ||
            item.BundleId.ToString(CultureInfo.InvariantCulture).Contains(text, StringComparison.Ordinal);
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        _refresh.IsEnabled = false;
        _status.Text = "Discovering and verifying original-stock bundle prices…";
        try
        {
            var found = await _research.DiscoverAsync(_lifetime.Token);
            // Keep verification results visible immediately. Optional PNGs never
            // affect prices or hide a valid bundle when thumbnails fail.
            _rows.Clear();
            foreach (var offer in found) _rows.Add(offer);
            var verified = found.Count(x => x.PrimaryMarketVerified);
            try
            {
                var imageUrls = await _thumbnails.GetBundleThumbnailUrlsAsync(
                    found.Select(x => x.BundleId), _lifetime.Token);
                for (var i = 0; i < _rows.Count; i++)
                    if (imageUrls.TryGetValue(_rows[i].BundleId, out var image))
                        _rows[i] = _rows[i] with { ThumbnailUrl = image };
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
            catch (Exception) { /* Pictures are optional research decoration. */ }
            _status.Text = $"{found.Count} bundles · {verified} verified original-stock offers";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _status.Text = $"Bundle research unavailable: {ex.Message}";
        }
        finally
        {
            _refreshing = false;
            if (!_lifetime.IsCancellationRequested) _refresh.IsEnabled = true;
        }
    }
}
