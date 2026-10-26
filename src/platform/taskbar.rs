//! The taskbar: it marks the main monitor and its DPI, which the island follows, and Explorer
//! restarting shows as a new one.

use windows::core::{w, Result};
use windows::Win32::Foundation::{HWND, RECT};
use windows::Win32::UI::Shell::{SHAppBarMessage, ABM_GETSTATE, APPBARDATA};
use windows::Win32::UI::WindowsAndMessaging::{FindWindowW, GetWindowRect};

use crate::render::layout::RectPx;

const ABS_AUTOHIDE: u32 = 1;

pub fn find_taskbar() -> Option<HWND> {
    unsafe { FindWindowW(w!("Shell_TrayWnd"), None).ok() }
}

pub fn window_rect(hwnd: HWND) -> Result<RectPx> {
    let mut r = RECT::default();
    unsafe { GetWindowRect(hwnd, &mut r)? };
    Ok(RectPx { x: r.left, y: r.top, w: r.right - r.left, h: r.bottom - r.top })
}

/// The taskbar hides itself until the mouse comes (then a maximised window covers the monitor).
pub fn auto_hides() -> bool {
    let mut data = APPBARDATA { cbSize: std::mem::size_of::<APPBARDATA>() as u32, ..Default::default() };
    unsafe { SHAppBarMessage(ABM_GETSTATE, &mut data) as u32 & ABS_AUTOHIDE != 0 }
}
