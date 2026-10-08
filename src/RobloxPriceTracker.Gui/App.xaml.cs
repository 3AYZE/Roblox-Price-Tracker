using System.Threading;
using System.Windows;
using System.Windows.Threading;
using RobloxPriceTracker.Infrastructure;
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
    private CrashDiagnostics? _crashDiagnostics;
    private int _fatalHandled;

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

        _crashDiagnostics = new CrashDiagnostics(CrashDiagnostics.ResolveDataDirectory());
        _crashDiagnostics.BeginSession();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

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
            _crashDiagnostics?.Record("startup", ex, fatal: true);
            MessageBox.Show(
                $"The application could not start.\n\n{ex.Message}\n\nCheck the application log for details.",
                "Startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _crashDiagnostics?.Record("wpf-dispatcher", e.Exception, fatal: true);
        e.Handled = true;
        if (Interlocked.Exchange(ref _fatalHandled, 1) != 0) return;
        try
        {
            MessageBox.Show(
                "Roblox Price Tracker encountered an unexpected error and must close. " +
                "The crash details were saved locally in logs\\crash.log.",
                "Roblox Price Tracker",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch { /* Window services may already be unavailable during a crash. */ }
        Shutdown(1);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            _crashDiagnostics?.Record("appdomain", exception, fatal: e.IsTerminating);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _crashDiagnostics?.Record("background-task", e.Exception, fatal: false);
        e.SetObserved();
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
        try
        {
            _services?.Dispose();
        }
        finally
        {
            DispatcherUnhandledException -= OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            if (Volatile.Read(ref _fatalHandled) == 0 && e.ApplicationExitCode == 0)
                _crashDiagnostics?.EndSession();
            _singleInstanceMutex?.Dispose();
            base.OnExit(e);
        }
    }
}
