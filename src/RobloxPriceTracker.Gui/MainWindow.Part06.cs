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

    private void InitializeTrayIcon()
    {
        if (_trayIcon is not null) return;

        try
        {
            var processPath = Environment.ProcessPath;
            _trayDrawingIcon = !string.IsNullOrWhiteSpace(processPath)
                ? System.Drawing.Icon.ExtractAssociatedIcon(processPath)
                : null;
        }
        catch
        {
            _trayDrawingIcon = null;
        }

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "Roblox Price Tracker",
            Visible = true,
            Icon = _trayDrawingIcon ?? System.Drawing.SystemIcons.Application
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        var openItem = new System.Windows.Forms.ToolStripMenuItem("Open Roblox Price Tracker");
        openItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(ShowFromTray));

        var checkItem = new System.Windows.Forms.ToolStripMenuItem("Check Now");
        checkItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(() => _ = RunCheckAsync(true, CancellationToken.None)));

        _trayMonitoringMenuItem = new System.Windows.Forms.ToolStripMenuItem(_monitoringCts is null ? "Resume Monitoring" : "Pause Monitoring");
        _trayMonitoringMenuItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (_startupDelayCts is not null)
            {
                CancelDelayedStartupMonitoring();
                return;
            }
            if (_monitoringCts is null) StartMonitoring();
            else await StopMonitoringAsync();
        }));

        _trayStartupMenuItem = new System.Windows.Forms.ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = _services.Settings.StartWithWindows
        };
        _trayStartupMenuItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (_trayStartupMenuItem is null) return;
            var requested = _trayStartupMenuItem.Checked;
            var previous = _services.Settings.StartWithWindows;
            _services.Settings.StartWithWindows = requested;
            try
            {
                WindowsStartupRegistration.Apply(_services.Settings);
                await _services.SettingsStore.SaveAsync(_services.Settings);
                StartWithWindowsCheckBox.IsChecked = requested;
                DiagnosticsStartupText.Text = FormatStartupStatus();
                ShowTrayBalloon("Windows startup updated", requested ? "Roblox Price Tracker will start with Windows." : "Automatic Windows startup is disabled.");
            }
            catch (Exception ex)
            {
                _services.Settings.StartWithWindows = previous;
                _trayStartupMenuItem.Checked = previous;
                _services.Logger.Error($"Tray startup toggle failed: {ex}");
                ShowTrayBalloon("Startup setting could not be changed", ex.Message);
            }
        }));

        var exitItem = new System.Windows.Forms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            _allowExit = true;
            Close();
        }));

        menu.Items.Add(openItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(checkItem);
        menu.Items.Add(_trayMonitoringMenuItem);
        menu.Items.Add(_trayStartupMenuItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(ShowFromTray));
    }

    internal void ShowFromExternalLaunch() => ShowFromTray();

    private void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void ShowTrayBalloon(string title, string body)
    {
        if (_trayIcon is null || !_trayIcon.Visible) return;
        var safeTitle = string.IsNullOrWhiteSpace(title) ? "Roblox Price Tracker" : title.Trim();
        var safeBody = string.IsNullOrWhiteSpace(body) ? "Marketplace update available." : body.Trim();
        if (safeTitle.Length > 63) safeTitle = safeTitle[..63];
        if (safeBody.Length > 240) safeBody = safeBody[..240] + "…";
        try
        {
            _trayIcon.BalloonTipTitle = safeTitle;
            _trayIcon.BalloonTipText = safeBody;
            _trayIcon.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Info;
            _trayIcon.ShowBalloonTip(5000);
        }
        catch
        {
            // Tray notifications are best-effort and never affect monitoring.
        }
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.ContextMenuStrip?.Dispose();
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        _trayMonitoringMenuItem = null;
        _trayStartupMenuItem = null;
        _trayDrawingIcon?.Dispose();
        _trayDrawingIcon = null;
    }

    private void NotificationSink_NotificationRaised(object? sender, GuiNotificationEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_services.Settings.PlayAlertSound) SystemSounds.Exclamation.Play();
            ShowBanner(e.Title, e.Body);
            if (_services.Settings.ShowTrayNotifications) ShowTrayBalloon(e.Title, e.Body);
        });
    }

    private void ShowBanner(string title, string body)
    {
        NotificationTitleText.Text = title;
        NotificationBodyText.Text = body;
        NotificationBanner.Visibility = Visibility.Visible;
        _notificationTimer?.Stop();
        _notificationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        _notificationTimer.Tick += (_, _) =>
        {
            NotificationBanner.Visibility = Visibility.Collapsed;
            _notificationTimer?.Stop();
        };
        _notificationTimer.Start();
    }

    private void DismissNotification_Click(object sender, RoutedEventArgs e)
    {
        NotificationBanner.Visibility = Visibility.Collapsed;
        _notificationTimer?.Stop();
    }

    private void CreatorLink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }
        catch (Exception ex)
        {
            _services.Logger.Error($"Could not open creator link: {ex}");
        }
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => OpenPath(_services.DataDirectory);

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(_services.DataDirectory, "logs", "app.log");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.WriteAllText(path, string.Empty);
        OpenPath(path);
    }

    private static string FormatMarketStatus(MarketStatus status) => status switch
    {
        MarketStatus.Available => "Available",
        MarketStatus.NoResellers => "No sellers",
        MarketStatus.OffSale => "Off sale",
        MarketStatus.InvalidPrice => "Price issue",
        MarketStatus.Unsupported => "Unsupported",
        MarketStatus.MissingFromResponse => "Missing",
        _ => "Unknown"
    };

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var cleaned = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "Item" : cleaned;
    }

    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    private static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { MessageBox.Show(path, "Path", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
}
