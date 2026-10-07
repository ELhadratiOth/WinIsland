using static WinIsland.Platform.Windows.Interop.NativeMethods;

namespace WinIsland.Platform.Windows.Input;

/// <summary>A global hotkey such as "Win+Alt+I", registered against the island window.</summary>
public sealed class Hotkey : IDisposable
{
    private readonly nint _hwnd;

    private Hotkey(nint hwnd, int id)
    {
        _hwnd = hwnd;
        Id = id;
    }

    public int Id { get; }

    /// <summary>Registers the hotkey, or returns null when the text is empty/invalid or the combination is taken.</summary>
    public static Hotkey? TryRegister(nint hwnd, int id, string? text)
    {
        if (!TryParse(text, out uint modifiers, out uint vk))
        {
            return null;
        }

        return RegisterHotKey(hwnd, id, modifiers | MOD_NOREPEAT, vk) != 0 ? new Hotkey(hwnd, id) : null;
    }

    public static bool TryParse(string? text, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (string raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                case "WIN": modifiers |= MOD_WIN; break;
                case "ALT": modifiers |= MOD_ALT; break;
                case "CTRL" or "CONTROL": modifiers |= MOD_CONTROL; break;
                case "SHIFT": modifiers |= MOD_SHIFT; break;
                case "SPACE": virtualKey = 0x20; break;
                case { Length: 1 } key when char.IsAsciiLetterOrDigit(key[0]): virtualKey = key[0]; break;
                case ['F', .. var number] when int.TryParse(number, out int f) && f is >= 1 and <= 24: virtualKey = (uint)(0x70 + f - 1); break;
                default: return false;
            }
        }

        // Require at least one modifier: a bare key would hijack normal typing.
        return modifiers != 0 && virtualKey != 0;
    }

    public void Dispose() => UnregisterHotKey(_hwnd, Id);
}
