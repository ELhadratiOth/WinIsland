# Starts WinIsland with a one-minute demo timer, checks that the pill's window sits at the top centre
# of the screen, photographs it, clicks the pill to open its card and photographs again. Everything
# it finds goes into the output folder: screenshots, windows.txt, report.txt and the app's log.
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
function Step([string]$name, [scriptblock]$body) {
    try { & $body } catch { $failures.Add("$name : $_"); Say "FAILED $name : $_" }
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
    if (-not ($open | Where-Object { $_ -match '^WinIslandCardInput visible' })) {
        $failures.Add('no card window after clicking the pill')
        Say 'no card window after clicking the pill'
    }

    [Smoke]::SetCursorPos($away.X, $away.Y) | Out-Null
    Start-Sleep -Seconds 4
    Shot '04-card-closed'

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
