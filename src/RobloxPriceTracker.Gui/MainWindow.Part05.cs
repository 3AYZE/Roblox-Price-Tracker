using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Media;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using RobloxPriceTracker.Core;
using RobloxPriceTracker.Infrastructure;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace RobloxPriceTracker.Gui;

public partial class MainWindow : Window
{

    private void ExportReport_Click(object sender, RoutedEventArgs e)
    {
        if (ReportItemCombo.SelectedItem is not WatchlistRow row || _currentReportEntries.Count == 0)
        {
            MessageBox.Show("There is no report data to export for the current selection.", "Export report", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export price history",
            Filter = "CSV file (*.csv)|*.csv",
            DefaultExt = ".csv",
            AddExtension = true,
            FileName = $"RobloxPriceTracker_{SanitizeFileName(row.Name)}_{DateTime.Now:yyyyMMdd}.csv"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("ObservedAtLocal,AssetId,ItemName,LowestResalePrice,MarketStatus");
            foreach (var entry in _currentReportEntries.OrderBy(x => x.ObservedAtUtc))
            {
                sb.Append(Csv(entry.ObservedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))).Append(',')
                  .Append(row.AssetId).Append(',')
                  .Append(Csv(row.Name)).Append(',')
                  .Append(entry.Price?.ToString() ?? string.Empty).Append(',')
                  .Append(Csv(entry.Status.ToString())).AppendLine();
            }
            File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            ShowBanner("Report exported", $"Saved {Path.GetFileName(dialog.FileName)}");
        }
        catch (Exception ex)
        {
            _services.Logger.Error(ex.ToString());
            MessageBox.Show(ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadSettingsIntoControls()
    {
        NormalPollTextBox.Text = _services.Settings.NormalPollSeconds.ToString();
        NearPollTextBox.Text = _services.Settings.NearTargetPollSeconds.ToString();
        StartMonitoringCheckBox.IsChecked = _services.Settings.StartMonitoringOnLaunch;
        PlaySoundCheckBox.IsChecked = _services.Settings.PlayAlertSound;
        ConfirmRemoveCheckBox.IsChecked = _services.Settings.ConfirmBeforeRemove;
        TrayNotificationsCheckBox.IsChecked = _services.Settings.ShowTrayNotifications;
        MinimizeToTrayCheckBox.IsChecked = _services.Settings.MinimizeToTrayOnClose;
        StartWithWindowsCheckBox.IsChecked = _services.Settings.StartWithWindows;
        StartMinimizedCheckBox.IsChecked = _services.Settings.StartMinimizedToTray;
        StartupDelayTextBox.Text = _services.Settings.StartupDelaySeconds.ToString();
        DataPathText.Text = _services.DataDirectory;
        DiagnosticsVersionText.Text = "v0.4.0";
        DiagnosticsStartupText.Text = FormatStartupStatus();
    }

    private string FormatStartupStatus()
    {
        if (!WindowsStartupRegistration.IsEnabled()) return "Disabled";
        return _services.Settings.StartMinimizedToTray ? "Enabled · background" : "Enabled · show window";
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        SettingsSavedText.Text = string.Empty;
        if (!int.TryParse(NormalPollTextBox.Text.Trim(), out var normal) || normal is < 10 or > 3600)
        {
            MessageBox.Show("Normal interval must be between 10 and 3600 seconds.", "Invalid setting", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(NearPollTextBox.Text.Trim(), out var near) || near is < 10 or > 600)
        {
            MessageBox.Show("Near-target interval must be between 10 and 600 seconds.", "Invalid setting", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (near > normal)
        {
            MessageBox.Show("Near-target polling should be equal to or faster than the normal interval.", "Invalid setting", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(StartupDelayTextBox.Text.Trim(), out var startupDelay) || startupDelay is < 0 or > 120)
        {
            MessageBox.Show("Startup delay must be between 0 and 120 seconds.", "Invalid setting", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _services.Settings.NormalPollSeconds = normal;
        _services.Settings.NearTargetPollSeconds = near;
        _services.Settings.StartMonitoringOnLaunch = StartMonitoringCheckBox.IsChecked == true;
        _services.Settings.PlayAlertSound = PlaySoundCheckBox.IsChecked == true;
        _services.Settings.ConfirmBeforeRemove = ConfirmRemoveCheckBox.IsChecked == true;
        _services.Settings.ShowTrayNotifications = TrayNotificationsCheckBox.IsChecked == true;
        _services.Settings.MinimizeToTrayOnClose = MinimizeToTrayCheckBox.IsChecked == true;
        _services.Settings.StartWithWindows = StartWithWindowsCheckBox.IsChecked == true;
        _services.Settings.StartMinimizedToTray = StartMinimizedCheckBox.IsChecked == true;
        _services.Settings.StartupDelaySeconds = startupDelay;
        await _services.SettingsStore.SaveAsync(_services.Settings);

        try
        {
            WindowsStartupRegistration.Apply(_services.Settings);
            DiagnosticsStartupText.Text = FormatStartupStatus();
            if (_trayStartupMenuItem is not null) _trayStartupMenuItem.Checked = _services.Settings.StartWithWindows;
            SettingsSavedText.Text = "Settings saved.";
        }
        catch (Exception ex)
        {
            _services.Logger.Error($"Windows startup registration update failed: {ex}");
            SettingsSavedText.Text = "Saved, but Windows startup could not be changed.";
        }

        await RefreshWatchlistAsync();
    }

    private void CreateBackup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Create Roblox Price Tracker backup",
            Filter = "ZIP archive (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            FileName = $"RobloxPriceTracker_Backup_{DateTime.Now:yyyyMMdd_HHmm}.zip"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            using var archive = ZipFile.Open(dialog.FileName, ZipArchiveMode.Create);
            AddBackupFile(archive, Path.Combine(_services.DataDirectory, "tracker-state.json"), "tracker-state.json");
            AddBackupFile(archive, Path.Combine(_services.DataDirectory, "app-settings.json"), "app-settings.json");
            var manifest = archive.CreateEntry("README.txt");
            using (var writer = new StreamWriter(manifest.Open()))
            {
                writer.WriteLine("Roblox Price Tracker data backup");
                writer.WriteLine($"Created: {DateTimeOffset.Now:O}");
                writer.WriteLine("Application: v0.4.0");
                writer.WriteLine("Contains local tracker state and application settings.");
            }
            ShowBanner("Backup created", $"Saved {Path.GetFileName(dialog.FileName)}");
        }
        catch (Exception ex)
        {
            _services.Logger.Error(ex.ToString());
            MessageBox.Show(ex.Message, "Backup failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void AddBackupFile(ZipArchive archive, string sourcePath, string entryName)
    {
        if (File.Exists(sourcePath)) archive.CreateEntryFromFile(sourcePath, entryName, CompressionLevel.Optimal);
    }
}
