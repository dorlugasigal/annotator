<#
.SYNOPSIS
  Scripted toolbar drags with real input: docking at each display edge, the ghost and free placement (development builds only).
.DESCRIPTION
  Starts the docked toolbar reference state (--ux-reference <state> --placement docked), which runs the app's real toolbar
  window and placement beside a running Annotator, with an in-memory profile and no tray icon, global shortcut or saved
  setting. Then it drags the toolbar's background with SendInput on the display the toolbar is on and checks
  (WIN-OVR-10, spec 009):

  - a drag that ends away from the edges leaves the toolbar where it was dropped, inside the work area, with no ghost;
  - with the pointer 3 points inside an edge there is no ghost (no near-edge attraction);
  - with the pointer at the outermost pixel of the top, left, bottom and right edges the ghost appears, and it goes when
    the pointer moves inward; the drop docks the toolbar at exactly the ghost's frame, flush against the work area and
    centered along the edge, vertical at the left and right edges and horizontal at the top and bottom;
  - a drop three quarters along an edge still docks at its center;
  - press and drag or press and hold (ToolbarHold: a move past 3 points, or 0.4 seconds within 3 points): a tool button
    held still and let go leaves the docked toolbar where it was and never clicks; a short press on a button still clicks
    it; a button held for 0.6 seconds, or dragged at once, picks the toolbar up, it follows the pointer from where the
    press started with Windows' move cursor (the Mac's closed hand), and the button never clicks; the Width slider gets
    its press at once, so holding and sliding it changes the width and never moves the toolbar;
  - the toolbar never enters a window manager move, so no Snap layout appears (the drag is the app's own).

  Safety: every press first checks with WindowFromPoint that the point is the toolbar's own background (UI Automation
  confirms it is no control), or, for the press-and-hold steps, that UI Automation finds the named toolbar control
  there. While the button is down the script checks that the toolbar follows the pointer, so the moves and the release
  reach Annotator; if it ever stops following, the pointer goes back over the toolbar before the button is released.
  The check moves the pointer for about 20 seconds and puts it back where it was. It takes no keyboard focus. The app
  quits when the script is done or after its own timeout.

  -Shot names Annotator.RenderHarness.exe: during the top-edge drag the toolbar sits exactly under the ghost, and the
  harness saves that area (Windows.Graphics.Capture) as ghost-top.png, with the pixels outside the toolbar's rounded
  corners blanked, so no other window shows.
.EXAMPLE
  dotnet build apps/windows/src/Annotator.App/Annotator.App.csproj -c Debug -p:Platform=x64
  pwsh -NoProfile -File tools/windows/dock-check.ps1 -Out <folder outside the repository>
#>
param(
    [Parameter(Mandatory)] [string] $Out,
    [string] $App = (Join-Path $PSScriptRoot '..\..\apps\windows\src\Annotator.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Annotator.exe'),
    [string] $State = 'toolbar/full-properties-pen',
    [ValidateSet('light', 'dark')] [string] $Theme = 'dark',
    [string] $Shot = '',
    [int] $TimeoutSeconds = 90
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase, System.Drawing
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class DockInput
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; public int Width { get { return Right - Left; } } public int Height { get { return Bottom - Top; } } public override string ToString() { return string.Format("{0},{1} {2}x{3}", Left, Top, Width, Height); } }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] struct UNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public UNION u; }
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)] struct GUITHREADINFO { public int cbSize; public uint flags; public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret; public RECT rcCaret; }
    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
    [StructLayout(LayoutKind.Sequential)] struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT ptScreenPos; }
    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);

    const uint Move = 0x1, LeftDown = 0x2, LeftUp = 0x4, Absolute = 0x8000, VirtualDesk = 0x4000;

    public static void PerMonitor() { SetProcessDpiAwarenessContext(new IntPtr(-4)); }

    public static int OwnerOf(IntPtr hwnd) { uint pid; GetWindowThreadProcessId(hwnd, out pid); return (int)pid; }

    public static IntPtr TopLevelAt(int x, int y)
    {
        IntPtr hwnd = WindowFromPoint(new POINT { X = x, Y = y });
        return hwnd == IntPtr.Zero ? hwnd : GetAncestor(hwnd, 2);
    }

    public static string ClassName(IntPtr hwnd) { var b = new StringBuilder(256); GetClassName(hwnd, b, 256); return b.ToString(); }

    public static string Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "none";
        var title = new StringBuilder(256); GetWindowText(hwnd, title, 256);
        return string.Format("0x{0:X} pid {1} {2} \"{3}\"", hwnd.ToInt64(), OwnerOf(hwnd), ClassName(hwnd), title);
    }

    public static RECT Rect(IntPtr hwnd) { RECT r; GetWindowRect(hwnd, out r); return r; }

    public static double Scale(IntPtr hwnd) { return GetDpiForWindow(hwnd) / 96.0; }

    public static MONITORINFO Monitor(IntPtr hwnd)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
        GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info);
        return info;
    }

    /// <summary>The visible dock ghost of a process, or zero.</summary>
    public static IntPtr Ghost(int pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) =>
        {
            if (OwnerOf(h) == pid && IsWindowVisible(h) && ClassName(h) == "Annotator.Platform.DockPreview") { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>The top-level window that holds the mouse capture on the toolbar's thread, or zero.</summary>
    public static IntPtr Capture(IntPtr toolbar)
    {
        uint pid;
        uint thread = GetWindowThreadProcessId(toolbar, out pid);
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(GUITHREADINFO)) };
        if (!GetGUIThreadInfo(thread, ref info) || info.hwndCapture == IntPtr.Zero) return IntPtr.Zero;
        return GetAncestor(info.hwndCapture, 2);
    }

    /// <summary>True while the toolbar's thread is in a window manager move or size loop (where Snap layouts appear).</summary>
    public static bool InMoveSize(IntPtr toolbar)
    {
        uint pid;
        uint thread = GetWindowThreadProcessId(toolbar, out pid);
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(GUITHREADINFO)) };
        return GetGUIThreadInfo(thread, ref info) && ((info.flags & 0x2) != 0 || info.hwndMoveSize != IntPtr.Zero);
    }

    static INPUT Mouse(int x, int y, uint flags)
    {
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        int ax = (int)Math.Round((x - vx) * 65535.0 / (vw - 1)), ay = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
        return new INPUT { type = 0, u = new UNION { mi = new MOUSEINPUT { dx = ax, dy = ay, dwFlags = flags | Absolute | VirtualDesk } } };
    }

    static void Send(INPUT input)
    {
        if (SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT))) != 1) throw new InvalidOperationException("SendInput failed: " + Marshal.GetLastWin32Error());
    }

    public static void MoveTo(int x, int y) { Send(Mouse(x, y, Move)); }

    public static void Press(int x, int y) { Send(Mouse(x, y, LeftDown)); }

    public static void Release(int x, int y) { Send(Mouse(x, y, LeftUp)); }
    /// <summary>Presses and lets go of Escape. Only the toolbar takes it, while it moves; it never reaches another app then.</summary>
    public static void Escape() { Send(new INPUT { type = 1, u = new UNION { ki = new KEYBDINPUT { wVk = 0x1B } } }); Send(new INPUT { type = 1, u = new UNION { ki = new KEYBDINPUT { wVk = 0x1B, dwFlags = 2 } } }); }

    /// <summary>True while the cursor on screen is Windows' move cursor (IDC_SIZEALL).</summary>
    public static bool MoveCursorShows()
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf(typeof(CURSORINFO)) };
        return GetCursorInfo(ref info) && info.hCursor == LoadCursor(IntPtr.Zero, new IntPtr(32646));
    }
}
'@
[DockInput]::PerMonitor()

New-Item -ItemType Directory -Force $Out | Out-Null
$log = Join-Path $Out 'dock-check.log'
Remove-Item $log -ErrorAction SilentlyContinue
$results = [ordered]@{}
function Note([string]$text) { $line = "$(Get-Date -Format 'HH:mm:ss.fff') $text"; $line; Add-Content -Path $log -Value $line }
function Check([string]$name, [bool]$passed, [string]$detail) {
    $key = $name; $n = 2
    while ($results.Contains($key)) { $key = "$name ($n)"; $n++ }
    $results[$key] = $passed
    Note ("{0} {1}: {2}" -f $(if ($passed) { 'PASS' } else { 'FAIL' }), $key, $detail)
}
function WaitFor([scriptblock]$condition, [int]$milliseconds = 2000) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.ElapsedMilliseconds -lt $milliseconds) { if (& $condition) { return $true }; Start-Sleep -Milliseconds 30 }
    return [bool](& $condition)
}
function Near([double]$a, [double]$b, [double]$tolerance = 1) { [Math]::Abs($a - $b) -le $tolerance }
function Same($a, $b) { (Near $a.Left $b.Left) -and (Near $a.Top $b.Top) -and (Near $a.Width $b.Width) -and (Near $a.Height $b.Height) }

if (-not (Test-Path $App)) { throw "Build the development app first: $App is missing." }
$pointer = New-Object DockInput+POINT
[void][DockInput]::GetCursorPos([ref]$pointer)
Remove-Item (Join-Path $Out 'ready.json') -ErrorAction SilentlyContinue
$process = Start-Process -FilePath $App -PassThru -ArgumentList @('--ux-reference', $State, '--placement', 'docked', '--theme', $Theme, '--out', $Out, '--timeout', "$TimeoutSeconds")
Note "started $App (process $($process.Id)) with $State, docked, $Theme"
$pressed = $false
$script:moveLoop = $false
try {
    $readyPath = Join-Path $Out 'ready.json'
    if (-not (WaitFor { Test-Path $readyPath } 20000)) { throw 'The toolbar did not get ready within 20 seconds.' }
    Start-Sleep -Milliseconds 200
    $ready = Get-Content $readyPath -Raw | ConvertFrom-Json
    if ($ready.status -ne 'shown') { throw "The reference state did not show: $($ready.status) $($ready.reason)" }
    $appPid = [int]$ready.processId
    $toolbar = [IntPtr][long]$ready.hwnd
    $scale = [DockInput]::Scale($toolbar)
    $monitor = [DockInput]::Monitor($toolbar)
    $bounds = $monitor.rcMonitor
    $work = $monitor.rcWork
    Note "toolbar $([DockInput]::Describe($toolbar)) at $([DockInput]::Rect($toolbar)), scale $scale; display $bounds, work area $work"

    # A point on the toolbar's background, 6 points in from its top-left corner (inside the rounded corner and the padding).
    function Background() {
        $r = [DockInput]::Rect($toolbar)
        $inset = [int][Math]::Round(6 * $scale)
        $x = $r.Left + $inset; $y = $r.Top + $inset
        if ([DockInput]::TopLevelAt($x, $y) -ne $toolbar) { throw "Refused: the toolbar's background is not under $x,$y ($([DockInput]::Describe([DockInput]::TopLevelAt($x, $y))))." }
        $element = [Windows.Automation.AutomationElement]::FromPoint([System.Windows.Point]::new($x, $y))
        $type = $element.Current.ControlType.ProgrammaticName
        if ($type -match 'Button|Edit|Slider|ComboBox|Thumb|MenuItem|RadioButton|CheckBox') { throw "Refused: $x,$y is a control ($type '$($element.Current.Name)')." }
        @($x, $y)
    }

    # Presses the toolbar's background, moves through $path (pairs of desktop pixels, $steps moves between each) and
    # releases at the last point after running $hold. The release happens only while the toolbar follows the pointer.
    function Drag([int[][]]$path, [scriptblock]$hold = $null, [int]$steps = 12) {
        $start = Background
        $before = [DockInput]::Rect($toolbar)
        [DockInput]::MoveTo($start[0], $start[1])
        Start-Sleep -Milliseconds 60
        if ([DockInput]::TopLevelAt($start[0], $start[1]) -ne $toolbar) { throw "Refused: the toolbar left $($start[0]),$($start[1])." }
        [DockInput]::Press($start[0], $start[1])
        $script:pressed = $true
        $from = $start
        $moved = $false
        foreach ($point in $path) {
            for ($i = 1; $i -le $steps; $i++) {
                $x = [int][Math]::Round($from[0] + ($point[0] - $from[0]) * $i / $steps)
                $y = [int][Math]::Round($from[1] + ($point[1] - $from[1]) * $i / $steps)
                [DockInput]::MoveTo($x, $y)
                Start-Sleep -Milliseconds 12
                if (-not (Same ([DockInput]::Rect($toolbar)) $before)) { $moved = $true }
                if ([DockInput]::InMoveSize($toolbar)) { $script:moveLoop = $true }
            }
            $from = $point
            Start-Sleep -Milliseconds 80
        }
        $held = if ($null -ne $hold) { & $hold } else { $null }
        $last = $path[$path.Count - 1]
        # The toolbar follows the pointer when it moved during the drag or shows the ghost now.
        $moved = $moved -or -not (Same ([DockInput]::Rect($toolbar)) $before) -or $null -ne (GhostRect)
        $captured = [DockInput]::Capture($toolbar) -eq $toolbar
        if (-not ($moved -or $captured)) {
            # The toolbar is not following: release over its own background instead of over another window.
            Note "the toolbar did not follow; releasing over it (capture $([DockInput]::Describe([DockInput]::Capture($toolbar))))"
            $last = $start
            [DockInput]::MoveTo($last[0], $last[1])
            Start-Sleep -Milliseconds 60
        }
        [DockInput]::Release($last[0], $last[1])
        $script:pressed = $false
        Start-Sleep -Milliseconds 350
        [pscustomobject]@{ Start = $start; Before = $before; Followed = ($moved -or $captured); Captured = $captured; Held = $held }
    }

    function GhostRect() { $g = [DockInput]::Ghost($appPid); if ($g -eq [IntPtr]::Zero) { $null } else { [DockInput]::Rect($g) } }

    # Has the app render the toolbar as it is now (surface-N.bgra, premultiplied BGRA at the window's scale), for review.
    $script:captures = 0
    function CaptureSurface([string]$name) {
        $script:captures++
        $event = [Threading.EventWaitHandle]::OpenExisting("Local\Annotator.UXReference.$appPid.capture")
        [void]$event.Set()
        $event.Dispose()
        $report = Join-Path $Out "capture-$($script:captures).json"
        if (-not (WaitFor { Test-Path $report } 5000)) { Note "no capture of $name"; return }
        Start-Sleep -Milliseconds 100
        $capture = Get-Content $report -Raw | ConvertFrom-Json
        Move-Item -Force (Join-Path $Out "surface-$($script:captures).bgra") (Join-Path $Out "$name.bgra")
        Note "captured $name.bgra: $($capture.status) $($capture.imageSize -join ' x ') $($capture.reason)"
    }

    # Blanks the pixels outside a rounded rectangle of $radius pixels (the toolbar's corners), so only Annotator shows.
    function BlankOutside([string]$png, [double]$radius) {
        $source = [System.Drawing.Bitmap]::FromFile($png)
        $image = New-Object System.Drawing.Bitmap $source
        $source.Dispose()
        $gray = [System.Drawing.Color]::FromArgb(255, 128, 128, 128)
        $limit = [int][Math]::Ceiling($radius)
        foreach ($corner in @(@(0, 0), @(1, 0), @(0, 1), @(1, 1))) {
            for ($i = 0; $i -lt $limit; $i++) {
                for ($j = 0; $j -lt $limit; $j++) {
                    $x = if ($corner[0] -eq 0) { $i } else { $image.Width - 1 - $i }
                    $y = if ($corner[1] -eq 0) { $j } else { $image.Height - 1 - $j }
                    if ([Math]::Sqrt([Math]::Pow($radius - ($i + 0.5), 2) + [Math]::Pow($radius - ($j + 0.5), 2)) -gt $radius - 1.5) { $image.SetPixel($x, $y, $gray) }
                }
            }
        }
        $image.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
        $image.Dispose()
    }
    $midX = [int][Math]::Floor(($bounds.Left + $bounds.Right) / 2)
    $midY = [int][Math]::Floor(($bounds.Top + $bounds.Bottom) / 2)
    # Away from the middle along the top and bottom: the toolbar starts at the top, and a drag that leaves its frame as it
    # was does not dock (Mac endToolbarDrag; ToolbarDockingTests.ADragThatEndsWhereItStartedNeverDocks).
    $edges = [ordered]@{
        top = @(($midX - [int](200 * $scale)), $bounds.Top)
        left = @($bounds.Left, $midY)
        bottom = @(($midX + [int](200 * $scale)), ($bounds.Bottom - 1))
        right = @(($bounds.Right - 1), $midY)
    }
    $inward = @{ top = @(0, 1); left = @(1, 0); bottom = @(0, -1); right = @(-1, 0) }

    # 1. A free drop: the toolbar stays where it was dropped, and no ghost shows on the way.
    $r = [DockInput]::Rect($toolbar)
    $target = @([int]($midX - 300 * $scale), [int]($midY - 100 * $scale))
    $ghostSeen = $false
    $drag = Drag @(, $target) { $script:ghostSeen = $null -ne (GhostRect) }
    $after = [DockInput]::Rect($toolbar)
    $dx = $target[0] - $drag.Start[0]; $dy = $target[1] - $drag.Start[1]
    Check 'free-drop' ($drag.Followed -and -not $ghostSeen -and (Near $after.Left ($drag.Before.Left + $dx)) -and (Near $after.Top ($drag.Before.Top + $dy)) -and $after.Width -eq $drag.Before.Width -and $after.Height -eq $drag.Before.Height) `
        "dropped at $after, expected $($drag.Before.Left + $dx),$($drag.Before.Top + $dy); ghost seen $ghostSeen; capture held $($drag.Captured)"

    # 2. Three points inside the top edge: no ghost, and the toolbar stays free; the drag keeps it inside the work area.
    $near = @($midX, ($bounds.Top + [int][Math]::Ceiling(3 * $scale)))
    $drag = Drag @(, $near) { $script:ghostSeen = $null -ne (GhostRect) }
    $after = [DockInput]::Rect($toolbar)
    Check 'near-edge-no-attraction' ($drag.Followed -and -not $ghostSeen -and $after.Top -ge $work.Top -and $after.Top -le $work.Top + 1) "pointer at $($near -join ','), toolbar $after, work area $work, ghost seen $ghostSeen"

    # 3. Each edge: the ghost shows at the outermost pixel, goes inward, and the drop docks at exactly its frame.
    foreach ($edge in $edges.Keys) {
        $point = $edges[$edge]
        $step = $inward[$edge]
        $inside = @(($point[0] + $step[0] * [int][Math]::Ceiling(4 * $scale)), ($point[1] + $step[1] * [int][Math]::Ceiling(4 * $scale)))
        $script:ghost = $null; $script:ghostInward = $null; $script:shotResult = $null
        $drag = Drag @(, $point) {
            if (-not (WaitFor { $null -ne (GhostRect) } 1500)) { return 'no ghost' }
            $script:ghost = GhostRect
            if ($edge -eq 'top' -and $Shot) {
                # Put the toolbar exactly under the ghost (it keeps its offset from the pointer), then save that area.
                $now = [DockInput]::Rect($toolbar)
                [DockInput]::MoveTo($point[0] + ($script:ghost.Left - $now.Left), $point[1])
                Start-Sleep -Milliseconds 250
                $png = Join-Path $Out 'ghost-top.png'
                $shotRun = Start-Process -FilePath $Shot -PassThru -NoNewWindow -ArgumentList @('shot', '--area', ('{0},{1},{2},{3}' -f ($script:ghost.Left - $bounds.Left), ($script:ghost.Top - $bounds.Top), $script:ghost.Width, $script:ghost.Height), '--evidence', $png, '--timeout', '20')
                if (-not $shotRun.WaitForExit(25000)) { Stop-Process -Id $shotRun.Id; $script:shotResult = 'timed out' }
                else {
                    $script:shotResult = "exit $($shotRun.ExitCode), toolbar $([DockInput]::Rect($toolbar))"
                    if ($shotRun.ExitCode -eq 0) { BlankOutside $png (15 * $scale) }
                }
                [DockInput]::MoveTo($point[0], $point[1])
                Start-Sleep -Milliseconds 120
            }
            [DockInput]::MoveTo($inside[0], $inside[1])
            Start-Sleep -Milliseconds 150
            $script:ghostInward = GhostRect
            [DockInput]::MoveTo($point[0], $point[1])
            Start-Sleep -Milliseconds 150
            'ok'
        }
        $after = [DockInput]::Rect($toolbar)
        $vertical = $after.Height -gt $after.Width
        $flush = switch ($edge) {
            'top' { $after.Top -eq $work.Top } 'bottom' { $after.Bottom -eq $work.Bottom }
            'left' { $after.Left -eq $work.Left } 'right' { $after.Right -eq $work.Right }
        }
        $centered = if ($edge -in 'top', 'bottom') { Near (($after.Left + $after.Right) / 2) (($work.Left + $work.Right) / 2) $scale } else { Near (($after.Top + $after.Bottom) / 2) (($work.Top + $work.Bottom) / 2) $scale }
        Check "ghost-$edge" ($null -ne $script:ghost -and $null -eq $script:ghostInward) "ghost at the outermost pixel $($point -join ','): $($script:ghost); 4 points inward: $(if ($script:ghostInward) { $script:ghostInward } else { 'none' })"
        Check "dock-$edge" ($drag.Followed -and $null -ne $script:ghost -and (Same $after $script:ghost) -and $flush -and $centered -and ($vertical -eq ($edge -in 'left', 'right'))) `
            "docked at $after (ghost $($script:ghost)); flush $flush, centered $centered, vertical $vertical; work area $work; capture held $($drag.Captured)"
        if ($script:shotResult) { Note "ghost screenshot: $($script:shotResult)" }
        if ($edge -in 'left', 'right') { CaptureSurface "docked-$edge" }
        Check "no-ghost-after-$edge" ($null -eq (GhostRect)) 'the ghost is hidden after the drop'
    }

    # 4. A drop three quarters along the top edge docks at its center, horizontal again.
    $quarter = @([int]($bounds.Left + ($bounds.Right - $bounds.Left) * 0.75), $bounds.Top)
    $drag = Drag @(, $quarter) { if (WaitFor { $null -ne (GhostRect) } 1500) { GhostRect } else { $null } }
    $after = [DockInput]::Rect($toolbar)
    Check 'dock-top-three-quarters' ($null -ne $drag.Held -and (Same $after $drag.Held) -and (Near (($after.Left + $after.Right) / 2) (($work.Left + $work.Right) / 2) $scale) -and $after.Width -gt $after.Height) "docked at $after (ghost $($drag.Held))"

    # 5. Press and hold. The toolbar control named $name (UI Automation) and its center in desktop pixels, after checking
    # that WindowFromPoint finds the toolbar there and UI Automation finds that control (or a part of it) under the point.
    function ControlAt([string]$name, $type) {
        $condition = [Windows.Automation.AndCondition]::new(
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $name),
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, $type))
        $element = ([Windows.Automation.AutomationElement]::FromHandle($toolbar)).FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -eq $element) { throw "Refused: the toolbar has no $($type.ProgrammaticName) named '$name'." }
        $box = $element.Current.BoundingRectangle
        $x = [int][Math]::Round($box.X + $box.Width / 2); $y = [int][Math]::Round($box.Y + $box.Height / 2)
        if ([DockInput]::TopLevelAt($x, $y) -ne $toolbar) { throw "Refused: the toolbar is not under '$name' at $x,$y ($([DockInput]::Describe([DockInput]::TopLevelAt($x, $y))))." }
        $under = [Windows.Automation.AutomationElement]::FromPoint([System.Windows.Point]::new($x, $y))
        $walker = [Windows.Automation.TreeWalker]::ControlViewWalker
        for ($i = 0; $null -ne $under -and $i -lt 4 -and -not [Windows.Automation.Automation]::Compare($under, $element); $i++) { $under = $walker.GetParent($under) }
        # UI Automation's FromPoint stops at the toolbar's XAML root (also on main); the control's own frame decides then.
        $inside = $x -ge $box.X -and $x -lt $box.X + $box.Width -and $y -ge $box.Y -and $y -lt $box.Y + $box.Height
        if (($null -eq $under -or -not [Windows.Automation.Automation]::Compare($under, $element)) -and -not $inside) { throw "Refused: $x,$y is not the toolbar's '$name'." }
        [pscustomobject]@{ Element = $element; X = $x; Y = $y }
    }
    # Whether the toolbar's tool button $name shows its tool as chosen (its SelectionItem pattern).
    function Chosen([string]$name) {
        $condition = [Windows.Automation.AndCondition]::new(
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $name),
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button))
        $element = ([Windows.Automation.AutomationElement]::FromHandle($toolbar)).FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
        $item = $null
        $null -ne $element -and $element.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$item) -and $item.Current.IsSelected
    }
    function RangeValue($element) {
        $range = $null
        if ($element.TryGetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern, [ref]$range)) { $range.Current.Value } else { $null }
    }
    # Presses $control (from ControlAt), keeps the pointer still for $holdMs, moves through $path (pairs of desktop pixels,
    # 12 moves between each), runs $during and releases at the last point. When the toolbar did not follow and that point
    # is not on the toolbar, the pointer goes back to where the press started first.
    function Hold($control, [int]$holdMs, [int[][]]$path = @(), [scriptblock]$during = $null) {
        $before = [DockInput]::Rect($toolbar)
        [DockInput]::MoveTo($control.X, $control.Y)
        Start-Sleep -Milliseconds 60
        if ([DockInput]::TopLevelAt($control.X, $control.Y) -ne $toolbar) { throw "Refused: the toolbar left $($control.X),$($control.Y)." }
        [DockInput]::Press($control.X, $control.Y)
        $script:pressed = $true
        Start-Sleep -Milliseconds $holdMs
        $from = @($control.X, $control.Y)
        $moved = $false
        foreach ($point in $path) {
            for ($i = 1; $i -le 12; $i++) {
                $x = [int][Math]::Round($from[0] + ($point[0] - $from[0]) * $i / 12)
                $y = [int][Math]::Round($from[1] + ($point[1] - $from[1]) * $i / 12)
                [DockInput]::MoveTo($x, $y)
                Start-Sleep -Milliseconds 12
                if (-not (Same ([DockInput]::Rect($toolbar)) $before)) { $moved = $true }
                if ([DockInput]::InMoveSize($toolbar)) { $script:moveLoop = $true }
            }
            $from = $point
            Start-Sleep -Milliseconds 80
        }
        $seen = if ($null -ne $during) { & $during } else { $null }
        $moved = $moved -or -not (Same ([DockInput]::Rect($toolbar)) $before)
        $last = $from
        if (-not $moved -and [DockInput]::TopLevelAt($last[0], $last[1]) -ne $toolbar) {
            Note "the toolbar did not follow the hold; releasing where the press started"
            $last = @($control.X, $control.Y)
            [DockInput]::MoveTo($last[0], $last[1])
            Start-Sleep -Milliseconds 60
        }
        [DockInput]::Release($last[0], $last[1])
        $script:pressed = $false
        Start-Sleep -Milliseconds 350
        [pscustomobject]@{ Start = @($control.X, $control.Y); Before = $before; Followed = $moved; During = $seen }
    }
    $buttonType = [Windows.Automation.ControlType]::Button

    # 5a. Held still and let go: the toolbar stays docked where it was, and Rectangle never clicks (Pen stays chosen).
    $hold = Hold (ControlAt 'Rectangle' $buttonType) 700 @() { [DockInput]::MoveCursorShows() }
    $after = [DockInput]::Rect($toolbar)
    Check 'hold-still-keeps-dock' ((Same $after $hold.Before) -and $null -eq (GhostRect) -and -not (Chosen 'Rectangle') -and (Chosen 'Pen')) `
        "held Rectangle for 0.7 s and let go: toolbar $after (was $($hold.Before)); move cursor while held $($hold.During); Rectangle chosen $(Chosen 'Rectangle'), Pen chosen $(Chosen 'Pen')"

    # 5b. A short press is a click: Rectangle, then Pen again.
    [void](Hold (ControlAt 'Rectangle' $buttonType) 80)
    $clicked = WaitFor { Chosen 'Rectangle' } 1500
    [void](Hold (ControlAt 'Pen' $buttonType) 80)
    $back = WaitFor { Chosen 'Pen' } 1500
    Check 'short-press-clicks' ($clicked -and $back) "a 0.08 s press chose Rectangle: $clicked; then Pen: $back"

    # 5c. Held for 0.6 s, then moved: the toolbar follows from the press with the move cursor, and Rectangle never clicks.
    $target = @([int]($midX - 250 * $scale), [int]($midY + 50 * $scale))
    $hold = Hold (ControlAt 'Rectangle' $buttonType) 600 @(, $target) { [DockInput]::MoveCursorShows() }
    $after = [DockInput]::Rect($toolbar)
    $dx = $target[0] - $hold.Start[0]; $dy = $target[1] - $hold.Start[1]
    Check 'hold-moves-toolbar' ($hold.Followed -and (Near $after.Left ($hold.Before.Left + $dx)) -and (Near $after.Top ($hold.Before.Top + $dy)) -and $null -eq (GhostRect)) `
        "held Rectangle for 0.6 s and moved to $($target -join ','): toolbar $after, expected $($hold.Before.Left + $dx),$($hold.Before.Top + $dy)"
    Check 'hold-move-cursor' ([bool]$hold.During) "Windows' move cursor showed while the toolbar moved: $($hold.During)"
    Check 'hold-never-clicks' (-not (Chosen 'Rectangle') -and (Chosen 'Pen')) "Rectangle chosen $(Chosen 'Rectangle'), Pen chosen $(Chosen 'Pen')"

    # 5e. Dragged at once, without a hold: the toolbar follows from the press too, and Rectangle never clicks.
    $target = @([int]($midX - 100 * $scale), [int]($midY + 120 * $scale))
    $hold = Hold (ControlAt 'Rectangle' $buttonType) 30 @(, $target) { [DockInput]::MoveCursorShows() }
    $after = [DockInput]::Rect($toolbar)
    $dx = $target[0] - $hold.Start[0]; $dy = $target[1] - $hold.Start[1]
    Check 'drag-from-button-moves-toolbar' ($hold.Followed -and (Near $after.Left ($hold.Before.Left + $dx)) -and (Near $after.Top ($hold.Before.Top + $dy)) -and -not (Chosen 'Rectangle') -and (Chosen 'Pen')) `
        "dragged Rectangle at once to $($target -join ','): toolbar $after, expected $($hold.Before.Left + $dx),$($hold.Before.Top + $dy); Rectangle chosen $(Chosen 'Rectangle')"

    # 5d. The Width slider gets its press at once: held for 0.6 s and slid 30 points, it changes and the toolbar stays.
    $slider = ControlAt 'Width' ([Windows.Automation.ControlType]::Slider)
    $width = RangeValue $slider.Element
    $hold = Hold $slider 600 @(, @(($slider.X + [int][Math]::Round(30 * $scale)), $slider.Y)) { RangeValue $slider.Element }
    $after = [DockInput]::Rect($toolbar)
    Check 'hold-slider-keeps-press' ((Same $after $hold.Before) -and $null -ne $width -and $null -ne $hold.During -and $hold.During -ne $width) `
        "held the Width slider for 0.6 s and slid 30 points: width $width, then $($hold.During); toolbar $after (was $($hold.Before))"

    # 5f. Escape during a move (Mac cancelToolbarDrag): the toolbar stays where Escape left it, letting go elsewhere doesn't
    # move it, and the held button never clicks. Escape is taken by the toolbar only while it moves.
    $first = @([int]($midX - 150 * $scale), [int]($midY + 30 * $scale))
    $second = @([int]($midX + 150 * $scale), [int]($midY + 120 * $scale))
    $control = ControlAt 'Rectangle' $buttonType
    $before = [DockInput]::Rect($toolbar)
    [DockInput]::MoveTo($control.X, $control.Y); Start-Sleep -Milliseconds 60
    [DockInput]::Press($control.X, $control.Y); $script:pressed = $true
    Start-Sleep -Milliseconds 600
    for ($i = 1; $i -le 12; $i++) { [DockInput]::MoveTo([int]($control.X + ($first[0] - $control.X) * $i / 12), [int]($control.Y + ($first[1] - $control.Y) * $i / 12)); Start-Sleep -Milliseconds 12 }
    Start-Sleep -Milliseconds 120
    $moved = [DockInput]::Rect($toolbar)
    [DockInput]::Escape()
    Start-Sleep -Milliseconds 200
    for ($i = 1; $i -le 12; $i++) { [DockInput]::MoveTo([int]($first[0] + ($second[0] - $first[0]) * $i / 12), [int]($first[1] + ($second[1] - $first[1]) * $i / 12)); Start-Sleep -Milliseconds 12 }
    $last = if ([DockInput]::TopLevelAt($second[0], $second[1]) -eq $toolbar) { $second } else { @([int](($moved.Left + $moved.Right) / 2), [int]($moved.Top + 10)) }
    [DockInput]::MoveTo($last[0], $last[1]); Start-Sleep -Milliseconds 60
    [DockInput]::Release($last[0], $last[1]); $script:pressed = $false
    Start-Sleep -Milliseconds 350
    $after = [DockInput]::Rect($toolbar)
    Check 'hold-escape-stops-move' (-not (Same $moved $before) -and (Same $after $moved) -and $null -eq (GhostRect) -and -not (Chosen 'Rectangle')) `
        "moved from $before to $moved, Escape, then the pointer went on to $($second -join ','): toolbar $after; Rectangle chosen $(Chosen 'Rectangle')"
    # The drag never enters a window manager move or size loop, so Windows offers no Snap layout.
    Check 'no-system-move-loop' (-not $script:moveLoop) "the toolbar's thread was in a move or size loop at some step: $($script:moveLoop)"
}
catch {
    Note "ERROR: $($_.Exception.Message) $($_.InvocationInfo.PositionMessage)"
    $results['completed'] = $false
}
finally {
    if ($pressed) {
        # Never leave the button down: release it over the toolbar.
        $r = [DockInput]::Rect($toolbar)
        [DockInput]::MoveTo($r.Left + [int](6 * $scale), $r.Top + [int](6 * $scale))
        [DockInput]::Release($r.Left + [int](6 * $scale), $r.Top + [int](6 * $scale))
    }
    [void][DockInput]::SetCursorPos($pointer.X, $pointer.Y)
    try {
        $done = [Threading.EventWaitHandle]::OpenExisting("Local\Annotator.UXReference.$($process.Id)")
        [void]$done.Set()
        $done.Dispose()
    }
    catch { Note "the done event was not open: $($_.Exception.Message)" }
    if (-not $process.WaitForExit(10000)) {
        Note "process $($process.Id) did not quit; stopping it"
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        [void]$process.WaitForExit(5000)
    }
}
$failed = @($results.Keys | Where-Object { -not $results[$_] })
$summary = [ordered]@{ passed = $results.Count - $failed.Count; failed = $failed.Count; checks = $results }
$summary | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $Out 'dock-check.json')
Note ("{0} passed, {1} failed{2}" -f $summary.passed, $summary.failed, $(if ($failed.Count) { ': ' + ($failed -join ', ') } else { '' }))
if ($failed.Count) { exit 1 }
