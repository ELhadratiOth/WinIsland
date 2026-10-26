//! The wallpaper's colour, for the accent (Görünüm › Renk › "Duvar kâğıdından"): the picture on
//! the taskbar's monitor (or the solid colour), decoded small on a thread of its own, and the
//! accent kept for the painters.

use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Mutex, RwLock};
use std::time::SystemTime;

use windows::core::{PCWSTR, PWSTR};
use windows::Win32::System::Com::{CoCreateInstance, CoInitializeEx, CoTaskMemFree, CLSCTX_ALL, COINIT_MULTITHREADED};
use windows::Win32::UI::Shell::{DesktopWallpaper, IDesktopWallpaper};

use crate::platform::log::log;
use crate::render::color::{solid_accent, wallpaper_accent};
use crate::render::crest::load_crest;
use crate::render::layout::RectPx;

/// What the desktop shows: a picture (and when it was written, to notice it changing in place)
/// or a solid colour.
#[derive(Clone, Debug, PartialEq)]
enum Shown {
    Picture(PathBuf, Option<SystemTime>),
    Colour([u8; 3]),
}

/// The wallpaper's accent (`None`: not read yet, or the wallpaper has too little colour).
static ACCENT: RwLock<Option<(u8, u8, u8)>> = RwLock::new(None);
/// What the accent was read from.
static SHOWN: Mutex<Option<Shown>> = Mutex::new(None);
static BUSY: AtomicBool = AtomicBool::new(false);

/// The accent the wallpaper gives, once read.
pub fn accent() -> Option<(u8, u8, u8)> {
    ACCENT.read().ok().and_then(|a| *a)
}

/// Takes a string the shell allocated.
unsafe fn take(p: PWSTR) -> String {
    let s = unsafe { p.to_string() }.unwrap_or_default();
    unsafe { CoTaskMemFree(Some(p.0 as *const _)) };
    s
}

/// The picture Windows keeps of the current wallpaper (Spotlight's and a moved original's too).
fn transcoded() -> Option<PathBuf> {
    let p = PathBuf::from(std::env::var_os("APPDATA")?).join("Microsoft\\Windows\\Themes\\TranscodedWallpaper");
    p.exists().then_some(p)
}

/// What the desktop shows on the monitor at `monitor` (the first monitor's when none matches).
/// Needs COM on the calling thread.
fn shown(monitor: Option<RectPx>) -> Option<Shown> {
    unsafe {
        let desk: IDesktopWallpaper = CoCreateInstance(&DesktopWallpaper, None, CLSCTX_ALL).ok()?;
        let mut first: Option<String> = None;
        let mut on_ours: Option<String> = None;
        for i in 0..desk.GetMonitorDevicePathCount().unwrap_or(0) {
            let Ok(id) = desk.GetMonitorDevicePathAt(i) else { continue };
            let id = take(id);
            let wide: Vec<u16> = id.encode_utf16().chain(Some(0)).collect();
            let at = PCWSTR(wide.as_ptr());
            let path = desk.GetWallpaper(at).map(|p| take(p)).unwrap_or_default();
            let rect = desk.GetMonitorRECT(at).ok();
            if first.is_none() {
                first = Some(path.clone());
            }
            if let (Some(r), Some(m)) = (rect, monitor) {
                if r.left == m.x && r.top == m.y && r.right == m.right() && r.bottom == m.bottom() {
                    on_ours = Some(path);
                }
            }
        }
        let path = on_ours.or(first).unwrap_or_default();
        if path.is_empty() {
            // COLORREF: 0x00BBGGRR.
            let c = desk.GetBackgroundColor().ok()?.0;
            return Some(Shown::Colour([(c & 0xFF) as u8, ((c >> 8) & 0xFF) as u8, ((c >> 16) & 0xFF) as u8]));
        }
        // A moved or deleted original: Windows keeps its own copy of the picture on show.
        let p = Some(PathBuf::from(&path)).filter(|p| p.exists()).or_else(transcoded)?;
        let written = std::fs::metadata(&p).and_then(|m| m.modified()).ok();
        Some(Shown::Picture(p, written))
    }
}

/// The accent of what the desktop shows (a 64 px decode of a picture): `Some(None)` for one with
/// too little colour, `None` when the picture couldn't be read (being written, say).
fn accent_of(shown: &Shown) -> Option<Option<(u8, u8, u8)>> {
    let rgb = match shown {
        Shown::Picture(path, _) => {
            let c = load_crest(path, 64)?;
            wallpaper_accent(&c.px, c.size.0 as usize, c.size.1 as usize)
        }
        Shown::Colour(c) => solid_accent(*c),
    };
    Some(rgb.map(|[r, g, b]| (r, g, b)))
}

/// The wallpaper's accent now, read on the calling thread (which must have COM): the settings
/// window's swatch.
pub fn read_accent(monitor: Option<RectPx>) -> Option<(u8, u8, u8)> {
    shown(monitor).as_ref().and_then(accent_of).flatten()
}

/// Reads the wallpaper again on a thread of its own when what the desktop shows has changed
/// since the last read, and calls `changed` (on that thread) with the new accent kept. Calls
/// while one runs are dropped: the next check catches what they would have.
pub fn check(monitor: RectPx, changed: fn()) {
    if BUSY.swap(true, Ordering::AcqRel) {
        return;
    }
    let spawned = std::thread::Builder::new().name("wallpaper".into()).spawn(move || {
        unsafe {
            let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
        }
        let now = shown(Some(monitor));
        let fresh = SHOWN.lock().map(|last| *last != now).unwrap_or(true);
        // A picture that can't be read now isn't recorded: the next check reads it again.
        let read = match &now {
            Some(s) => accent_of(s),
            None => Some(None),
        };
        if fresh && read.is_none() {
            log(&format!("wallpaper: couldn't read {now:?}"));
        }
        if let (true, Some(accent)) = (fresh, read) {
            log(&format!("wallpaper: {now:?} → accent {accent:?}"));
            if let Ok(mut a) = ACCENT.write() {
                *a = accent;
            }
            if let Ok(mut last) = SHOWN.lock() {
                *last = now;
            }
            changed();
        }
        BUSY.store(false, Ordering::Release);
    });
    if spawned.is_err() {
        BUSY.store(false, Ordering::Release);
    }
}

#[cfg(test)]
mod tests {
    /// Reads this PC's wallpaper: `cargo t --lib live_wallpaper -- --ignored --nocapture`.
    #[test]
    #[ignore]
    fn live_wallpaper() {
        unsafe {
            let _ = super::CoInitializeEx(None, super::COINIT_MULTITHREADED);
        }
        println!("shown: {:?}", super::shown(None));
        println!("accent: {:?}", super::read_accent(None).map(|(r, g, b)| format!("#{r:02X}{g:02X}{b:02X}")));
    }
}
