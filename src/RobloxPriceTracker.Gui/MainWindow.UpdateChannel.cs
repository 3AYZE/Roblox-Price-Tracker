using System.Windows;
using System.Windows.Controls;

namespace RobloxPriceTracker.Gui;

public partial class MainWindow : Window
{
    private ComboBox? _updateChannelCombo;
    private bool _updateChannelUiLoading;

    private void EnsureUpdateChannelUi()
    {
        if (_updateChannelCombo is not null) return;
        if (AutoUpdateCheckBox.Parent is not StackPanel parent) return;

        _updateChannelUiLoading = true;
        try
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 10, 0, 0)
            };

            row.Children.Add(new TextBlock
            {
                Text = "Update channel",
                Width = 118,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = System.Windows.Media.Brushes.LightSlateGray,
                FontSize = 9.5
            });

            var combo = new ComboBox
            {
                Width = 205,
                Height = 28
            };
            combo.Items.Add(new ComboBoxItem
            {
                Content = "Stable · GitHub Releases",
                Tag = "Stable"
            });
            combo.Items.Add(new ComboBoxItem
            {
                Content = "Latest · successful main builds",
                Tag = "Latest"
            });
            combo.SelectedIndex = IsLatestUpdateChannel() ? 1 : 0;
            combo.SelectionChanged += async (_, _) =>
            {
                if (_updateChannelUiLoading) return;
                var selected = (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
                _services.Settings.UpdateChannel = string.Equals(selected, "Latest", StringComparison.OrdinalIgnoreCase)
                    ? "Latest"
                    : "Stable";
                await _services.SettingsStore.SaveAsync(_services.Settings);

                _stagedRelease = null;
                _stagedUpdatePath = null;
                _deferredUpdateAssetUrl = null;
                UpdateStatusText.Text = $"Update channel changed to {_services.Settings.UpdateChannel}.";
                if (_services.Settings.AutoUpdateEnabled)
                {
                    await CheckForUpdatesAsync(userInitiated: false);
                }
            };

            _updateChannelCombo = combo;
            row.Children.Add(combo);

            var index = parent.Children.IndexOf(AutoUpdateCheckBox);
            parent.Children.Insert(index >= 0 ? index + 1 : 0, row);
        }
        finally
        {
            _updateChannelUiLoading = false;
        }
    }

    private bool IsLatestUpdateChannel() =>
        string.Equals(_services.Settings.UpdateChannel, "Latest", StringComparison.OrdinalIgnoreCase);

    private string UpdateChannelLabel() => IsLatestUpdateChannel() ? "Latest" : "Stable";
}
