//! Typing into another app's window: the Claude card's message box and its Compact button put
//! their text in the terminal Claude Code runs in, as if typed there, and press Enter.

use windows::Win32::Foundation::HWND;
use windows::Win32::UI::Input::KeyboardAndMouse::{SendInput, INPUT, INPUT_0, INPUT_KEYBOARD, KEYBDINPUT, KEYEVENTF_KEYUP, KEYEVENTF_UNICODE, VIRTUAL_KEY, VK_RETURN};
use windows::Win32::UI::WindowsAndMessaging::{GetForegroundWindow, IsIconic, IsWindow, SetForegroundWindow, ShowWindow, SW_RESTORE};

use super::log::log;

fn key(vk: VIRTUAL_KEY, unit: u16, up: bool) -> INPUT {
    let mut flags = if up { KEYEVENTF_KEYUP } else { Default::default() };
    if unit != 0 {
        flags |= KEYEVENTF_UNICODE;
    }
    INPUT { r#type: INPUT_KEYBOARD, Anonymous: INPUT_0 { ki: KEYBDINPUT { wVk: vk, wScan: unit, dwFlags: flags, ..Default::default() } } }
}

/// The key presses that type `text` (each UTF-16 unit as a Unicode key, so any language and
/// emoji go through whatever the keyboard layout is).
pub fn text_inputs(text: &str) -> Vec<INPUT> {
    text.encode_utf16().flat_map(|u| [key(VIRTUAL_KEY(0), u, false), key(VIRTUAL_KEY(0), u, true)]).collect()
}

/// The clipboard's text, if it has some.
pub fn clipboard_text() -> Option<String> {
    use windows::Win32::Foundation::HGLOBAL;
    use windows::Win32::System::DataExchange::{CloseClipboard, GetClipboardData, OpenClipboard};
    use windows::Win32::System::Memory::{GlobalLock, GlobalUnlock};
    use windows::Win32::System::Ole::CF_UNICODETEXT;
    unsafe {
        OpenClipboard(None).ok()?;
        let text = GetClipboardData(CF_UNICODETEXT.0 as u32).ok().and_then(|data| {
            let memory = HGLOBAL(data.0);
            let p = GlobalLock(memory) as *const u16;
            if p.is_null() {
                return None;
            }
            let len = (0..).take_while(|&i| *p.add(i) != 0).count();
            let s = String::from_utf16_lossy(std::slice::from_raw_parts(p, len));
            let _ = GlobalUnlock(memory);
            Some(s)
        });
        let _ = CloseClipboard();
        text
    }
}

/// Types `text` and Enter into `window` on a thread of its own, then gives the foreground back
/// to `back_to`. False when the window is gone. Called from a click or a key in the WinIsland,
/// so this process may hand the foreground on.
pub fn type_into(window: HWND, text: &str, back_to: Option<HWND>) -> bool {
    if !unsafe { IsWindow(Some(window)) }.as_bool() {
        log("keys: the window is gone");
        return false;
    }
    let (target, back) = (window.0 as isize, back_to.map(|h| h.0 as isize));
    let text = text.to_string();
    std::thread::spawn(move || unsafe {
        let window = HWND(target as *mut _);
        if IsIconic(window).as_bool() {
            let _ = ShowWindow(window, SW_RESTORE);
        }
        let _ = SetForegroundWindow(window);
        for _ in 0..25 {
            if GetForegroundWindow() == window {
                break;
            }
            std::thread::sleep(std::time::Duration::from_millis(20));
        }
        if GetForegroundWindow() != window {
            log("keys: the window didn't come to the front; nothing typed");
            return;
        }
        std::thread::sleep(std::time::Duration::from_millis(60));
        let size = std::mem::size_of::<INPUT>() as i32;
        SendInput(&text_inputs(&text), size);
        // Apart from the text, so a terminal that reads a burst as a paste still sees the Enter.
        std::thread::sleep(std::time::Duration::from_millis(150));
        SendInput(&[key(VK_RETURN, 0, false), key(VK_RETURN, 0, true)], size);
        log(&format!("keys: typed {} characters and Enter", text.chars().count()));
        if let Some(back) = back.map(|h| HWND(h as *mut _)).filter(|h| !h.is_invalid() && *h != window) {
            std::thread::sleep(std::time::Duration::from_millis(100));
            let _ = SetForegroundWindow(back);
        }
    });
    true
}
