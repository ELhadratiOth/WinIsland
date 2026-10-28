# Checks the Claude Code connection on Windows with the real Claude Code CLI: `claude-hooks install`
# writes the hooks into ~\.claude\settings.json, a WinIsland runs, and Claude Code (talking to
# tools\fake-anthropic.js, no key needed) runs one turn that asks to run `mkdir`. The permission is
# given from the WinIsland's card (its "Allow" button clicked), so the folder exists only if
# the ask went hook -> card -> hook -> Claude. Then `claude-hooks remove` takes the hooks out again.
# Everything it finds goes into the output folder: screenshots, report.txt, Claude's output and
# both logs.
#
#   powershell -ExecutionPolicy Bypass -File tools\claude-check.ps1 [-Exe target\quick\winisland.exe] [-Out claude-check]
param([string]$Exe = 'target\quick\winisland.exe', [string]$Out = 'claude-check')
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$report = Join-Path $Out 'report.txt'
function Say([string]$line) { $line; Add-Content -Path $report -Value $line -Encoding utf8 }

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class Check {
    public delegate bool EnumProc(IntPtr hwnd, IntPtr lparam);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("gdi32.dll")] public static extern int GetDeviceCaps(IntPtr hdc, int index);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, IntPtr lparam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder s, int n);

    static string ClassOf(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }

    // The visible top-level windows of class `cls`, as "x y w h" (any process).
    public static List<string> Visible(string cls) {
        var rows = new List<string>();
        EnumWindows((h, l) => {
            RECT r;
            if (IsWindowVisible(h) && ClassOf(h) == cls && GetWindowRect(h, out r)) rows.Add(String.Format("{0} {1} {2} {3}", r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
            return true;
        }, IntPtr.Zero);
        return rows;
    }

    // Closes the message boxes (#32770) of process `pid`; how many.
    public static int CloseBoxes(uint pid) {
        int n = 0;
        EnumWindows((h, l) => {
            uint owner;
            GetWindowThreadProcessId(h, out owner);
            if (owner == pid && ClassOf(h) == "#32770") { PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero); n++; }
            return true;
        }, IntPtr.Zero);
        return n;
    }
}
"@
[Check]::SetProcessDPIAware() | Out-Null
$scale = [Check]::GetDeviceCaps([Check]::GetDC([IntPtr]::Zero), 88) / 96.0
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$failures = New-Object System.Collections.Generic.List[string]
function Fail([string]$why) { $failures.Add($why); Say "FAILED $why" }
function Step([string]$name, [scriptblock]$body) {
    try { & $body } catch { Fail "$name : $_" }
}

function Capture {
    $bmp = New-Object System.Drawing.Bitmap $screen.Width, $screen.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $dst = $g.GetHdc()
    $src = [Check]::GetDC([IntPtr]::Zero)
    # SRCCOPY | CAPTUREBLT: layered windows (the pill and the card) are in the picture too.
    $ok = [Check]::BitBlt($dst, 0, 0, $screen.Width, $screen.Height, $src, $screen.X, $screen.Y, 0x40CC0020)
    [Check]::ReleaseDC([IntPtr]::Zero, $src) | Out-Null
    $g.ReleaseHdc($dst)
    $g.Dispose()
    if (-not $ok) { $bmp.Dispose(); throw 'BitBlt failed' }
    $bmp
}

function Shot([string]$name) {
    Step "screenshot $name" {
        $bmp = Capture
        $bmp.Save((Join-Path $Out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        Say "shot $name"
    }
}

function Click([int]$x, [int]$y) {
    [Check]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 300
    [Check]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero) # left down
    Start-Sleep -Milliseconds 80
    [Check]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero) # left up
}

function Until([int]$seconds, [scriptblock]$cond) {
    $end = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $end) {
        if (& $cond) { return $true }
        Start-Sleep -Milliseconds 500
    }
    $false
}

$claudeDir = Join-Path $env:USERPROFILE '.claude'
$claudeSettings = Join-Path $claudeDir 'settings.json'
$appLog = Join-Path $env:LOCALAPPDATA 'WinIsland\log.txt'
$events = 'UserPromptSubmit', 'PreToolUse', 'PostToolUse', 'PostToolUseFailure', 'PermissionRequest', 'PermissionDenied', 'Notification', 'Stop', 'StopFailure', 'SessionEnd'
Say "claude: $((& claude --version) 2>&1)"
Say "screen $($screen.Width)x$($screen.Height), scale $scale"

# No location prompt or morning brief over the desktop; long enough for the card's answer to be clicked.
$settingsDir = Join-Path $env:APPDATA 'WinIsland'
New-Item -ItemType Directory -Force $settingsDir, $claudeDir | Out-Null
Set-Content -Path (Join-Path $settingsDir 'settings.json') -Value '{"weather_locate": false, "brief_morning": false, "claude_ask_wait_s": 120}' -Encoding ascii
# A setting of the user's own, which the install must keep.
Set-Content -Path $claudeSettings -Value '{"theme": "dark"}' -Encoding ascii

# 1. `claude-hooks install` (it says so in a message box, closed here).
function HooksCommand([string]$what) {
    $p = Start-Process -FilePath $Exe -ArgumentList 'claude-hooks', $what -PassThru
    Until 15 { [Check]::CloseBoxes([uint32]$p.Id) -gt 0 } | Out-Null
    if (-not $p.WaitForExit(10000)) { $p.Kill(); throw "claude-hooks $what did not end" }
}
Step 'claude-hooks install' {
    HooksCommand 'install'
    Copy-Item $claudeSettings (Join-Path $Out 'settings-installed.json')
    $s = Get-Content $claudeSettings -Raw | ConvertFrom-Json
    if ($s.theme -ne 'dark') { Fail 'install lost the user''s own setting' }
    foreach ($e in $events) {
        $h = @($s.hooks.$e | ForEach-Object { $_.hooks }) | Where-Object { $_.args -and $_.args[0] -eq 'claude-hook' }
        if (-not $h) { Fail "no hook for $e"; continue }
        if ($h[0].command -ne $Exe) { Fail "$e points at $($h[0].command), not $Exe" }
        $ask = $h[0].args -contains '--ask'
        if (($e -eq 'PermissionRequest') -ne $ask) { Fail "$e has --ask $ask" }
    }
    Say "hooks installed for $($events.Count) events"
}

# 2. The WinIsland and the stand-in API.
# Claude Code talks to the stand-in, the WinIsland's own `claude -p --resume` included.
$env:ANTHROPIC_BASE_URL = 'http://127.0.0.1:8765'
$env:ANTHROPIC_API_KEY = 'sk-ant-winisland-check'
$env:CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC = '1'
$away = @{ X = $screen.Width - 10; Y = [int]($screen.Height / 2) }
[Check]::SetCursorPos($away.X, $away.Y) | Out-Null
$app = Start-Process -FilePath $Exe -PassThru
$api = Start-Process -FilePath 'node' -ArgumentList (Join-Path $PSScriptRoot 'fake-anthropic.js'), '8765', (Join-Path $Out 'fake-anthropic.log') -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 15
Shot '01-idle'

# 3. One Claude Code turn, from a project folder (the pill shows its name).
$project = Join-Path $env:RUNNER_TEMP 'winisland-claude-check'
Remove-Item $project -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $project | Out-Null
$claudeOut = Join-Path $Out 'claude-output.json'
$claudeErr = Join-Path $Out 'claude-stderr.txt'
$claude = Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', "claude -p `"make the folder`" --output-format json --debug < nul > `"$claudeOut`" 2> `"$claudeErr`"" -WorkingDirectory $project -PassThru -WindowStyle Hidden
Say "claude started (pid $($claude.Id))"

# 4. The ask: the hook's waiting window, the card with "Allow", the click.
# [NullString]: a $null passed to a string parameter arrives as "" (a window with no title).
if (Until 90 { [Check]::FindWindow('WinIslandAsk', [NullString]::Value) -ne [IntPtr]::Zero }) {
    Say 'the PermissionRequest hook waits for the card'
    Start-Sleep -Seconds 2
    Shot '02-asking'
    Click ([int]($screen.Width / 2)) ([int](24 * $scale))
    Start-Sleep -Seconds 2
    Shot '03-card'
    $card = [Check]::Visible('WinIslandCardInput') | Select-Object -First 1
    Say "card window: $card"
    if ($card -match '^(-?\d+) (-?\d+) (\d+) (\d+)$') {
        $cx, $cy, $cw, $ch = [int]$Matches[1], [int]$Matches[2], [int]$Matches[3], [int]$Matches[4]
        # "Allow" is the accent chip at the card's bottom right: the lowest coloured pixels in the
        # card's right 130 DIP (the rest of the card is greys and white).
        $bmp = Capture
        $x0 = [math]::Max($cx, $cx + $cw - [int](130 * $scale))
        $found = New-Object System.Collections.Generic.List[object]
        for ($y = $cy + $ch - 1; $y -ge $cy; $y -= 2) {
            for ($x = $x0; $x -lt $cx + $cw; $x += 2) {
                $p = $bmp.GetPixel($x, $y)
                $hi = [math]::Max($p.R, [math]::Max($p.G, $p.B)); $lo = [math]::Min($p.R, [math]::Min($p.G, $p.B))
                if ($hi - $lo -gt 80) { $found.Add(@($x, $y)) }
            }
            if ($found.Count -gt 20 -and $y -lt $found[0][1] - [int](28 * $scale)) { break }
        }
        $bmp.Dispose()
        if ($found.Count -gt 0) {
            $ax = [int](($found | ForEach-Object { $_[0] } | Measure-Object -Average).Average)
            $ay = [int](($found | ForEach-Object { $_[1] } | Measure-Object -Average).Average)
            Say "clicking Allow at $ax,$ay ($($found.Count) accent pixels)"
            Click $ax $ay
        } else {
            Fail 'no accent-coloured Allow chip on the card'
        }
    } else {
        Fail 'no card window after clicking the pill'
    }
    [Check]::SetCursorPos($away.X, $away.Y) | Out-Null
} else {
    Fail 'the PermissionRequest hook never waited for the card'
}

# 5. Claude goes on with the answer; the end shows on the pill.
if (-not $claude.WaitForExit(120000)) { Fail 'claude did not finish'; Stop-Process -Id $claude.Id -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 2
Shot '04-done'
Step 'claude output' {
    $result = Get-Content $claudeOut -Raw | ConvertFrom-Json
    Say "claude result: $($result.result) (is_error $($result.is_error), denials $(@($result.permission_denials).Count))"
    if ($result.is_error) { Fail 'claude reported an error' }
    if (@($result.permission_denials).Count) { Fail "claude was denied: $($result.permission_denials | ConvertTo-Json -Compress)" }
}
if (Test-Path (Join-Path $project 'winisland-check')) { Say 'the folder Claude asked to make is there: the card''s Allow reached Claude' }
else { Fail 'the folder is not there: the card''s Allow did not reach Claude' }

# Claude's debug log names each hook it ran.
$debugLog = Get-ChildItem (Join-Path $claudeDir 'debug') -Filter *.txt -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
if ($debugLog) { Copy-Item $debugLog.FullName (Join-Path $Out 'claude-debug.txt') }

# 6. The WinIsland's side, from its log.
Start-Sleep -Seconds 3
Step 'app log' {
    Copy-Item $appLog (Join-Path $Out 'winisland-log.txt')
    $log = Get-Content $appLog -Raw
    foreach ($want in 'claude: Run', 'claude: Wait', 'claude: allow .*, delivered true', 'claude: Done') {
        if ($log -match $want) { Say "log has '$want'" } else { Fail "the log has no '$want'" }
    }
    # A `claude -p` that exits may end its SessionEnd hook before it is sent: only said (Done
    # leaves the pill by itself).
    Say "log has 'claude: Clear': $($log -match 'claude: Clear')"
}

# 7. `claude-hooks remove`: ours go, the user's setting stays.
Step 'claude-hooks remove' {
    HooksCommand 'remove'
    $s = Get-Content $claudeSettings -Raw | ConvertFrom-Json
    if ($s.theme -ne 'dark') { Fail 'remove lost the user''s own setting' }
    if ($s.hooks -and ($s.hooks | ConvertTo-Json -Depth 10) -match 'claude-hook') { Fail 'hooks of ours are left after remove' }
    Say 'hooks removed'
}

# 8. A session whose hooks never reach the WinIsland (the desktop app's case): it is followed from
# its transcript. A slow stand-in API keeps its turn going long enough to be seen.
$slow = Start-Process -FilePath 'node' -ArgumentList (Join-Path $PSScriptRoot 'fake-anthropic.js'), '8766', (Join-Path $Out 'fake-anthropic-slow.log'), '8000' -PassThru -WindowStyle Hidden
$quiet = Join-Path $env:RUNNER_TEMP 'winisland-no-hooks'
Remove-Item $quiet -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $quiet | Out-Null
Start-Sleep -Seconds 2
$second = Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', "set ANTHROPIC_BASE_URL=http://127.0.0.1:8766&& claude -p `"make the folder`" --allowedTools `"Bash(mkdir:*)`" --output-format json < nul > `"$(Join-Path $Out 'claude-no-hooks.json')`" 2>&1" -WorkingDirectory $quiet -PassThru -WindowStyle Hidden
if (Until 30 { (Get-Content $appLog -Raw) -match 'followed from its transcript' }) {
    Say 'a session without hooks shows from its transcript'
    Shot '05-no-hooks'
} else {
    Fail 'a session without hooks never showed'
}
if (-not $second.WaitForExit(90000)) { Fail 'the second claude did not finish'; Stop-Process -Id $second.Id -Force -ErrorAction SilentlyContinue }
if (Until 20 { (Get-Content $appLog -Raw) -match 'Done from its transcript' }) { Say 'its end came from the transcript too' }
else { Fail 'the session without hooks never ended on the pill' }
Stop-Process -Id $slow.Id -Force -ErrorAction SilentlyContinue

# 9. The Claude tab: at the back of the stack, the wheel brings it to the front; its card shows
# the sessions, and Compact on the first goes through `claude -p --resume`.
Start-Sleep -Seconds 6
$pillAt = @{ X = [int]($screen.Width / 2); Y = [int](24 * $scale) }
$opened = $false
foreach ($notch in -120, -120, 120, 120, 120) {
    [Check]::SetCursorPos($pillAt.X, $pillAt.Y) | Out-Null
    Start-Sleep -Milliseconds 400
    # MOUSEEVENTF_WHEEL; the delta's bits as the unsigned argument.
    [Check]::mouse_event(0x0800, 0, 0, [BitConverter]::ToUInt32([BitConverter]::GetBytes([int32]$notch), 0), [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 900
    Click $pillAt.X $pillAt.Y
    Start-Sleep -Seconds 2
    if ((Get-Content $appLog -Raw) -match 'toggle Activity\(ClaudeTab\)') { $opened = $true; break }
    [Check]::SetCursorPos($away.X, $away.Y) | Out-Null
    Start-Sleep -Seconds 2
}
if ($opened) {
    Shot '06-claude-tab'
    $card = [Check]::Visible('WinIslandCardInput') | Select-Object -First 1
    Say "Claude tab card: $card"
    if ($card -match '^(-?\d+) (-?\d+) (\d+) (\d+)$') {
        $cx, $cy, $cw = [int]$Matches[1], [int]$Matches[2], [int]$Matches[3]
        # The first row's Compact: 78 DIP wide at the row's right, 58 DIP down it (render::card).
        Click ($cx + $cw - [int](71 * $scale)) ($cy + [int]((170 + 71) * $scale))
        if (Until 60 { (Get-Content $appLog -Raw) -match 'claude: /compact to \S+ done' }) { Say 'Compact from the tab went through claude -p --resume' }
        else { Fail 'Compact from the tab never finished' }
        Shot '07-after-compact'
    } else {
        Fail 'no card window for the Claude tab'
    }
    [Check]::SetCursorPos($away.X, $away.Y) | Out-Null
} else {
    Fail 'the wheel never brought the Claude tab to the front'
}
Step 'tab log' {
    $log = Get-Content $appLog -Raw
    Say "plan limits: $([regex]::Match($log, 'claude: plan limits: [^\r\n]*').Value)"
}

Copy-Item $appLog (Join-Path $Out 'winisland-log.txt') -Force
$alive = -not $app.HasExited
Say "app still running: $alive"
if (-not $alive) { Fail "the app exited (code $($app.ExitCode))" } else { Stop-Process -Id $app.Id -Force }
Stop-Process -Id $api.Id -Force -ErrorAction SilentlyContinue

if ($failures.Count) {
    Say "$($failures.Count) failure(s)"
    exit 1
}
Say 'Claude Code works with the WinIsland'
