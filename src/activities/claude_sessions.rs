//! Claude Code's sessions from their transcripts (`~/.claude/projects/<folder>/<session>.jsonl`),
//! which the terminal and the desktop app both write: what each is about, how full its context
//! is and whether a turn is under way; and the plan's 5-hour and weekly limits from Claude's
//! usage answer. The pure part; `claude_worker` reads the files and asks for the usage.

use serde_json::Value;

use super::claude::{step_of, summary_of, Limit, Step};

/// `s` at most `n` characters, a cut one ending in "…".
fn cut(s: &str, n: usize) -> String {
    if s.chars().count() <= n {
        return s.to_string();
    }
    s.chars().take(n - 1).chain(['…']).collect()
}

/// Where a session's last turn is, as its transcript tells.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Turn {
    /// A prompt or a tool's result is waiting for Claude, or Claude is still writing.
    Working,
    /// Claude answered and stopped.
    Done,
    /// Esc: the turn was cut off.
    Interrupted,
    /// Nothing that says (a new transcript, or only local commands).
    #[default]
    Unknown,
}

/// One session as the Claude tab shows it.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct Session {
    pub id: String,
    /// The folder it runs in.
    pub cwd: String,
    /// The name it was given, else its first prompt (one line).
    pub title: String,
    /// Where it runs: Claude Code's `entrypoint` ("cli", "claude-desktop", …).
    pub entry: String,
    /// The transcript's last write (Unix seconds).
    pub modified: i64,
    pub model: String,
    /// Tokens in the context after the last answer (or after the last compaction).
    pub context: Option<u64>,
    pub turn: Turn,
    /// The tool call under way, while working.
    pub step: Option<Step>,
    /// The first line of the last answer.
    pub answer: Option<String>,
}

impl Session {
    /// The project: the last part of its folder.
    pub fn project(&self) -> &str {
        self.cwd.rsplit(['\\', '/']).find(|p| !p.is_empty()).unwrap_or("Claude")
    }

    /// How full the context is (0–100).
    pub fn context_pct(&self) -> Option<f32> {
        self.context.map(|t| (t as f32 * 100.0 / context_window(&self.model, t) as f32).min(100.0))
    }
}

/// The context window a model's session has: 200k tokens, or 1M for one that holds more (an
/// extended-context model, `[1m]` in its name, or a session already past 200k).
pub fn context_window(model: &str, tokens: u64) -> u64 {
    if model.contains("[1m]") || tokens > 200_000 {
        1_000_000
    } else {
        200_000
    }
}

/// "Terminal", "Desktop", "IDE", or nothing for an entry point it doesn't know.
pub fn entry_label(entry: &str) -> &'static str {
    let e = entry.to_ascii_lowercase();
    if e.contains("desktop") {
        "Desktop"
    } else if e.contains("vscode") || e.contains("jetbrains") || e.contains("ide") {
        "IDE"
    } else if e == "cli" {
        "Terminal"
    } else {
        ""
    }
}

/// What the start of a transcript says: the folder, the first prompt and the entry point.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Head {
    pub cwd: String,
    pub title: String,
    pub entry: String,
}

/// A user entry's text: a plain prompt's string, or its text blocks.
fn user_text(d: &Value) -> Option<String> {
    match &d["message"]["content"] {
        Value::String(s) => Some(s.clone()),
        Value::Array(blocks) => {
            let texts: Vec<&str> = blocks.iter().filter(|b| b["type"] == "text").filter_map(|b| b["text"].as_str()).collect();
            (!texts.is_empty()).then(|| texts.join("\n"))
        }
        _ => None,
    }
}

/// A prompt the user typed: not Claude Code's own (meta, a compaction's summary, a command's
/// tags or output, an interruption).
fn typed_prompt(d: &Value) -> Option<String> {
    if d["type"] != "user" || d["isMeta"] == true || d["isCompactSummary"] == true || d["isSidechain"] == true {
        return None;
    }
    let text = user_text(d)?;
    let line = text.lines().map(str::trim).find(|l| !l.is_empty())?;
    (!line.starts_with('<') && !line.starts_with("[Request interrupted")).then(|| line.to_string())
}

fn entries(text: &str) -> impl Iterator<Item = Value> + '_ {
    text.lines().filter_map(|l| serde_json::from_str::<Value>(l.trim()).ok())
}

pub fn read_head(text: &str) -> Head {
    let mut head = Head::default();
    for d in entries(text) {
        if head.cwd.is_empty() {
            head.cwd = d["cwd"].as_str().unwrap_or("").to_string();
        }
        if head.entry.is_empty() {
            head.entry = d["entrypoint"].as_str().unwrap_or("").to_string();
        }
        if head.title.is_empty() {
            if let Some(p) = typed_prompt(&d) {
                head.title = cut(&p, 90);
            }
        }
        if !head.cwd.is_empty() && !head.entry.is_empty() && !head.title.is_empty() {
            break;
        }
    }
    head
}

/// What the end of a transcript says (each `None` when the part read doesn't say).
#[derive(Clone, Debug, Default, PartialEq)]
pub struct Tail {
    pub model: Option<String>,
    pub context: Option<u64>,
    pub turn: Turn,
    pub step: Option<Step>,
    pub answer: Option<String>,
    /// A name given with `/rename`, or a summary Claude Code wrote.
    pub title: Option<String>,
    /// The folder, for a transcript whose start wasn't read yet.
    pub cwd: Option<String>,
}

pub fn read_tail(text: &str) -> Tail {
    let mut t = Tail::default();
    for d in entries(text) {
        if let Some(cwd) = d["cwd"].as_str().filter(|c| !c.is_empty()) {
            t.cwd = Some(cwd.to_string());
        }
        match d["type"].as_str().unwrap_or("") {
            "custom-title" => t.title = d["customTitle"].as_str().map(|s| cut(s, 90)).or(t.title.take()),
            "summary" if t.title.is_none() => t.title = d["summary"].as_str().map(|s| cut(s, 90)),
            "system" if d["subtype"] == "compact_boundary" => {
                t.context = d["compactMetadata"]["postTokens"].as_u64().or(t.context);
            }
            // A subagent's work is its parent's step, not its turn.
            _ if d["isSidechain"] == true => {}
            "assistant" => {
                let m = &d["message"];
                if let Some(model) = m["model"].as_str().filter(|s| !s.is_empty() && !s.starts_with('<')) {
                    t.model = Some(model.to_string());
                }
                let u = &m["usage"];
                let tokens = ["input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens"].iter().filter_map(|k| u[*k].as_u64()).sum::<u64>();
                if tokens > 0 {
                    t.context = Some(tokens);
                }
                let blocks = m["content"].as_array().map(Vec::as_slice).unwrap_or_default();
                if let Some(call) = blocks.iter().find(|b| b["type"] == "tool_use") {
                    t.step = Some(step_of(call["name"].as_str().unwrap_or(""), &call["input"]));
                }
                match m["stop_reason"].as_str() {
                    Some("end_turn" | "stop_sequence" | "max_tokens" | "refusal") => {
                        t.turn = Turn::Done;
                        t.step = None;
                        let text: Vec<&str> = blocks.iter().filter(|b| b["type"] == "text").filter_map(|b| b["text"].as_str()).collect();
                        if let Some(s) = summary_of(&text.join("\n")) {
                            t.answer = Some(s);
                        }
                    }
                    _ => t.turn = Turn::Working,
                }
            }
            "user" => {
                let interrupted = user_text(&d).is_some_and(|s| s.trim_start().starts_with("[Request interrupted"));
                let result = d["message"]["content"].as_array().is_some_and(|b| b.iter().any(|b| b["type"] == "tool_result"));
                if interrupted {
                    t.turn = Turn::Interrupted;
                    t.step = None;
                } else if result || typed_prompt(&d).is_some() {
                    if t.turn != Turn::Working {
                        t.answer = None;
                    }
                    t.turn = Turn::Working;
                }
            }
            _ => {}
        }
    }
    t
}

/// "2026-02-06T22:00:00+00:00", "…22:00:00.123Z" in Unix seconds.
pub fn iso_seconds(s: &str) -> Option<i64> {
    let (date, rest) = s.trim().split_once('T')?;
    let mut d = date.split('-').map(|p| p.parse::<i64>().ok());
    let (y, mo, da) = (d.next()??, d.next()??, d.next()??);
    let time_end = rest.find(['Z', '+', '-']).unwrap_or(rest.len());
    let (time, zone) = rest.split_at(time_end);
    let mut tp = time.split(':');
    let (h, mi) = (tp.next()?.parse::<i64>().ok()?, tp.next()?.parse::<i64>().ok()?);
    let sec = tp.next().map_or(Some(0.0), |s| s.parse::<f64>().ok())? as i64;
    let offset = match zone.chars().next() {
        Some(sign @ ('+' | '-')) => {
            let z = &zone[1..];
            let (zh, zm) = z.split_once(':').unwrap_or((z.get(..2)?, z.get(2..).unwrap_or("0")));
            let o = zh.parse::<i64>().ok()? * 3600 + zm.parse::<i64>().unwrap_or(0) * 60;
            if sign == '+' { o } else { -o }
        }
        _ => 0,
    };
    // Days from 1970-01-01 (Howard Hinnant's days_from_civil).
    let y = if mo <= 2 { y - 1 } else { y };
    let era = y.div_euclid(400);
    let yoe = y - era * 400;
    let doy = (153 * (mo + if mo > 2 { -3 } else { 9 }) + 2) / 5 + da - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    let days = era * 146_097 + doe - 719_468;
    Some(days * 86_400 + h * 3600 + mi * 60 + sec - offset)
}

fn limit_of(pct: &Value, resets: &Value) -> Option<Limit> {
    let used = pct.as_f64()?.round().clamp(0.0, 999.0) as u16;
    let resets_at = resets.as_i64().or_else(|| resets.as_str().and_then(iso_seconds)).unwrap_or(0);
    Some(Limit { used, resets_at })
}

/// The 5-hour and weekly limits in Claude's usage answer (`/api/oauth/usage`): its `limits`
/// list (`session`, `weekly_all`), else the older `five_hour` and `seven_day` windows.
pub fn plan_limits(v: &Value) -> (Option<Limit>, Option<Limit>) {
    let from_list = |kind: &str| {
        v["limits"].as_array()?.iter().find(|l| l["kind"] == kind).and_then(|l| {
            let pct = if l["percent"].is_number() { &l["percent"] } else { &l["utilization"] };
            limit_of(pct, &l["resets_at"])
        })
    };
    let window = |key: &str| limit_of(&v[key]["utilization"], &v[key]["resets_at"]);
    (from_list("session").or_else(|| window("five_hour")), from_list("weekly_all").or_else(|| window("seven_day")))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::activities::claude::StepKind;
    use serde_json::json;

    fn lines(v: &[Value]) -> String {
        v.iter().map(Value::to_string).collect::<Vec<_>>().join("\n")
    }

    fn assistant(stop: &str, content: Value, tokens: u64) -> Value {
        json!({"type": "assistant", "cwd": "C:\\code\\winisland", "message": {"model": "claude-opus-5-5", "stop_reason": stop, "content": content,
            "usage": {"input_tokens": 2, "cache_creation_input_tokens": 0, "cache_read_input_tokens": tokens - 2, "output_tokens": 50}}})
    }

    #[test]
    fn the_head_is_the_folder_the_first_typed_prompt_and_where_it_runs() {
        let text = lines(&[
            json!({"type": "queue-operation"}),
            json!({"type": "user", "isMeta": true, "cwd": "C:\\code\\winisland", "entrypoint": "claude-desktop", "message": {"content": "<local-command-caveat>x</local-command-caveat>"}}),
            json!({"type": "user", "message": {"content": [{"type": "text", "text": "\n  Fix the pill's shadow\nit is cut"}]}}),
        ]);
        let head = read_head(&text);
        assert_eq!(head, Head { cwd: "C:\\code\\winisland".into(), title: "Fix the pill's shadow".into(), entry: "claude-desktop".into() });
        assert_eq!(entry_label(&head.entry), "Desktop");
        assert_eq!(entry_label("cli"), "Terminal");
        assert_eq!(entry_label("sdk-ts"), "");
    }

    #[test]
    fn a_tool_call_is_working_and_an_end_turn_is_done_with_its_answer() {
        let call = assistant("tool_use", json!([{"type": "tool_use", "name": "Edit", "input": {"file_path": "C:\\code\\app.rs"}}]), 48_000);
        let t = read_tail(&lines(std::slice::from_ref(&call)));
        assert_eq!(t.turn, Turn::Working);
        assert_eq!(t.step, Some(Step { kind: StepKind::Edit, target: "app.rs".into() }));
        assert_eq!(t.context, Some(48_000));
        assert_eq!(t.model.as_deref(), Some("claude-opus-5-5"));
        let result = json!({"type": "user", "message": {"content": [{"type": "tool_result", "content": "ok"}]}});
        let done = assistant("end_turn", json!([{"type": "text", "text": "## Done\n**Fixed** the shadow."}]), 52_000);
        let t = read_tail(&lines(&[call, result, done]));
        assert_eq!((t.turn, t.step, t.answer.as_deref(), t.context), (Turn::Done, None, Some("Fixed the shadow."), Some(52_000)));
    }

    #[test]
    fn esc_is_interrupted_and_a_new_prompt_works_again() {
        let call = assistant("tool_use", json!([{"type": "tool_use", "name": "Bash", "input": {"command": "cargo test"}}]), 1_000);
        let esc = json!({"type": "user", "message": {"content": [{"type": "text", "text": "[Request interrupted by user for tool use]"}]}});
        assert_eq!(read_tail(&lines(&[call.clone(), esc.clone()])).turn, Turn::Interrupted);
        let next = json!({"type": "user", "message": {"content": "now run clippy"}});
        assert_eq!(read_tail(&lines(&[call, esc, next])).turn, Turn::Working);
    }

    #[test]
    fn subagents_and_local_commands_leave_the_turn_alone() {
        let done = assistant("end_turn", json!([{"type": "text", "text": "Done."}]), 9_000);
        let side = json!({"type": "assistant", "isSidechain": true, "message": {"stop_reason": "tool_use", "content": [{"type": "tool_use", "name": "Read", "input": {}}]}});
        let local = json!({"type": "user", "message": {"content": "<local-command-stdout>Compacted</local-command-stdout>"}});
        let t = read_tail(&lines(&[done, side, local]));
        assert_eq!((t.turn, t.answer.as_deref()), (Turn::Done, Some("Done.")));
    }

    #[test]
    fn a_compaction_sets_the_context_and_a_rename_the_title() {
        let done = assistant("end_turn", json!([{"type": "text", "text": "ok"}]), 180_000);
        let compact = json!({"type": "system", "subtype": "compact_boundary", "compactMetadata": {"trigger": "manual", "preTokens": 180_000, "postTokens": 2_914}});
        let named = json!({"type": "custom-title", "customTitle": "Pill shadow"});
        let t = read_tail(&lines(&[done, compact, named]));
        assert_eq!((t.context, t.title.as_deref()), (Some(2_914), Some("Pill shadow")));
    }

    #[test]
    fn a_broken_line_is_skipped() {
        let done = assistant("end_turn", json!([{"type": "text", "text": "ok"}]), 10);
        let text = format!("{{\"type\": \"user\", \"mess\n{}", done);
        assert_eq!(read_tail(&text).turn, Turn::Done);
    }

    #[test]
    fn the_context_window_is_200k_or_1m() {
        let s = |model: &str, tokens| Session { model: model.into(), context: Some(tokens), ..Default::default() };
        assert_eq!(s("claude-sonnet-5-5", 50_000).context_pct(), Some(25.0));
        assert_eq!(s("claude-opus-5-5", 287_000).context_pct(), Some(28.7), "past 200k: a 1M window");
        assert_eq!(s("claude-opus-5-5[1m]", 100_000).context_pct(), Some(10.0));
        assert_eq!(Session { cwd: "C:\\code\\winisland\\".into(), ..Default::default() }.project(), "winisland");
    }

    #[test]
    fn iso_times_are_unix_seconds() {
        assert_eq!(iso_seconds("1970-01-01T00:00:00Z"), Some(0));
        assert_eq!(iso_seconds("2026-02-06T22:00:00+00:00"), Some(1_770_415_200));
        assert_eq!(iso_seconds("2026-02-06T23:30:00.512+01:30"), Some(1_770_415_200));
        assert_eq!(iso_seconds("2026-02-06T20:00:00-02:00"), Some(1_770_415_200));
        assert_eq!(iso_seconds("yesterday"), None);
    }

    #[test]
    fn plan_limits_come_from_the_list_or_the_windows() {
        let list = json!({"five_hour": null, "limits": [
            {"kind": "weekly_scoped", "percent": 90, "resets_at": "2026-02-07T00:00:00Z", "scope": {"model": {"display_name": "Opus"}}},
            {"kind": "session", "percent": 23.4, "resets_at": "2026-02-06T22:00:00Z"},
            {"kind": "weekly_all", "percent": 41.6, "resets_at": 1_770_800_000}]});
        assert_eq!(plan_limits(&list), (Some(Limit { used: 23, resets_at: 1_770_415_200 }), Some(Limit { used: 42, resets_at: 1_770_800_000 })));
        let flat = json!({"five_hour": {"utilization": 35.0, "resets_at": "2026-02-06T22:00:00+00:00"}, "seven_day": null});
        assert_eq!(plan_limits(&flat), (Some(Limit { used: 35, resets_at: 1_770_415_200 }), None));
        assert_eq!(plan_limits(&json!({})), (None, None));
    }
}
