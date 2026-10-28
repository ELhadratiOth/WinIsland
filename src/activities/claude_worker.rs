//! Reads Claude Code's transcripts every few seconds (the newest dozen sessions, only the files
//! that changed, only their start and end), asks Claude for the plan's 5-hour and weekly limits
//! every five minutes with Claude Code's own login, and runs `claude -p --resume` for the Claude
//! tab's messages and Compact. The token is sent to api.anthropic.com only, and never logged.

use std::collections::{HashMap, HashSet};
use std::io::{Read, Seek, SeekFrom, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use super::claude::Limit;
use super::claude_sessions::{plan_limits, read_head, read_tail, Head, Session, Tail, Turn};
use crate::platform::log::log;

/// What the UI reads: the sessions, newest first, and the plan's limits (none until read).
#[derive(Clone, Debug, Default, PartialEq)]
pub struct ClaudeBox {
    pub sessions: Vec<Session>,
    pub limits: (Option<Limit>, Option<Limit>),
}

pub type ClaudeMailbox = Arc<Mutex<ClaudeBox>>;

/// The sessions the tab keeps: the newest this many, written to in the last two weeks.
const SESSIONS: usize = 12;
const RECENT_S: i64 = 14 * 86_400;
const HEAD_BYTES: u64 = 64 * 1024;
const TAIL_BYTES: u64 = 512 * 1024;
const LIMITS_EVERY: Duration = Duration::from_secs(300);
/// The card opening asks again, but not more often than this.
const LIMITS_SOONEST: Duration = Duration::from_secs(60);

static LIMITS_NOW: AtomicBool = AtomicBool::new(false);

/// The Claude card opened: the limits are read again soon.
pub fn want_limits() {
    LIMITS_NOW.store(true, Ordering::Relaxed);
}

/// Claude Code's folder: `%CLAUDE_CONFIG_DIR%`, else `~\.claude`.
pub fn config_dir() -> Option<PathBuf> {
    std::env::var_os("CLAUDE_CONFIG_DIR").map(PathBuf::from).or_else(|| std::env::var_os("USERPROFILE").map(|h| PathBuf::from(h).join(".claude")))
}

fn unix_s(t: SystemTime) -> i64 {
    t.duration_since(UNIX_EPOCH).map_or(0, |d| d.as_secs() as i64)
}

struct Cached {
    len: u64,
    modified: i64,
    head: Head,
    tail: Tail,
}

/// `len` bytes from `at`, cut to whole lines (a partial first line dropped when `at` isn't the start).
fn read_part(path: &Path, at: u64, len: u64) -> Option<String> {
    let mut f = std::fs::File::open(path).ok()?;
    f.seek(SeekFrom::Start(at)).ok()?;
    let mut bytes = Vec::new();
    f.take(len).read_to_end(&mut bytes).ok()?;
    let text = String::from_utf8_lossy(&bytes).into_owned();
    Some(if at > 0 { text.split_once('\n').map_or(String::new(), |(_, rest)| rest.to_string()) } else { text })
}

/// The transcripts: `projects\<folder>\<session>.jsonl`, with their length and last write.
fn transcripts(dir: &Path) -> Vec<(PathBuf, u64, i64)> {
    let mut found = Vec::new();
    let Ok(folders) = std::fs::read_dir(dir.join("projects")) else { return found };
    for folder in folders.flatten().filter(|f| f.file_type().is_ok_and(|t| t.is_dir())) {
        let Ok(files) = std::fs::read_dir(folder.path()) else { continue };
        for f in files.flatten() {
            let path = f.path();
            if path.extension().is_some_and(|e| e == "jsonl") {
                if let Ok(meta) = f.metadata() {
                    found.push((path, meta.len(), meta.modified().map_or(0, unix_s)));
                }
            }
        }
    }
    found
}

/// The newest sessions, re-reading only the files that changed since `cache` saw them.
fn scan(dir: &Path, cache: &mut HashMap<PathBuf, Cached>) -> Vec<Session> {
    let now = unix_s(SystemTime::now());
    let mut files: Vec<(PathBuf, u64, i64)> = transcripts(dir).into_iter().filter(|(_, len, m)| *len > 0 && now - m < RECENT_S).collect();
    files.sort_by_key(|(_, _, m)| std::cmp::Reverse(*m));
    files.truncate(SESSIONS);
    cache.retain(|p, _| files.iter().any(|(f, _, _)| f == p));
    let mut sessions = Vec::new();
    for (path, len, modified) in files {
        let fresh = cache.get(&path).is_none_or(|c| c.len != len || c.modified != modified);
        if fresh {
            let tail = read_part(&path, len.saturating_sub(TAIL_BYTES), TAIL_BYTES).map(|t| read_tail(&t)).unwrap_or_default();
            let entry = cache.entry(path.clone()).or_insert_with(|| Cached { len, modified, head: Head::default(), tail: Tail::default() });
            // A new prompt's title may come after the first read: the start until it has one.
            if entry.head.title.is_empty() || entry.head.cwd.is_empty() {
                entry.head = read_part(&path, 0, HEAD_BYTES).map(|t| read_head(&t)).unwrap_or_default();
            }
            // A part with one giant line says nothing: keep what was known.
            let keep = |new: Option<String>, old: &Option<String>| new.or_else(|| old.clone());
            let merged = Tail {
                model: keep(tail.model.clone(), &entry.tail.model),
                context: tail.context.or(entry.tail.context),
                title: keep(tail.title.clone(), &entry.tail.title),
                cwd: keep(tail.cwd.clone(), &entry.tail.cwd),
                ..tail
            };
            (entry.len, entry.modified, entry.tail) = (len, modified, merged);
        }
        let Some(c) = cache.get(&path) else { continue };
        let id = path.file_stem().map(|s| s.to_string_lossy().into_owned()).unwrap_or_default();
        sessions.push(Session {
            id,
            cwd: if c.head.cwd.is_empty() { c.tail.cwd.clone().unwrap_or_default() } else { c.head.cwd.clone() },
            title: c.tail.title.clone().unwrap_or_else(|| c.head.title.clone()),
            entry: c.head.entry.clone(),
            modified: c.modified,
            model: c.tail.model.clone().unwrap_or_default(),
            context: c.tail.context,
            turn: c.tail.turn,
            step: c.tail.step.clone(),
            answer: c.tail.answer.clone(),
        });
    }
    sessions
}

/// The plan's limits, asked with Claude Code's login (`.credentials.json`); why not, otherwise.
fn read_limits(dir: &Path) -> Result<(Option<Limit>, Option<Limit>), String> {
    let text = std::fs::read_to_string(dir.join(".credentials.json")).map_err(|_| "no Claude Code login on this PC".to_string())?;
    let creds: serde_json::Value = serde_json::from_str(text.trim_start_matches('\u{feff}')).map_err(|_| "the login file isn't JSON".to_string())?;
    let oauth = &creds["claudeAiOauth"];
    let token = oauth["accessToken"].as_str().filter(|t| !t.is_empty()).ok_or("no token in the login file")?;
    // Claude Code renews it when it next runs; until then the old one is refused anyway.
    if oauth["expiresAt"].as_i64().is_some_and(|ms| ms < unix_s(SystemTime::now()) * 1000) {
        return Err("the login has expired (Claude Code renews it when it runs)".into());
    }
    let auth = format!("Bearer {token}");
    let headers = [("Authorization", auth.as_str()), ("anthropic-beta", "oauth-2025-04-20"), ("Accept", "application/json")];
    match crate::platform::http::get_bytes("https://api.anthropic.com/api/oauth/usage", &headers)? {
        (200, body) => {
            let v: serde_json::Value = serde_json::from_slice(&body).map_err(|_| "the answer isn't JSON".to_string())?;
            Ok(plan_limits(&v))
        }
        (status, _) => Err(format!("HTTP {status}")),
    }
}

/// Starts the thread; `post` runs on it whenever the sessions or the limits change.
pub fn start(mailbox: ClaudeMailbox, post: fn()) {
    let spawned = std::thread::Builder::new().name("claude".into()).spawn(move || {
        let mut cache = HashMap::new();
        let mut asked: Option<Instant> = None;
        let mut said = String::new();
        loop {
            let Some(dir) = config_dir() else { return };
            let sessions = scan(&dir, &mut cache);
            let working = sessions.iter().any(|s| s.turn == Turn::Working);
            let wanted = crate::settings::current().claude_plan_limits;
            let due = asked.is_none_or(|t| t.elapsed() >= LIMITS_EVERY || (LIMITS_NOW.load(Ordering::Relaxed) && t.elapsed() >= LIMITS_SOONEST));
            let limits = (wanted && due).then(|| {
                LIMITS_NOW.store(false, Ordering::Relaxed);
                asked = Some(Instant::now());
                let read = read_limits(&dir);
                // Each new reason once, not every five minutes.
                let now_said = read.as_ref().map_or_else(Clone::clone, |_| "ok".to_string());
                if now_said != said {
                    log(&format!("claude: plan limits: {now_said}"));
                    said = now_said;
                }
                read.ok()
            });
            let changed = match mailbox.lock() {
                Ok(mut b) => {
                    let mut next = ClaudeBox { sessions, limits: b.limits };
                    if let Some(l) = limits {
                        next.limits = l.unwrap_or(b.limits);
                    }
                    if !wanted {
                        next.limits = (None, None);
                    }
                    let changed = *b != next;
                    *b = next;
                    changed
                }
                Err(_) => false,
            };
            if changed {
                post();
            }
            std::thread::sleep(Duration::from_secs(if working { 2 } else { 5 }));
        }
    });
    if let Err(e) = spawned {
        log(&format!("claude: no reader thread: {e}"));
    }
}

/// The `claude` command: `claude.exe` (the native install, or on PATH), else npm's `claude.cmd`.
pub fn claude_exe() -> Option<PathBuf> {
    let home = std::env::var_os("USERPROFILE").map(PathBuf::from);
    let mut places: Vec<PathBuf> = home.iter().map(|h| h.join(".local").join("bin").join("claude.exe")).collect();
    let path_dirs: Vec<PathBuf> = std::env::var_os("PATH").map(|p| std::env::split_paths(&p).collect()).unwrap_or_default();
    places.extend(path_dirs.iter().map(|d| d.join("claude.exe")));
    places.extend(path_dirs.iter().map(|d| d.join("claude.cmd")));
    places.extend(std::env::var_os("APPDATA").map(|a| PathBuf::from(a).join("npm").join("claude.cmd")));
    places.into_iter().find(|p| p.is_file())
}

/// The sessions a message is going to now (one at a time each).
static SENDING: Mutex<Option<HashSet<String>>> = Mutex::new(None);

/// Whether a message or a Compact is on its way to `id`.
pub fn sending(id: &str) -> bool {
    SENDING.lock().is_ok_and(|s| s.as_ref().is_some_and(|s| s.contains(id)))
}

/// A session id is a UUID: nothing else goes on a command line.
fn plain_id(id: &str) -> bool {
    !id.is_empty() && id.len() <= 64 && id.chars().all(|c| c.is_ascii_hexdigit() || c == '-')
}

/// Sends `text` (a message, or "/compact") to session `s` with `claude -p --resume`, in its
/// folder, without a window; the text goes in on stdin, so nothing in it is a command line.
/// Claude's progress shows through the hooks and the transcript; the result is logged.
pub fn send(s: &Session, text: &str, post: fn()) -> Result<(), String> {
    if !plain_id(&s.id) {
        return Err("not a session id".into());
    }
    let exe = claude_exe().ok_or("Claude Code's `claude` command isn't installed")?;
    {
        let mut set = SENDING.lock().map_err(|_| "busy")?;
        if !set.get_or_insert_with(HashSet::new).insert(s.id.clone()) {
            return Err("still sending the last one".into());
        }
    }
    let args = ["-p", "--resume", s.id.as_str(), "--output-format", "json"];
    let mut cmd = if exe.extension().is_some_and(|e| e.eq_ignore_ascii_case("cmd")) {
        let mut c = std::process::Command::new("cmd.exe");
        c.arg("/d").arg("/c").arg(&exe).args(args);
        c
    } else {
        let mut c = std::process::Command::new(&exe);
        c.args(args);
        c
    };
    use std::os::windows::process::CommandExt;
    const CREATE_NO_WINDOW: u32 = 0x0800_0000;
    cmd.creation_flags(CREATE_NO_WINDOW).stdin(std::process::Stdio::piped()).stdout(std::process::Stdio::piped()).stderr(std::process::Stdio::piped());
    if Path::new(&s.cwd).is_dir() {
        cmd.current_dir(&s.cwd);
    }
    let done = |id: &str| {
        if let Ok(mut set) = SENDING.lock() {
            if let Some(set) = set.as_mut() {
                set.remove(id);
            }
        }
    };
    let mut child = match cmd.spawn() {
        Ok(c) => c,
        Err(e) => {
            done(&s.id);
            return Err(format!("claude didn't start: {e}"));
        }
    };
    if let Some(mut stdin) = child.stdin.take() {
        let _ = stdin.write_all(text.as_bytes());
    }
    let (id, what) = (s.id.clone(), if text.starts_with('/') { text.to_string() } else { "a message".to_string() });
    log(&format!("claude: {what} to {id} with {}", exe.display()));
    std::thread::spawn(move || {
        let out = child.wait_with_output();
        let line = match &out {
            Ok(o) if o.status.success() => {
                let v: serde_json::Value = serde_json::from_slice(&o.stdout).unwrap_or_default();
                format!("done{}", if v["is_error"] == true { " with an error" } else { "" })
            }
            Ok(o) => format!("failed ({}): {}", o.status, String::from_utf8_lossy(&o.stderr).lines().last().unwrap_or("")),
            Err(e) => format!("failed: {e}"),
        };
        log(&format!("claude: {what} to {id} {line}"));
        done(&id);
        post();
    });
    Ok(())
}
