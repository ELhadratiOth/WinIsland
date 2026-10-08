A native Dynamic Island for Windows 10/11, built with C# / .NET 10 / WinUI 3.

> **Pre-release.** The core logic is unit-tested and the app builds in CI, but window behaviour
> (transparency, click-through, focus hand-off, fullscreen hiding) still needs testing on real
> Windows desktops. Please report anything that misbehaves.

## Install

1. Download the zip for your PC: `win-x64` (most PCs) or `win-arm64` (Snapdragon / ARM devices).
2. Extract it anywhere, e.g. `%LOCALAPPDATA%\Programs\WinIsland`.
3. Run `WinIsland.exe`. Nothing else needs installing: .NET and the Windows App SDK are bundled.

Windows SmartScreen may warn about an unsigned app; choose **More info → Run anyway**.
Use the tray icon to change settings, enable **Start with Windows**, or exit.

## What's included

- **Overlay behaviour.** Click-through when idle, never steals focus. It becomes interactive
  after resting the pointer on it (180 ms) or pressing **Win+Alt+I**, and **Esc** ends interaction.
- **Visibility policy.** Always show / hide in games / hide in fullscreen (default) / hide when
  an app is maximized. A fullscreen app on another monitor doesn't hide the island.
- **Positioning.** Top-centre, DPI-aware and multi-monitor aware. It avoids the taskbar, Snap
  Layouts and shell overlays.
- **Settings window** (tray › Settings…) for everything below; modules can be switched off one
  by one.

### Modules

| | What it does |
| --- | --- |
| **Clock + weather** | Time and date; temperature and conditions from Open-Meteo (city or Windows location). |
| **Now playing** | Real cover art, title, artist, album and app. Shuffle, repeat, drag to seek, choose between players, time-synced **lyrics** (lrclib.net) and a **like** button for Spotify Liked Songs (connect your own Spotify app in Settings). |
| **Claude Code** | Working/idle sessions, a prompt box, today's tokens and the current 5-hour window. |
| **Claude approvals** | Permission prompts appear on the island with Allow / Deny / In terminal (Settings › Claude Code › Connect). |
| **Calendar** | Your next meeting from an ICS feed: shown 10 minutes before, reminders at 5 minutes and at the start, **Join** for Teams / Zoom / Meet / Webex. |
| **Builds** | GitHub Actions for the repositories you list: live while building, green/red when done. |
| **Controls** | Volume, brightness, microphone mute, focus mode, battery. A slim on-screen display for the volume and brightness keys, plugging in and low battery. |
| **Microphone & camera** | Which apps are recording (orange = mic, green = camera), with one-click mute. |
| **Timer** | Countdown, stopwatch and Pomodoro with a progress ring and a chime. |
| **Downloads** | Live size and speed of browser downloads; Open / Show in folder when done. |
| **Clipboard** | The last 12 text copies (memory only, skips password-manager copies); click to paste. |
| **Shelf** | Drop files on the island, drag them out later. |
| **Focus mode** | Only the clock, timers, meetings, microphone/camera and approvals appear. |

### Privacy

Nothing leaves your PC unless you turn it on: cover-art and lyrics lookups send only artist,
title (and album/length for lyrics); weather sends a city or rounded coordinates; Spotify,
GitHub and calendar links are used only after you add them, and their tokens/links are stored
encrypted for your Windows account (DPAPI).

## Known limitations

- Builds are not code-signed.
- Notification previews (WhatsApp, Discord, Teams messages) need a packaged (MSIX) build:
  Windows only lets apps with package identity read notifications.
- Explorer file-copy progress isn't exposed by Windows; the Downloads module covers browser
  downloads.
- Brightness works on built-in displays (laptops, tablets), not external monitors.
- Claude usage costs are estimates at public API prices; subscription limits aren't published,
  so the island shows the 5-hour window and when it resets.
- Sending a message to a Claude Code session requires the native `claude.exe` on `PATH`.

Verify downloads against `SHA256SUMS.txt`.
