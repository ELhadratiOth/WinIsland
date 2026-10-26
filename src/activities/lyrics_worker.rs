//! Fetches synced lyrics from lrclib.net on its own thread, cached on disk under
//! %LOCALAPPDATA%\WinIsland\lyrics2 (a track lrclib doesn't have is cached as such, so it isn't
//! asked again; a busy or unreachable server isn't cached: the worker asks again a few times,
//! as the app asks only once per track). Each track: lrclib's exact match first, then a search
//! for its cleaned title and first artist (players and YouTube decorate both).

use std::path::PathBuf;
use std::sync::mpsc::{channel, RecvTimeoutError, Sender};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use windows::core::HSTRING;
use windows::Foundation::Uri;
use windows::Web::Http::HttpClient;

use super::lyrics::{is_speed_edit, parse_lrc, pick, query_url, search_terms, search_url, Lines};
use crate::platform::log::log;
use crate::platform::windows::post_lyrics;

/// The cache's folder. "lyrics" held the exact-match-only answers, whose many "none"s the
/// search may now find; a new folder gives every track that second chance.
const CACHE_DIR: &str = "lyrics2";

/// When a track whose lookup failed (lrclib is often busy) is asked again: after each of these.
const RETRY_AFTER: [Duration; 3] = [Duration::from_secs(5), Duration::from_secs(20), Duration::from_secs(60)];

/// A track to find lyrics for.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Track {
    pub artist: String,
    pub title: String,
    pub duration_s: u64,
}

impl Track {
    /// The key results come back under (and the cache file's name is made from).
    pub fn key(&self) -> String {
        format!("{}\u{1f}{}\u{1f}{}", self.artist, self.title, self.duration_s)
    }
}

/// Answers, by track key: the lines, or `None` when lrclib has no synced lyrics for it.
pub type LyricsMailbox = Arc<Mutex<Vec<(String, Option<Lines>)>>>;

pub struct LyricsWorker {
    tx: Sender<Track>,
}

impl LyricsWorker {
    pub fn start(mailbox: LyricsMailbox) -> LyricsWorker {
        let (tx, rx) = channel::<Track>();
        std::thread::spawn(move || {
            let client = client();
            // Failed lookups to try again: when, the track, how many tries it has had.
            let mut retries: Vec<(Instant, Track, usize)> = Vec::new();
            loop {
                let next = retries.iter().enumerate().min_by_key(|(_, r)| r.0).map(|(i, r)| (i, r.0));
                let received = match next {
                    None => rx.recv().map_err(|_| RecvTimeoutError::Disconnected),
                    Some((_, due)) => rx.recv_timeout(due.saturating_duration_since(Instant::now())),
                };
                let (track, tries) = match (received, next) {
                    (Ok(track), _) => (track, 0),
                    (Err(RecvTimeoutError::Timeout), Some((i, _))) => {
                        let (_, track, tries) = retries.swap_remove(i);
                        (track, tries)
                    }
                    _ => break,
                };
                let answer = match cached(&track) {
                    Some(answer) => Some(answer),
                    None => client.as_ref().and_then(|c| fetch(c, &track)),
                };
                match answer {
                    Some(answer) => {
                        if let Ok(mut slot) = mailbox.lock() {
                            slot.push((track.key(), answer));
                        }
                        post_lyrics();
                    }
                    // A busy or unreachable server gives no answer: ask again in a while.
                    None => {
                        if let Some(wait) = RETRY_AFTER.get(tries) {
                            retries.push((Instant::now() + *wait, track, tries + 1));
                        }
                    }
                }
            }
        });
        LyricsWorker { tx }
    }

    pub fn request(&self, track: Track) {
        let _ = self.tx.send(track);
    }
}

/// An HTTP client for this thread, naming the app as lrclib asks.
fn client() -> Option<HttpClient> {
    unsafe {
        let _ = windows::Win32::System::Com::CoInitializeEx(None, windows::Win32::System::Com::COINIT_MULTITHREADED);
    }
    let client = HttpClient::new().ok()?;
    let _ = client.DefaultRequestHeaders().and_then(|h| h.UserAgent()).and_then(|u| u.TryParseAdd(&HSTRING::from("WinIsland/0.1 (Windows)")));
    Some(client)
}

fn cache_path(track: &Track) -> PathBuf {
    let hash = track.key().bytes().fold(0xcbf2_9ce4_8422_2325u64, |h, b| (h ^ b as u64).wrapping_mul(0x0100_0000_01b3));
    crate::platform::log::log_path().with_file_name(CACHE_DIR).join(format!("{hash:016x}.lrc"))
}

/// A cached answer: the file's lines, or `None` (an empty file: lrclib had none).
fn cached(track: &Track) -> Option<Option<Lines>> {
    let path = cache_path(track);
    let text = std::fs::read_to_string(&path).ok()?;
    let lines = parse_lrc(&text);
    if lines.is_empty() {
        // lrclib keeps growing: a "none" is asked again after a while.
        let age = std::fs::metadata(&path).and_then(|m| m.modified()).ok().and_then(|t| t.elapsed().ok());
        if age.is_some_and(none_expired) {
            return None;
        }
    }
    Some((!lines.is_empty()).then_some(lines))
}

/// A cached "lrclib has none" this old is asked again.
fn none_expired(age: std::time::Duration) -> bool {
    age > std::time::Duration::from_secs(7 * 86_400)
}

fn store(track: &Track, lrc: &str) {
    let path = cache_path(track);
    if let Some(dir) = path.parent() {
        let _ = std::fs::create_dir_all(dir);
    }
    let _ = std::fs::write(path, lrc);
}

/// Asks lrclib: `Some(Some(lines))` found, `Some(None)` it has none (cached), `None` try later.
fn fetch(client: &HttpClient, track: &Track) -> Option<Option<Lines>> {
    let lrc = lookup(track, |url| get(client, url))?.unwrap_or_default();
    store(track, &lrc);
    let lines = parse_lrc(&lrc);
    Some((!lines.is_empty()).then_some(lines))
}

/// What lrclib answered a request.
#[derive(Debug)]
enum Reply {
    Body(String),
    NotFound,
    /// Unreachable, busy (after waiting once), or some other error: ask again another time.
    Failed,
}

/// The fallback chain: lrclib's exact match (artist, title and duration as the player gives
/// them), then its search for the cleaned title and first artist, picked by `pick`. A slowed
/// or sped-up edit skips the exact match: it may be the original's lyrics filed under the
/// edit's length, which only the search's other versions show. The synced LRC; `Some(None)`
/// when lrclib has none (plain lyrics don't count: the card shows only synced ones); `None`
/// when a request failed.
fn lookup(track: &Track, mut get: impl FnMut(&str) -> Reply) -> Option<Option<String>> {
    if !is_speed_edit(&track.title) {
        if let Some(lrc) = exact(track, &mut get)? {
            return Some(Some(lrc));
        }
    }
    let (artist, title) = search_terms(&track.artist, &track.title);
    match get(&search_url(&artist, &title)) {
        Reply::Body(body) => {
            let results: serde_json::Value = serde_json::from_str(&body).ok()?;
            Some(pick(&results, &track.title, &track.artist, track.duration_s))
        }
        Reply::NotFound => Some(None),
        Reply::Failed => None,
    }
}

/// lrclib's exact match alone (all the lookup there was before the search).
fn exact(track: &Track, get: &mut impl FnMut(&str) -> Reply) -> Option<Option<String>> {
    match get(&query_url(&track.artist, &track.title, "", track.duration_s)) {
        Reply::Body(body) => {
            let json: serde_json::Value = serde_json::from_str(&body).ok()?;
            Some(json["syncedLyrics"].as_str().filter(|l| !l.trim().is_empty()).map(str::to_string))
        }
        Reply::NotFound => Some(None),
        Reply::Failed => None,
    }
}

/// One GET; a busy server (503) or a rate limit (429) is waited out once, as long as its
/// Retry-After says (1 s if it doesn't, at most 10 s) plus a little jitter.
fn get(client: &HttpClient, url: &str) -> Reply {
    let Ok(uri) = Uri::CreateUri(&HSTRING::from(url)) else { return Reply::Failed };
    for attempt in 0..2 {
        let response = match client.GetAsync(&uri).and_then(|op| crate::join_within!(op, 20)) {
            Ok(r) => r,
            Err(e) => {
                log(&format!("lyrics: {e:?}"));
                return Reply::Failed;
            }
        };
        let status = response.StatusCode().map(|s| s.0).unwrap_or(0);
        match status {
            200 => {
                let body = response.Content().and_then(|c| c.ReadAsStringAsync()).and_then(|op| crate::join_within!(op, 20));
                return body.map(|b| Reply::Body(b.to_string())).unwrap_or(Reply::Failed);
            }
            // 400: lrclib can't take the request as it is (a duration past an hour, say), which
            // asking again won't change.
            400 | 404 => return Reply::NotFound,
            429 | 503 if attempt == 0 => {
                let wait = response
                    .Headers()
                    .and_then(|h| h.RetryAfter())
                    .and_then(|r| r.Delta())
                    .and_then(|d| d.Value())
                    .map(|t| Duration::from_nanos(t.Duration.max(0) as u64 * 100))
                    .unwrap_or(Duration::from_secs(1))
                    .min(Duration::from_secs(10));
                let jitter = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.subsec_millis() % 300).unwrap_or(0);
                std::thread::sleep(wait + Duration::from_millis(jitter as u64));
            }
            _ => {
                log(&format!("lyrics: lrclib answered {status}"));
                return Reply::Failed;
            }
        }
    }
    Reply::Failed
}

#[cfg(test)]
mod tests {

    #[test]
    fn a_cached_none_is_asked_again_after_a_week() {
        let day = std::time::Duration::from_secs(86_400);
        assert!(!none_expired(6 * day));
        assert!(none_expired(7 * day + std::time::Duration::from_secs(1)));
    }

    use super::*;

    fn track(artist: &str, title: &str, duration_s: u64) -> Track {
        Track { artist: artist.into(), title: title.into(), duration_s }
    }

    /// Answers the exact match with `exact` and the search with `search`, noting each URL.
    fn server(exact: fn() -> Reply, search: fn() -> Reply, asked: &mut Vec<String>) -> impl FnMut(&str) -> Reply + '_ {
        move |url| {
            asked.push(url.to_string());
            if url.contains("/api/get?") {
                exact()
            } else {
                search()
            }
        }
    }

    fn found() -> Reply {
        Reply::Body(r#"{"syncedLyrics":"[00:01.00]exact"}"#.into())
    }
    fn plain_only() -> Reply {
        Reply::Body(r#"{"syncedLyrics":null,"plainLyrics":"words"}"#.into())
    }
    fn results() -> Reply {
        Reply::Body(r#"[{"trackName":"Shape of You","artistName":"Ed Sheeran","duration":263.0,"syncedLyrics":"[00:01.00]searched"}]"#.into())
    }
    fn no_results() -> Reply {
        Reply::Body("[]".into())
    }

    #[test]
    fn chain_stops_at_an_exact_match() {
        let mut asked = Vec::new();
        let answer = lookup(&track("Ed Sheeran", "Shape of You", 263), server(found, results, &mut asked));
        assert_eq!(answer, Some(Some("[00:01.00]exact".into())));
        assert_eq!(asked.len(), 1);
    }

    #[test]
    fn chain_searches_the_cleaned_title_and_first_artist_when_the_exact_match_misses() {
        let mut asked = Vec::new();
        let t = track("Ed Sheeran", "Ed Sheeran - Shape of You (Official Music Video)", 263);
        assert_eq!(lookup(&t, server(|| Reply::NotFound, results, &mut asked)), Some(Some("[00:01.00]searched".into())));
        assert_eq!(asked[1], "https://lrclib.net/api/search?track_name=Shape%20of%20You&artist_name=Ed%20Sheeran");
    }

    #[test]
    fn chain_searches_when_the_exact_match_has_only_plain_lyrics() {
        let mut asked = Vec::new();
        assert_eq!(lookup(&track("Ed Sheeran", "Shape of You", 263), server(plain_only, results, &mut asked)), Some(Some("[00:01.00]searched".into())));
    }

    #[test]
    fn chain_goes_straight_to_the_search_for_a_slowed_edit() {
        let mut asked = Vec::new();
        let t = track("Ed Sheeran", "Shape of You (Slowed)", 263);
        assert_eq!(lookup(&t, server(found, results, &mut asked)), Some(Some("[00:01.00]searched".into())));
        assert_eq!(asked.len(), 1);
    }

    #[test]
    fn chain_answers_none_when_nothing_synced_fits() {
        let mut asked = Vec::new();
        assert_eq!(lookup(&track("Ed Sheeran", "Shape of You", 263), server(|| Reply::NotFound, no_results, &mut asked)), Some(None));
        let mut asked = Vec::new();
        assert_eq!(lookup(&track("Ed Sheeran", "Shape of You", 200), server(|| Reply::NotFound, results, &mut asked)), Some(None));
    }

    #[test]
    fn chain_gives_no_answer_when_a_request_fails_so_it_is_asked_again() {
        let mut asked = Vec::new();
        assert_eq!(lookup(&track("Ed Sheeran", "Shape of You", 263), server(|| Reply::Failed, results, &mut asked)), None);
        let mut asked = Vec::new();
        assert_eq!(lookup(&track("Ed Sheeran", "Shape of You", 263), server(|| Reply::NotFound, || Reply::Failed, &mut asked)), None);
    }

    /// Tracks as the user's players name them, with their real lengths.
    const LIVE: &[(&str, &str, u64)] = &[
        ("TWXNY, Sxilwix ve Innxcence", "HEAVENLY JUMPSTYLE (Ultra Slowed)", 161),
        ("ZXVRF", "GEM DELÍRIO (Slowed)", 141),
        ("Ed Sheeran", "Ed Sheeran - Shape of You (Official Music Video)", 263),
        ("Tarkan", "Şımarık", 235),
        ("Tarkan - Topic", "Kuzu Kuzu", 233),
        ("Sezen Aksu", "Sezen Aksu - Firuze (Official Audio)", 304),
        ("INTERWORLD", "METAMORPHOSIS (Slowed + Reverb)", 172),
        ("Luis Fonsi, Daddy Yankee", "Despacito (feat. Daddy Yankee)", 229),
        ("Queen", "Bohemian Rhapsody - Remastered 2011", 355),
        ("The Weeknd", "Blinding Lights", 200),
    ];

    /// Asks lrclib for real, the old way (exact match only) and with the new chain, each asked
    /// again while lrclib is busy (as the worker does), so they're compared on real answers:
    /// `cargo test --lib live_lyrics -- --ignored --nocapture`.
    #[test]
    #[ignore]
    fn live_lyrics() {
        let client = client().expect("an HTTP client");
        let ask = |url: &str| {
            std::thread::sleep(Duration::from_millis(400));
            get(&client, url)
        };
        let answered = |run: &mut dyn FnMut() -> Option<Option<String>>| {
            let mut answer = run();
            for wait in RETRY_AFTER {
                if answer.is_some() {
                    break;
                }
                std::thread::sleep(wait / 5);
                answer = run();
            }
            answer
        };
        let shown = |a: &Option<Option<String>>| match a {
            Some(Some(lrc)) => format!("found ({} lines)", parse_lrc(lrc).len()),
            Some(None) => "none".into(),
            None => "failed".into(),
        };
        let (mut before, mut after) = (0, 0);
        for &(artist, title, duration_s) in LIVE {
            let t = track(artist, title, duration_s);
            let old = answered(&mut || exact(&t, &mut |url: &str| ask(url)));
            let new = answered(&mut || lookup(&t, ask));
            println!("{title:?} by {artist:?} ({duration_s} s): before {}, after {}", shown(&old), shown(&new));
            before += matches!(old, Some(Some(_))) as usize;
            after += matches!(new, Some(Some(_))) as usize;
        }
        println!("found before: {before}/{n}, after: {after}/{n}", n = LIVE.len());
    }
}
