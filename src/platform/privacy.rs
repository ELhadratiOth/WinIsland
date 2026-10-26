//! Which apps are using the microphone or the camera now. Windows records each use under
//! CapabilityAccessManager\ConsentStore: an app is using a device while its LastUsedTimeStart
//! is set and its LastUsedTimeStop is 0. A thread waits on that key (no polling) and posts
//! the new list whenever it changes.

use std::sync::{Arc, Mutex};

use windows::core::{w, PCWSTR};
use windows::Win32::Foundation::ERROR_SUCCESS;
use windows::Win32::System::Registry::{
    RegCloseKey, RegEnumKeyExW, RegGetValueW, RegNotifyChangeKeyValue, RegOpenKeyExW, HKEY, HKEY_CURRENT_USER, KEY_NOTIFY, KEY_READ,
    REG_NOTIFY_CHANGE_LAST_SET, REG_NOTIFY_CHANGE_NAME, RRF_RT_REG_QWORD,
};

use crate::activities::media::app_name;
use crate::platform::windows::post_privacy;

/// One app using the microphone, the camera or both.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct InUse {
    pub app: String,
    pub mic: bool,
    pub cam: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Device {
    Mic,
    Cam,
}

const STORE: PCWSTR = w!(r"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore");

/// The latest list, for the main thread (it is told with `post_privacy`).
pub type PrivacyMailbox = Arc<Mutex<Vec<InUse>>>;

/// Starts the thread that watches the consent store; the first list arrives at once.
pub fn watch(mailbox: PrivacyMailbox) {
    std::thread::spawn(move || unsafe {
        let mut key = HKEY::default();
        if RegOpenKeyExW(HKEY_CURRENT_USER, STORE, None, KEY_NOTIFY | KEY_READ, &mut key) != ERROR_SUCCESS {
            return;
        }
        let mut last = None;
        loop {
            let now = in_use();
            if last.as_ref() != Some(&now) {
                if let Ok(mut slot) = mailbox.lock() {
                    *slot = now.clone();
                }
                post_privacy();
                last = Some(now);
            }
            // Blocks until something under the store changes.
            if RegNotifyChangeKeyValue(key, true, REG_NOTIFY_CHANGE_LAST_SET | REG_NOTIFY_CHANGE_NAME, None, false) != ERROR_SUCCESS {
                break;
            }
        }
        let _ = RegCloseKey(key);
    });
}

/// The apps using the microphone or the camera now, by name.
pub fn in_use() -> Vec<InUse> {
    let mut found = Vec::new();
    let boot = boot_filetime();
    for (device, cap) in [(Device::Mic, "microphone"), (Device::Cam, "webcam")] {
        for sub in ["", r"\NonPackaged"] {
            let path = format!(r"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{cap}{sub}");
            for (name, start, stop) in entries(&path) {
                if consent_in_use(start, stop, boot) {
                    found.push((consent_app_name(&name), device));
                }
            }
        }
    }
    merge(&found)
}

/// Each subkey of `path` with its LastUsedTimeStart and LastUsedTimeStop (0 when missing).
fn entries(path: &str) -> Vec<(String, u64, u64)> {
    let mut out = Vec::new();
    let path: Vec<u16> = path.encode_utf16().chain(Some(0)).collect();
    unsafe {
        let mut key = HKEY::default();
        if RegOpenKeyExW(HKEY_CURRENT_USER, PCWSTR(path.as_ptr()), None, KEY_READ, &mut key) != ERROR_SUCCESS {
            return out;
        }
        let mut buf = [0u16; 512];
        for i in 0.. {
            let mut len = buf.len() as u32;
            if RegEnumKeyExW(key, i, Some(windows::core::PWSTR(buf.as_mut_ptr())), &mut len, None, None, None, None) != ERROR_SUCCESS {
                break;
            }
            let name = String::from_utf16_lossy(&buf[..len as usize]);
            let mut sub: Vec<u16> = buf[..len as usize].to_vec();
            sub.push(0);
            let value = |v: PCWSTR| {
                let (mut q, mut size) = (0u64, 8u32);
                let ok = RegGetValueW(key, PCWSTR(sub.as_ptr()), v, RRF_RT_REG_QWORD, None, Some(&mut q as *mut u64 as *mut _), Some(&mut size));
                if ok == ERROR_SUCCESS { q } else { 0 }
            };
            out.push((name, value(w!("LastUsedTimeStart")), value(w!("LastUsedTimeStop"))));
        }
        let _ = RegCloseKey(key);
    }
    out
}

/// When Windows started, as a FILETIME.
fn boot_filetime() -> u64 {
    let now = crate::activities::media_worker::filetime_now() as u64;
    now.saturating_sub(unsafe { windows::Win32::System::SystemInformation::GetTickCount64() } * 10_000)
}

/// An entry is in use while it has started and not stopped. A start from before Windows started
/// is a use that never recorded its end (the app crashed or was updated): not in use.
pub fn consent_in_use(start: u64, stop: u64, boot: u64) -> bool {
    start != 0 && stop == 0 && start >= boot
}

/// The app's name from its consent key: an exe path with '#' for '\' ("C:#…#brave.exe"), or a
/// package family name ("5319275A.WhatsAppDesktop_cv1g1gvanyjgm").
pub fn consent_app_name(key: &str) -> String {
    let name = if key.contains('#') { app_name(&key.replace('#', "\\")) } else { app_name(&format!("{key}!App")) };
    let name = name.strip_suffix("Desktop").filter(|n| !n.is_empty()).map(str::to_string).unwrap_or(name);
    let mut chars = name.chars();
    chars.next().map(|c| c.to_uppercase().chain(chars).collect()).unwrap_or_default()
}

/// One row per app, the devices it uses together, in first-seen order.
pub fn merge(found: &[(String, Device)]) -> Vec<InUse> {
    let mut rows: Vec<InUse> = Vec::new();
    for (app, device) in found {
        let i = rows.iter().position(|r| r.app == *app).unwrap_or_else(|| {
            rows.push(InUse { app: app.clone(), mic: false, cam: false });
            rows.len() - 1
        });
        match device {
            Device::Mic => rows[i].mic = true,
            Device::Cam => rows[i].cam = true,
        }
    }
    rows
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn consent_entry_in_use() {
        let boot = 134_188_000_000_000_000;
        assert!(consent_in_use(134_188_500_000_000_000, 0, boot));
        assert!(!consent_in_use(134_188_500_000_000_000, 134_188_500_040_000_000, boot), "stopped");
        assert!(!consent_in_use(0, 0, boot), "never used");
        // An app's microphone, started months before and never stopped.
        assert!(!consent_in_use(133_600_000_000_000_000, 0, boot), "started before Windows did");
        let rows = merge(&[("Discord".into(), Device::Mic), ("Brave".into(), Device::Cam), ("Discord".into(), Device::Cam)]);
        assert_eq!(
            rows,
            vec![InUse { app: "Discord".into(), mic: true, cam: true }, InUse { app: "Brave".into(), mic: false, cam: true }]
        );
    }

    #[test]
    fn exe_name_from_consent_key() {
        assert_eq!(consent_app_name("C:#Program Files#BraveSoftware#Brave-Browser#Application#brave.exe"), "Brave");
        assert_eq!(consent_app_name("C:#Users#Ekin#AppData#Local#Discord#app-1.0.9217#Discord.exe"), "Discord");
        assert_eq!(consent_app_name("C:#Program Files (x86)#Steam#steam.exe"), "Steam");
        assert_eq!(consent_app_name("5319275A.WhatsAppDesktop_cv1g1gvanyjgm"), "WhatsApp");
        assert_eq!(consent_app_name("Microsoft.WindowsCamera_8wekyb3d8bbwe"), "WindowsCamera");
    }
}
