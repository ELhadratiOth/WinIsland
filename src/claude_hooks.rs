//! Claude Code's hooks for the WinIsland in `~/.claude/settings.json`: `winisland.exe claude-hooks
//! install | remove`, and the exe's path kept current at start (as autostart follows the exe).
//! The pure part works on the settings' JSON; ours are the hooks whose first arg is `claude-hook`.

use std::path::PathBuf;

use serde_json::{json, Map, Value};

/// The events the WinIsland listens to (PermissionRequest waits for the card's answer).
pub const EVENTS: [&str; 10] = ["UserPromptSubmit", "PreToolUse", "PostToolUse", "PostToolUseFailure", "PermissionRequest", "PermissionDenied", "Notification", "Stop", "StopFailure", "SessionEnd"];

/// A hook entry of ours.
pub fn is_ours(hook: &Value) -> bool {
    hook["args"].get(0).and_then(Value::as_str) == Some("claude-hook")
}

fn our_hook(event: &str, exe: &str) -> Value {
    if event == "PermissionRequest" {
        // Synchronous: it may answer; 320 s outlasts the longest wait (300 s).
        json!({"type": "command", "command": exe, "args": ["claude-hook", "--ask"], "timeout": 320})
    } else {
        json!({"type": "command", "command": exe, "args": ["claude-hook"], "async": true})
    }
}

/// Our status line's command: `<exe> claude-status`, the path with forward slashes (Claude Code
/// runs it in Git Bash, which eats backslashes, or in PowerShell) and without spaces, so it needs
/// no quotes (a quoted path followed by an argument isn't a command in PowerShell).
pub fn status_command(exe: &str) -> String {
    format!("{} claude-status", short_path(exe).replace('\\', "/"))
}

/// `exe` as an 8.3 path when it has spaces (that form has none).
fn short_path(exe: &str) -> String {
    if !exe.contains(' ') {
        return exe.to_string();
    }
    let wide: Vec<u16> = exe.encode_utf16().chain([0]).collect();
    let mut buf = [0u16; 1024];
    let len = unsafe { windows::Win32::Storage::FileSystem::GetShortPathNameW(windows::core::PCWSTR(wide.as_ptr()), Some(&mut buf)) } as usize;
    if len == 0 || len > buf.len() { exe.to_string() } else { String::from_utf16_lossy(&buf[..len]) }
}

/// The status line is ours (`… claude-status`).
pub fn is_our_status(settings: &Value) -> bool {
    settings["statusLine"]["command"].as_str().is_some_and(|c| c.trim_end().ends_with(" claude-status"))
}

/// `settings` without our hooks and status line; events left with no hooks go.
pub fn remove(mut settings: Value) -> Value {
    if is_our_status(&settings) {
        if let Some(s) = settings.as_object_mut() {
            s.remove("statusLine");
        }
    }
    let Some(hooks) = settings.get_mut("hooks").and_then(Value::as_object_mut) else { return settings };
    for entries in hooks.values_mut() {
        let Some(list) = entries.as_array_mut() else { continue };
        // Only the entries we emptied go: another tool's odd shapes stay as they are.
        list.retain_mut(|entry| {
            let Some(inner) = entry.get_mut("hooks").and_then(Value::as_array_mut) else { return true };
            let had = inner.len();
            inner.retain(|h| !is_ours(h));
            !(inner.is_empty() && had > 0)
        });
    }
    hooks.retain(|_, entries| entries.as_array().is_none_or(|l| !l.is_empty()));
    settings
}

/// `settings` with our hooks for every event, pointing at `exe` (any old ones replaced), and our
/// status line (the Claude card's context and limits) unless the user has one of their own;
/// `None` when its `hooks` isn't an object (nothing we could add to).
pub fn install(settings: Value, exe: &str) -> Option<Value> {
    let mut settings = remove(settings);
    if !settings.is_object() {
        settings = Value::Object(Map::new());
    }
    let obj = settings.as_object_mut()?;
    if !obj.contains_key("statusLine") {
        obj.insert("statusLine".into(), json!({"type": "command", "command": status_command(exe)}));
    }
    let hooks = obj.entry("hooks").or_insert_with(|| json!({})).as_object_mut()?;
    for event in EVENTS {
        let entries = hooks.entry(event).or_insert_with(|| json!([]));
        if let Some(list) = entries.as_array_mut() {
            list.push(json!({"matcher": "", "hooks": [our_hook(event, exe)]}));
        }
    }
    Some(settings)
}

/// Our hooks repointed at `exe` when the exe they point at is gone (the WinIsland was moved): the
/// events, matchers and timeouts stay as the user left them. Hooks that point here bring our
/// status line along (hooks installed before it had none; the user's own stays). `None` when
/// nothing changes: there are no hooks of ours, or their exe still exists elsewhere (a dev build
/// running from elsewhere must not take them over).
pub fn follow(mut settings: Value, exe: &str, exists: impl Fn(&str) -> bool) -> Option<Value> {
    let mut changed = false;
    {
        let ours: Vec<&mut Value> = settings
            .get_mut("hooks")
            .and_then(Value::as_object_mut)?
            .values_mut()
            .filter_map(Value::as_array_mut)
            .flatten()
            .filter_map(|entry| entry.get_mut("hooks").and_then(Value::as_array_mut))
            .flatten()
            .filter(|h| is_ours(h))
            .collect();
        let commands: Vec<&str> = ours.iter().filter_map(|h| h["command"].as_str()).collect();
        if commands.is_empty() || commands.iter().any(|c| *c != exe && exists(c)) {
            return None;
        }
        if commands.iter().any(|c| *c != exe) {
            for h in ours {
                h["command"] = json!(exe);
            }
            changed = true;
        }
    }
    let want = status_command(exe);
    if settings.get("statusLine").is_none() {
        settings["statusLine"] = json!({"type": "command", "command": want});
        changed = true;
    } else if is_our_status(&settings) && settings["statusLine"]["command"].as_str() != Some(want.as_str()) {
        settings["statusLine"]["command"] = json!(want);
        changed = true;
    }
    changed.then_some(settings)
}

/// The settings file's text: `{}` when it doesn't exist yet. Any other failure (locked by Claude
/// Code or an antivirus, not UTF-8) is an error: reading it as empty would overwrite it.
pub fn read_settings(path: &std::path::Path) -> std::io::Result<String> {
    match std::fs::read_to_string(path) {
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok("{}".into()),
        other => other,
    }
}

/// Claude Code's user settings: `%CLAUDE_CONFIG_DIR%\settings.json`, else `~\.claude\settings.json`.
pub fn settings_path() -> Option<PathBuf> {
    let dir = std::env::var_os("CLAUDE_CONFIG_DIR").map(PathBuf::from).or_else(|| std::env::var_os("USERPROFILE").map(|h| PathBuf::from(h).join(".claude")))?;
    Some(dir.join("settings.json"))
}

/// Reads, changes and writes Claude's settings (backups first); `Ok(false)` when nothing changed.
pub fn rewrite(change: impl FnOnce(Value) -> Option<Value>) -> std::io::Result<bool> {
    let path = settings_path().ok_or_else(|| std::io::Error::other("no user profile"))?;
    rewrite_at(&path, change)
}

/// `rewrite` for the settings file at `path` (the setup's sandbox has its own).
pub fn rewrite_at(path: &std::path::Path, change: impl FnOnce(Value) -> Option<Value>) -> std::io::Result<bool> {
    let path = path.to_path_buf();
    let text = read_settings(&path)?;
    let current: Value = serde_json::from_str(text.trim_start_matches('\u{feff}')).map_err(std::io::Error::other)?;
    let Some(next) = change(current) else { return Ok(false) };
    if let Some(dir) = path.parent() {
        std::fs::create_dir_all(dir)?;
    }
    let backup = path.with_extension("json.before-winisland");
    // The first is the file before the WinIsland ever touched it; `.winisland-last` is the one before this write.
    if path.exists() {
        std::fs::copy(&path, if backup.exists() { path.with_extension("json.winisland-last") } else { backup })?;
    }
    let pretty = serde_json::to_string_pretty(&next).map_err(std::io::Error::other)?;
    let tmp = path.with_extension(format!("json.{}.tmp", std::process::id()));
    std::fs::write(&tmp, if text.contains("\r\n") { pretty.replace('\n', "\r\n") } else { pretty })?;
    std::fs::rename(&tmp, &path)?;
    Ok(true)
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    const EXE: &str = r"C:\Apps\WinIsland\winisland.exe";

    fn ours(v: &Value, event: &str) -> Vec<Value> {
        v["hooks"][event].as_array().into_iter().flatten().flat_map(|e| e["hooks"].as_array().cloned().unwrap_or_default()).filter(is_ours).collect()
    }

    #[test]
    fn install_adds_every_event_with_the_ask_synchronous() {
        let v = install(json!({}), EXE).unwrap();
        for event in EVENTS {
            let hooks = ours(&v, event);
            assert_eq!(hooks.len(), 1, "{event}");
            assert_eq!(hooks[0]["command"], EXE);
        }
        let ask = &ours(&v, "PermissionRequest")[0];
        assert_eq!(ask["args"], json!(["claude-hook", "--ask"]));
        assert_eq!(ask["timeout"], 320);
        assert!(ask.get("async").is_none(), "an async hook can't decide");
        assert_eq!(ours(&v, "Stop")[0]["async"], true, "the rest never hold Claude up");
    }

    #[test]
    fn install_twice_is_once_and_moves_the_path() {
        let v = install(install(json!({}), r"C:\old\winisland.exe").unwrap(), EXE).unwrap();
        assert_eq!(ours(&v, "PreToolUse").len(), 1);
        assert_eq!(ours(&v, "PreToolUse")[0]["command"], EXE);
    }

    #[test]
    fn other_hooks_and_settings_stay() {
        let orca = json!({"type": "command", "command": "orca-hook.cmd", "timeout": 10});
        let before = json!({"model": "opus", "hooks": {"Stop": [{"hooks": [orca.clone()]}]}});
        let v = install(before, EXE).unwrap();
        assert_eq!(v["model"], "opus");
        assert!(v["hooks"]["Stop"].to_string().contains("orca-hook.cmd"));
        let gone = remove(v);
        assert_eq!(ours(&gone, "Stop").len(), 0);
        assert!(gone["hooks"]["Stop"].to_string().contains("orca-hook.cmd"), "Orca's stays");
        assert!(gone["hooks"].get("PreToolUse").is_none(), "an event left empty goes");
    }

    #[test]
    fn the_users_key_order_stays() {
        let before: Value = serde_json::from_str(r#"{"permissions": {}, "model": "opus", "env": {}}"#).unwrap();
        let after = serde_json::to_string(&install(before, EXE).unwrap()).unwrap();
        let at = |k: &str| after.find(k).unwrap();
        assert!(at("permissions") < at("model") && at("model") < at("env") && at("env") < at("\"hooks\""));
    }

    #[test]
    fn remove_leaves_entries_it_cant_read() {
        let odd = json!({"hooks": {"Stop": [{"matcher": "", "command": "legacy.cmd"}]}});
        assert_eq!(remove(odd.clone()), odd, "not ours: kept as it is");
    }

    #[test]
    fn install_refuses_a_hooks_that_isnt_an_object() {
        assert_eq!(install(json!({"hooks": "off"}), EXE), None);
        assert!(install(json!({}), EXE).is_some());
    }

    #[test]
    fn follow_only_repoints_what_is_there() {
        let mut v = install(json!({}), r"C:\old\winisland.exe").unwrap();
        v["hooks"].as_object_mut().unwrap().remove("Notification");
        v["hooks"]["PermissionRequest"][0]["hooks"][0]["timeout"] = json!(100);
        let moved = follow(v, EXE, |_| false).unwrap();
        assert!(moved["hooks"].get("Notification").is_none(), "an event the user dropped stays dropped");
        assert_eq!(ours(&moved, "PermissionRequest")[0]["timeout"], 100, "the user's timeout stays");
        assert_eq!(ours(&moved, "PermissionRequest")[0]["command"], EXE);
    }

    #[test]
    fn only_a_missing_file_reads_as_empty() {
        let dir = std::env::temp_dir().join(format!("winisland-hooks-{}", std::process::id()));
        let _ = std::fs::create_dir_all(&dir);
        assert_eq!(read_settings(&dir.join("absent.json")).unwrap(), "{}");
        assert!(read_settings(&dir).is_err(), "unreadable (locked, a folder) is an error, not an empty file to overwrite");
    }

    #[test]
    fn the_status_line_comes_and_goes_with_the_hooks() {
        let v = install(json!({}), EXE).unwrap();
        assert_eq!(v["statusLine"], json!({"type": "command", "command": "C:/Apps/WinIsland/winisland.exe claude-status"}));
        assert!(is_our_status(&v));
        let gone = remove(v);
        assert!(gone.get("statusLine").is_none(), "ours goes with the hooks");
        let theirs = json!({"statusLine": {"type": "command", "command": "~/.claude/statusline.sh"}});
        let v = install(theirs.clone(), EXE).unwrap();
        assert_eq!(v["statusLine"], theirs["statusLine"], "the user's own status line stays");
        assert_eq!(remove(v)["statusLine"], theirs["statusLine"]);
    }

    #[test]
    fn hooks_from_before_the_status_line_get_it() {
        let mut v = install(json!({}), EXE).unwrap();
        v.as_object_mut().unwrap().remove("statusLine");
        let with = follow(v, EXE, |_| false).expect("the status line is added");
        assert!(is_our_status(&with));
        assert_eq!(follow(with.clone(), EXE, |_| false), None, "then nothing to do");
        let moved = follow(install(json!({}), r"C:\old\winisland.exe").unwrap(), EXE, |_| false).unwrap();
        assert_eq!(moved["statusLine"]["command"], "C:/Apps/WinIsland/winisland.exe claude-status", "it moves with the hooks");
    }

    #[test]
    fn follow_moves_only_existing_hooks() {
        let gone = |_: &str| false;
        assert_eq!(follow(json!({}), EXE, gone), None, "never installed: nothing to move");
        let v = install(json!({}), r"C:\old\winisland.exe").unwrap();
        assert_eq!(follow(v.clone(), EXE, |_| true), None, "the other exe is still there (a dev build ran): leave them");
        let moved = follow(v, EXE, gone).expect("their exe is gone: the app was moved");
        assert_eq!(ours(&moved, "Stop")[0]["command"], EXE);
        assert_eq!(follow(moved, EXE, gone), None, "already here");
    }
}
