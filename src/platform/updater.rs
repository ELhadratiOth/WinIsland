//! The update thread: asks GitHub for the latest release once a day, and on demand downloads its
//! installer, checks it against the SHA-256 GitHub keeps for it and runs it: the installer closes
//! this WinIsland, puts the new files in place and starts the new one.

use std::path::{Path, PathBuf};
use std::sync::mpsc::{channel, RecvTimeoutError, Sender};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use windows::core::{w, HSTRING};
use windows::Win32::Security::Cryptography::{BCryptHash, BCRYPT_SHA256_ALG_HANDLE};
use windows::Win32::UI::Shell::ShellExecuteW;
use windows::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;

use super::http::get_bytes;
use super::log::{log, log_path};
use crate::update::{is_newer, latest_url, parse_release, Release, UpdateState, REPO, SETUP};

/// The running version.
pub const VERSION: &str = env!("CARGO_PKG_VERSION");
/// The first ask waits for the start to settle; then one a day (GitHub allows 60 an hour).
const FIRST: Duration = Duration::from_secs(60);
const EVERY: Duration = Duration::from_secs(24 * 3600);

/// What the thread tells the app.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Progress {
    /// The latest release (newer or not).
    Found(UpdateState),
    /// The installer is running: this WinIsland quits, the installer starts the new one.
    Installed,
    Failed(String),
}

pub type UpdateMailbox = Arc<Mutex<Vec<Progress>>>;

enum Msg {
    Apply,
    Enable(bool),
}

pub struct Updater {
    tx: Sender<Msg>,
}

impl Updater {
    /// `post` runs on the thread after each `Progress` (the mailbox then holds it).
    pub fn start(mailbox: UpdateMailbox, post: impl Fn() + Send + 'static) -> Updater {
        let (tx, rx) = channel::<Msg>();
        std::thread::Builder::new()
            .name("update".into())
            .spawn(move || {
                let report = |p: Progress| {
                    if let Ok(mut m) = mailbox.lock() {
                        m.push(p);
                    }
                    post();
                };
                let mut enabled = crate::settings::current().update_check;
                let mut found: Option<Release> = None;
                let mut next = Instant::now() + FIRST;
                loop {
                    match rx.recv_timeout(next.saturating_duration_since(Instant::now())) {
                        Ok(Msg::Enable(on)) => {
                            if on && !enabled {
                                next = Instant::now() + FIRST;
                            }
                            enabled = on;
                        }
                        Ok(Msg::Apply) => {
                            if found.is_none() {
                                found = check().map(|(r, _)| r);
                            }
                            let result = match found.as_ref().filter(|r| is_newer(r, VERSION)) {
                                Some(r) => download(r).and_then(|setup| run_setup(&setup)).inspect_err(|_| open_page(&r.page)),
                                None => Err("no newer release".into()),
                            };
                            report(result.map_or_else(Progress::Failed, |()| Progress::Installed));
                        }
                        Err(RecvTimeoutError::Timeout) => {
                            if enabled && !REPO.is_empty() {
                                if let Some((r, state)) = check() {
                                    found = Some(r);
                                    report(Progress::Found(state));
                                }
                            }
                            next = Instant::now() + EVERY;
                        }
                        Err(RecvTimeoutError::Disconnected) => return,
                    }
                }
            })
            .expect("spawn update thread");
        Updater { tx }
    }

    /// Download, check and run the newer release's installer (asks GitHub first if it hasn't yet).
    pub fn apply(&self) {
        let _ = self.tx.send(Msg::Apply);
    }

    pub fn set_enabled(&self, on: bool) {
        let _ = self.tx.send(Msg::Enable(on));
    }
}

/// `%LOCALAPPDATA%\WinIsland\update.json`.
fn state_path() -> PathBuf {
    log_path().with_file_name("update.json")
}

/// What the last check found (the settings window and the tray read it).
pub fn state() -> Option<UpdateState> {
    serde_json::from_slice(&std::fs::read(state_path()).ok()?).ok()
}

fn unix_now() -> i64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_secs() as i64).unwrap_or(0)
}

/// Asks GitHub for the latest release and keeps the answer in update.json.
fn check() -> Option<(Release, UpdateState)> {
    let (status, body) = get_bytes(&latest_url(REPO), &[("Accept", "application/vnd.github+json")])
        .inspect_err(|e| log(&format!("update: {e}")))
        .ok()?;
    if status != 200 {
        log(&format!("update: GitHub answered {status}"));
        return None;
    }
    let r = parse_release(&serde_json::from_slice(&body).ok()?)?;
    let (a, b, c) = r.version;
    let state = UpdateState { latest: format!("{a}.{b}.{c}"), page: r.page.clone(), checked: unix_now() };
    let _ = std::fs::write(state_path(), serde_json::to_vec(&state).unwrap_or_default());
    log(&format!("update: latest {} (running {VERSION})", r.tag));
    Some((r, state))
}

/// The release's installer into `%LOCALAPPDATA%\WinIsland\update\<tag>\`, checked against GitHub's
/// SHA-256 for it (no checksum, no install).
fn download(r: &Release) -> Result<PathBuf, String> {
    let asset = r.asset(SETUP).ok_or(format!("{} has no {SETUP}", r.tag))?;
    let want = asset.sha256.as_deref().ok_or(format!("{SETUP}: GitHub gave no checksum"))?;
    let (status, bytes) = get_bytes(&asset.url, &[])?;
    if status != 200 {
        return Err(format!("{SETUP}: HTTP {status}"));
    }
    let got = sha256_hex(&bytes);
    if got != want {
        return Err(format!("{SETUP}: checksum {got}, expected {want}"));
    }
    let dir = log_path().with_file_name("update").join(&r.tag);
    std::fs::create_dir_all(&dir).map_err(|e| format!("{}: {e}", dir.display()))?;
    let path = dir.join(SETUP);
    std::fs::write(&path, bytes).map_err(|e| format!("{SETUP}: {e}"))?;
    log(&format!("update: {} downloaded and checked", r.tag));
    Ok(path)
}

/// Starts the installer quietly as an update: it waits for this WinIsland to quit, keeps the
/// choices made at install (start with Windows, shortcuts, Claude Code) and starts the new one.
fn run_setup(setup: &Path) -> Result<(), String> {
    std::process::Command::new(setup).args(["--quiet", "--update"]).spawn().map_err(|e| format!("{SETUP}: {e}"))?;
    log(&format!("update: started {}", setup.display()));
    Ok(())
}

/// The release page, for installing by hand when the update didn't go through.
fn open_page(page: &str) {
    if !page.is_empty() {
        unsafe {
            ShellExecuteW(None, w!("open"), &HSTRING::from(page), None, None, SW_SHOWNORMAL);
        }
    }
}

/// The old exes an earlier update left beside the new ones (one may still be open: next time, then).
pub fn cleanup_old() {
    let Some(dir) = std::env::current_exe().ok().and_then(|e| e.parent().map(Path::to_path_buf)) else { return };
    for old in ["winisland.old.exe", "winisland-settings.old.exe"] {
        let _ = std::fs::remove_file(dir.join(old));
    }
}

/// The file's SHA-256 as lowercase hex (Windows' own BCrypt).
pub fn sha256_hex(bytes: &[u8]) -> String {
    let mut out = [0u8; 32];
    let status = unsafe { BCryptHash(BCRYPT_SHA256_ALG_HANDLE, None, bytes, &mut out) };
    if status.is_err() {
        return String::new();
    }
    out.iter().map(|b| format!("{b:02x}")).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sha256_known_vector() {
        assert_eq!(sha256_hex(b"abc"), "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        assert_eq!(sha256_hex(b""), "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    }

    /// Asks GitHub for real: `cargo test --lib live_github -- --ignored --nocapture`.
    #[test]
    #[ignore]
    fn live_github() {
        if REPO.is_empty() {
            println!("REPO is empty: nothing to ask");
            return;
        }
        let (status, body) = get_bytes(&latest_url(REPO), &[("Accept", "application/vnd.github+json")]).unwrap();
        println!("HTTP {status}: {:?}", serde_json::from_slice(&body).ok().as_ref().and_then(parse_release));
    }
}
