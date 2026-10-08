using System.Runtime.InteropServices;
using WinIsland.Core.Diagnostics;
using WinIsland.Core.Helpers;
using WinIsland.Platform.Windows.Windowing;

namespace WinIsland.Platform.Windows.Helpers;

/// <summary>
/// Clipboard history source: WM_CLIPBOARDUPDATE on a hidden window (event-driven), text only.
/// Respects the formats password managers set to keep a copy out of clipboard history.
/// Pasting puts the text on the clipboard and sends Ctrl+V to the app that has focus; the
/// island never takes focus, so that is the app the user was working in.
/// </summary>
public sealed unsafe partial class ClipboardMonitor : IClipboardService, IDisposable
{
    private const uint WM_CLIPBOARDUPDATE = 0x031D;
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const int MaxLength = 100_000;

    private readonly IWindowMessageSource _window;
    private readonly Func<bool> _enabled;
    private readonly TimeProvider _time;
    private readonly uint _excludeFormat;
    private readonly uint _historyFormat;
    private readonly uint _viewerIgnoreFormat;
    private bool _listening;

    public ClipboardMonitor(IWindowMessageSource window, Func<bool> enabled, TimeProvider time)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _excludeFormat = RegisterFormat("ExcludeClipboardContentFromMonitorProcessing");
        _historyFormat = RegisterFormat("CanIncludeInClipboardHistory");
        _viewerIgnoreFormat = RegisterFormat("Clipboard Viewer Ignore");
        _window.AddHandler(OnMessage);
    }

    public event EventHandler<ClipboardEntry>? Copied;

    public void Start()
    {
        if (!_listening)
        {
            _listening = AddClipboardFormatListener(_window.Handle) != 0;
            if (!_listening)
            {
                AppLog.Warn(nameof(ClipboardMonitor), $"AddClipboardFormatListener failed ({Marshal.GetLastPInvokeError()})");
            }
        }
    }

    public void Dispose()
    {
        if (_listening)
        {
            RemoveClipboardFormatListener(_window.Handle);
            _listening = false;
        }
    }

    public Task CopyAsync(string text)
    {
        SetText(text);
        return Task.CompletedTask;
    }

    public Task PasteAsync(string text)
    {
        SetText(text);

        // Let the target notice the new clipboard content, then press Ctrl+V for the user.
        return Task.Delay(40).ContinueWith(_ => SendCtrlV(), TaskScheduler.Default);
    }

    private nint? OnMessage(uint message, nint wParam, nint lParam)
    {
        if (message != WM_CLIPBOARDUPDATE)
        {
            return null;
        }

        if (_enabled() && !IsPrivate() && ReadText() is { Length: > 0 and <= MaxLength } text)
        {
            Copied?.Invoke(this, new ClipboardEntry(text, _time.GetUtcNow()));
        }

        return 0;
    }

    private bool IsPrivate()
    {
        if (IsClipboardFormatAvailable(_excludeFormat) != 0 || IsClipboardFormatAvailable(_viewerIgnoreFormat) != 0)
        {
            return true;
        }

        // "CanIncludeInClipboardHistory" = 0 means "don't keep this" (password managers).
        if (IsClipboardFormatAvailable(_historyFormat) == 0 || !Open())
        {
            return false;
        }

        try
        {
            nint data = GetClipboardData(_historyFormat);
            uint* value = data == 0 ? null : (uint*)GlobalLock(data);
            try
            {
                return value is not null && *value == 0;
            }
            finally
            {
                if (value is not null)
                {
                    GlobalUnlock(data);
                }
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private string? ReadText()
    {
        if (IsClipboardFormatAvailable(CF_UNICODETEXT) == 0 || !Open())
        {
            return null;
        }

        try
        {
            nint data = GetClipboardData(CF_UNICODETEXT);
            if (data == 0)
            {
                return null;
            }

            char* chars = (char*)GlobalLock(data);
            if (chars is null)
            {
                return null;
            }

            try
            {
                int max = (int)Math.Min((long)GlobalSize(data) / 2, MaxLength + 1);
                int length = new ReadOnlySpan<char>(chars, max).IndexOf('\0');
                return new string(chars, 0, length < 0 ? max : length);
            }
            finally
            {
                GlobalUnlock(data);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private void SetText(string text)
    {
        if (!Open())
        {
            throw new InvalidOperationException("The clipboard is busy.");
        }

        try
        {
            EmptyClipboard();
            nuint bytes = (nuint)((text.Length + 1) * sizeof(char));
            nint memory = GlobalAlloc(GMEM_MOVEABLE, bytes);
            char* target = (char*)GlobalLock(memory);
            text.AsSpan().CopyTo(new Span<char>(target, text.Length));
            target[text.Length] = '\0';
            GlobalUnlock(memory);
            if (SetClipboardData(CF_UNICODETEXT, memory) == 0)
            {
                GlobalFree(memory);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>Another app may hold the clipboard for a moment; retry briefly.</summary>
    private bool Open()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(_window.Handle) != 0)
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return false;
    }

    private static void SendCtrlV()
    {
        const ushort VK_CONTROL = 0x11;
        const ushort VK_V = 0x56;
        const uint KEYEVENTF_KEYUP = 0x2;
        Input* inputs = stackalloc Input[4];
        inputs[0] = Input.Key(VK_CONTROL, 0);
        inputs[1] = Input.Key(VK_V, 0);
        inputs[2] = Input.Key(VK_V, KEYEVENTF_KEYUP);
        inputs[3] = Input.Key(VK_CONTROL, KEYEVENTF_KEYUP);
        if (SendInput(4, inputs, sizeof(Input)) != 4)
        {
            AppLog.Warn(nameof(ClipboardMonitor), $"SendInput failed ({Marshal.GetLastPInvokeError()})");
        }
    }

    private static uint RegisterFormat(string name)
    {
        fixed (char* n = name)
        {
            return RegisterClipboardFormatW(n);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint Data;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;

        public static Input Key(ushort vk, uint flags) => new()
        {
            Type = 1,
            Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = vk, Flags = flags } },
        };
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int AddClipboardFormatListener(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int RemoveClipboardFormatListener(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int OpenClipboard(nint owner);

    [LibraryImport("user32.dll")]
    private static partial int CloseClipboard();

    [LibraryImport("user32.dll")]
    private static partial int EmptyClipboard();

    [LibraryImport("user32.dll")]
    private static partial int IsClipboardFormatAvailable(uint format);

    [LibraryImport("user32.dll")]
    private static partial nint GetClipboardData(uint format);

    [LibraryImport("user32.dll")]
    private static partial nint SetClipboardData(uint format, nint memory);

    [LibraryImport("user32.dll")]
    private static partial uint RegisterClipboardFormatW(char* name);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint count, Input* inputs, int size);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalFree(nint memory);

    [LibraryImport("kernel32.dll")]
    private static partial void* GlobalLock(nint memory);

    [LibraryImport("kernel32.dll")]
    private static partial int GlobalUnlock(nint memory);

    [LibraryImport("kernel32.dll")]
    private static partial nuint GlobalSize(nint memory);
}
