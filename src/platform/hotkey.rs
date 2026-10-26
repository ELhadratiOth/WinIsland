//! The timer picker's shortcut (settings; Win+Alt+N by default — Win+Alt+T, the first choice,
//! is often taken: PowerToys holds it).

use windows::core::Result;
use windows::Win32::Foundation::HWND;
use windows::Win32::UI::Input::KeyboardAndMouse::{
    RegisterHotKey, UnregisterHotKey, HOT_KEY_MODIFIERS, MOD_ALT, MOD_CONTROL, MOD_NOREPEAT, MOD_SHIFT, MOD_WIN,
};

use crate::settings::Hotkey;

const ID: i32 = 1;

fn keys(h: Hotkey) -> Option<(HOT_KEY_MODIFIERS, char)> {
    match h {
        Hotkey::WinAltN => Some((MOD_WIN | MOD_ALT, 'N')),
        Hotkey::WinAltT => Some((MOD_WIN | MOD_ALT, 'T')),
        Hotkey::CtrlAltN => Some((MOD_CONTROL | MOD_ALT, 'N')),
        Hotkey::WinShiftN => Some((MOD_WIN | MOD_SHIFT, 'N')),
        Hotkey::Off => None,
    }
}

/// Registers `h` (replacing the previous one); `Off` only unregisters.
pub fn register_hotkey(owner: HWND, h: Hotkey) -> Result<()> {
    unsafe {
        let _ = UnregisterHotKey(Some(owner), ID);
        match keys(h) {
            Some((mods, key)) => RegisterHotKey(Some(owner), ID, mods | MOD_NOREPEAT, key as u32),
            None => Ok(()),
        }
    }
}
