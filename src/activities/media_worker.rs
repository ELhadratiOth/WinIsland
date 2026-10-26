//! The GSMTC side of media: a worker thread subscribes to the session manager and every session,
//! debounces their bursts of events, and posts a snapshot of every live session to the UI.
//! All blocking WinRT calls happen here, never on the UI thread (it shares Explorer's input queue).

use std::collections::HashMap;
use std::sync::mpsc::{channel, RecvTimeoutError, Sender};
use std::sync::Arc;
use std::time::Duration;

use windows::core::Result;
use windows::Foundation::TypedEventHandler;
use windows::Graphics::Imaging::{
    BitmapAlphaMode, BitmapBounds, BitmapDecoder, BitmapInterpolationMode, BitmapPixelFormat, BitmapTransform,
    ColorManagementMode, ExifOrientationMode,
};
use windows::Media::Control::{
    GlobalSystemMediaTransportControlsSession as Session, GlobalSystemMediaTransportControlsSessionManager as Manager,
    GlobalSystemMediaTransportControlsSessionPlaybackStatus as Status,
};
use windows::Media::MediaPlaybackAutoRepeatMode;
use windows::Storage::Streams::IRandomAccessStream;
use windows::Win32::System::SystemInformation::{GetSystemTimePreciseAsFileTime, GetTickCount64};
use windows::Win32::System::WinRT::{RoInitialize, RO_INIT_MULTITHREADED};
use windows_core::Interface;

use super::media::{
    app_accent, app_name, display_title, live_sessions, next_grace_wake, with_departed, Caps, Debouncer, Departed, MediaCommand, MediaSnapshot,
    session_keys, SessionIds, SessionInfo, Timeline,
};
use crate::platform::log::log;
use crate::render::color::accent_from_rgba;

const DEBOUNCE_MS: u64 = 200;
const ART_MAX_PX: u32 = 512;

enum Msg {
    Poke,
    SessionsChanged,
    /// A command for the session with this id.
    Command(u32, MediaCommand),
}

pub struct MediaWorker {
    tx: Sender<Msg>,
}

impl MediaWorker {
    /// `post` runs on the worker thread with the live sessions, front first, whenever they change.
    pub fn start(post: impl Fn(Vec<MediaSnapshot>) + Send + 'static) -> Self {
        let (tx, rx) = channel::<Msg>();
        let events = tx.clone();
        std::thread::Builder::new()
            .name("media".into())
            .spawn(move || {
                if let Err(e) = run(events, rx, post) {
                    log(&format!("media worker stopped: {e:?}"));
                }
            })
            .expect("spawn media thread");
        MediaWorker { tx }
    }

    /// Sends `c` to the session with this id (from `MediaSnapshot::id`).
    pub fn send(&self, id: u32, c: MediaCommand) {
        let _ = self.tx.send(Msg::Command(id, c));
    }
}

fn now_ms() -> u64 {
    unsafe { GetTickCount64() }
}

fn now_filetime() -> i64 {
    let ft = unsafe { GetSystemTimePreciseAsFileTime() };
    ((ft.dwHighDateTime as i64) << 32) | ft.dwLowDateTime as i64
}

struct Tracked {
    id: u32,
    /// The app (AUMID): several sessions of one browser share it.
    app_id: String,
    session: Session,
    playing: bool,
    last_change_ms: u64,
    tokens: [i64; 3],
}

/// Decoded cover, cached by (app, title, artist) so a burst of events decodes it once.
struct Art {
    key: (String, String, String),
    pixels: Option<Arc<Vec<u8>>>,
    size: (u32, u32),
    accent: [u8; 3],
    /// When this track's cover was first and last asked for (ms).
    asked: (u64, u64),
}

fn run(events: Sender<Msg>, rx: std::sync::mpsc::Receiver<Msg>, post: impl Fn(Vec<MediaSnapshot>)) -> Result<()> {
    unsafe { RoInitialize(RO_INIT_MULTITHREADED)? };
    let manager = Manager::RequestAsync()?.join()?;
    {
        let tx = events.clone();
        manager.SessionsChanged(&TypedEventHandler::new(move |_, _| {
            let _ = tx.send(Msg::SessionsChanged);
            Ok(())
        }))?;
        let tx = events.clone();
        manager.CurrentSessionChanged(&TypedEventHandler::new(move |_, _| {
            let _ = tx.send(Msg::Poke);
            Ok(())
        }))?;
    }
    let mut tracked: HashMap<String, Tracked> = HashMap::new();
    let mut ids = SessionIds::default();
    let mut departed: Vec<Departed> = Vec::new();
    let mut arts: HashMap<String, Art> = HashMap::new();
    let mut debounce = Debouncer::new(DEBOUNCE_MS);
    let mut last_posted: Option<Vec<MediaSnapshot>> = None;
    sync_sessions(&manager, &mut tracked, &mut ids, &mut departed, &[], &events);
    debounce.poke(0);

    loop {
        let grace = next_grace_wake(&departed, now_ms());
        let wait = [debounce.wait_ms(now_ms()), grace].into_iter().flatten().min().unwrap_or(60_000);
        match rx.recv_timeout(Duration::from_millis(wait)) {
            Ok(Msg::Poke) => debounce.poke(now_ms()),
            Ok(Msg::SessionsChanged) => {
                let shown = last_posted.as_deref().unwrap_or(&[]);
                sync_sessions(&manager, &mut tracked, &mut ids, &mut departed, shown, &events);
                debounce.poke(now_ms());
            }
            Ok(Msg::Command(id, c)) => {
                if let Some(t) = tracked.values().find(|t| t.id == id) {
                    if let Err(e) = command(&t.session, c) {
                        log(&format!("media command {c:?}: {e:?}"));
                    }
                }
            }
            Err(RecvTimeoutError::Timeout) => {}
            Err(RecvTimeoutError::Disconnected) => return Ok(()),
        }
        let grace_over = next_grace_wake(&departed, now_ms()) == Some(0);
        if debounce.fire(now_ms()) || grace_over {
            let live = with_departed(snapshots(&mut tracked, &mut arts), &mut departed, now_ms());
            if last_posted.as_ref() != Some(&live) {
                post(live.clone());
                last_posted = Some(live);
            }
        }
    }
}

fn unlisten(t: &Tracked) {
    let _ = t.session.RemoveMediaPropertiesChanged(t.tokens[0]);
    let _ = t.session.RemovePlaybackInfoChanged(t.tokens[1]);
    let _ = t.session.RemoveTimelinePropertiesChanged(t.tokens[2]);
}

/// An event handler that only nudges the worker; the debouncer does the rest.
fn poke<A: windows_core::RuntimeType + 'static>(tx: Sender<Msg>) -> TypedEventHandler<Session, A> {
    TypedEventHandler::new(move |_, _| {
        let _ = tx.send(Msg::Poke);
        Ok(())
    })
}

/// Subscribes to sessions that appeared and forgets those that went away; a session that went
/// away keeps its last `shown` snapshot for the grace, and one that comes back keeps its id.
fn sync_sessions(
    manager: &Manager,
    tracked: &mut HashMap<String, Tracked>,
    ids: &mut SessionIds,
    departed: &mut Vec<Departed>,
    shown: &[MediaSnapshot],
    events: &Sender<Msg>,
) {
    let Ok(sessions) = manager.GetSessions() else { return };
    let (apps, sessions): (Vec<String>, Vec<Session>) = sessions.into_iter().filter_map(|s| Some((s.SourceAppUserModelId().ok()?.to_string(), s))).unzip();
    let now_there: Vec<(String, String, Session)> = session_keys(&apps).into_iter().zip(apps).zip(sessions).map(|((key, app), s)| (key, app, s)).collect();
    // The gone first: a session recreated between two syncs takes its old id back.
    tracked.retain(|id, t| {
        let keep = now_there.iter().any(|(seen, ..)| seen == id);
        if !keep {
            if let Some(last) = shown.iter().find(|m| m.id == t.id) {
                departed.push(Departed { snapshot: last.clone(), gone_ms: now_ms() });
            }
            unlisten(t);
        }
        keep
    });
    let listen = |session: &Session| {
        [
            session.MediaPropertiesChanged(&poke(events.clone())).unwrap_or_default(),
            session.PlaybackInfoChanged(&poke(events.clone())).unwrap_or_default(),
            session.TimelinePropertiesChanged(&poke(events.clone())).unwrap_or_default(),
        ]
    };
    for (id, app_id, session) in now_there {
        // Still there, as a new object (maybe of another session: a tab before it closed).
        if let Some(t) = tracked.get_mut(&id) {
            unlisten(t);
            t.tokens = listen(&session);
            t.session = session;
            continue;
        }
        let tokens = listen(&session);
        let used: Vec<u32> = tracked.values().map(|t| t.id).collect();
        let gone: Vec<(u32, String)> = departed.iter().map(|d| (d.snapshot.id, d.snapshot.app_id.clone())).collect();
        let now_id = ids.id_for_session(&app_id, &used, &gone);
        // Back within the grace: carry on where it was, so the stack doesn't reorder.
        let (playing, last_change_ms) = departed
            .iter()
            .find(|d| d.snapshot.id == now_id)
            .map_or((false, now_ms()), |d| (d.snapshot.playing, d.snapshot.changed_ms));
        log(&format!("media: tracking {id} as {now_id}"));
        tracked.insert(id, Tracked { id: now_id, app_id, session: session.clone(), playing, last_change_ms, tokens });
    }
}

fn infos(tracked: &HashMap<String, Tracked>) -> (Vec<String>, Vec<SessionInfo>) {
    let mut ids: Vec<String> = tracked.keys().cloned().collect();
    ids.sort();
    let infos = ids
        .iter()
        .map(|id| {
            let t = &tracked[id];
            SessionInfo { app_id: id.clone(), playing: t.playing, last_change_ms: t.last_change_ms }
        })
        .collect();
    (ids, infos)
}

/// Every live session, front first; a session that fails to answer is left out this time.
fn snapshots(tracked: &mut HashMap<String, Tracked>, arts: &mut HashMap<String, Art>) -> Vec<MediaSnapshot> {
    // Refresh play state first: it decides which sessions are live and their order.
    for t in tracked.values_mut() {
        let playing = t.session.GetPlaybackInfo().and_then(|p| p.PlaybackStatus()).is_ok_and(|s| s == Status::Playing);
        if playing != t.playing {
            t.playing = playing;
            t.last_change_ms = now_ms();
        }
    }
    let (ids, infos) = infos(tracked);
    let live = live_sessions(&infos, now_ms());
    arts.retain(|app, _| live.iter().any(|&i| ids[i] == *app));
    live.into_iter()
        .filter_map(|i| {
            session_snapshot(&ids[i], &tracked[&ids[i]], arts)
                .inspect_err(|e| log(&format!("media snapshot {}: {e:?}", ids[i])))
                .ok()
        })
        .collect()
}

fn session_snapshot(key: &str, t: &Tracked, arts: &mut HashMap<String, Art>) -> Result<MediaSnapshot> {
    let session = &t.session;
    let app_id = t.app_id.clone();
    let name = app_name(&app_id);
    let props = session.TryGetMediaPropertiesAsync()?.join()?;
    let title = display_title(&props.Title().map(|h: windows_core::HSTRING| h.to_string()).unwrap_or_default(), &name);
    let artist = props.Artist().map(|h: windows_core::HSTRING| h.to_string()).unwrap_or_default().trim().to_string();

    let info = session.GetPlaybackInfo()?;
    let controls = info.Controls()?;
    let can = Caps {
        play_pause: controls.IsPlayPauseToggleEnabled().unwrap_or(false)
            || controls.IsPlayEnabled().unwrap_or(false)
            || controls.IsPauseEnabled().unwrap_or(false),
        prev: controls.IsPreviousEnabled().unwrap_or(false),
        next: controls.IsNextEnabled().unwrap_or(false),
        seek: controls.IsPlaybackPositionEnabled().unwrap_or(false),
        shuffle: controls.IsShuffleEnabled().unwrap_or(false),
        repeat: controls.IsRepeatEnabled().unwrap_or(false),
    };
    let shuffle = info.IsShuffleActive().and_then(|r| r.Value()).ok();
    let repeat = info.AutoRepeatMode().and_then(|r| r.Value()).ok().map(|m| m != MediaPlaybackAutoRepeatMode::None);
    let rate = info.PlaybackRate().and_then(|r| r.Value()).ok();
    let timeline = session.GetTimelineProperties().ok().and_then(|tl| {
        Some(Timeline {
            start: tl.StartTime().ok()?.Duration,
            end: tl.EndTime().ok()?.Duration,
            position: tl.Position().ok()?.Duration,
            last_updated: tl.LastUpdatedTime().ok()?.UniversalTime,
            rate,
            playing: t.playing,
        })
    });

    // Cover: decode once per track, per session; accepted only if the track didn't change
    // meanwhile. While there is none yet it is asked again (players send the artwork late), but
    // every 2 s for a minute, not on every timeline poke: each ask is a round trip to the player.
    let art_key = (app_id.clone(), title.clone(), artist.clone());
    let now = now_ms();
    let first = arts.get(key).filter(|a| a.key == art_key).map(|a| a.asked.0);
    let again = |a: &Art| a.pixels.is_none() && now >= a.asked.1 + 2_000 && now < a.asked.0 + 60_000;
    if arts.get(key).is_none_or(|a| a.key != art_key || again(a)) {
        let decoded = props.Thumbnail().ok().and_then(|thumb| decode_cover(&thumb).ok());
        let still_same = session
            .TryGetMediaPropertiesAsync()
            .and_then(|op| op.join())
            .is_ok_and(|p| display_title(&p.Title().map(|h: windows_core::HSTRING| h.to_string()).unwrap_or_default(), &name) == title);
        let (pixels, size) = match decoded {
            Some((px, w, h)) if still_same => (Some(Arc::new(px)), (w, h)),
            _ => (None, (0, 0)),
        };
        let accent = match &pixels {
            Some(px) => accent_from_rgba(px, size.0 as usize, size.1 as usize),
            None => app_accent(&app_id),
        };
        arts.insert(key.to_string(), Art { key: art_key, pixels, size, accent, asked: (first.unwrap_or(now), now) });
    }
    let a = &arts[key];
    Ok(MediaSnapshot {
        id: t.id,
        app_id,
        app_name: name,
        title,
        artist,
        playing: t.playing,
        timeline,
        can,
        shuffle,
        repeat,
        art: a.pixels.clone(),
        art_size: a.size,
        accent: a.accent,
        changed_ms: t.last_change_ms,
    })
}

/// Decodes a cover, centre-cropped to a square (YouTube thumbnails are 16:9) and at most 512 px.
fn decode_cover(thumb: &windows::Storage::Streams::IRandomAccessStreamReference) -> Result<(Vec<u8>, u32, u32)> {
    let stream: IRandomAccessStream = thumb.OpenReadAsync()?.join()?.cast()?;
    let decoder = BitmapDecoder::CreateAsync(&stream)?.join()?;
    let (w, h) = (decoder.PixelWidth()?, decoder.PixelHeight()?);
    let side = w.min(h).max(1);
    let scale = (ART_MAX_PX as f64 / side as f64).min(1.0);
    let (sw, sh) = (((w as f64) * scale).round() as u32, ((h as f64) * scale).round() as u32);
    let out = ((side as f64) * scale).round() as u32;
    let transform = BitmapTransform::new()?;
    transform.SetScaledWidth(sw)?;
    transform.SetScaledHeight(sh)?;
    transform.SetInterpolationMode(BitmapInterpolationMode::Fant)?;
    transform.SetBounds(BitmapBounds { X: (sw - out) / 2, Y: (sh - out) / 2, Width: out, Height: out })?;
    let data = decoder
        .GetPixelDataTransformedAsync(
            BitmapPixelFormat::Bgra8,
            BitmapAlphaMode::Premultiplied,
            &transform,
            ExifOrientationMode::IgnoreExifOrientation,
            ColorManagementMode::DoNotColorManage,
        )?
        .join()?;
    Ok((data.DetachPixelData()?.to_vec(), out, out))
}

fn command(session: &Session, c: MediaCommand) -> Result<()> {
    let _ = match c {
        MediaCommand::PlayPause => session.TryTogglePlayPauseAsync()?.join()?,
        MediaCommand::Pause => session.TryPauseAsync()?.join()?,
        MediaCommand::Play => session.TryPlayAsync()?.join()?,
        MediaCommand::Prev => session.TrySkipPreviousAsync()?.join()?,
        MediaCommand::Next => session.TrySkipNextAsync()?.join()?,
        MediaCommand::Seek(position) => session.TryChangePlaybackPositionAsync(position)?.join()?,
        MediaCommand::ToggleShuffle => {
            let on = session.GetPlaybackInfo()?.IsShuffleActive().and_then(|r| r.Value()).unwrap_or(false);
            session.TryChangeShuffleActiveAsync(!on)?.join()?
        }
        MediaCommand::CycleRepeat => {
            let mode = session.GetPlaybackInfo()?.AutoRepeatMode().and_then(|r| r.Value()).unwrap_or(MediaPlaybackAutoRepeatMode::None);
            let next = match mode {
                MediaPlaybackAutoRepeatMode::None => MediaPlaybackAutoRepeatMode::List,
                MediaPlaybackAutoRepeatMode::List => MediaPlaybackAutoRepeatMode::Track,
                _ => MediaPlaybackAutoRepeatMode::None,
            };
            session.TryChangeAutoRepeatModeAsync(next)?.join()?
        }
    };
    Ok(())
}

/// Current FILETIME, for interpolating a snapshot's timeline on the UI side.
pub fn filetime_now() -> i64 {
    now_filetime()
}

#[cfg(test)]
mod tests {
    use super::*;
    use windows::Graphics::Imaging::BitmapEncoder;
    use windows::Storage::Streams::{InMemoryRandomAccessStream, RandomAccessStreamReference};

    /// A 1280×720 PNG (a YouTube-style 16:9 thumbnail): red side bands outside the centred
    /// 720×720 square, blue inside.
    fn youtube_like_png() -> Result<RandomAccessStreamReference> {
        let (w, h) = (1280u32, 720u32);
        let mut px = Vec::with_capacity((w * h * 4) as usize);
        for _ in 0..h {
            for x in 0..w {
                let inside = (280..1000).contains(&x);
                px.extend_from_slice(if inside { &[255, 0, 0, 255] } else { &[0, 0, 255, 255] });
            }
        }
        let stream = InMemoryRandomAccessStream::new()?;
        let encoder = BitmapEncoder::CreateAsync(BitmapEncoder::PngEncoderId()?, &stream)?.join()?;
        encoder.SetPixelData(BitmapPixelFormat::Bgra8, BitmapAlphaMode::Premultiplied, w, h, 96.0, 96.0, &px)?;
        encoder.FlushAsync()?.join()?;
        stream.Seek(0)?;
        RandomAccessStreamReference::CreateFromStream(&stream)
    }

    #[test]
    fn cover_is_centre_cropped_square_and_capped() {
        unsafe {
            let _ = RoInitialize(RO_INIT_MULTITHREADED);
        }
        let reference: windows::Storage::Streams::IRandomAccessStreamReference = youtube_like_png().unwrap().cast().unwrap();
        let (px, w, h) = decode_cover(&reference).unwrap();
        assert_eq!((w, h), (512, 512));
        assert_eq!(px.len(), 512 * 512 * 4);
        // Corners come from inside the crop: blue, not the red side bands (the resampling filter
        // bleeds a little across the sub-pixel crop edge; a wrong crop would be solid red).
        for (x, y) in [(0, 0), (511, 0), (0, 511), (511, 511)] {
            let i = (y * 512 + x) * 4;
            let (b, r) = (px[i], px[i + 2]);
            assert!(b > 200 && r < 40, "pixel ({x},{y}) is b {b} r {r}");
        }
    }
}
