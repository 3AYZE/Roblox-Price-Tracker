using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace RobloxPriceTracker.Gui;

internal static class ProductBrandingBootstrap
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

        // Run after the legacy/clean-UI loaded handlers so old labels cannot overwrite the
        // public product name or version text.
        window.Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(window.ApplyProductBranding));
    }
}

public partial class MainWindow
{
    internal void ApplyProductBranding()
    {
        Title = "Roblox Market Helper";

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.12.0";
        foreach (var text in FindVisualChildren<TextBlock>(this))
        {
            if (string.Equals(text.Text, "RPT TERMINAL", StringComparison.OrdinalIgnoreCase))
            {
                text.Text = "ROBLOX MARKET HELPER";
                text.FontSize = 9.8;
                continue;
            }

            if (string.Equals(text.Text, "ROBLOX RESALE MONITOR", StringComparison.OrdinalIgnoreCase))
            {
                text.Text = "LIMITED MARKET TERMINAL";
                continue;
            }

            if (text.Text.TrimStart().StartsWith("v0.", StringComparison.OrdinalIgnoreCase) &&
                text.Text.Contains("3AYZE", StringComparison.OrdinalIgnoreCase))
            {
                text.Text = $"v{version} · 3AYZE";
            }
        }
    }
}
