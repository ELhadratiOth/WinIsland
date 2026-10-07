using WinIsland.Platform.Windows.Interop;
using static WinIsland.Platform.Windows.Interop.NativeMethods;

namespace WinIsland.Platform.Windows.Input;

public static unsafe class CursorPosition
{
    public static bool TryGet(out int x, out int y)
    {
        POINT p;
        bool ok = GetCursorPos(&p) != 0;
        x = p.X;
        y = p.Y;
        return ok;
    }
}
