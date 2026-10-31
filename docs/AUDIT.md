# WinIsland audit, October 2026 (0.3.3)

What was checked in the code, what is already good, what was fixed in this pass, and what is left,
with the evidence for each finding. "Fixed" items are in the same branch as this file.

## What is already in good shape

Checked before proposing anything, so no work was spent on these:

- **Motion design.** The pill⇄card morph is an under-damped spring sampled into composition key
  frames (`render/motion.rs`, `render/morph.rs`). It runs on the compositor, it can be interrupted
  (`resize` stops the running animation and starts from where it is) and it has a speed setting
  (Off / Slow / Normal / Fast). The weather scenes are composition animations too: no CPU while they
  play (`render/weather_fx.rs`).
- **Shelf file safety.** Drags out are copy-only (`platform/dragdrop.rs` refuses move-only drags,
  and its tests cover it). Nothing in the shelf deletes or moves a user's file, and network or USB
  paths are never asked about on the UI thread (`local_exists` in `app.rs`).
- **Claude Code permission safeguards.** The card answers only what it shows in full
  (`card_can_answer`), an answer carries a nonce only the WinIsland knows, a click is ignored when
  the ask changed under it (`ask_settled`), and Claude's own dialog stays open in parallel.
- **The live activity API.** Loopback only, off by default, a per-app rate limit (10/s, bursts of
  20), the user asked once per app, body and header size limits, and request validation with tests
  (`activities/api.rs`, `platform/api_server.rs`, `DOCS.md`).
- **Fullscreen and urgency.** Only what can't wait goes on the in-game island
  (`activities/island.rs`), and moments are short and ranked (`activities/mod.rs`).
- **DPI.** Per-monitor v2 awareness, `WM_DPICHANGED` rebuilds the windows, and sizes are in DIP
  throughout.
- **Timers.** The card's raise loop (30 ms) runs only while the card shrinks back; the outside-click
  poll only while a card is open; the meter only while music plays with the waveform on.

## Findings

### Bugs

| # | Finding | Evidence | Status |
|---|---|---|---|
| B1 | The shelf is emptied by every restart, including the in-app update (which restarts WinIsland). Screenshots added automatically are lost with it. | `activities/shelf.rs` header: "kept … until removed or the app restarts"; no save or load anywhere. | **Fixed** |
| B2 | The animation speed "Off" still leaves the weather scenes moving: their loops don't use the speed factor. | `render/weather_fx.rs` builds `Forever` loops with its own `ticks()`, not `scaled_ticks`. | **Fixed** (reduced motion stops them) |
| B3 | The README said a status line of the user's own leaves the Claude card without usage bars; since 0.3.3 the context comes from the transcript and the limits from the usage endpoint. | README, "Claude Code" section. | **Fixed** |

### Accessibility

| # | Finding | Evidence | Status |
|---|---|---|---|
| A1 | Windows' **Animation effects** switch (Settings → Accessibility → Visual effects) is ignored: the morph, page turns, the weather scenes and the waveform move regardless. | No `SPI_GETCLIENTAREAANIMATION` read anywhere. | **Fixed** |
| A2 | The pill and the cards are invisible to screen readers: no UI Automation provider, no window text. | No `WM_GETOBJECT` / `IRawElementProviderSimple` in `src/`. | Recommended (below) |
| A3 | The cards are mouse-only; only the timer picker and the Claude message box take the keyboard. | `UiEvent::Key` is handled for the picker only (`app.rs`). | Recommended |
| A4 | High contrast themes aren't followed: the cards stay white on translucent dark. | No `SPI_GETHIGHCONTRAST` / `HIGHCONTRAST` in `src/`. | Recommended |

### UX improvements

| # | Finding | Status |
|---|---|---|
| U1 | Finished activities (a timer done, a Claude turn done, a backup pushed through the API) are gone after 5 seconds; nothing shows what finished while you were away. The Claude tab covers Claude sessions only. | Candidate: activity history (below) |
| U2 | The settings for the shelf were spread under Screenshots; there is now **Keep the shelf** beside **Add to the shelf**. | Done with B1 |

### Performance

| # | Finding | Status |
|---|---|---|
| P1 | Under reduced motion the music waveform's 50 ms meter timer no longer runs at all. | **Fixed** with A1 |
| P2 | The Claude worker (0.3.3) wakes every 5 s (2 s while a session works) and stats the newest transcripts; it reads only files that changed, and only their first 64 KB and last 512 KB. Idle cost was not re-measured on Windows in this pass. | Measure (next steps) |

### Windows compatibility

| # | Finding | Status |
|---|---|---|
| W1 | Main display only (the README says so): no monitor choice, and the pill doesn't follow the taskbar the user works on. | Recommended: a "Show on" setting (primary, the monitor with the mouse) |

## New features considered

| Candidate | Verdict | Why |
|---|---|---|
| Theme studio (accent, presets, compactness) | Not now | The pill already takes its colour from the album cover, the wallpaper's accent and each activity's own accent; card blur, saturation, animation speed and pill size settings exist. A studio adds surface without fixing a reported problem. |
| Activity history | Worth doing next | U1: nothing else keeps what finished. It must be local, short-lived (e.g. a day) and off for Claude's prompts by default. |
| SDK and schemas for the API | Partly there | `DOCS.md` documents every endpoint with examples and errors; a JSON schema file would let editors validate payloads, small value. |
| Shelf pinning and favourites | Later | Persistence (B1) was the missing base; pinning only makes sense once items outlive a restart. |
| Calendar or clipboard integrations | Not now | Calendar needs an account connection (Graph/Google) that this app avoids by design (no keys, no sign-in); clipboard history is built into Windows (Win+V). |

## Done in this pass

1. **Reduced motion** (A1, B2, P1): `platform/os.rs` reads Windows' Animation effects;
   `render/motion.rs` keeps it (read at start and on every `WM_SETTINGCHANGE`) and makes the speed
   factor instant when reduced; the weather scenes and the waveform stop. **Follow Windows**
   (Settings → Appearance → Motion, on by default) opts out. The smoke test turns the setting off
   and on again on the runner and checks the app follows both ways.
2. **The shelf outlives a restart** (B1, U2): `Shelf::to_json` / `restore` keep the paths and which
   are folders (never the files) in `%LOCALAPPDATA%\WinIsland\shelf.json`, written atomically on
   every change; at start, files gone meanwhile drop off (local drives only, as before); turning
   **Keep the shelf** off deletes the file. The smoke test checks a kept file comes back and a gone
   one drops.

## Recommended next steps

1. UI Automation for the pill and the card (A2): a provider that reads out the front activity
   ("Spotify: Song, Artist, playing") and the card's buttons; it is what makes the app usable with
   Narrator. Then keyboard focus for the card's buttons (A3).
2. Activity history (U1), local and short-lived.
3. A "Show on" monitor setting (W1).
4. Re-measure idle CPU and memory on Windows with the Claude worker running (P2) and update the
   README's figures.
5. High contrast (A4): solid system colours instead of the translucent card.
