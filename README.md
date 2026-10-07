# WinIsland

A Dynamic Island for Windows: a small native overlay at the top of the screen that stays out
of the way, expands when something happens, and gets out of the way again.

Built with **C# / .NET 10 / WinUI 3 (Windows App SDK 1.8)**, MVVM, Windows Composition and Win32
where WinUI has no API. No Electron, no browser engine.

## Building

| What | Where | Command |
| --- | --- | --- |
| Core library + tests | any OS | `dotnet test tests/WinIsland.Core.Tests` |
| Windows platform layer | any OS (`EnableWindowsTargeting`) | `dotnet build src/WinIsland.Platform.Windows` |
| WinUI app | Windows only (XAML compiler) | `dotnet build src/WinIsland.App -c Release -p:Platform=x64` |

The app is unpackaged and bundles the Windows App SDK, so the output folder runs as a plain
`WinIsland.exe`. CI (`.github/workflows/ci.yml`) runs the tests on Linux and Windows, builds the
app on `windows-latest`, and uploads the build as an artifact.

Settings live in `%LOCALAPPDATA%\WinIsland\settings.json`. Common settings are also in the
tray menu. Default hotkey: **Win+Alt+I** toggles interactive mode.

The tray icon (in the **^** overflow on Windows 11) appears even if the island fails to start.
It always offers **Open log file** and **Exit WinIsland**. The log is at
`%LOCALAPPDATA%\WinIsland\winisland.log`; if startup fails, an error box also points to it.

## Layout

```
src/
  WinIsland.Core/               platform-agnostic, unit-tested
    Interaction/                passive ⇄ interactive state machine (hover dwell, leave grace, focus hold)
    Visibility/                 fullscreen / game / maximized policy
    Layout/                     sizes, timings, placement (DPI, multi-monitor, safe zones)
    State/                      context + state manager → immutable IslandState
    Modules/                    clock, media, Claude Code (view models)
    Integrations/               background host: isolation, retry/backoff, offline pause
    Claude/                     local session discovery (FileSystemWatcher) + CLI messenger
  WinIsland.Platform.Windows/   Win32 + WinRT adapters (no WinUI dependency)
    Windowing/                  click-through / no-activate / topmost styles, message subclass
    Foreground/                 WinEvent hooks + fullscreen/game classification
    Input/                      low-level mouse hook thread (hover), global hotkey
    Display/                    monitors, effective DPI, auto-hide taskbar
    Media/ Networking/ Shell/   GSMTC media, connectivity events, tray icon, Run-key startup
  WinIsland.App/                WinUI 3 shell: IslandWindow, composition animator, IslandHost
tests/WinIsland.Core.Tests/     xUnit + FakeTimeProvider (deterministic timing)
```

## How the requirements are met

### 23 · Non-blocking overlay
- **Passive (default):** the window has `WS_EX_LAYERED | WS_EX_TRANSPARENT`, so every click
  goes to the window underneath, including the taskbar and title bars (`IslandWindowController`).
- **Becoming interactive:** a click-through window gets no mouse messages, so hover is detected
  by a low-level mouse hook on its **own thread** (`PointerHotZoneWatcher`). That way UI work
  can never delay system-wide mouse input. The pointer must rest on the island for 180 ms
  before it turns interactive, so passing over it on the way to a title bar doesn't capture
  anything. The hotkey and tray menu are the other ways in.
- **Back to passive:** 400 ms after the pointer leaves (unless a text field has focus), on
  **Escape**, or when the user switches to another window. `WS_EX_TRANSPARENT` is restored.

### 24 · No focus stealing
`WS_EX_NOACTIVATE`, `SW_SHOWNOACTIVATE`, `SWP_NOACTIVATE` on every move, and `WM_MOUSEACTIVATE →
MA_NOACTIVATE`. Buttons and sliders work without activation. The island only takes the
foreground when the user **clicks into its text box** or presses the hotkey. It remembers the
previous foreground window and gives focus back when interaction ends.

### 25 · Fullscreen and games
WinEvent hooks (foreground, minimize, move/size, and location changes for the foreground
thread only) feed `ForegroundInspector`. A window is *fullscreen* when it covers its monitor or
`SHQueryUserNotificationState` reports exclusive D3D. It is a *game* when it is exclusive D3D,
or fullscreen and either listed in `GameProcesses` or installed under a known launcher folder.
Policies: **Always show / Hide in games / Hide in fullscreen (default) / Hide when maximized**.
Only `Always show` overlays games. A fullscreen app on *another* monitor doesn't hide the
island. While hidden, the mouse hook is removed entirely.

### 26 · Positioning
`PlacementCalculator`: top-centre of the chosen monitor, below the work-area top (so a
top-docked taskbar is respected), sized by the monitor's effective DPI (per-monitor v2
manifest), and always clamped inside the monitor. It re-runs on `WM_DISPLAYCHANGE`,
`WM_DPICHANGED` and `WM_SETTINGCHANGE` (resolution, scaling, monitors attached or removed). The
monitor is **Primary** or **Follow active window**. If a monitor is disconnected, the island
falls back to the primary.

### 27 · Safe zones
- **Taskbar:** a docked taskbar is excluded through the work area; an auto-hide taskbar on the
  top edge counts as an obstacle.
- **Snap Layouts:** the island hides while a window is dragged near the top of its monitor.
- **Shell overlays** (Start, Search, notification centre, Task View, …): the island moves down
  out of their way, or hides if that would push it more than 120 DIP.
- **Title bars:** the island is click-through whenever you aren't using it.

### 28 · Dynamic size
`Compact` (180×36), `Expanded` (380×84) and `Large` (440×~300, grows with the session list).
Each module picks its interactive size; the switcher strip adds 28 DIP when shown. The window is
resized to the union of the old and new shapes for the animation, then shrunk to the final
shape, so no invisible area is left catching clicks.

### 29 · Animation
The pill's shape is a composition `CompositionRoundedRectangleGeometry` clip whose
size/offset/radius are animated on the **compositor thread** (`PillAnimator`), with no XAML
layout per frame. That keeps it smooth at 60–165 Hz, and the compositor goes idle afterwards.
Resize takes 220 ms; content cross-fades take 160 ms (implicit show/hide animations). No blur or
acrylic. The Windows "Animation effects" setting is respected.

### 30 · Resource usage
Nothing polls:

- The clock wakes once per minute, aligned to the minute.
- Media updates come from GSMTC events.
- Claude Code sessions come from a `FileSystemWatcher`, plus a single timer for the next
  "working → idle" transition.
- Connectivity comes from `NetworkStatusChanged`.
- Foreground and display changes come from WinEvents and window messages.
- The media progress bar ticks only while it is visible.

State is published only when it changes (`IslandStateManager`, `IslandViewModel`). The concurrent
(background) GC is disabled.

### 31–33 · Technology
Native C# + WinUI 3 + Win32 interop (`LibraryImport`, blittable signatures, no runtime marshalling).
Only WinUIEx (for a transparent window backdrop) is added on top of the Windows App SDK.

### 34–36 · Event-driven, off the UI thread, never waiting on the network
`IntegrationHost` starts every integration on the thread pool **after** the island is already
on screen. Modules marshal results to the UI thread via `IUiDispatcher`. Commands (media
controls, sending to Claude) are async and never block the UI.

### 37 · Offline
Integrations declare `RequiresNetwork`. Network-bound ones are stopped when connectivity drops
and restarted when it returns. Clock, media and local Claude Code sessions work offline. A
failing integration is retried with exponential backoff (5 s → 5 min) and never affects the
others.

### 38 · Startup
Load settings → apply window styles → show the clock → then the tray, hotkey, connectivity
and integrations. Start-with-Windows uses the per-user Run key. Publishing uses ReadyToRun.

### 39 · Memory
Every subscription is unsubscribed on dispose. The Claude monitor keeps at most 8 sessions
from the last 2 days and drops cached metadata for sessions that fall out. Process-path and
monitor caches are bounded or invalidated by events. Integrations release everything in `StopAsync`.

## Status and known limitations

- The core logic is covered by unit tests. The Windows layer and the app compile in CI, but
  window behaviour (click-through, transparency, focus hand-off) needs checking on a real
  Windows desktop. In particular, the combination of `WS_EX_LAYERED` with the WinUIEx
  transparent backdrop should be tested on Windows 10 and 11.
- Toast notifications aren't tracked as obstacles. They appear in the bottom corner, away from
  the island, unless the taskbar is moved to the top with third-party tools.
- Sending a message to Claude Code runs `claude --resume <id> --print` with the prompt on stdin,
  and needs the native `claude.exe` on `PATH`. The `.cmd` npm shim is deliberately not used,
  because it would route user text through `cmd.exe`.
- Calendar and Claude usage modules aren't implemented yet. They plug into `IntegrationHost` as
  `RequiresNetwork` integrations.
