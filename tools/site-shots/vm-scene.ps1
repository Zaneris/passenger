# The Windows VM's half of vm-shots.sh, one step at a time, each run by prlctl exec, its words in Step.log beside it:
#   prepare  the page's folder under Root, and Node.js from the share's zip for the HTML server
#   state    a run's Passenger state: the theme, that Node.js, the HTML preview on, What's New of Version shown, and its
#            folder's layout
#   ready    Passenger's window placed at Width x Height pixels as it shows, the pointer beside it; its visible bounds
#            written to bounds.txt
#   capture  a picture of the window as the desktop composes it, into the share's frames folder
#   stop     every Passenger of this sitting ended
param(
    [Parameter(Mandatory)] [string] $Step,
    [string] $Share = "C:\Mac\Home\Documents\passenger-vm\site-shots",
    [string] $Root = "$env:USERPROFILE\site-shots",
    [string] $Scene, [string] $Theme, [string] $Folder, [string] $Version, [string] $Node,
    [int] $Width = 2352, [int] $Height = 1470, [int] $Limit = 300)
$ErrorActionPreference = "Stop"
$log = Join-Path $Share "$Step.log"
[IO.File]::WriteAllText($log, "")
function Say([string] $line) { [IO.File]::AppendAllText($log, "$line`r`n") }
function WriteJson([string] $path, $value) {
    New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
    [IO.File]::WriteAllText($path, ($value | ConvertTo-Json -Depth 6))
}
$state = Join-Path $Root "state-$Scene-$Theme"
# This sitting's Passengers alone, by the folders they were started on: other runs may share the VM.
function Mine { Get-CimInstance Win32_Process -Filter "Name='Passenger.exe'" | Where-Object { $_.CommandLine -like "*$Root*" } }

function Placed {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Placing {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect rect, int size);
}
"@
    [Placing]::SetProcessDpiAwarenessContext([IntPtr]::new(-4)) | Out-Null
}

# Passenger over everything, the console this step runs in among it, which would cover the window.
function OnTop {
    $ide = Mine | ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue } | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    [Placing]::SetWindowPos($ide.MainWindowHandle, [IntPtr]::new(-1), 0, 0, 0, 0, 0x0003) | Out-Null
    Start-Sleep -Milliseconds 800
}

try {
    switch ($Step) {
        "prepare" {
            $site = Join-Path $Root "GreenhouseSite"
            Remove-Item -Recurse -Force $site -ErrorAction SilentlyContinue
            New-Item -ItemType Directory -Force -Path $site | Out-Null
            Copy-Item -Force (Join-Path $Share "index.html") (Join-Path $site "index.html")
            git -C $site init -q -b main
            git -C $site add -- .
            git -C $site -c user.name="Rosa Field" -c user.email="rosa@greenhouse.example" commit -q -m "The greenhouse's page"
            if (-not (Test-Path (Join-Path $Root "node\$Node\node.exe"))) {
                Expand-Archive -Force (Join-Path $Share "node\$Node.zip") (Join-Path $Root "node")
            }
            Say "prepared"
        }
        "state" {
            Remove-Item -Recurse -Force $state -ErrorAction SilentlyContinue
            WriteJson (Join-Path $state "settings.json") @{ version = 3; theme = (Get-Culture).TextInfo.ToTitleCase($Theme); look = "Passenger"
                nodeInterpreter = (Join-Path $Root "node\$Node\node.exe") }
            WriteJson (Join-Path $state "previews.json") @{ version = 1; markdown = $true; svg = $true; html = $true; xaml = $false }
            WriteJson (Join-Path $state "updates.json") @{ version = 1; notesShown = $Version }
            $digest = [Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($Folder))
            $hash = (-join ($digest | ForEach-Object { $_.ToString("x2") })).Substring(0, 12)
            $workspace = Join-Path $state "workspaces\$(Split-Path $Folder -Leaf)-$hash"
            WriteJson (Join-Path $workspace "layout.json") @{ version = 1; windows = @{ explorer = @{ anchor = "LeftTop"; order = 0; shown = $false } } }
            Say "state $state"
        }
        "ready" {
            Placed
            $window = [IntPtr]::Zero
            for ($i = 0; $i -lt $Limit; $i++) {
                $ide = Mine | ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue } | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
                if ($ide) { $window = $ide.MainWindowHandle; break }
                Start-Sleep -Seconds 1
            }
            if ($window -eq [IntPtr]::Zero) { throw "Passenger showed no window within $Limit s" }
            [Placing]::ShowWindow($window, 9) | Out-Null
            [Placing]::SetWindowPos($window, [IntPtr]::Zero, 0, 0, $Width, $Height, 0x0044) | Out-Null
            [Placing]::SetForegroundWindow($window) | Out-Null
            # The pointer on the desktop beside the window, where no hover of the window's shows.
            [Placing]::SetCursorPos($Width + 200, [int]($Height / 2)) | Out-Null
            Start-Sleep -Seconds 4
            $bounds = New-Object Placing+Rect
            [Placing]::DwmGetWindowAttribute($window, 9, [ref] $bounds, 16) | Out-Null
            [IO.File]::WriteAllText((Join-Path $Share "bounds.txt"), "$($bounds.Left) $($bounds.Top) $($bounds.Right - $bounds.Left) $($bounds.Bottom - $bounds.Top)")
            Say "ready at $($bounds.Left),$($bounds.Top) $($bounds.Right - $bounds.Left)x$($bounds.Bottom - $bounds.Top)"
        }
        "capture" {
            Placed
            OnTop
            Add-Type -AssemblyName System.Drawing
            $x, $y, $w, $h = ([IO.File]::ReadAllText((Join-Path $Share "bounds.txt"))) -split " " | ForEach-Object { [int]$_ }
            $into = Join-Path $Share "frames"
            New-Item -ItemType Directory -Force -Path $into | Out-Null
            $picture = New-Object Drawing.Bitmap $w, $h
            $drawing = [Drawing.Graphics]::FromImage($picture)
            $drawing.CopyFromScreen($x, $y, 0, 0, $picture.Size)
            $picture.Save((Join-Path $into "$Scene-$Theme.png"), [Drawing.Imaging.ImageFormat]::Png)
            $drawing.Dispose()
            $picture.Dispose()
            Say "took $Scene-$Theme at $x,$y ${w}x$h"
        }
        "stop" {
            Mine | ForEach-Object { cmd /c "taskkill /T /F /PID $($_.ProcessId) >nul 2>&1" }
            Start-Sleep -Seconds 2
            Say "left: $(@(Mine).Count) Passenger"
        }
    }
} catch {
    Say "failed: $_"
    exit 1
}
