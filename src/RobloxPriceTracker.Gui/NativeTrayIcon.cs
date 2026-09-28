using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Interop;

namespace RobloxPriceTracker.Gui;

/// <summary>
/// Lightweight Win32 notification-area host used instead of Windows Forms.
/// Keeping the tray native avoids shipping the WinForms desktop stack just for NotifyIcon.
/// </summary>
internal sealed class NativeTrayIcon : IDisposable
{
    private const int WmAppTray = 0x8000 + 41;
    private const int WmLButtonDoubleClick = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMButtonDown = 0x0207;
    private const int WmXButtonDown = 0x020B;
    private const int WhMouseLl = 14;

    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint NiifInfo = 0x00000001;

    private readonly HwndSource _messageWindow;
    private readonly ContextMenu _menu;
    private readonly MenuItem _monitoringItem;
    private readonly MenuItem _startupItem;
    private readonly Action _openAction;

    // A WPF ContextMenu attached to an invisible notification-area HWND does not
    // reliably receive clicks in other applications. Install this lightweight
    // native listener only while the tray flyout is visible; pass clicks through.
    private readonly LowLevelMouseProc _outsideClickProc;
    private IntPtr _outsideClickHook;
    private IntPtr _menuWindowHandle;
    private IntPtr _iconHandle;
    private bool _ownsIcon;
    private bool _visible;
    private string _toolTip = "Roblox Price Tracker";
    private readonly uint _taskbarCreatedMessage;

    public NativeTrayIcon(
        Action openAction,
        Action checkNowAction,
        Func<Task> toggleMonitoringAction,
        Func<bool, Task> setStartupAction,
        Action exitAction,
        bool monitoring,
        bool startWithWindows)
    {
        _openAction = openAction;
        _outsideClickProc = OutsideClickProc;

        var sourceParameters = new HwndSourceParameters("RPT.NativeTrayHost")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0
        };
        _messageWindow = new HwndSource(sourceParameters);
        _messageWindow.AddHook(WindowProc);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        // A full control template is required here: changing ContextMenu.Background alone
        // leaves Windows' light MenuItem gutter, blue focus rectangle, and square chrome.
        // Keep the flyout resources local so other WPF menus retain their own styling.
        var flyoutStyles = new System.Windows.ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/RobloxPriceTracker;component/TrayMenuStyles.xaml",
                UriKind.Absolute)
        };
        var flyoutItemStyle = (System.Windows.Style)flyoutStyles["TrayFlyoutItemStyle"];
        _menu = new ContextMenu
        {
            Style = (System.Windows.Style)flyoutStyles["TrayFlyoutStyle"],
            StaysOpen = false
        };
        _menu.Opened += Menu_Opened;
        _menu.Closed += Menu_Closed;

        MenuItem CreateItem(string header) => new()
        {
            Header = header,
            Style = flyoutItemStyle
        };

        var openItem = CreateItem("Open Roblox Price Tracker");
        openItem.Click += (_, _) => _openAction();
        _menu.Items.Add(openItem);

        var checkItem = CreateItem("Check Now");
        checkItem.Click += (_, _) => checkNowAction();
        _menu.Items.Add(checkItem);

        _monitoringItem = CreateItem(monitoring ? "Pause Monitoring" : "Resume Monitoring");
        _monitoringItem.Click += async (_, _) => await toggleMonitoringAction();
        _menu.Items.Add(_monitoringItem);

        _startupItem = CreateItem("Start with Windows");
        _startupItem.IsCheckable = true;
        _startupItem.IsChecked = startWithWindows;
        _startupItem.Click += async (_, _) => await setStartupAction(_startupItem.IsChecked);
        _menu.Items.Add(_startupItem);

        var exitItem = CreateItem("Exit");
        exitItem.Click += (_, _) => exitAction();
        _menu.Items.Add(exitItem);

        LoadApplicationIcon();
        AddIcon();
    }

    public bool Visible => _visible;

    public void SetStartupChecked(bool enabled) => _startupItem.IsChecked = enabled;

    public void UpdateMonitoring(bool monitoring, bool startupDelay = false, int secondsRemaining = 0)
    {
        if (startupDelay)
        {
            _monitoringItem.Header = "Pause Startup";
            ToolTip = secondsRemaining > 0
                ? $"Roblox Price Tracker - starting in {secondsRemaining}s"
                : "Roblox Price Tracker - starting";
            return;
        }

        _monitoringItem.Header = monitoring ? "Pause Monitoring" : "Resume Monitoring";
        ToolTip = monitoring ? "Roblox Price Tracker - Monitoring" : "Roblox Price Tracker - Paused";
    }

    public string ToolTip
    {
        get => _toolTip;
        set
        {
            _toolTip = Truncate(value, 127, "Roblox Price Tracker");
            if (_visible) ModifyIcon(includeInfo: false, null, null);
        }
    }

    public void ShowBalloon(string title, string body)
    {
        if (!_visible) return;
        ModifyIcon(
            includeInfo: true,
            Truncate(title, 63, "Roblox Price Tracker"),
            Truncate(body, 255, "Marketplace update available."));
    }

    private void AddIcon()
    {
        var data = CreateData(NifMessage | NifIcon | NifTip);
        if (!Shell_NotifyIcon(NimAdd, ref data))
            return;
        _visible = true;
    }

    private void ModifyIcon(bool includeInfo, string? title, string? body)
    {
        var flags = NifMessage | NifIcon | NifTip;
        if (includeInfo) flags |= NifInfo;
        var data = CreateData(flags);
        if (includeInfo)
        {
            data.szInfoTitle = title ?? string.Empty;
            data.szInfo = body ?? string.Empty;
            data.dwInfoFlags = NiifInfo;
            data.uTimeoutOrVersion = 5000;
        }
        Shell_NotifyIcon(NimModify, ref data);
    }

    private NotifyIconData CreateData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _messageWindow.Handle,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WmAppTray,
        hIcon = _iconHandle,
        szTip = _toolTip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty
    };

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (unchecked((uint)msg) == _taskbarCreatedMessage)
        {
            _visible = false;
            AddIcon();
            handled = true;
            return IntPtr.Zero;
        }

        if (msg != WmAppTray) return IntPtr.Zero;

        var shellMessage = unchecked((int)lParam.ToInt64());
        switch (shellMessage)
        {
            case WmLButtonDoubleClick:
                _openAction();
                handled = true;
                break;
            case WmRButtonUp:
            case WmContextMenu:
                _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                _menu.IsOpen = true;
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    private void Menu_Opened(object? sender, System.Windows.RoutedEventArgs e)
    {
        // The popup has its own top-level HWND, separate from the zero-size
        // notification host. Native screen coordinates avoid WPF DPI confusion.
        _menuWindowHandle = (System.Windows.PresentationSource.FromVisual(_menu) as HwndSource)?.Handle
            ?? IntPtr.Zero;
        if (_menuWindowHandle == IntPtr.Zero || _outsideClickHook != IntPtr.Zero)
            return;

        _outsideClickHook = SetWindowsHookEx(
            WhMouseLl, _outsideClickProc, GetModuleHandle(null), 0);
    }

    private void Menu_Closed(object? sender, System.Windows.RoutedEventArgs e)
    {
        StopOutsideClickCapture();
    }

    private IntPtr OutsideClickProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _menu.IsOpen && _menuWindowHandle != IntPtr.Zero &&
            IsMouseButtonDown(unchecked((int)wParam.ToInt64())) &&
            GetWindowRect(_menuWindowHandle, out var bounds))
        {
            var mouse = Marshal.PtrToStructure<LowLevelMouseHookData>(lParam);
            if (mouse.Point.X < bounds.Left || mouse.Point.X >= bounds.Right ||
                mouse.Point.Y < bounds.Top || mouse.Point.Y >= bounds.Bottom)
            {
                // Do not consume the click: the selected desktop, taskbar or
                // other-app control must still receive its normal mouse input.
                // Defer closing to avoid re-entering WPF inside a Win32 hook.
                _menu.Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Send,
                    new Action(() => { if (_menu.IsOpen) _menu.IsOpen = false; }));
            }
        }

        return CallNextHookEx(_outsideClickHook, code, wParam, lParam);
    }

    private static bool IsMouseButtonDown(int message) =>
        message is WmLButtonDown or WmRButtonDown or WmMButtonDown or WmXButtonDown;

    private void StopOutsideClickCapture()
    {
        var hook = _outsideClickHook;
        _outsideClickHook = IntPtr.Zero;
        _menuWindowHandle = IntPtr.Zero;
        if (hook != IntPtr.Zero)
            UnhookWindowsHookEx(hook);
    }

    private void LoadApplicationIcon()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(path) && ExtractIconEx(path, 0, out var large, out var small, 1) > 0)
        {
            if (small != IntPtr.Zero)
            {
                _iconHandle = small;
                _ownsIcon = true;
                if (large != IntPtr.Zero) DestroyIcon(large);
                return;
            }
            if (large != IntPtr.Zero)
            {
                _iconHandle = large;
                _ownsIcon = true;
                return;
            }
        }

        _iconHandle = LoadIcon(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION; shared handle.
        _ownsIcon = false;
    }

    public void Dispose()
    {
        if (_visible)
        {
            var data = CreateData(0);
            Shell_NotifyIcon(NimDelete, ref data);
            _visible = false;
        }

        StopOutsideClickCapture();
        _menu.Opened -= Menu_Opened;
        _menu.Closed -= Menu_Closed;
        _menu.IsOpen = false;
        _messageWindow.RemoveHook(WindowProc);
        _messageWindow.Dispose();
        if (_ownsIcon && _iconHandle != IntPtr.Zero)
            DestroyIcon(_iconHandle);
        _iconHandle = IntPtr.Zero;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelMouseHookData
    {
        public ScreenPoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowBounds
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static string Truncate(string? value, int maxLength, string fallback)
    {
        var result = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return result.Length <= maxLength ? result : result[..maxLength];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NotifyIconData lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string szFileName, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookType, LowLevelMouseProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out WindowBounds bounds);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
