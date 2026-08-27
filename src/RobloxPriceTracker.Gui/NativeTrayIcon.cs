using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

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
    private IntPtr _iconHandle;
    private bool _ownsIcon;
    private bool _visible;
    private string _toolTip = "RPT Markets";
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

        var sourceParameters = new HwndSourceParameters("RPT.NativeTrayHost")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0
        };
        _messageWindow = new HwndSource(sourceParameters);
        _messageWindow.AddHook(WindowProc);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        _menu = new ContextMenu
        {
            Background = new SolidColorBrush(Color.FromRgb(17, 24, 33)),
            Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(45, 58, 74)),
            BorderThickness = new System.Windows.Thickness(1)
        };

        var openItem = new MenuItem { Header = "Open RPT Markets" };
        openItem.Click += (_, _) => _openAction();
        _menu.Items.Add(openItem);
        _menu.Items.Add(new Separator());

        var checkItem = new MenuItem { Header = "Check Now" };
        checkItem.Click += (_, _) => checkNowAction();
        _menu.Items.Add(checkItem);

        _monitoringItem = new MenuItem { Header = monitoring ? "Pause Monitoring" : "Resume Monitoring" };
        _monitoringItem.Click += async (_, _) => await toggleMonitoringAction();
        _menu.Items.Add(_monitoringItem);

        _startupItem = new MenuItem
        {
            Header = "Start with Windows",
            IsCheckable = true,
            IsChecked = startWithWindows
        };
        _startupItem.Click += async (_, _) => await setStartupAction(_startupItem.IsChecked);
        _menu.Items.Add(_startupItem);

        _menu.Items.Add(new Separator());
        var exitItem = new MenuItem { Header = "Exit" };
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
                ? $"RPT Markets - starting in {secondsRemaining}s"
                : "RPT Markets - starting";
            return;
        }

        _monitoringItem.Header = monitoring ? "Pause Monitoring" : "Resume Monitoring";
        ToolTip = monitoring ? "RPT Markets - Monitoring" : "RPT Markets - Paused";
    }

    public string ToolTip
    {
        get => _toolTip;
        set
        {
            _toolTip = Truncate(value, 127, "RPT Markets");
            if (_visible) ModifyIcon(includeInfo: false, null, null);
        }
    }

    public void ShowBalloon(string title, string body)
    {
        if (!_visible) return;
        ModifyIcon(
            includeInfo: true,
            Truncate(title, 63, "RPT Markets"),
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

        _menu.IsOpen = false;
        _messageWindow.RemoveHook(WindowProc);
        _messageWindow.Dispose();
        if (_ownsIcon && _iconHandle != IntPtr.Zero)
            DestroyIcon(_iconHandle);
        _iconHandle = IntPtr.Zero;
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
}
