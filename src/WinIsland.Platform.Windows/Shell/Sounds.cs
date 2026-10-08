using System.Runtime.InteropServices;

namespace WinIsland.Platform.Windows.Shell;

/// <summary>System sounds (respect the user's sound scheme and volume).</summary>
public static partial class Sounds
{
    private const uint MB_ICONASTERISK = 0x40;

    /// <summary>The "notification" chime, played asynchronously.</summary>
    public static void Notify() => _ = MessageBeep(MB_ICONASTERISK);

    [LibraryImport("user32.dll")]
    private static partial int MessageBeep(uint type);
}
