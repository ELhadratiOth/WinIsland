<div align="center">

<img src="assets/readme/pill.png" width="800" alt="The WinIsland pill at the top centre of the screen, showing a running timer">

# WinIsland

**A Dynamic Island for Windows 11 and 10, at the top centre of your screen.**

A small pill sits at the top centre of the screen and shows what is going on right now: the playing song, a timer, a match<br>
score, what Claude Code is doing. Click the pill and a card opens below it.

**[Download](https://github.com/ELhadratiOth/WinIsland/releases/latest)** &nbsp;·&nbsp;
[What it shows](#what-it-shows) &nbsp;·&nbsp;
[Installation](#installation) &nbsp;·&nbsp;
[Building](#building)

<sub>Inspired by One UI's Now Bar and Apple's Dynamic Island.</sub>

</div>

<br>

## What it shows

- **Music:** any app that opens a Windows media session (Spotify, YouTube Music, browsers).
  The pill takes the colour of the album cover; the card has the cover, a wavy progress bar, lyrics
  and the choice of audio output.
- **Timer and stopwatch:** click the island when it shows the clock: the day's summary ends with a
  timer panel (5, 10 or 25 minutes in one click, any other length, or the stopwatch). They also start
  from the picker (Win+Alt+N) or the tray menu; when the time is up an alarm sounds and the card opens.
- **Live match:** the score, minute and goal moments of the football and basketball teams you follow.
- **Weather:** the clock and weather when idle; warnings for rain, snow, storms, strong wind, extreme
  heat, frost and poor air quality. If no city is chosen, the location comes from Windows.
- **Galaxy Buds:** the charge of both earbuds and the case, and noise control. If more than one Buds
  is paired, the one shown is chosen in the settings.
- **Claude Code:** what Claude is doing at the moment and its task list, how full its context is and how
  much of your plan's 5-hour and weekly limits is used. Type a message on the card to send it to the
  session, or press **Compact**. When it asks for permission, the card shows **Allow / Deny** buttons
  ([see below](#live-activities)). The **Claude tab** waits at the back of the stack, behind the
  weather: your latest sessions with their context, a message box and **Compact** each, and the plan's
  5-hour and weekly limits.
- **Games:** Steam and Epic Games downloads, updates, pauses and play time.
- **Digital Wellbeing:** the day's screen time, each app's share, and the limits you set.
- **Shelf:** files you drop on the pill wait there until you drag them where you need them; new
  screenshots are added to the shelf automatically. Hover a file for its copy button: an image is
  copied as its picture (paste it into a chat or a document), a text or code file as its text, and
  every file as the file itself (paste it into a folder).
- **System:** volume, charging and low-battery warnings, the app using the microphone or camera, and
  the charge of Bluetooth headphones.
- When several activities are active, thin strips show beside the front one; scroll the mouse wheel
  over the pill to switch between them. When a game, video or presentation is full screen, the pill
  is hidden; things that can't wait (Claude's permission request, a weather warning) appear as a small
  island at the top of the screen.
- **Right-click the pill** to hide the island for 1, 5 or 10 minutes, or to open the settings of what
  it shows. **Show WinIsland now** in the tray menu brings it back sooner.

## Cards

The card grows out of the pill when it opens, keeping the same distance from the top of the screen,
and shrinks back into it when the mouse leaves. On the music card, **Lyrics** in the corner of the
cover opens the song's lyrics; they come from `lrclib.net` and scroll along with the song (the cover
says so when a song has no synced lyrics).

For Spotify, the pill and card show Spotify's logo, and a **heart** beside the song's title adds it to
your Liked Songs, or removes it. Windows' media controls have no "like", so WinIsland brings Spotify to
the front for a moment, presses Spotify's own Like shortcut (Alt+Shift+B) and gives the focus back.
Spotify doesn't tell other apps which songs you've liked, so the heart shows only what you pressed
in WinIsland.

When idle, click the clock to open the day's summary: weather, matches, the last song played and screen
time.

## Settings

The settings window opens from **Settings…** in the tray menu; every change applies to the pill
immediately. The search box at the top finds settings; on the Live match page it searches for teams,
and on the Full screen page it adds exceptions. Ctrl+F goes to search, Ctrl+K opens the actions menu.

## Installation

Download `winisland-setup.exe` from the [latest release](https://github.com/ELhadratiOth/WinIsland/releases/latest)
and run it. It needs no administrator rights and installs to `%LOCALAPPDATA%\Programs\WinIsland` (use
"Change" to pick another folder). Options on the setup screen:

- **Start with Windows**
- **Connect to Claude Code** (shown when Claude Code is on the PC)
- **Turn on app notifications** (off by default): lets other apps show notifications on the pill
  ([see below](#live-activities)). The apps you allow, and what they show, are your responsibility.
- **Desktop shortcut**
- **Open settings when done**

A Start menu shortcut is always added. WinIsland appears in Windows' "Settings → Apps" list and can be
removed from there. Removing it also deletes the shortcuts, the start-with-Windows entry and the Claude
Code hooks; deleting the settings is optional.

If WinIsland is already installed and you run the setup again, WinIsland is updated or repaired: the
running WinIsland is closed, its files are replaced and it starts again.

For a silent install, use `winisland-setup.exe --quiet` (it doesn't change the app notifications
setting); for a silent uninstall, use `"%LOCALAPPDATA%\Programs\WinIsland\winisland-setup.exe" --uninstall --quiet`.

## Usage

| What | How |
|---|---|
| Open / close the card | Click the pill; the card closes by itself when the mouse leaves |
| Play / pause | Middle-click the pill |
| Switch between activities | Mouse wheel over the pill or the card |
| Timer picker | Win+Alt+N (can be changed in Settings → General) |
| Tray menu | Start timer, Start stopwatch, Start with Windows, Settings…, Quit |

## Live activities

Another program or script can show an activity on the pill. If WinIsland isn't running, the command
exits without doing anything (the exit code is always 0 and it prints nothing):

```
winisland.exe push --id backup --title "Backup" --text "Documents" --progress 40
winisland.exe push --id backup --title "Backup" --state done
```

`--state`: `run` (default), `wait` (waiting for the user; it comes to the front briefly), `done`
(shows "Done" for 5 seconds), `clear` (removes it at once). An activity that isn't updated for an hour
goes away by itself. This can be turned off in Settings → System → Live activities.

**API for apps:** other apps can show a notification or a live activity on the pill with a single HTTP
request; they can use their own icons, colours and up to two buttons. The API is reachable only from
this PC (`127.0.0.1:20034`) and stays off until you turn it on (during setup, **Turn on app
notifications**, or Settings → System → App notifications). The first time an app sends a request, the
pill asks "Wants to show a notification"; your answer is remembered and can be changed in the settings.
The apps you allow, and what they show, are your responsibility; WinIsland only displays them. For
developers: [DOCS.md](DOCS.md).

```
curl -H "Content-Type: application/json" -d "{\"app\":\"Backup tool\",\"title\":\"Backup finished\"}" http://127.0.0.1:20034/v1/notify
```

**Claude Code:** the **Connect to Claude Code** option in setup installs the hooks. To install or remove
them by hand, use the commands below. The command writes the path of the exe itself. The first time, it
saves a backup as `settings.json.before-winisland`; after that, every write saves `settings.json.winisland-last`.
It doesn't touch other hooks. It also sets WinIsland as Claude Code's status line, unless you already have
one of your own (yours stays, and the card then has no usage bars):

```
winisland.exe claude-hooks install
winisland.exe claude-hooks remove
```

The pill shows what Claude is doing and how long it has been running ("winisland · Editing:
app.rs"). The card shows the progress of the task list, the latest steps, and, when the work is done,
the first line of the answer ("Done · 12 min · 9 files"). When Claude asks for permission, the card
shows **Allow / Deny** buttons. This only applies to single-line commands, files, addresses and
searches that fit entirely on the card. For file permissions, the card shows the file's full path; the
change itself stays in Claude's window. Multi-line commands, questions and plan approvals are also
answered there. Claude's own window stays open at the same time, and whichever answer comes first
counts. The card waits 60 seconds for an answer (Settings → System → Live activities). If WinIsland is
moved to another folder, the hooks are updated to the new path the next time it starts. It works in the
Code tab of the desktop app, in the CLI and in VS Code.

The status line (`winisland.exe claude-status`) shows the model, the context and the limits under Claude's
prompt ("Opus 5.5 · context 34% · 5h 23% · week 41%") and passes the same numbers to the card. The card
draws them as rings: **Context** (with **Compact** under it), the **5-hour limit** and the **weekly
limit**, each limit with the time until it resets. The limits are only there with a Pro or Max plan,
after Claude's first answer in the session. The rings turn amber at 70% and red at 90%. The button
beside the card's × brings the session's window to the front.

The card's message box sends what you type to the session's window: WinIsland brings that window to the
front, types the message and Enter there, and puts the window you were in back in front. **Compact** does
the same with `/compact`. The window is the one the session's last prompt was typed in; in a terminal
with tabs, the message goes to the tab that is open.

**The Claude tab** is always in the stack (scroll the pill to it, behind the weather): "Claude Code",
how many sessions are working, and the 5-hour limit. Its card shows the plan's 5-hour and weekly limits
as bars, then the four latest sessions of the last two weeks: the project, where it ran (Terminal,
Desktop, IDE) and when, its name or first prompt, a context bar, **Message** and **Compact**. A message
to a session whose terminal is open is typed there; any other (the desktop app's, a closed one) goes
through `claude -p --resume <session>` in the session's folder, so Claude Code's `claude` command must
be installed. Its progress shows on the pill like any session's. A working session's row opens its
card. Turn the tab off in Settings → System → Live activities.

The tab reads the sessions from Claude Code's transcripts (`~\.claude\projects`), which the terminal and
the desktop app both write. So a session shows on the pill even when its hooks don't reach WinIsland
(the desktop app's sessions, for one), and a turn ended with Esc stops showing as working. The context
comes from the transcript too, where there is no status line.

The plan's limits come from the status line, and also from Claude's usage endpoint with Claude Code's
own sign-in (`~\.claude\.credentials.json`), every five minutes and when the tab's card opens. The token
is sent to `api.anthropic.com` only and never logged. The endpoint isn't documented, so if it changes,
the bars stay empty until a terminal session's status line fills them. WinIsland doesn't renew the
sign-in: when it has expired, the bars wait for Claude Code to renew it. Turn this off with **Plan
limits** in the same settings section.

## Building

You need Rust (with the MSVC toolchain) and the Windows SDK (`rc.exe`), which embeds the icon in the exes.
It still builds without the SDK, but the exes keep Windows' default icon.

```
cargo b
```

`cargo b` is an alias defined in `.cargo\config.toml`: `cargo build --profile quick --bin
winisland --bin winisland-settings`. It produces an exe that runs about as fast as a release build but
doesn't optimise the whole program together (no LTO), so with a warm cache a build takes a few seconds.
The settings window's interface (`ui\`, Slint) is built without optimisation in this profile: a `.slint`
change compiles in 40 seconds instead of 4–5 minutes.

The output goes to `target\quick\`; clone the project to a short path (the MSVC path limit is 260
characters). The two exes (`winisland.exe` and `winisland-settings.exe`) must be in **the same folder**:
the app looks for the settings window next to itself. Copy both into a `bin\` folder and run them from
there. If "Start with Windows" is on, the startup entry is updated automatically to the most recently run copy.

**Installer:** `tools\make-setup.ps1` builds `winisland-setup.exe` (with both exes inside), the one
file a GitHub release carries, into `dist\`, along with the two exes and `SHA256SUMS.txt`. The C runtime is linked statically, so the exes open on
Windows machines without the Visual C++ Redistributable. The release build (`target\dist\`, several GB)
is kept: the first build takes 13–14 minutes, and later ones rebuild only what changed (about 5 minutes
after a code change). `-Clean` deletes that build at the end; `-Jobs` sets how many build jobs run at once.

```
powershell -ExecutionPolicy Bypass -File tools\make-setup.ps1 -Jobs 6
```

### Fonts

The pill and card look for a `fonts\` folder next to the exe: if it has `OneUISans*.ttf`, they use One UI
Sans (you can take it from your phone); otherwise `Inter*.ttf`; otherwise Windows' Segoe UI Variable.
The settings window includes Inter itself (`ui\fonts`, SIL Open Font License).

## Files

- Settings: `%APPDATA%\WinIsland\settings.json`
- Log: `%LOCALAPPDATA%\WinIsland\log.txt`. It also records when and why the pill is hidden
  (for example `pill: hidden (nothing active)` or `media: Brave left (paused 10 min)`).
- Team crests: `%LOCALAPPDATA%\WinIsland\crests\`

## Updates

A minute after WinIsland starts, and then once a day, it checks GitHub for the latest release
(`api.github.com`; nothing is sent besides the request itself). If a new version exists, **Update: x.y.z**
appears in the tray menu and on a line in Settings → About. Clicking it downloads the new
`winisland-setup.exe`, checks it against the SHA-256 GitHub keeps for it, and runs it quietly: it closes
WinIsland, puts the new version in place and starts it again. Your settings, start with Windows, the
desktop shortcut and the Claude Code connection stay as they are. If a step fails, the release page opens
so you can install it by hand.
Update checks can be turned off in Settings → About → Check for updates.

## Privacy and network

Where WinIsland connects (none of these need an API key):

- **Lyrics:** the playing song's artist, title and length are sent to `lrclib.net` (can be turned off
  in Settings → Media → Lyrics).
- **Weather:** the coordinates of the chosen city are sent to Open-Meteo (`api.open-meteo.com`,
  `geocoding-api.open-meteo.com`). If **Use my location** is selected, the location comes from
  Windows' location service (Windows asks for permission); to find the place's name, coordinates
  rounded to about 1 km are sent to OpenStreetMap (`nominatim.openstreetmap.org`).
- **Matches:** for the teams you follow, ESPN (`site.api.espn.com`) and EuroLeague/EuroCup
  (`feeds.incrowdsports.com`, `live.euroleague.net`); crests are also downloaded from these sources'
  image addresses.
- **Updates:** GitHub (`api.github.com`), as described above.

When **App notifications** is on, WinIsland listens only on `127.0.0.1:20034` and `[::1]`, which are
reachable only from this PC. If the port is taken, it uses another port Windows gives it and writes that
to `%LOCALAPPDATA%\WinIsland\api.json`. When it's off, no port is opened at all. Requests from web
pages, from the network and from other Windows users on this PC are rejected; details are in [DOCS.md](DOCS.md).

Watched folders: ShareX's screenshot folder and Windows' `Pictures\Screenshots` folder (a new image
shows on the pill for a short time and is added to the shelf; Settings → System → Screenshots).
Claude Code's steps and permission requests show at the top of the screen; be mindful of this when you share your
screen.

## Known limits

- **Windows 11 or Windows 10 (1809 and later).** On Windows 10 the card's background isn't blurred
  (a flat background is used) and the icons come from Segoe MDL2 Assets. Windows 10 support arrived in
  0.2.1.
- **Galaxy Buds:** tested with Buds2. Buds2 Pro, Buds FE, Buds Core, Buds3, Buds3 FE and Buds4 use the same
  protocol, and Buds Pro is recognised too. The first Galaxy Buds, Buds+ and Buds Live are not supported.
  While GalaxyBudsClient or Samsung's Galaxy Buds app is using the connection, WinIsland can't read the earbuds.
- **Paused media leaves the pill after 10 minutes** (Settings → Media → Keep when paused). If there is no
  other activity, the pill is hidden at that point; it comes back when the music plays again.
- The settings window doesn't show live data: instead of the match's current score, the Buds' current
  charge or a running timer, it shows a sample view of that activity's pill.
- It shows on the main display only (the one with the taskbar).
- Resource use when idle (media paused, measured 2026-10-21): 5–10 ms of CPU time over 10 seconds,
  39–66 MB working set (25 MB private memory). The target was 5 ms and 50 MB per 10 seconds.
- If Explorer restarts, the pill comes back by itself; an open card is closed.

## Development

```
cargo t
cargo lint
```

Tests and clippy (the aliases in `.cargo\config.toml`) run in the quick profile; a plain `cargo test`
builds a second full copy of the dependencies under `debug\` (tens of GB).

- What speeds up building: Rust's own linker (`rust-lld`), debug information turned off for dependencies,
  and the settings window's Slint interface in its own crate (`ui\`), so it's rebuilt only when a `.slint`
  file changes.
- `WINISLAND_SETTINGS_SHOTS=<folder> winisland-settings.exe`: opens the settings window off-screen,
  takes a screenshot of each page with the real renderer, and closes without saving the settings.
- `tools\smoke.ps1`: starts the app with a demo timer, clicks the pill, hides it from its right-click
  menu and takes screenshots (the **Smoke test** workflow runs it on a Windows runner, then paints
  the Spotify card and pill with `cargo test --lib render::card::tests::spotify_pictures -- --ignored`).
- `tools\claude-check.ps1`: runs the real Claude Code CLI against the app (against a stand-in API,
  `tools\fake-anthropic.js`, so no key is needed): installs the hooks, gives Claude's permission from
  the card's **Allow** button, checks the result and removes the hooks again (the **Claude Code check**
  workflow runs it on a Windows runner when the Claude Code side changes).
- `winisland.exe --demo`: starts with a one-minute sample timer; `--match-replay <folder>` replays a match
  from recorded score tables.
- `assets\make_icon.py`: redraws the icon (the white pill in the tray).
- **Publishing a release:** increase `version` in `Cargo.toml`, commit, then push a matching tag
  (`git tag vX.Y.Z && git push origin vX.Y.Z`) or run the **Release** workflow by hand. It builds the
  installer on Windows and publishes `winisland-setup.exe` as a GitHub release (run again for the same
  version, it replaces that release).
- **Old workflow runs** are deleted automatically: the **Clean up old runs** workflow keeps only the newest
  run of each workflow.

---

<sub>Not affiliated with Samsung; it's an independent project inspired by One UI. The Galaxy Buds
noise-control icons belong to Samsung.</sub>
