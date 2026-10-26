//! Is a fullscreen game or video in front? Then the pill and cards get out of the way.
//! Event-driven: checked when the foreground window changes or moves, never polled.

use std::cell::Cell;

use windows::Win32::Foundation::{HWND, RECT};
use windows::Win32::Graphics::Dwm::{DwmGetWindowAttribute, DWMWA_EXTENDED_FRAME_BOUNDS};
use windows::Win32::Graphics::Gdi::{GetMonitorInfoW, MonitorFromWindow, MONITORINFO, MONITOR_DEFAULTTONEAREST};
use windows::Win32::UI::Accessibility::{SetWinEventHook, UnhookWinEvent, HWINEVENTHOOK};
use windows::Win32::UI::Shell::{SHQueryUserNotificationState, QUNS_BUSY, QUNS_PRESENTATION_MODE, QUNS_RUNNING_D3D_FULL_SCREEN};
use windows::Win32::UI::WindowsAndMessaging::{
    GetClassNameW, GetForegroundWindow, GetWindowThreadProcessId, EVENT_OBJECT_LOCATIONCHANGE, WINEVENT_OUTOFCONTEXT,
};

use super::windows::{push_event, UiEvent};
use crate::render::layout::RectPx;

/// Desktop and taskbar windows can cover the monitor without being "fullscreen".
const NOT_FULLSCREEN: [&str; 4] = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

/// True when a window of class `class` with bounds `fg` covers the whole `monitor`.
/// Screenshot tools cover the screen while you pick a region: that isn't a fullscreen app (and
/// the pill belongs in the screenshot).
const SCREENSHOT_TOOLS: [&str; 12] = [
    "ShareX.exe",
    "SnippingTool.exe",
    "ScreenClippingHost.exe",
    "ScreenSketch.exe",
    "Greenshot.exe",
    "Lightshot.exe",
    "flameshot.exe",
    "PicPick.exe",
    "Snagit32.exe",
    "SnagitCapture.exe",
    "ScreenToGif.exe",
    "Monosnap.exe",
];

/// Windows 11's Alt+Tab, Task View and Snap are explorer's own monitor-wide XAML layers
/// (XamlExplorerHostIslandWindow): the pill blinked away and back while they were up.
const SHELL: [&str; 1] = ["explorer.exe"];

/// Whether this app never counts as fullscreen, whatever its window covers.
pub fn never_fullscreen(exe: &str) -> bool {
    SCREENSHOT_TOOLS.iter().chain(SHELL.iter()).any(|t| t.eq_ignore_ascii_case(exe))
}

pub fn is_fullscreen_rect(fg: RectPx, monitor: RectPx, class: &str) -> bool {
    !NOT_FULLSCREEN.contains(&class)
        && fg.x <= monitor.x
        && fg.y <= monitor.y
        && fg.right() >= monitor.right()
        && fg.bottom() >= monitor.bottom()
}

fn rect(r: RECT) -> RectPx {
    RectPx { x: r.left, y: r.top, w: r.right - r.left, h: r.bottom - r.top }
}

/// The monitor that `hwnd` is on.
pub fn monitor_of(hwnd: HWND) -> Option<RectPx> {
    unsafe {
        let monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        let mut info = MONITORINFO { cbSize: std::mem::size_of::<MONITORINFO>() as u32, ..Default::default() };
        GetMonitorInfoW(monitor, &mut info).as_bool().then(|| rect(info.rcMonitor))
    }
}

/// File name of the foreground window's process ("vlc.exe"), for the fullscreen exceptions.
pub fn foreground_exe() -> Option<String> {
    exe_of(unsafe { GetForegroundWindow() })
}

/// The apps with a visible, titled top-level window now: (exe, window title), one per exe, for
/// the settings window's exception suggestions.
pub fn visible_apps() -> Vec<(String, String)> {
    use windows::core::BOOL;
    use windows::Win32::Foundation::LPARAM;
    use windows::Win32::UI::WindowsAndMessaging::{
        EnumWindows, GetWindow, GetWindowLongW, GetWindowTextW, IsWindowVisible, GWL_EXSTYLE, GW_OWNER, WS_EX_TOOLWINDOW,
    };
    unsafe extern "system" fn each(hwnd: HWND, out: LPARAM) -> BOOL {
        let out = unsafe { &mut *(out.0 as *mut Vec<(String, String)>) };
        let tool = unsafe { GetWindowLongW(hwnd, GWL_EXSTYLE) } as u32 & WS_EX_TOOLWINDOW.0 != 0;
        let owned = unsafe { GetWindow(hwnd, GW_OWNER) }.is_ok_and(|o| !o.is_invalid());
        let mut cloaked = 0u32;
        let _ = unsafe { DwmGetWindowAttribute(hwnd, windows::Win32::Graphics::Dwm::DWMWA_CLOAKED, &mut cloaked as *mut u32 as _, 4) };
        if unsafe { IsWindowVisible(hwnd) }.as_bool() && !tool && !owned && cloaked == 0 {
            let mut buf = [0u16; 256];
            let len = unsafe { GetWindowTextW(hwnd, &mut buf) } as usize;
            if len > 0 {
                if let Some(exe) = exe_of(hwnd) {
                    if !out.iter().any(|(e, _)| e.eq_ignore_ascii_case(&exe)) {
                        out.push((exe, String::from_utf16_lossy(&buf[..len])));
                    }
                }
            }
        }
        true.into()
    }
    let mut out: Vec<(String, String)> = Vec::new();
    unsafe {
        let _ = EnumWindows(Some(each), LPARAM(&mut out as *mut _ as isize));
    }
    out
}

/// File name of `hwnd`'s process ("vlc.exe").
pub fn exe_of(hwnd: HWND) -> Option<String> {
    use windows::Win32::System::Threading::{OpenProcess, QueryFullProcessImageNameW, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION};
    unsafe {
        let mut pid = 0u32;
        GetWindowThreadProcessId(hwnd, Some(&mut pid));
        let process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid).ok()?;
        let mut buf = [0u16; 512];
        let mut len = buf.len() as u32;
        let ok = QueryFullProcessImageNameW(process, PROCESS_NAME_WIN32, windows::core::PWSTR(buf.as_mut_ptr()), &mut len).is_ok();
        let _ = windows::Win32::Foundation::CloseHandle(process);
        ok.then(|| String::from_utf16_lossy(&buf[..len as usize]).rsplit('\\').next().unwrap_or_default().to_string())
    }
}

/// Fullscreen on `monitor` (the taskbar's): a D3D/presentation/busy state reported by the shell,
/// or a foreground window covering that monitor (borderless games, F11 video).
pub fn fullscreen_now(monitor: RectPx) -> bool {
    unsafe {
        let fg = GetForegroundWindow();
        if fg.is_invalid() || monitor_of(fg) != Some(monitor) {
            return false;
        }
        if let Ok(state) = SHQueryUserNotificationState() {
            if [QUNS_BUSY, QUNS_RUNNING_D3D_FULL_SCREEN, QUNS_PRESENTATION_MODE].contains(&state) {
                return true;
            }
        }
        // With an auto-hiding taskbar a maximised window covers the monitor too, and isn't fullscreen.
        if windows::Win32::UI::WindowsAndMessaging::IsZoomed(fg).as_bool() && super::taskbar::auto_hides() {
            return false;
        }
        let mut bounds = RECT::default();
        let size = std::mem::size_of::<RECT>() as u32;
        if DwmGetWindowAttribute(fg, DWMWA_EXTENDED_FRAME_BOUNDS, &mut bounds as *mut RECT as _, size).is_err() {
            return false;
        }
        let mut buf = [0u16; 128];
        let n = GetClassNameW(fg, &mut buf) as usize;
        is_fullscreen_rect(rect(bounds), monitor, &String::from_utf16_lossy(&buf[..n]))
    }
}

/// The island may go over what's in front: not exclusive D3D fullscreen (a topmost window can
/// knock a game out of its mode) and not a presentation.
pub fn island_allowed() -> bool {
    unsafe { SHQueryUserNotificationState() }.is_ok_and(|s| s != QUNS_RUNNING_D3D_FULL_SCREEN && s != QUNS_PRESENTATION_MODE)
}

extern "system" fn on_move(_: HWINEVENTHOOK, _: u32, hwnd: HWND, id_object: i32, _: i32, _: u32, _: u32) {
    // OBJID_WINDOW is 0: the window itself moved or resized, not a caret or a child object.
    if id_object == 0 && hwnd == unsafe { GetForegroundWindow() } {
        push_event(UiEvent::ForegroundMoved);
    }
}

/// Watches the foreground window for moves and resizes (a window going fullscreen without
/// changing focus, e.g. F11). Hooks only the foreground window's process, re-targeted on every
/// foreground change, so other apps' location events never reach us.
#[derive(Default)]
pub struct ForegroundMoveHook {
    hook: Cell<Option<HWINEVENTHOOK>>,
}

impl ForegroundMoveHook {
    pub fn follow_foreground(&self) {
        unsafe {
            if let Some(old) = self.hook.take() {
                let _ = UnhookWinEvent(old);
            }
            let fg = GetForegroundWindow();
            let mut pid = 0u32;
            GetWindowThreadProcessId(fg, Some(&mut pid));
            if pid == 0 || pid == std::process::id() {
                return;
            }
            let hook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, None, Some(on_move), pid, 0, WINEVENT_OUTOFCONTEXT);
            if !hook.is_invalid() {
                self.hook.set(Some(hook));
            }
        }
    }
}

impl Drop for ForegroundMoveHook {
    fn drop(&mut self) {
        if let Some(hook) = self.hook.take() {
            unsafe {
                let _ = UnhookWinEvent(hook);
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const MONITOR: RectPx = RectPx { x: 0, y: 0, w: 1920, h: 1080 };

    #[test]
    fn the_shell_is_never_fullscreen() {
        assert!(never_fullscreen("explorer.exe"), "Alt+Tab, Task View and Snap are explorer's own layers");
        assert!(never_fullscreen("EXPLORER.EXE"));
        assert!(never_fullscreen("ShareX.exe"), "screenshot tools still");
        assert!(!never_fullscreen("vlc.exe"));
    }

    #[test]
    fn rects() {
        assert!(is_fullscreen_rect(MONITOR, MONITOR, "Chrome_WidgetWin_1"));
        assert!(!is_fullscreen_rect(MONITOR, MONITOR, "WorkerW"));
        assert!(!is_fullscreen_rect(MONITOR, MONITOR, "Progman"));
        assert!(!is_fullscreen_rect(RectPx { x: 0, y: 0, w: 1920, h: 1030 }, MONITOR, "Chrome_WidgetWin_1"));
        // Borderless windows often overhang the monitor by a few pixels.
        assert!(is_fullscreen_rect(RectPx { x: -8, y: -8, w: 1936, h: 1096 }, MONITOR, "UnityWndClass"));
    }

    #[test]
    fn screenshot_tools_never_hide_the_pill() {
        assert!(never_fullscreen("ShareX.exe"));
        assert!(never_fullscreen("sharex.exe"), "any case");
        assert!(never_fullscreen("ScreenClippingHost.exe"), "Win + Shift + S");
        assert!(!never_fullscreen("vlc.exe"));
    }
}
