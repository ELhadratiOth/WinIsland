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
- **Modules:**
  - Clock.
  - Now playing, with controls (any app using Windows media controls).
  - Local Claude Code sessions, with a prompt box.
- **Lightweight.** Event-driven with no polling, GPU-composited animations, and integrations
  that load in the background and degrade gracefully when offline.

## Known limitations

- Builds are not code-signed.
- Calendar and Claude usage modules are not implemented yet.
- Sending a message to a Claude Code session requires the native `claude.exe` on `PATH`.

Verify downloads against `SHA256SUMS.txt`.
