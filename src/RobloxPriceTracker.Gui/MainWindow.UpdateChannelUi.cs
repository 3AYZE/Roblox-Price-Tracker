using System;
using System.Windows;
using System.Windows.Controls;

namespace RobloxPriceTracker.Gui;

public partial class MainWindow : Window
{
    private ComboBox? _updateChannelComboBox;
    private DevelopmentUpdateService? _developmentUpdateService;

    private void ConfigureUpdateChannelUi()
    {
        if (_updateChannelComboBox is not null) return;
        if (AutoUpdateCheckBox.Parent is not StackPanel stack) return;

        AutoUpdateCheckBox.Content = "Automatically check the selected update channel for updates";

        var row = new Grid { Margin = new Thickness(0, 13, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Text = "Update channel",
            Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(label);

        _updateChannelComboBox = new ComboBox
        {
            Width = 220,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _updateChannelComboBox.Items.Add(new ComboBoxItem { Content = "Stable (recommended)", Tag = "Stable" });
        _updateChannelComboBox.Items.Add(new ComboBoxItem { Content = "Latest / Development", Tag = "Development" });
        _updateChannelComboBox.SelectedIndex = NormalizeUpdateChannel(_services.Settings.UpdateChannel) == "Development" ? 1 : 0;
        Grid.SetColumn(_updateChannelComboBox, 1);
        row.Children.Add(_updateChannelComboBox);

        var hint = new TextBlock
        {
            Text = "Stable installs normal releases. Latest / Development installs only main-branch builds that passed the Windows build, tests, and Lite EXE smoke test.",
            Foreground = (System.Windows.Media.Brush)FindResource("TextTertiary"),
            FontSize = 9,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        Grid.SetColumn(hint, 2);
        row.Children.Add(hint);

        stack.Children.Insert(1, row);

        _updateChannelComboBox.SelectionChanged += (_, _) =>
        {
            if (_updateChannelComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                _services.Settings.UpdateChannel = NormalizeUpdateChannel(tag);
                UpdateStatusText.Text = $"Update channel changed to {UpdateChannelDisplayName()} · click SAVE SETTINGS to keep it.";
            }
        };
    }

    private DevelopmentUpdateService GetDevelopmentUpdateService()
    {
        return _developmentUpdateService ??= new DevelopmentUpdateService(_services.UpdateService.Repository, _services.Logger);
    }

    private void DisposeDevelopmentUpdateService()
    {
        _developmentUpdateService?.Dispose();
        _developmentUpdateService = null;
    }

    private static string NormalizeUpdateChannel(string? channel) =>
        string.Equals(channel, "Development", StringComparison.OrdinalIgnoreCase) ? "Development" : "Stable";

    private string UpdateChannelDisplayName() =>
        NormalizeUpdateChannel(_services.Settings.UpdateChannel) == "Development" ? "Latest / Development" : "Stable";

    private TimeSpan GetUpdateCheckInterval() =>
        NormalizeUpdateChannel(_services.Settings.UpdateChannel) == "Development"
            ? TimeSpan.FromMinutes(30)
            : TimeSpan.FromHours(6);
}
