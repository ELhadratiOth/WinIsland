//! Spotify's desktop app: its Like shortcut (Alt+Shift+B) pressed in its window. Windows' media
//! controls have no like and the Web API needs an account link, so the card's heart brings
//! Spotify to the front for a moment, presses the keys there and gives the foreground back.

use windows::core::BOOL;
use windows::Win32::Foundation::{HWND, LPARAM};
use windows::Win32::UI::Input::KeyboardAndMouse::{SendInput, INPUT, INPUT_0, INPUT_KEYBOARD, KEYBDINPUT, KEYEVENTF_KEYUP, VIRTUAL_KEY, VK_MENU, VK_SHIFT};
use windows::Win32::UI::WindowsAndMessaging::{
    EnumWindows, GetClassNameW, GetForegroundWindow, GetWindow, GetWindowTextLengthW, IsIconic, IsWindowVisible, SetForegroundWindow, ShowWindow, GW_OWNER, SW_HIDE,
    SW_MINIMIZE, SW_RESTORE,
};

use super::fullscreen::exe_of;
use super::log::log;

/// Spotify's main window: a titled, unowned Chromium window of Spotify.exe (a visible one first;
/// closed to the tray it is hidden).
pub fn main_window() -> Option<HWND> {
    unsafe extern "system" fn each(hwnd: HWND, out: LPARAM) -> BOOL {
        let out = unsafe { &mut *(out.0 as *mut Vec<(HWND, bool)>) };
        let mut class = [0u16; 64];
        let len = unsafe { GetClassNameW(hwnd, &mut class) } as usize;
        let chromium = String::from_utf16_lossy(&class[..len]).starts_with("Chrome_WidgetWin");
        let owned = unsafe { GetWindow(hwnd, GW_OWNER) }.is_ok_and(|o| !o.is_invalid());
        if chromium && !owned && unsafe { GetWindowTextLengthW(hwnd) } > 0 && exe_of(hwnd).is_some_and(|e| e.eq_ignore_ascii_case("Spotify.exe")) {
            out.push((hwnd, unsafe { IsWindowVisible(hwnd) }.as_bool()));
        }
        true.into()
    }
    let mut found: Vec<(HWND, bool)> = Vec::new();
    unsafe {
        let _ = EnumWindows(Some(each), LPARAM(&mut found as *mut _ as isize));
    }
    found.sort_by_key(|(_, visible)| !visible);
    found.first().map(|(hwnd, _)| *hwnd)
}

fn key(vk: VIRTUAL_KEY, up: bool) -> INPUT {
    INPUT {
        r#type: INPUT_KEYBOARD,
        Anonymous: INPUT_0 { ki: KEYBDINPUT { wVk: vk, dwFlags: if up { KEYEVENTF_KEYUP } else { Default::default() }, ..Default::default() } },
    }
}

/// Likes the playing song (or takes it out of Liked Songs: the shortcut toggles) on a thread of
/// its own. False when Spotify has no window to press it in. Called right after the click on the
/// card, so this process may still hand the foreground on.
pub fn toggle_like() -> bool {
    let Some(spotify) = main_window() else {
        log("spotify: no window for the like");
        return false;
    };
    let hwnd = spotify.0 as isize;
    std::thread::spawn(move || unsafe {
        let spotify = HWND(hwnd as *mut _);
        let before = GetForegroundWindow();
        let (minimized, hidden) = (IsIconic(spotify).as_bool(), !IsWindowVisible(spotify).as_bool());
        if minimized || hidden {
            let _ = ShowWindow(spotify, SW_RESTORE);
        }
        let _ = SetForegroundWindow(spotify);
        // Up to half a second for it to come to the front.
        for _ in 0..25 {
            if GetForegroundWindow() == spotify {
                break;
            }
            std::thread::sleep(std::time::Duration::from_millis(20));
        }
        if GetForegroundWindow() != spotify {
            log("spotify: it didn't come to the front; no like");
        } else {
            std::thread::sleep(std::time::Duration::from_millis(60));
            let b = VIRTUAL_KEY(u16::from(b'B'));
            let keys = [key(VK_MENU, false), key(VK_SHIFT, false), key(b, false), key(b, true), key(VK_SHIFT, true), key(VK_MENU, true)];
            SendInput(&keys, std::mem::size_of::<INPUT>() as i32);
            std::thread::sleep(std::time::Duration::from_millis(150));
            log("spotify: like pressed");
        }
        if hidden {
            let _ = ShowWindow(spotify, SW_HIDE);
        } else if minimized {
            let _ = ShowWindow(spotify, SW_MINIMIZE);
        }
        if !before.is_invalid() && before != spotify {
            let _ = SetForegroundWindow(before);
        }
    });
    true
}
