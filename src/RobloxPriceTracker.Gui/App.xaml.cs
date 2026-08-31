using System.Threading;
using System.Windows;
using Application = System.Windows.Application;

namespace RobloxPriceTracker.Gui;

public partial class App : Application
{
    private const string MutexName = "Local\\RobloxPriceTracker.Gui";
    private const string ShowEventName = "Local\\RobloxPriceTracker.Gui.Show";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showEvent;
    private CancellationTokenSource? _showListenerCts;
    private AppServices? _services;

    private async void App_OnStartup(object sender, StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            try
            {
                using var showEvent = EventWaitHandle.OpenExisting(ShowEventName);
                showEvent.Set();
            }
            catch
            {
                MessageBox.Show(
                    "Roblox Price Tracker is already running in the background.",
                    "Roblox Price Tracker",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            Shutdown();
            return;
        }

        try
        {
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/RobloxPriceTracker;component/TooltipStyles.xaml", UriKind.Absolute),
            });

            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            _services = await AppServices.CreateAsync();

            try
            {
                WindowsStartupRegistration.Apply(_services.Settings);
            }
            catch (Exception startupEx)
            {
                _services.Logger.Error($"Windows startup registration sync failed: {startupEx}");
            }

            var backgroundLaunch = e.Args.Any(arg => string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase));
            var startupLaunch = e.Args.Any(arg => string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase));
            var window = new MainWindow(_services, backgroundLaunch, startupLaunch);
            MainWindow = window;
            StartShowListener(window);

            if (backgroundLaunch)
            {
                await window.InitializeAsync();
            }
            else
            {
                window.Show();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"The application could not start.\n\n{ex.Message}\n\nCheck the application log for details.",
                "Startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void StartShowListener(MainWindow window)
    {
        if (_showEvent is null) return;
        _showListenerCts = new CancellationTokenSource();
        var token = _showListenerCts.Token;
        var showEvent = _showEvent;

        _ = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                if (!showEvent.WaitOne(750)) continue;
                if (token.IsCancellationRequested) break;
                Dispatcher.BeginInvoke(new Action(window.ShowFromExternalLaunch));
            }
        }, token);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showListenerCts?.Cancel();
        _showListenerCts?.Dispose();
        _showEvent?.Dispose();
        _services?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
