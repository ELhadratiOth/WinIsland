using WinIsland.Core.Geometry;
using WinIsland.Platform.Windows.Interop;
using static WinIsland.Platform.Windows.Interop.NativeMethods;

namespace WinIsland.Platform.Windows.Display;

/// <summary>
/// The taskbar's safe zone. A docked taskbar is already excluded from the work area, which the
/// placement uses; an auto-hide taskbar is not, so when it lives on the top edge we report its
/// rectangle as an obstacle so the revealed taskbar never slides over the island.
/// </summary>
public static unsafe class TaskbarInfo
{
    public static PixelRect? AutoHideTopTaskbar()
    {
        APPBARDATA data = default;
        data.cbSize = (uint)sizeof(APPBARDATA);
        bool autoHide = (SHAppBarMessage(ABM_GETSTATE, &data) & ABS_AUTOHIDE) != 0;
        if (!autoHide)
        {
            return null;
        }

        data = default;
        data.cbSize = (uint)sizeof(APPBARDATA);
        if (SHAppBarMessage(ABM_GETTASKBARPOS, &data) == 0 || data.uEdge != ABE_TOP)
        {
            return null;
        }

        return data.rc.ToPixelRect();
    }
}
