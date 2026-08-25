using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace RobloxPriceTracker.Gui;

public partial class MainWindow : Window
{
    private void ConfigureStockUi()
    {
        ConfigureStockColumns(DashboardWatchlistList, compact: true);
        ConfigureStockColumns(WatchlistGrid, compact: false);

        if (!WatchlistSortCombo.Items.OfType<ComboBoxItem>().Any(x => Equals(x.Tag, "Change")))
        {
            WatchlistSortCombo.Items.Add(new ComboBoxItem { Content = "24H change", Tag = "Change" });
        }
        if (!WatchlistSortCombo.Items.OfType<ComboBoxItem>().Any(x => Equals(x.Tag, "ForecastConfidence")))
        {
            WatchlistSortCombo.Items.Add(new ComboBoxItem { Content = "Forecast confidence", Tag = "ForecastConfidence" });
            WatchlistSortCombo.Items.Add(new ComboBoxItem { Content = "Forecast price", Tag = "ForecastPrice" });
        }

        if (!ReportRangeCombo.Items.OfType<ComboBoxItem>().Any(x => Equals(x.Tag, "1H")))
        {
            ReportRangeCombo.Items.Insert(0, new ComboBoxItem { Content = "1 Hour", Tag = "1H" });
        }

        WatchlistSearchBox.ToolTip = "Ctrl+F · Search item name or asset ID";
        WatchlistGrid.ToolTip = "Forecasts are statistical estimates, not guaranteed prices. Enter: details · Delete: remove · F5: refresh";
        PreviewKeyDown += Window_StockPreviewKeyDown;
        Loaded += (_, _) => ApplyStockBranding();
    }

    private void ConfigureStockColumns(DataGrid grid, bool compact)
    {
        if (grid.Columns.Count == 0) return;

        var asset = grid.Columns[0];
        DataGridColumn? signal = compact
            ? grid.Columns.Count > 3 ? grid.Columns[3] : null
            : grid.Columns.Count > 5 ? grid.Columns[5] : null;

        grid.Columns.Clear();
        grid.Columns.Add(asset);
        grid.Columns.Add(CreateTextColumn("LAST", nameof(WatchlistRow.CurrentPrice), compact ? 0.90 : 0.84, nameof(WatchlistRow.PriceForeground), fontSize: compact ? 11d : 11.5d, semiBold: true));
        grid.Columns.Add(CreateMovementColumn(compact ? 0.82 : 0.82));
        grid.Columns.Add(CreateSparklineColumn(compact ? 0.95 : 0.96));
        grid.Columns.Add(CreateForecastColumn(compact ? 1.10 : 1.05));
        grid.Columns.Add(CreateTextColumn("CONF", nameof(WatchlistRow.ForecastConfidence), compact ? 0.58 : 0.58, nameof(WatchlistRow.ForecastForeground), fontSize: 9.5d, semiBold: true));

        if (!compact)
        {
            grid.Columns.Add(CreateTextColumn("SALES/D", nameof(WatchlistRow.ForecastSalesVelocity), 0.72, null, "#A7B2C0", 9d));
            grid.Columns.Add(CreateTextColumn("TARGET 24H", nameof(WatchlistRow.ForecastTarget24h), 0.72, null, "#C7D0DA", 9.5d, true));
            grid.Columns.Add(CreateTextColumn("VS TARGET", nameof(WatchlistRow.TargetDistance), 1.15, null, "#8996A6", 8.7d));
        }

        grid.Columns.Add(CreateTextColumn("TARGET", nameof(WatchlistRow.TargetPrice), compact ? 0.82 : 0.78, null, "#C7D0DA", compact ? 9.8d : 10d));

        if (signal is not null)
        {
            signal.Header = "SIGNAL";
            signal.Width = new DataGridLength(compact ? 0.85 : 0.82, DataGridLengthUnitType.Star);
            grid.Columns.Add(signal);
        }

        if (!compact)
        {
            grid.Columns.Add(CreateTextColumn("UPDATED", nameof(WatchlistRow.LastChecked), 0.86, null, "#758294", 8.7d));
        }

        grid.RowHeight = compact ? 56 : 60;
        grid.ColumnHeaderHeight = 34;
    }

    private static DataGridTemplateColumn CreateTextColumn(
        string header,
        string bindingPath,
        double width,
        string? foregroundBinding = null,
        string? fixedForeground = null,
        double fontSize = 10.5,
        bool semiBold = false)
    {
        var factory = new FrameworkElementFactory(typeof(TextBlock));
        factory.SetBinding(TextBlock.TextProperty, new Binding(bindingPath));
        factory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        factory.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        factory.SetValue(TextBlock.FontSizeProperty, fontSize);
        if (semiBold) factory.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        if (!string.IsNullOrWhiteSpace(foregroundBinding))
        {
            factory.SetBinding(TextBlock.ForegroundProperty, new Binding(foregroundBinding));
        }
        else if (!string.IsNullOrWhiteSpace(fixedForeground))
        {
            factory.SetValue(TextBlock.ForegroundProperty, (Brush)new BrushConverter().ConvertFromString(fixedForeground)!);
        }

        return new DataGridTemplateColumn
        {
            Header = header,
            Width = new DataGridLength(width, DataGridLengthUnitType.Star),
            CellTemplate = new DataTemplate { VisualTree = factory }
        };
    }

    private static DataGridTemplateColumn CreateMovementColumn(double width)
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.VerticalAlignmentProperty, VerticalAlignment.Center);

        var percent = new FrameworkElementFactory(typeof(TextBlock));
        percent.SetBinding(TextBlock.TextProperty, new Binding(nameof(WatchlistRow.Change24h)));
        percent.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(WatchlistRow.TrendForeground)));
        percent.SetValue(TextBlock.FontSizeProperty, 10.2d);
        percent.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        panel.AppendChild(percent);

        var amount = new FrameworkElementFactory(typeof(TextBlock));
        amount.SetBinding(TextBlock.TextProperty, new Binding(nameof(WatchlistRow.Change24hAmount)));
        amount.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(103, 116, 132)));
        amount.SetValue(TextBlock.FontSizeProperty, 8.0d);
        amount.SetValue(TextBlock.MarginProperty, new Thickness(0, 2, 0, 0));
        panel.AppendChild(amount);

        return new DataGridTemplateColumn
        {
            Header = "24H",
            Width = new DataGridLength(width, DataGridLengthUnitType.Star),
            CellTemplate = new DataTemplate { VisualTree = panel }
        };
    }

    private static DataGridTemplateColumn CreateSparklineColumn(double width)
    {
        var spark = new FrameworkElementFactory(typeof(MiniSparkline));
        spark.SetBinding(MiniSparkline.ValuesProperty, new Binding(nameof(WatchlistRow.SparklineValues)));
        spark.SetBinding(MiniSparkline.StrokeProperty, new Binding(nameof(WatchlistRow.TrendForeground)));
        spark.SetValue(FrameworkElement.HeightProperty, 28d);
        spark.SetValue(FrameworkElement.MinWidthProperty, 58d);
        spark.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        return new DataGridTemplateColumn
        {
            Header = "TREND",
            Width = new DataGridLength(width, DataGridLengthUnitType.Star),
            CellTemplate = new DataTemplate { VisualTree = spark }
        };
    }

    private static DataGridTemplateColumn CreateForecastColumn(double width)
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.VerticalAlignmentProperty, VerticalAlignment.Center);

        var price = new FrameworkElementFactory(typeof(TextBlock));
        price.SetBinding(TextBlock.TextProperty, new Binding(nameof(WatchlistRow.ForecastPrice)));
        price.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(WatchlistRow.ForecastForeground)));
        price.SetValue(TextBlock.FontSizeProperty, 10.6d);
        price.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        panel.AppendChild(price);

        var direction = new FrameworkElementFactory(typeof(TextBlock));
        direction.SetBinding(TextBlock.TextProperty, new Binding(nameof(WatchlistRow.ForecastDirection)));
        direction.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(WatchlistRow.ForecastForeground)));
        direction.SetValue(TextBlock.FontSizeProperty, 7.8d);
        direction.SetValue(TextBlock.MarginProperty, new Thickness(0, 2, 0, 0));
        panel.AppendChild(direction);

        return new DataGridTemplateColumn
        {
            Header = "FORECAST",
            Width = new DataGridLength(width, DataGridLengthUnitType.Star),
            CellTemplate = new DataTemplate { VisualTree = panel }
        };
    }

    private void ApplyStockBranding()
    {
        var version = CurrentVersionText();
        foreach (var text in FindLogicalChildren<TextBlock>(this))
        {
            if (text.Text == "RPT TERMINAL") text.Text = "RPT MARKETS";
            else if (text.Text == "ROBLOX RESALE MONITOR") text.Text = "ROBLOX MARKET WATCH";
            else if (text.Text.StartsWith("v0.", StringComparison.Ordinal) && text.Text.Contains("3AYZE", StringComparison.Ordinal)) text.Text = $"v{version} · 3AYZE";
            else if (text.Text.StartsWith("Roblox Price Tracker v0.", StringComparison.Ordinal)) text.Text = $"Roblox Price Tracker v{version}";
        }
    }

    private static IEnumerable<T> FindLogicalChildren<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is T typed) yield return typed;
            if (child is DependencyObject dependency)
            {
                foreach (var nested in FindLogicalChildren<T>(dependency)) yield return nested;
            }
        }
    }

    private async void Window_StockPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            e.Handled = true;
            await RunCheckAsync(true, CancellationToken.None);
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.F)
        {
            e.Handled = true;
            WatchlistNav.IsChecked = true;
            ShowPage("Watchlist");
            WatchlistSearchBox.Focus();
            WatchlistSearchBox.SelectAll();
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.N)
        {
            e.Handled = true;
            AddItemButton_Click(this, new RoutedEventArgs());
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.OemComma)
        {
            e.Handled = true;
            SettingsNav.IsChecked = true;
            ShowPage("Settings");
            return;
        }

        if (WatchlistPage.Visibility == Visibility.Visible && WatchlistGrid.SelectedItem is WatchlistRow)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await OpenSelectedItemDetailsAsync();
            }
            else if (e.Key == Key.Delete)
            {
                e.Handled = true;
                RemoveItemButton_Click(this, new RoutedEventArgs());
            }
        }
    }
}
