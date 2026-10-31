# Starts WinIsland with a one-minute demo timer, checks that the pill's window sits at the top centre
# of the screen, photographs it, clicks the pill to open its card (its top level with the pill's),
# then hides the pill from its right-click menu. Everything it finds goes into the output folder:
# screenshots, windows.txt, report.txt and the app's log.
#
#   powershell -ExecutionPolicy Bypass -File tools\smoke.ps1 [-Exe target\quick\winisland.exe] [-Out shots]
param([string]$Exe = 'target\quick\winisland.exe', [string]$Out = 'shots')
$ErrorActionPreference = 'Stop'
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
public static class Smoke {
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
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder s, int n);
    // Windows' "Animation effects": SPI_GETCLIENTAREAANIMATION (0x1042), and SPI_SETCLIENTAREAANIMATION
    // (0x1043) written to the profile and broadcast (SPIF_UPDATEINIFILE | SPIF_SENDCHANGE: the change
    // is only broadcast once it is written).
    [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint flags);
    [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint flags);
    public static bool AnimationsOn() { int on = 1; SystemParametersInfo(0x1042, 0, ref on, 0); return on != 0; }
    public static bool Animations(bool on) { return SystemParametersInfo(0x1043, 0, on ? (IntPtr)1 : IntPtr.Zero, 3); }

    // "class visible x y w h exstyle" for each top-level window of process `pid`.
    public static List<string> Windows(uint pid) {
        var rows = new List<string>();
        EnumWindows((h, l) => {
            uint owner;
            GetWindowThreadProcessId(h, out owner);
            if (owner == pid) {
                var cls = new StringBuilder(256);
                GetClassName(h, cls, 256);
                RECT r;
                GetWindowRect(h, out r);
                rows.Add(String.Format("{0} {1} {2} {3} {4} {5} 0x{6:X8}", cls, IsWindowVisible(h) ? "visible" : "hidden",
                    r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, GetWindowLong(h, -20)));
            }
            return true;
        }, IntPtr.Zero);
        return rows;
    }
}
"@
[Smoke]::SetProcessDPIAware() | Out-Null
$dpi = [Smoke]::GetDeviceCaps([Smoke]::GetDC([IntPtr]::Zero), 88) # LOGPIXELSX
$scale = $dpi / 96.0
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
Say "screen $($screen.Width)x$($screen.Height) at $dpi dpi"
Say "taskbar present: $([Smoke]::FindWindow('Shell_TrayWnd', $null) -ne [IntPtr]::Zero)"

$failures = New-Object System.Collections.Generic.List[string]
function Step([string]$label, [scriptblock]$body) {
    try { & $body } catch { $failures.Add("$label : $_"); Say "FAILED $label : $_" }
}

function Shot([string]$name) {
    Step "screenshot $name" {
        $bmp = New-Object System.Drawing.Bitmap $screen.Width, $screen.Height
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $dst = $g.GetHdc()
        $src = [Smoke]::GetDC([IntPtr]::Zero)
        # SRCCOPY | CAPTUREBLT: layered windows (the pill and the card) are in the picture too.
        $ok = [Smoke]::BitBlt($dst, 0, 0, $screen.Width, $screen.Height, $src, $screen.X, $screen.Y, 0x40CC0020)
        [Smoke]::ReleaseDC([IntPtr]::Zero, $src) | Out-Null
        $g.ReleaseHdc($dst)
        if (-not $ok) { throw 'BitBlt failed' }
        $bmp.Save((Join-Path $Out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose()
        $bmp.Dispose()
        Say "shot $name"
    }
}

function Click([int]$x, [int]$y) {
    [Smoke]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 300
    [Smoke]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero) # left down
    Start-Sleep -Milliseconds 80
    [Smoke]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero) # left up
}

function Windows([string]$when) {
    Step "windows $when" {
        Add-Content -Path (Join-Path $Out 'windows.txt') -Value "== $when" -Encoding utf8
        $rows = [Smoke]::Windows([uint32]$app.Id)
        Add-Content -Path (Join-Path $Out 'windows.txt') -Value $rows -Encoding utf8
        $rows
    }
}

# No location prompt over the desktop: the weather doesn't ask Windows where the PC is.
$settingsDir = Join-Path $env:APPDATA 'WinIsland'
New-Item -ItemType Directory -Force $settingsDir | Out-Null
Set-Content -Path (Join-Path $settingsDir 'settings.json') -Value '{"weather_locate": false}' -Encoding ascii

# A shelf kept by an earlier run: one file that is there and one that is gone. The one there
# comes back; the gone one drops off.
$kept = Join-Path $Out 'kept-on-the-shelf.txt'
Set-Content -Path $kept -Value 'shelf' -Encoding ascii
$localDir = Join-Path $env:LOCALAPPDATA 'WinIsland'
New-Item -ItemType Directory -Force $localDir | Out-Null
$shelfJson = @{ items = @(@{ path = $kept; dir = $false }, @{ path = (Join-Path $Out 'gone.txt'); dir = $false }) } | ConvertTo-Json -Depth 4
Set-Content -Path (Join-Path $localDir 'shelf.json') -Value $shelfJson -Encoding utf8

$away = @{ X = $screen.Width - 10; Y = [int]($screen.Height / 2) }
[Smoke]::SetCursorPos($away.X, $away.Y) | Out-Null
$app = $null
Step 'start' { $script:app = Start-Process -FilePath (Resolve-Path $Exe).Path -ArgumentList '--demo' -PassThru }
if ($app) {
    Start-Sleep -Seconds 12
    Shot '01-started'
    Start-Sleep -Seconds 22
    Shot '02-pill'
    $rows = Windows 'pill'

    # The pill: a visible window of ours whose input part is centred at the top of the screen.
    $pillCenterY = [int](24 * $scale)
    $pill = $rows | Where-Object { $_ -match 'visible (-?\d+) (-?\d+) (\d+) (\d+)' -and [int]$Matches[2] -lt $pillCenterY -and [int]$Matches[2] + [int]$Matches[4] -gt $pillCenterY -and [int]$Matches[3] -lt $screen.Width } | Select-Object -First 1
    if ($pill -and $pill -match 'visible (-?\d+) (-?\d+) (\d+) (\d+)') {
        $centre = [int]$Matches[1] + [int]$Matches[3] / 2
        Say "pill window: $pill (centre x $centre, screen centre $($screen.Width / 2))"
        if ([math]::Abs($centre - $screen.Width / 2) -gt 4) { $failures.Add("the pill is not centred: $pill") }
    } else {
        $failures.Add('no visible pill window at the top of the screen')
        Say 'no visible pill window at the top of the screen'
    }

    Click ([int]($screen.Width / 2)) $pillCenterY
    Start-Sleep -Seconds 2
    Shot '03-card-open'
    $open = Windows 'card open'
    $card = $open | Where-Object { $_ -match '^WinIslandCardInput visible (-?\d+) (-?\d+)' } | Select-Object -First 1
    if (-not $card) {
        $failures.Add('no card window after clicking the pill')
        Say 'no card window after clicking the pill'
    } elseif ($pill -and $pill -match 'visible (-?\d+) (-?\d+)') {
        # The big island opens as far from the top of the screen as the small one.
        $pillTop = [int]$Matches[2]
        $null = $card -match 'visible (-?\d+) (-?\d+)'
        Say "card window: $card (top $($Matches[2]), pill top $pillTop)"
        if ([math]::Abs([int]$Matches[2] - $pillTop) -gt 2) { $failures.Add("the card's top ($($Matches[2])) is not the pill's ($pillTop)") }
    }

    [Smoke]::SetCursorPos($away.X, $away.Y) | Out-Null
    Start-Sleep -Seconds 4
    Shot '04-card-closed'

    # Right-click: the pill's menu, then its first item (hide for a minute).
    [Smoke]::SetCursorPos([int]($screen.Width / 2), $pillCenterY) | Out-Null
    Start-Sleep -Milliseconds 300
    [Smoke]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero) # right down
    Start-Sleep -Milliseconds 80
    [Smoke]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero) # right up
    Start-Sleep -Seconds 1
    Shot '05-pill-menu'
    [System.Windows.Forms.SendKeys]::SendWait('{DOWN}{ENTER}')
    [Smoke]::SetCursorPos($away.X, $away.Y) | Out-Null
    Start-Sleep -Seconds 2
    Shot '06-hidden'
    $hidden = Windows 'hidden for a minute'
    if ($hidden | Where-Object { $_ -match 'visible (-?\d+) (-?\d+) (\d+) (\d+)' -and [int]$Matches[2] -lt $pillCenterY -and [int]$Matches[2] + [int]$Matches[4] -gt $pillCenterY }) {
        $failures.Add('the pill is still there after "Hide for 1 minute"')
        Say 'the pill is still there after "Hide for 1 minute"'
    } else {
        Say 'pill hidden from its menu'
    }

    # Claude Code's status line: the JSON in, the short line out (and the numbers to the app).
    Step 'claude-status' {
        $status = '{"session_id":"smoke","model":{"display_name":"Opus 5.5"},"context_window":{"used_percentage":34.4},"rate_limits":{"five_hour":{"used_percentage":23.5,"resets_at":4102444800},"seven_day":{"used_percentage":41.2,"resets_at":4102444800}}}'
        # With pipes of its own, as Claude Code runs it (PowerShell's own piping doesn't wait for a
        # windowed exe's output).
        $psi = New-Object System.Diagnostics.ProcessStartInfo (Resolve-Path $Exe).Path, 'claude-status'
        $psi.UseShellExecute = $false
        $psi.RedirectStandardInput = $true
        $psi.RedirectStandardOutput = $true
        $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
        $p = [System.Diagnostics.Process]::Start($psi)
        $p.StandardInput.Write($status)
        $p.StandardInput.Close()
        $line = $p.StandardOutput.ReadToEnd().Trim()
        $null = $p.WaitForExit(10000)
        Say "claude-status printed: $line (exit $($p.ExitCode))"
        if ($line -notmatch 'context 34%' -or $line -notmatch '5h 24%' -or $line -notmatch 'week 41%') { throw "unexpected status line: $line" }
        Start-Sleep -Seconds 1
        $log = Get-Content (Join-Path $env:LOCALAPPDATA 'WinIsland\log.txt') -Raw
        if ($log -notmatch 'claude: usage from smoke') { throw 'the app got no usage from the status line' }
        Say 'the app got the usage'
    }

    Step 'the shelf kept from the last run' {
        $log = Get-Content (Join-Path $env:LOCALAPPDATA 'WinIsland\log.txt') -Raw
        if ($log -notmatch 'shelf: 1 kept from the last run \(some files are gone\)') { throw 'the kept shelf did not come back as one file' }
        $now = Get-Content (Join-Path $localDir 'shelf.json') -Raw | ConvertFrom-Json
        if (@($now.items).Count -ne 1) { throw "the kept list still has $(@($now.items).Count) items" }
        Say 'the shelf came back from the last run, the gone file dropped'
    }

    # Windows' "Animation effects" on, then off: the WinIsland follows each change at once (a
    # runner may start with them off). The runner's own setting is put back after.
    Step 'reduced motion' {
        $appLog = Join-Path $env:LOCALAPPDATA 'WinIsland\log.txt'
        $was = [Smoke]::AnimationsOn()
        Say "Windows animation effects at the start: $was"
        $count = { param($what) ([regex]::Matches((Get-Content $appLog -Raw), "motion: $what")).Count }
        try {
            $full = & $count 'full'
            if (-not [Smoke]::Animations($true)) { throw 'could not turn Windows animation effects on' }
            Start-Sleep -Seconds 2
            if ($was -eq $false -and (& $count 'full') -le $full) { throw 'the app did not follow animation effects turned on' }
            $reduced = & $count 'reduced'
            if (-not [Smoke]::Animations($false)) { throw 'could not turn Windows animation effects off' }
            Start-Sleep -Seconds 2
            if ((& $count 'reduced') -le $reduced) { throw 'the app did not reduce motion' }
            Shot '09-reduced-motion'
        } finally {
            [Smoke]::Animations($was) | Out-Null
        }
        Say 'motion follows Windows animation effects'
    }

    $alive = -not $app.HasExited
    Say "app still running: $alive"
    if (-not $alive) { $failures.Add("the app exited (code $($app.ExitCode))") }
    if ($alive) { Stop-Process -Id $app.Id -Force }
}

$log = Join-Path $env:LOCALAPPDATA 'WinIsland\log.txt'
if (Test-Path $log) { Copy-Item $log (Join-Path $Out 'log.txt') } else { Say 'no app log' }

if ($failures.Count) {
    Say "SMOKE TEST FAILED ($($failures.Count)):"
    $failures | ForEach-Object { Say " - $_" }
    exit 1
}
Say 'SMOKE TEST PASSED'
