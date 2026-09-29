using System.Runtime.InteropServices;
using System.Windows.Interop;
using static SweeplyForWindows.Platform.NativeMethods;

namespace SweeplyForWindows.Platform;

/// <summary>One item of the icon's right-click menu. <see cref="Id"/> 0 is a separator.</summary>
internal sealed record TrayMenuItem(int Id, string Text = "", bool Checked = false)
{
    public static TrayMenuItem Separator { get; } = new(0);
}

/// <summary>
/// The app's icon in the notification area, written directly against Shell_NotifyIcon so the app
/// needs no extra libraries and owns every icon handle it creates (it may redraw the icon every second
/// for days, so a leaked handle per update would add up).
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = WM_USER + 0x55;
    private static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private readonly HwndSource _window;
    private IntPtr _icon;
    private string _toolTip = "";
    private bool _added;
    private bool _disposed;

    public TrayIcon()
    {
        // An invisible top-level window receives the icon's mouse messages. A message-only window
        // would be lighter, but it can never be the foreground window, and the menu needs that to
        // close when you click somewhere else.
        var parameters = new HwndSourceParameters("SweeplyForWindowsTray")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, never shown
            ExtendedWindowStyle = 0x00000080,          // WS_EX_TOOLWINDOW: no taskbar button
        };
        _window = new HwndSource(parameters);
        _window.AddHook(WndProc);
    }

    /// <summary>Left click, or Enter / Space on the icon.</summary>
    public event Action? Clicked;

    /// <summary>Right click, or the menu key; screen coordinates in physical pixels.</summary>
    public event Action<int, int>? MenuRequested;

    /// <summary>The user clicked a notification shown with <see cref="ShowNotification"/>.</summary>
    public event Action? NotificationClicked;

    /// <summary>Icon size in pixels for the notification area at the current display scale.</summary>
    public int IconSize => GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForWindow(_window.Handle));

    public void Show()
    {
        if (_added) return;
        var data = NewData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
        data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
    }

    /// <summary>Replaces the icon. Takes ownership of <paramref name="icon"/> and destroys the previous one.</summary>
    public void SetIcon(IntPtr icon)
    {
        IntPtr old = _icon;
        _icon = icon;
        Modify(NIF_ICON);
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    public void SetToolTip(string text)
    {
        _toolTip = text.Length > 127 ? text[..127] : text;
        Modify(NIF_TIP | NIF_SHOWTIP);
    }

    /// <summary>Shows a Windows notification that comes from this icon.</summary>
    public void ShowNotification(string title, string text)
    {
        if (!_added) return;
        var data = NewData(NIF_INFO);
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = text.Length > 255 ? text[..255] : text;
        data.dwInfoFlags = NIIF_USER | NIIF_LARGE_ICON;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    /// <summary>Shows the right-click menu and returns the chosen item's id, or 0 if none was chosen.</summary>
    public int ShowMenu(int x, int y, IEnumerable<TrayMenuItem> items)
    {
        IntPtr menu = CreatePopupMenu();
        try
        {
            foreach (var item in items)
            {
                if (item.Id == 0) AppendMenu(menu, MF_SEPARATOR, 0, null);
                else AppendMenu(menu, MF_STRING | (item.Checked ? MF_CHECKED : 0), (nuint)item.Id, item.Text);
            }
            SetForegroundWindow(_window.Handle);
            int chosen = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY, x, y, _window.Handle, IntPtr.Zero);
            PostMessage(_window.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero); // lets the menu close properly next time
            return chosen;
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_added)
        {
            var data = NewData(0);
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        _icon = IntPtr.Zero;
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }

    private void Modify(int flags)
    {
        if (!_added) return;
        var data = NewData(flags);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private NOTIFYICONDATA NewData(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _window.Handle,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _toolTip,
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            int message = (int)(lParam.ToInt64() & 0xFFFF);
            int x = (short)(wParam.ToInt64() & 0xFFFF);
            int y = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
            switch (message)
            {
                case NIN_SELECT:
                case NIN_KEYSELECT:
                    Clicked?.Invoke();
                    break;
                case WM_CONTEXTMENU:
                    MenuRequested?.Invoke(x, y);
                    break;
                case NIN_BALLOONUSERCLICK:
                    NotificationClicked?.Invoke();
                    break;
            }
            handled = true;
        }
        else if (msg == TaskbarCreated)
        {
            // Explorer restarted and forgot every icon: add ours again.
            _added = false;
            Show();
        }
        return IntPtr.Zero;
    }
}
