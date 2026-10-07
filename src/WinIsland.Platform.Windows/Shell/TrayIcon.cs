using WinIsland.Core.Diagnostics;
using WinIsland.Platform.Windows.Interop;
using WinIsland.Platform.Windows.Windowing;
using static WinIsland.Platform.Windows.Interop.NativeMethods;

namespace WinIsland.Platform.Windows.Shell;

/// <summary>
/// Notification-area icon: the island's only "chrome" (settings, log, exit). Receives its
/// callbacks through any <see cref="IWindowMessageSource"/> — the app uses a dedicated hidden
/// window so the icon (and Exit) keeps working even if the island itself fails. Re-adds
/// itself when Explorer restarts.
/// </summary>
public sealed unsafe class TrayIcon : IDisposable
{
    private const uint CallbackMessage = WM_APP + 1;
    private const uint IconId = 1;

    private readonly nint _hwnd;
    private readonly uint _taskbarCreatedMessage;
    private readonly string _tooltip;
    private bool _added;

    public TrayIcon(IWindowMessageSource window, string tooltip)
    {
        ArgumentNullException.ThrowIfNull(window);
        _hwnd = window.Handle;
        _tooltip = tooltip;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        window.AddHandler(OnMessage);
    }

    /// <summary>Raised when the user clicks the icon; the argument is the anchor point in screen pixels.</summary>
    public event EventHandler<(int X, int Y)>? MenuRequested;

    public void Show()
    {
        NOTIFYICONDATAW data = Create();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = LoadAppIcon();
        CopyTo(_tooltip, data.szTip, 128);

        _added = Shell_NotifyIcon(NIM_ADD, &data) != 0;
        if (!_added)
        {
            AppLog.Warn(nameof(TrayIcon), "Shell_NotifyIcon(NIM_ADD) failed");
            return;
        }

        data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, &data);
    }

    /// <summary>Shows a native popup menu and returns the chosen item id, or 0 when dismissed.</summary>
    public int ShowMenu(IReadOnlyList<TrayMenuItem> items, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(items);
        nint menu = BuildMenu(items);
        try
        {
            // The owner must be foreground or the menu won't close when clicking elsewhere.
            SetForegroundWindow(_hwnd);
            int command = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY, x, y, _hwnd, 0);
            PostMessage(_hwnd, WM_NULL, 0, 0);
            return command;
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            NOTIFYICONDATAW data = Create();
            Shell_NotifyIcon(NIM_DELETE, &data);
            _added = false;
        }
    }

    private nint? OnMessage(uint msg, nint wParam, nint lParam)
    {
        if (msg == _taskbarCreatedMessage)
        {
            _added = false;
            Show();
            return 0;
        }

        if (msg != CallbackMessage)
        {
            return null;
        }

        uint notification = (uint)(lParam & 0xFFFF);
        if (notification is NIN_SELECT or NIN_KEYSELECT or WM_CONTEXTMENU)
        {
            // NOTIFYICON_VERSION_4: wParam carries the anchor point.
            int x = (short)(wParam & 0xFFFF);
            int y = (short)((wParam >> 16) & 0xFFFF);
            MenuRequested?.Invoke(this, (x, y));
        }

        return 0;
    }

    private NOTIFYICONDATAW Create()
    {
        NOTIFYICONDATAW data = default;
        data.cbSize = (uint)sizeof(NOTIFYICONDATAW);
        data.hWnd = _hwnd;
        data.uID = IconId;
        return data;
    }

    private static nint LoadAppIcon()
    {
        // The SDK embeds <ApplicationIcon> as resource 32512 in the executable.
        nint icon = LoadIcon(GetModuleHandle(null), IDI_APPLICATION);
        return icon != 0 ? icon : LoadIcon(0, IDI_APPLICATION);
    }

    private static nint BuildMenu(IReadOnlyList<TrayMenuItem> items)
    {
        nint menu = CreatePopupMenu();
        foreach (TrayMenuItem item in items)
        {
            if (item.IsSeparator)
            {
                AppendMenu(menu, MF_SEPARATOR, 0, null);
                continue;
            }

            uint flags = MF_STRING | (item.IsChecked ? MF_CHECKED : 0) | (item.IsEnabled ? 0 : MF_GRAYED);
            nuint id = (nuint)item.Id;
            if (item.Children is { Count: > 0 } children)
            {
                flags |= MF_POPUP;
                id = (nuint)BuildMenu(children);
            }

            fixed (char* text = item.Text)
            {
                AppendMenu(menu, flags, id, text);
            }
        }

        return menu;
    }

    private static void CopyTo(string text, char* destination, int capacity)
    {
        int length = Math.Min(text.Length, capacity - 1);
        text.AsSpan(0, length).CopyTo(new Span<char>(destination, capacity));
        destination[length] = '\0';
    }
}
