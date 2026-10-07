using static WinIsland.Platform.Windows.Interop.NativeMethods;

namespace WinIsland.Platform.Windows.Shell;

/// <summary>A plain Win32 message box: works even when WinUI failed to start.</summary>
public static unsafe class NativeDialog
{
    private const uint MB_OK = 0x0;
    private const uint MB_ICONERROR = 0x10;
    private const uint MB_SETFOREGROUND = 0x10000;

    public static void ShowError(string title, string message)
    {
        fixed (char* t = title)
        fixed (char* m = message)
        {
            MessageBox(0, m, t, MB_OK | MB_ICONERROR | MB_SETFOREGROUND);
        }
    }
}
