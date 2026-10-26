# Starts WinIsland with a one-minute demo timer, photographs the screen, clicks the pill at the top
# centre to open its card, photographs again, and saves the app's log next to the pictures.
#
#   powershell -ExecutionPolicy Bypass -File tools\smoke.ps1 [-Exe target\quick\winisland.exe] [-Out shots]
param([string]$Exe = 'target\quick\winisland.exe', [string]$Out = 'shots')
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Smoke {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("gdi32.dll")] public static extern int GetDeviceCaps(IntPtr hdc, int index);
}
"@
[Smoke]::SetProcessDPIAware() | Out-Null
$dpi = [Smoke]::GetDeviceCaps([Smoke]::GetDC([IntPtr]::Zero), 88) # LOGPIXELSX
$scale = $dpi / 96.0
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
New-Item -ItemType Directory -Force $Out | Out-Null
"screen $($screen.Width)x$($screen.Height) at $dpi dpi" | Tee-Object (Join-Path $Out 'info.txt')

function Shot([string]$name) {
    $bmp = New-Object System.Drawing.Bitmap $screen.Width, $screen.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    # CaptureBlt: layered windows (the pill and the card) are in the picture too.
    $op = [System.Drawing.CopyPixelOperation]::SourceCopy -bor [System.Drawing.CopyPixelOperation]::CaptureBlt
    $g.CopyFromScreen($screen.Location, [System.Drawing.Point]::Empty, $screen.Size, $op)
    $bmp.Save((Join-Path $Out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $bmp.Dispose()
    "shot $name"
}

function Click([int]$x, [int]$y) {
    [Smoke]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 300
    [Smoke]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero) # left down
    Start-Sleep -Milliseconds 80
    [Smoke]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero) # left up
}

# Away from the pill, so nothing is hovered while it starts.
[Smoke]::SetCursorPos($screen.Width - 10, $screen.Height / 2) | Out-Null
$app = Start-Process -FilePath $Exe -ArgumentList '--demo' -PassThru
Start-Sleep -Seconds 12
Shot '01-started'
Start-Sleep -Seconds 22
Shot '02-pill'

# The pill's centre: the middle of the screen, 24 DIP down (a 48 DIP strip at the top).
Click ($screen.Width / 2) ([int](24 * $scale))
Start-Sleep -Seconds 2
Shot '03-card-open'

# Away again: the card closes by itself.
[Smoke]::SetCursorPos($screen.Width - 10, $screen.Height / 2) | Out-Null
Start-Sleep -Seconds 4
Shot '04-card-closed'

$alive = -not $app.HasExited
"app still running: $alive" | Tee-Object -Append (Join-Path $Out 'info.txt')
$log = Join-Path $env:LOCALAPPDATA 'WinIsland\log.txt'
if (Test-Path $log) {
    Copy-Item $log (Join-Path $Out 'log.txt')
    Get-Content $log
}
if ($alive) { Stop-Process -Id $app.Id -Force }
if (-not $alive) { throw 'WinIsland exited during the smoke test' }
