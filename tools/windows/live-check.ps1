<#
.SYNOPSIS
  Live check of the Windows app with real input, beside a running Annotator (development builds only).
.DESCRIPTION
  Starts a development build with --live-check: the app's real toolbar, drawing canvas, color popover and workspace on
  one made-up display covering a band at the top of the primary display, with no tray icon, global shortcut or saved
  setting. Then it clicks, drags and types only into that app's own windows (each mouse event first checks
  WindowFromPoint; keys need the app's window in the foreground) and checks:

  - toolbar-hit-test: WindowFromPoint returns the toolbar at every enabled toolbar button, after Draw activates the
    canvas, after a stroke, after the color popover closed with Done after a choice and with Escape, while a tooltip
    shows, in Use apps and after Resume drawing (the owner's "sometimes i cant press the fill, or any button on the tool
    bar"); as on the Mac, the popover stays open after a choice;
  - toolbar buttons and the properties row's Fill color take clicks in those states, after hovering a button, and after a
    press on a button that was released elsewhere; a tooltip opens outside the toolbar;
  - the shape drawn is selected and the tool becomes Select, as on the Mac (canvas.png shows its selection chrome);
  - the keyboard (WIN-A11Y-02, WIN-A11Y-04): Shift+F10 on the canvas opens its context menu, and Up and Enter choose its
    last item, Focus drawing tools, which gives the toolbar the keyboard; Tab moves to the next control, Escape gives
    the keyboard back to the canvas while drawing goes on, and Escape there releases input (Use apps);
  - text: the Text tool opens the editor at a click; typing (Unicode input, whatever the keyboard layout), Backspace and
    Ctrl+Z edit the text, UI Automation reads it as the focused edit field, its caret blinks at Windows' rate, and
    Escape commits it while drawing goes on;
  - Tools (WIN-UI-05): Ctrl+L on the canvas opens the Tools window with the keyboard on the chosen section, and Right
    chooses the next one; My shapes saves the selected shape under a typed name and inserts a copy, which hides the
    window; the toolbar's Tool library button opens it again; Edit, Rename... and Delete... rename and delete the saved
    shape (an in-memory library, never the person's shapes); a tile chooses its tool, and Escape hides the window,
    each giving the keyboard back to the canvas;
  - Spotlight menu: the toolbar's menu, added to the live check's toolbar, opens with Spotlight, Cursor halo, Clear
    drawings (keep effects) and Remove all censors available; Remove all censors, with no effect on the canvas, closes the
    menu and changes nothing;
  - Pointer effects: Cursor halo and Spotlight turn on from that menu and show Stop buttons in the toolbar; over the
    canvas the screen shows the yellow ring at the pointer and Spotlight dims the canvas away from it; the Stop buttons
    turn them off; the result shows the raw input watcher's activity and clicks, and both effects off at the end;
  - live zoom: the Zoom menu turns on the pointer lens (its window shows) and full-screen zoom (the strip's level shows,
    and Increase changes 2.0x to 2.5x); during zoom Cursor halo and Spotlight turn on, the pointer watcher starts again
    and the ring shows at the pointer, then their Stop buttons turn them off; the strip's Stop buttons end zoom and
    the lens. No hook is installed unless -ZoomMeasure asks for the zoom measurements;
  - screen access (WIN-CAP-04): a development build needs no Windows consent, so the lens starts without the explanation
    a packaged build shows before Windows asks, and Settings > Permissions shows capture as working or allowed with Open
    screen recording settings (the packaged flow needs a registered package; see the README's screen access notes);
  - Settings (WIN-UI-03): Ctrl+, on the canvas opens Settings on General with the keyboard and the app icon in its title
    bar; Share style between tools and
    the theme (Dark, then System) change the in-memory profile; Share usage counts is off and unavailable with the
    Windows words (WIN-PRV-01); the About and Permissions tabs show; About's Check for updates (WIN-SYS-03) asks before
    installing while the canvas has drawings, and Not now and Install update end as the live check's fixed Store answers
    (it never calls the Microsoft Store); the Pointer tab's
    Show halo turns the cursor halo on and off; after the Toolbar & Shortcuts steps Alt+F4 hides the window and the
    toolbar's gear opens it again (the live check never opens a file dialog, link or Windows Settings);
  - Shift: a line and an arrow snap to 45 degrees, a rectangle becomes a square and a highlighter stroke is straight;
  - Toolbar & Shortcuts (spec 005, ADR 0012), on its tab in the Settings window: J becomes Pen's key, 1 moves from Select
    to Pen and Undo gives it back, and Delete removes a key; dragging Highlighter to More tools takes it out of the
    toolbar, and Reset brings the default tools and keys back; Show / hide records
    Ctrl+Alt+Shift+9 (held by the check's own registrar until it quits) and recording Draw then refuses it through the
    recording gate; the canvas uses the new key. The check never presses a default global shortcut;
  - on-screen tip (spec 009): right after Resume drawing wakes it, the canvas reads the tip as static text
    ('Drawing  ·  Escape: use apps') with its Turn off tips button, and an ink key leaves it as it was (no status line, as
    on the Mac);
  - first use (WIN-UI-04): after the script sets the first-use event, the tutorial's welcome card shows as at a launch
    without taking the keyboard, with Share usage counts off and unavailable (WIN-PRV-01); Start tutorial turns drawing on
    and starts the coach, which never takes the keyboard; a tool key, a drag and an ink key each finish a step while the
    click-through tutorial layer shows; End tutorial closes it; with four days of use recorded in memory, the next Draw makes
    the coffee reminder due, Use apps shows it without the keyboard, and Not now snoozes it;
  - region sharing: the Tools tile opens Share a region, a custom region drawn on the selection panel fills the
    numbers, Left half starts sharing into an ordinary window (never topmost or active) that turns live within the Mac's
    8 seconds; Cursor halo shows in it (two layers show the effects), and it stays live during full-screen zoom; a hide
    and a show with Show / hide (the recorded Ctrl+Alt+Shift+9) play in it as copies of the drawings that hand off to the
    live region, covered only as each starts (as on the Mac), and it stays live; Stop covers it with "Region sharing is
    off" and closing it removes it;
  - boards (WIN-SYS-04): Ctrl+S saves the board through the Save dialog, above the canvas; the Tools window's File menu
    exports a PNG of the display's pixel size; Ctrl+O shows a broken board's error in the error window, above the
    canvas, without changing the drawing, and OK closes it; the saved board replaces a shape drawn after the save; and
    Insert image adds the PNG as a selected image. The files stay in the output folder.
  - idle work (WIN-SYS-07, WIN-PERF-03): at the end, over 4 s without input, Annotator's own threads use at most 10 ms of
    CPU per second (1% of one core) and the app's GPU engines at most 10 ms per second; graphics driver threads are
    reported apart and not counted (D-11). The capture that sharing used may still be paused then;
  - capture: screen zoom reuses the capture session that the pointer lens left paused, and nothing takes frames at the
    end (the app's capture counters in result.json).

  The check covers a band at the top of the primary display (at most 2200 by 1000 pixels) for about a minute and a
  half and takes the keyboard focus while it runs; Escape gives the focus back to the app that had it. Every key it
  presses is released, and the pointer goes back where it was. The app quits when the script is done, or after its own
  timeout.
.EXAMPLE
  dotnet build apps/windows/src/Annotator.App/Annotator.App.csproj -c Debug -p:Platform=x64
  pwsh -NoProfile -File tools/windows/live-check.ps1 -Out <folder outside the repository>
#>
param(
    [Parameter(Mandatory)] [string] $Out,
    [string] $App = (Join-Path $PSScriptRoot '..\..\apps\windows\src\Annotator.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Annotator.exe'),
    # The app's own safety limit: it quits after this many seconds even if the script stops (at most 300).
    [int] $TimeoutSeconds = 240,
    # Zoom measurements (spike S-3): the app runs its zoom hooks, and during screen zoom the check compares screen captures
    # before and during zoom (in memory only), moves the pointer in small circles over the canvas and records the app's CPU
    # and GPU time.
    [switch] $ZoomMeasure,
    # With -ZoomMeasure: Annotator.RenderHarness.exe takes the captures with Windows.Graphics.Capture, as meeting apps
    # do, instead of a GDI screen copy. Its PNG files are read and deleted at once.
    [string] $Shot = ''
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class LiveInput
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] struct UNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public UNION u; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    const uint Move = 0x1, LeftDown = 0x2, LeftUp = 0x4, Absolute = 0x8000, VirtualDesk = 0x4000;

    public static void PerMonitor() { SetProcessDpiAwarenessContext(new IntPtr(-4)); }

    public static RECT PrimaryBounds()
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
        GetMonitorInfo(MonitorFromPoint(new POINT(), 1), ref info);
        return info.rcMonitor;
    }

    public static int OwnerOf(IntPtr hwnd) { uint pid; GetWindowThreadProcessId(hwnd, out pid); return (int)pid; }

    public static IntPtr TopLevelAt(int x, int y)
    {
        IntPtr hwnd = WindowFromPoint(new POINT { X = x, Y = y });
        return hwnd == IntPtr.Zero ? hwnd : GetAncestor(hwnd, 2);
    }

    public static string Title(IntPtr hwnd) { var b = new StringBuilder(256); GetWindowText(hwnd, b, 256); return b.ToString(); }

    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    public static bool PostClose(IntPtr hwnd) { return PostMessage(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero); }

    public static string ClassName(IntPtr hwnd) { var b = new StringBuilder(256); GetClassName(hwnd, b, 256); return b.ToString(); }

    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    /// WM_GETICON: the window's small and big title-bar icons, zero when it has none.
    public static bool HasIcons(IntPtr hwnd) { return SendMessage(hwnd, 0x007F, IntPtr.Zero, IntPtr.Zero) != IntPtr.Zero && SendMessage(hwnd, 0x007F, (IntPtr)1, IntPtr.Zero) != IntPtr.Zero; }

    public static long ExStyle(IntPtr hwnd) { return GetWindowLongPtr(hwnd, -20).ToInt64(); }

    public static string Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "none";
        return string.Format("0x{0:X} pid {1} {2} \"{3}\"", hwnd.ToInt64(), OwnerOf(hwnd), ClassName(hwnd), Title(hwnd));
    }

    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    /// <summary>True while DWM cloaks the window (DWMWA_CLOAKED): it counts as shown for Windows but nothing draws it.</summary>
    public static bool IsCloaked(IntPtr hwnd) { int value; return DwmGetWindowAttribute(hwnd, 14, out value, 4) == 0 && value != 0; }

    /// <summary>
    /// The visible top-level windows of a process, top first. A cloaked window (the hidden Tools window, the toolbar while
    /// its motion plays) is not visible.
    /// </summary>
    public static IntPtr[] Windows(int pid)
    {
        var found = new List<IntPtr>();
        EnumWindows((h, l) => { if (OwnerOf(h) == pid && IsWindowVisible(h) && !IsCloaked(h)) found.Add(h); return true; }, IntPtr.Zero);
        return found.ToArray();
    }

    [DllImport("user32.dll")] static extern IntPtr GetTopWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    /// <summary>Every top-level window from the top of the z-order down to the lower of two windows.</summary>
    public static string Above(IntPtr first, IntPtr second)
    {
        var lines = new List<string>();
        int seen = 0, index = 0;
        for (IntPtr h = GetTopWindow(IntPtr.Zero); h != IntPtr.Zero && index < 5000 && seen < 2; h = GetWindow(h, 2), index++)
        {
            if (h == first || h == second) seen++;
            lines.Add(string.Format("{0}: {1}{2} ex 0x{3:X}", index, Describe(h), IsWindowVisible(h) ? "" : " (hidden)", ExStyle(h)));
        }
        return string.Join(" | ", lines);
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

    static string Refused(int x, int y, IntPtr expected)
    {
        return string.Format("refused at {0},{1}: expected {2}, found {3}", x, y, Describe(expected), Describe(TopLevelAt(x, y)));
    }

    /// <summary>
    /// The share of pixels of a capture during zoom that match the capture before it, magnified by factor around (cx, cy)
    /// with nearest sampling (each channel within 24 levels).
    /// </summary>
    public static double ZoomMatch(int[] before, int[] zoomed, int width, int height, int cx, int cy, double factor)
    {
        int matched = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int sx = Math.Max(0, Math.Min(width - 1, (int)Math.Floor(cx + (x + 0.5 - cx) / factor)));
                int sy = Math.Max(0, Math.Min(height - 1, (int)Math.Floor(cy + (y + 0.5 - cy) / factor)));
                int a = before[sy * width + sx], b = zoomed[y * width + x];
                if (Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255)) <= 24 && Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)) <= 24 && Math.Abs((a & 255) - (b & 255)) <= 24) matched++;
            }
        }
        return (double)matched / (width * height);
    }

    /// <summary>Moves the pointer by (dx, dy) pixels, without pressing (zoom measurements).</summary>
    public static void Nudge(int dx, int dy)
    {
        Send(new INPUT { type = 0, u = new UNION { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = Move } } });
    }

    /// <summary>Moves the pointer onto the expected window, without pressing.</summary>
    public static string Hover(int x, int y, IntPtr expected)
    {
        if (TopLevelAt(x, y) != expected) return Refused(x, y, expected);
        Send(Mouse(x, y, Move));
        return "ok";
    }

    /// <summary>
    /// Presses on the expected window at the first point, moves through the others and releases at the last. Every point
    /// must be over a window of the same process; the press must be over the expected window.
    /// </summary>
    public static string Drag(int[] xs, int[] ys, IntPtr expected, int stepMs)
    {
        int pid = OwnerOf(expected);
        for (int i = 0; i < xs.Length; i++) if (OwnerOf(TopLevelAt(xs[i], ys[i])) != pid) return Refused(xs[i], ys[i], expected);
        if (TopLevelAt(xs[0], ys[0]) != expected) return Refused(xs[0], ys[0], expected);
        Send(Mouse(xs[0], ys[0], Move));
        Thread.Sleep(40);
        if (TopLevelAt(xs[0], ys[0]) != expected) return "after the move, " + Refused(xs[0], ys[0], expected);
        Send(Mouse(xs[0], ys[0], LeftDown));
        for (int i = 1; i < xs.Length; i++)
        {
            Thread.Sleep(stepMs);
            Send(Mouse(xs[i], ys[i], Move));
        }
        Thread.Sleep(stepMs);
        Send(Mouse(xs[xs.Length - 1], ys[ys.Length - 1], LeftUp));
        return "ok";
    }

    public static string Click(int x, int y, IntPtr expected) { return Drag(new[] { x }, new[] { y }, expected, 40); }

    /// <summary>
    /// Like Drag, holding a key from just after the press (which activates the canvas) until after the release. The key
    /// goes only to a foreground window of the same process; otherwise the drag runs without it and says so.
    /// </summary>
    public static string DragWithKey(int[] xs, int[] ys, IntPtr expected, int stepMs, ushort vk)
    {
        int pid = OwnerOf(expected);
        for (int i = 0; i < xs.Length; i++) if (OwnerOf(TopLevelAt(xs[i], ys[i])) != pid) return Refused(xs[i], ys[i], expected);
        if (TopLevelAt(xs[0], ys[0]) != expected) return Refused(xs[0], ys[0], expected);
        Send(Mouse(xs[0], ys[0], Move));
        Thread.Sleep(40);
        if (TopLevelAt(xs[0], ys[0]) != expected) return "after the move, " + Refused(xs[0], ys[0], expected);
        Send(Mouse(xs[0], ys[0], LeftDown));
        Thread.Sleep(60);
        string key = Key(vk, false, pid);
        for (int i = 1; i < xs.Length; i++)
        {
            Thread.Sleep(stepMs);
            Send(Mouse(xs[i], ys[i], Move));
        }
        Thread.Sleep(stepMs);
        Send(Mouse(xs[xs.Length - 1], ys[ys.Length - 1], LeftUp));
        Thread.Sleep(20);
        if (key == "ok") Key(vk, true, pid);
        return key == "ok" ? "ok" : "drawn without the key: " + key;
    }

    /// <summary>
    /// Presses a key only while a window of the process is the foreground window. A release always goes out, so a key the
    /// check pressed is never left down when the focus moved on.
    /// </summary>
    public static string Key(ushort vk, bool up, int pid)
    {
        IntPtr foreground = GetForegroundWindow();
        if (!up && (foreground == IntPtr.Zero || OwnerOf(foreground) != pid)) return "refused: the foreground window is " + Describe(foreground);
        Send(new INPUT { type = 1, u = new UNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? 0x2u : 0u } } });
        Thread.Sleep(20);
        return "ok";
    }

    /// <summary>
    /// Presses and releases a key in one batch while a window of the process is the foreground window, so the release
    /// reaches the same window even when the key moves the focus (Escape releasing drawing input).
    /// </summary>
    public static string Tap(ushort vk, int pid)
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || OwnerOf(foreground) != pid) return "refused: the foreground window is " + Describe(foreground);
        var inputs = new[]
        {
            new INPUT { type = 1, u = new UNION { ki = new KEYBDINPUT { wVk = vk } } },
            new INPUT { type = 1, u = new UNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = 0x2u } } },
        };
        if (SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT))) != 2) throw new InvalidOperationException("SendInput failed: " + Marshal.GetLastWin32Error());
        Thread.Sleep(20);
        return "ok";
    }

    /// <summary>
    /// Types text as Unicode input (KEYEVENTF_UNICODE), so the person's keyboard layout does not change it, one character
    /// at a time and only while a window of the process is the foreground window.
    /// </summary>
    public static string Type(string text, int pid)
    {
        foreach (char character in text)
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || OwnerOf(foreground) != pid) return "stopped: the foreground window is " + Describe(foreground);
            var inputs = new[]
            {
                new INPUT { type = 1, u = new UNION { ki = new KEYBDINPUT { wScan = character, dwFlags = 0x4u } } },
                new INPUT { type = 1, u = new UNION { ki = new KEYBDINPUT { wScan = character, dwFlags = 0x4u | 0x2u } } },
            };
            if (SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT))) != 2) throw new InvalidOperationException("SendInput failed: " + Marshal.GetLastWin32Error());
            Thread.Sleep(20);
        }
        return "ok";
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] public static extern uint GetCaretBlinkTime();
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr item);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint operation);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, int[] bits, ref BITMAPINFOHEADER info, uint usage);

    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);

    /// <summary>A copy of one window's pixels (PrintWindow with PW_RENDERFULLCONTENT), read into memory only; null if refused.</summary>
    public static int[] CaptureWindow(IntPtr hwnd, int width, int height)
    {
        IntPtr screen = GetDC(IntPtr.Zero), memory = CreateCompatibleDC(screen), bitmap = CreateCompatibleBitmap(screen, width, height);
        IntPtr previous = SelectObject(memory, bitmap);
        bool printed = false;
        try
        {
            printed = PrintWindow(hwnd, memory, 2);
        }
        finally
        {
            SelectObject(memory, previous);
        }
        try
        {
            if (!printed) return null;
            var info = new BITMAPINFOHEADER { biSize = 40, biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
            var pixels = new int[width * height];
            if (GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref info, 0) != height) return null;
            return pixels;
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Yellow pixels (the cursor halo's ring) among the pixels.</summary>
    public static int Yellow(int[] pixels)
    {
        int count = 0;
        foreach (int pixel in pixels)
        {
            int red = (pixel >> 16) & 255, green = (pixel >> 8) & 255, blue = pixel & 255;
            if (red > 200 && green > 160 && blue < 80) count++;
        }
        return count;
    }

    /// <summary>Whether any pixel is not black (a window copy that shows something).</summary>
    public static bool Lit(int[] pixels)
    {
        if (pixels == null) return false;
        foreach (int pixel in pixels) if ((pixel & 0xFFFFFF) != 0) return true;
        return false;
    }

    /// <summary>The share of pixels that differ between two copies of the same size.</summary>
    public static double Changed(int[] first, int[] second)
    {
        int changed = 0;
        for (int i = 0; i < first.Length; i++) if (((first[i] ^ second[i]) & 0xFFFFFF) != 0) changed++;
        return first.Length == 0 ? 0 : (double)changed / first.Length;
    }

    /// <summary>The screen's pixels in a rectangle (0x00RRGGBB, rows from the top), read into memory only.</summary>
    public static int[] Capture(int left, int top, int width, int height)
    {
        IntPtr screen = GetDC(IntPtr.Zero), memory = CreateCompatibleDC(screen), bitmap = CreateCompatibleBitmap(screen, width, height);
        IntPtr previous = SelectObject(memory, bitmap);
        try
        {
            if (!BitBlt(memory, 0, 0, width, height, screen, left, top, 0x00CC0020)) throw new InvalidOperationException("BitBlt failed.");
            SelectObject(memory, previous);
            var info = new BITMAPINFOHEADER { biSize = 40, biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
            var pixels = new int[width * height];
            if (GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref info, 0) != height) throw new InvalidOperationException("GetDIBits failed.");
            return pixels;
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>
    /// The columns where at least <paramref name="share"/> of the rows changed between two captures (a blinking caret as
    /// tall as its line), as "first-last", or an empty string.
    /// </summary>
    public static string ChangedColumns(int[] first, int[] second, int width, int height, double share)
    {
        int from = -1, to = -1;
        for (int x = 0; x < width; x++)
        {
            int changed = 0;
            for (int y = 0; y < height; y++)
            {
                int a = first[y * width + x], b = second[y * width + x];
                int difference = Math.Max(Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255)), Math.Max(Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)), Math.Abs((a & 255) - (b & 255))));
                if (difference > 48) changed++;
            }
            if (changed >= share * height)
            {
                if (from < 0) from = x;
                to = x;
            }
        }
        return from < 0 ? "" : from + "-" + to;
    }
}
'@
[LiveInput]::PerMonitor()

New-Item -ItemType Directory -Force $Out | Out-Null
$log = Join-Path $Out 'live-check.log'
Remove-Item $log -ErrorAction SilentlyContinue
$results = [ordered]@{}
function Note([string]$text) { $line = "$(Get-Date -Format 'HH:mm:ss.fff') $text"; $line; Add-Content -Path $log -Value $line }
function Check([string]$name, [bool]$passed, [string]$detail) {
    $key = $name; $n = 2
    while ($results.Contains($key)) { $key = "$name ($n)"; $n++ }
    $results[$key] = $passed
    Note ("{0} {1}: {2}" -f $(if ($passed) { 'PASS' } else { 'FAIL' }), $key, $detail)
}
function WaitFor([scriptblock]$condition, [int]$milliseconds = 3000) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.ElapsedMilliseconds -lt $milliseconds) { if (& $condition) { return $true }; Start-Sleep -Milliseconds 40 }
    return [bool](& $condition)
}
# Whether the display shows: compositor clock ticks within half a second (bounded). While the display is off nothing is
# composed, and a window that just showed takes no pointer input until it is (see ClickUntil).
Add-Type -Namespace LiveCheckDisplay -Name Native -MemberDefinition '[System.Runtime.InteropServices.DllImport("dcomp.dll")] public static extern uint DCompositionWaitForCompositorClock(uint count, System.IntPtr handles, uint timeoutInMs);'
function DisplayOn() {
    $clock = [Diagnostics.Stopwatch]::StartNew(); $ticks = 0
    while ($clock.ElapsedMilliseconds -lt 500) {
        $r = [LiveCheckDisplay.Native]::DCompositionWaitForCompositorClock(0, [IntPtr]::Zero, 100)
        if ($r -eq 0 -or $r -eq 1) { $ticks++ } else { Start-Sleep -Milliseconds 50 }
    }
    return $ticks -gt 0
}

if (-not (Test-Path $App)) { throw "Build the development app first: $App is missing." }
$primary = [LiveInput]::PrimaryBounds()
$area = @($primary.Left, $primary.Top, [Math]::Min(2200, $primary.Right - $primary.Left), [Math]::Min(1000, $primary.Bottom - $primary.Top))
$pointer = New-Object LiveInput+POINT
[void][LiveInput]::GetCursorPos([ref]$pointer)
foreach ($stale in 'ready.json', 'result.json', 'canvas.png') { Remove-Item (Join-Path $Out $stale) -ErrorAction SilentlyContinue }
$arguments = @('--live-check', '--area', ($area -join ','), '--out', $Out, '--timeout', "$TimeoutSeconds")
if ($ZoomMeasure) { $arguments += '--zoom-hooks' }
$process = Start-Process -FilePath $App -PassThru -ArgumentList $arguments
Note "started $App (process $($process.Id)) on the area $($area -join ',')"
Note "display: $(if (DisplayOn) { 'on' } else { 'off (no compositor clock ticks); windows are composed late' })"
try {
    $readyPath = Join-Path $Out 'ready.json'
    if (-not (WaitFor { Test-Path $readyPath } 20000)) { throw 'The live check did not get ready within 20 seconds.' }
    $ready = Get-Content $readyPath -Raw | ConvertFrom-Json
    $appPid = [int]$ready.processId
    $toolbar = [IntPtr][long]$ready.toolbar
    $overlay = [IntPtr][long]$ready.overlay
    Note "toolbar $([LiveInput]::Describe($toolbar)); canvas $([LiveInput]::Describe($overlay)); scale $($ready.scale)"
    $uia = [Windows.Automation.AutomationElement]::FromHandle($toolbar)

    function Find([string]$name, [IntPtr]$window = $toolbar) {
        $root = [Windows.Automation.AutomationElement]::FromHandle($window)
        $root.FindFirst([Windows.Automation.TreeScope]::Descendants,
            ([Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $name)))
    }
    function Center($element) {
        $r = $element.Current.BoundingRectangle
        @([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
    }
    function IsOn([string]$name) {
        $element = Find $name
        if ($null -eq $element) { return $false }
        $toggle = $null
        if ($element.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$toggle)) {
            return $toggle.Current.ToggleState -eq [Windows.Automation.ToggleState]::On
        }
        $item = $null
        if ($element.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$item)) { return $item.Current.IsSelected }
        return $false
    }
    function ClickButton([string]$name, [IntPtr]$window = $toolbar) {
        $element = Find $name $window
        if ($null -eq $element) { return "no element named '$name'" }
        $c = Center $element
        $result = [LiveInput]::Click($c[0], $c[1], $window)
        Start-Sleep -Milliseconds 250
        return $result
    }
    # A window takes pointer input once Windows routes it to the window's content (WinUI's content island), which waits for
    # the content to be composed on screen: a press before that reaches the window's bridge and does nothing. So Annotator's
    # windows show cloaked until their content has drawn and committed, and hide by cloaking, so they take clicks as soon as
    # they are on screen (WindowCloak). The click waits for two compositor frames first, as a person aims at a window that
    # shows. With the display off the compositor clock stops, so that can take a second or more and one more click is noted;
    # while the display shows, the first click must work.
    function ClickUntil([string]$name, [IntPtr]$window, [scriptblock]$done) {
        for ($tick = 0; $tick -lt 2; $tick++) { [void][LiveCheckDisplay.Native]::DCompositionWaitForCompositorClock(0, [IntPtr]::Zero, 100) }
        Note "click ${name}: $(ClickButton $name $window)"
        if (WaitFor $done 1500) { Check "first-click $name" $true 'the first click worked'; return }
        if (DisplayOn) { Check "first-click $name" $false 'the first click changed nothing while the display showed' }
        else { Note "the display is off (no compositor clock ticks): the window is not composed yet" }
        Note "click $name again (the first click changed nothing): $(ClickButton $name $window)"
    }
    # An element's Value pattern value, or null.
    function ValueOf($element) {
        $pattern = $null
        if ($null -ne $element -and $element.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { return $pattern.Current.Value }
        return $null
    }
    # WindowFromPoint at the center of every enabled, visible toolbar button.
    function HitTest([string]$when) {
        $buttons = $uia.FindAll([Windows.Automation.TreeScope]::Descendants,
            ([Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button)))
        $checked = 0; $missed = @()
        foreach ($button in $buttons) {
            if (-not $button.Current.IsEnabled -or $button.Current.IsOffscreen) { continue }
            $r = $button.Current.BoundingRectangle
            if ($r.Width -lt 2 -or $r.Height -lt 2) { continue }
            $c = Center $button
            $checked++
            $found = [LiveInput]::TopLevelAt($c[0], $c[1])
            if ($found -ne $toolbar) { $missed += "$($button.Current.Name) -> $([LiveInput]::Describe($found))" }
        }
        Check "toolbar-hit-test $when" ($checked -gt 5 -and $missed.Count -eq 0) "$checked buttons; not the toolbar: $($missed -join '; ')"
        if ($missed.Count -gt 0) {
            $order = @([LiveInput]::Windows($appPid) | ForEach-Object { '{0} ex 0x{1:X}' -f [LiveInput]::Describe($_), [LiveInput]::ExStyle($_) })
            Note "z-order of the app's visible windows, top first: $($order -join ' | ')"
            Note "z-order from the top to the toolbar: $([LiveInput]::Above($toolbar, $overlay))"
        }
    }
    function Foreground() { [LiveInput]::GetForegroundWindow() }
    # Escape only while the color popover has the keyboard; otherwise it would reach the canvas and release input.
    function EscapePopover() {
        $popover = Popover
        if ($null -ne $popover -and (WaitFor { (Foreground) -eq $popover })) {
            # The popover moves the keyboard to its first control once it is laid out.
            Start-Sleep -Milliseconds 400
            try { $focused = [Windows.Automation.AutomationElement]::FocusedElement; Note "focused in the popover: '$($focused.Current.Name)' ($($focused.Current.ClassName), window 0x$('{0:X}' -f $focused.Current.NativeWindowHandle))" } catch { Note "focused element unknown: $($_.Exception.Message)" }
            Note "Escape in the popover: $([LiveInput]::Tap(0x1B, $appPid))"
        }
        else {
            Note "no Escape: the popover does not have the keyboard ($(Show $popover))"
        }
    }
    function Show($window) { if ($null -eq $window) { 'none' } else { [LiveInput]::Describe($window) } }

    function Popover() { [LiveInput]::Windows($appPid) | Where-Object { [LiveInput]::Title($_) -eq 'Fill color' } | Select-Object -First 1 }
    # Canvas points (scaled like the app) from a line below the toolbar's tallest layout.
    $scale = [double]$ready.scale
    # Below the toolbar with full properties, the default: the Arrow's, the tallest, is 282 points high.
    $drawTop = $area[1] + 360 * $scale
    function Canvas([double]$x, [double]$y) { @([int]($area[0] + $x * $scale), [int]($drawTop + $y * $scale)) }
    function DrawOnCanvas([int[]]$from, [int[]]$to, [string]$label, [int]$key = 0) {
        $xs = @(); $ys = @()
        for ($i = 0; $i -le 8; $i++) { $xs += [int]($from[0] + ($to[0] - $from[0]) * $i / 8); $ys += [int]($from[1] + ($to[1] - $from[1]) * $i / 8) }
        $result = if ($key -ne 0) { [LiveInput]::DragWithKey($xs, $ys, $overlay, 25, [uint16]$key) } else { [LiveInput]::Drag($xs, $ys, $overlay, 25) }
        Start-Sleep -Milliseconds 250
        Note "draw ${label}: $result"
        return $result
    }

    HitTest 'at start'

    # Draw: a tool click enters Draw and the canvas takes the keyboard focus.
    Note "click Rectangle: $(ClickButton 'Rectangle')"
    Check 'draw-activates-canvas' (WaitFor { (Foreground) -eq $overlay }) "foreground $([LiveInput]::Describe((Foreground)))"
    HitTest 'after Draw activated the canvas'

    # A shape: selected and the tool becomes Select, as on the Mac.
    [void](DrawOnCanvas (Canvas 60 30) (Canvas 165 100) 'rectangle')
    Check 'shape-selected-select-tool' (WaitFor { IsOn 'Select' }) "Select on: $(IsOn 'Select')"
    HitTest 'after a stroke'

    # Tools: Ctrl+L, My shapes (save, insert, rename, delete) and a tile, with the selected rectangle.
    function ToolsWindow() { [LiveInput]::Windows($appPid) | Where-Object { [LiveInput]::Title($_) -eq 'Tools' } | Select-Object -First 1 }
    function Named([IntPtr]$window, [string]$name) { $null -ne $window -and $null -ne (Find $name $window) }
    function Chosen([IntPtr]$window, [string]$name) {
        $element = Find $name $window
        $item = $null
        $null -ne $element -and $element.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$item) -and $item.Current.IsSelected
    }
    function Focused() { try { [Windows.Automation.AutomationElement]::FocusedElement.Current.Name } catch { "unknown: $($_.Exception.Message)" } }
    # My shapes says what failed (saving, reading or drawing the previews) in a text that starts "My shapes could not".
    function Problem([IntPtr]$window) {
        $texts = ([Windows.Automation.AutomationElement]::FromHandle($window)).FindAll([Windows.Automation.TreeScope]::Descendants,
            ([Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text)))
        foreach ($text in $texts) { if ($text.Current.Name -like '*could not*') { return $text.Current.Name } }
        ''
    }
    function CtrlL([string]$where) {
        try { Note "Ctrl+L ${where}: $([LiveInput]::Key(0x11, $false, $appPid)), $([LiveInput]::Tap(0x4C, $appPid))" }
        finally { [void][LiveInput]::Key(0x11, $true, $appPid) }
    }
    # Letters and digits only: their virtual-key codes are their upper-case characters.
    function TypeKeys([string]$text) { foreach ($c in $text.ToUpperInvariant().ToCharArray()) { $r = [LiveInput]::Tap([uint16][int]$c, $appPid); if ($r -ne 'ok') { return $r } }; 'ok' }
    # A dialog may show in its own popup window: the click goes to whichever window of the app is at the element.
    function ClickIn([string]$name, [IntPtr]$window) {
        $element = Find $name $window
        if ($null -eq $element) { return "no element named '$name'" }
        $c = Center $element
        $target = [LiveInput]::TopLevelAt($c[0], $c[1])
        if ([LiveInput]::OwnerOf($target) -ne $appPid) { return "refused: '$name' is under $([LiveInput]::Describe($target))" }
        $result = [LiveInput]::Click($c[0], $c[1], $target)
        Start-Sleep -Milliseconds 250
        return $result
    }
    [void](WaitFor { (Foreground) -eq $overlay })
    CtrlL 'on the canvas'
    Check 'ctrl-l-opens-tools' (WaitFor { $null -ne (ToolsWindow) -and (Foreground) -eq (ToolsWindow) }) "Tools $(Show (ToolsWindow)); foreground $([LiveInput]::Describe((Foreground)))"
    $tools = ToolsWindow
    if ($null -ne $tools) {
        # The keyboard starts on the chosen section; the arrow keys choose the next one, as on the Mac.
        Check 'tools-focus-on-section' (WaitFor { (Focused) -eq 'Shapes' }) "focused: '$(Focused)'"
        Note "Right: $([LiveInput]::Tap(0x27, $appPid))"
        Check 'tools-arrow-chooses-section' (WaitFor { (Chosen $tools 'Focus') -and (Focused) -eq 'Focus' }) "Focus chosen: $(Chosen $tools 'Focus'); focused: '$(Focused)'"
        Note "click My shapes: $(ClickButton 'My shapes' $tools)"
        # My shapes builds its view after the click; on a busy machine that takes longer than the click's pause.
        [void](WaitFor { Named $tools 'Shape name' })
        Note "click Shape name: $(ClickButton 'Shape name' $tools)"
        Note "type box: $(TypeKeys 'box')"
        Note "click Save selection: $(ClickButton 'Save selection' $tools)"
        Check 'my-shapes-saves' (WaitFor { Named $tools 'Insert box' }) "Insert box: $(Named $tools 'Insert box'); message: $(Named $tools 'Saved box to My shapes.')"
        # The preview is drawn on a worker thread; a failure would show as a message.
        Start-Sleep -Milliseconds 300
        $problem = Problem $tools
        Check 'my-shapes-preview-drawn' ($problem -eq '') "problem: '$problem'"
        Note "click Insert box: $(ClickButton 'Insert box' $tools)"
        Check 'my-shapes-inserts-and-hides' (WaitFor { $null -eq (ToolsWindow) -and (Foreground) -eq $overlay }) "Tools $(Show (ToolsWindow)); foreground $([LiveInput]::Describe((Foreground)))"
        Note "click Tool library: $(ClickButton 'Tool library')"
        Check 'tool-library-button-opens-tools' (WaitFor { (ToolsWindow) -eq $tools -and (Foreground) -eq $tools }) "Tools $(Show (ToolsWindow)); foreground $([LiveInput]::Describe((Foreground)))"
        Note "click Edit: $(ClickButton 'Edit saved shapes' $tools)"
        Note "select box: $(ClickButton 'Select saved shape box' $tools)"
        Note "click Rename...: $(ClickButton 'Rename...' $tools)"
        if (WaitFor { Named $tools 'Rename saved shape' }) {
            Note "click the new name's field: $(ClickIn 'Shape name' $tools)"
            Note "type 2: $(TypeKeys '2')"
            Note "click Save: $(ClickIn 'Save' $tools)"
        }
        Check 'my-shapes-renames' (WaitFor { Named $tools 'Select saved shape box2' }) "box2: $(Named $tools 'Select saved shape box2'); dialog open: $(Named $tools 'Rename saved shape')"
        Note "click Delete...: $(ClickButton 'Delete...' $tools)"
        if (WaitFor { Named $tools 'Delete saved shape?' }) {
            Note "click Delete: $(ClickIn 'Delete' $tools)"
        }
        Check 'my-shapes-deletes' (WaitFor { Named $tools 'No saved shapes yet.' }) "empty: $(Named $tools 'No saved shapes yet.'); dialog open: $(Named $tools 'Delete saved shape?')"
        Note "click Done: $(ClickButton 'Done editing saved shapes' $tools)"
        Note "click Shapes: $(ClickButton 'Shapes' $tools)"
        Note "click Diamond: $(ClickButton 'Diamond' $tools)"
        Check 'tools-tile-chooses-and-hides' (WaitFor { $null -eq (ToolsWindow) -and (IsOn 'Diamond') -and (Foreground) -eq $overlay }) "Tools $(Show (ToolsWindow)); Diamond on: $(IsOn 'Diamond'); foreground $([LiveInput]::Describe((Foreground)))"
        CtrlL 'on the canvas again'
        if (WaitFor { (Foreground) -eq $tools }) { Note "Escape in Tools: $([LiveInput]::Tap(0x1B, $appPid))" }
        Check 'tools-escape-hides' (WaitFor { $null -eq (ToolsWindow) -and (Foreground) -eq $overlay }) "Tools $(Show (ToolsWindow)); foreground $([LiveInput]::Describe((Foreground)))"
    }
    HitTest 'after Tools closed'

    # Spotlight menu (Mac focusMenuItems): Spotlight, Cursor halo and the effect cleanup are available. Remove all censors
    # finds no effect on this canvas, so it changes nothing (the result's checks below would see a change). The live check's
    # toolbar has the Spotlight and Zoom menus; a new profile keeps them in More tools.
    function MenuItem([string]$name) {
        $condition = [Windows.Automation.AndCondition]::new(
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $name),
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::MenuItem))
        foreach ($window in [LiveInput]::Windows($appPid)) {
            $element = ([Windows.Automation.AutomationElement]::FromHandle($window)).FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
            if ($null -ne $element) { return $element }
        }
        return $null
    }
    Note "click Spotlight menu: $(ClickButton 'Spotlight menu')"
    if (WaitFor { $null -ne (MenuItem 'Remove all censors') }) {
        $states = @('Spotlight', 'Cursor halo', 'Clear drawings (keep effects)', 'Remove all censors' | ForEach-Object {
            $item = MenuItem $_
            "$_=$(if ($null -ne $item) { $item.Current.IsEnabled } else { 'missing' })"
        })
        Check 'focus-menu-items' (($states -join '; ') -eq 'Spotlight=True; Cursor halo=True; Clear drawings (keep effects)=True; Remove all censors=True') ($states -join '; ')
        $c = Center (MenuItem 'Remove all censors')
        $target = [LiveInput]::TopLevelAt($c[0], $c[1])
        if ([LiveInput]::OwnerOf($target) -eq $appPid) { Note "click Remove all censors: $([LiveInput]::Click($c[0], $c[1], $target))" }
        else { Note "refused: Remove all censors is under $([LiveInput]::Describe($target))" }
        Check 'focus-menu-closes' (WaitFor { $null -eq (MenuItem 'Remove all censors') }) "menu still open: $($null -ne (MenuItem 'Remove all censors'))"
    }
    else {
        Check 'focus-menu-items' $false 'the Spotlight menu did not open'
    }
    HitTest 'after the Spotlight menu'

    # Pointer effects: Cursor halo and Spotlight turn on from the Spotlight menu and show Stop buttons in the toolbar's mode
    # strip; over the canvas the screen shows the yellow ring at the pointer and Spotlight dims the canvas away from it. The
    # Stop buttons turn them off again. Clicks anywhere (here the Stop buttons) count for the click effect.
    function MenuChoose([string]$item) {
        Note "click Spotlight menu: $(ClickButton 'Spotlight menu')"
        if (-not (WaitFor { $null -ne (MenuItem $item) })) { Note "no menu item $item"; return }
        $c = Center (MenuItem $item)
        $target = [LiveInput]::TopLevelAt($c[0], $c[1])
        if ([LiveInput]::OwnerOf($target) -ne $appPid) { Note "refused: $item is under $([LiveInput]::Describe($target))"; return }
        Note "click ${item}: $([LiveInput]::Click($c[0], $c[1], $target))"
        [void](WaitFor { $null -eq (MenuItem $item) })
    }
    function Luma([int]$pixel) { 0.299 * (($pixel -shr 16) -band 255) + 0.587 * (($pixel -shr 8) -band 255) + 0.114 * ($pixel -band 255) }
    $spot = Canvas 420 200
    $far = Canvas 60 30
    Note "hover the canvas before the effects: $([LiveInput]::Hover($spot[0], $spot[1], $overlay))"
    Start-Sleep -Milliseconds 200
    $farBefore = Luma ([LiveInput]::Capture($far[0], $far[1], 1, 1)[0])
    MenuChoose 'Cursor halo'
    Check 'pointer-halo-on' (WaitFor { $null -ne (Find 'Stop cursor halo') }) "Stop cursor halo in the mode strip: $($null -ne (Find 'Stop cursor halo'))"
    MenuChoose 'Spotlight'
    Check 'pointer-spotlight-on' (WaitFor { $null -ne (Find 'Stop spotlight') }) "Stop spotlight in the mode strip: $($null -ne (Find 'Stop spotlight'))"
    Note "hover the canvas: $([LiveInput]::Hover($spot[0], $spot[1], $overlay))"
    Start-Sleep -Milliseconds 500
    $ring = [int][Math]::Round(18 * $scale)
    $box = 2 * $ring + 24
    $pixels = [LiveInput]::Capture($spot[0] - [int]($box / 2), $spot[1] - [int]($box / 2), $box, $box)
    $yellow = 0
    for ($i = 0; $i -lt $pixels.Length; $i++) {
        $red = ($pixels[$i] -shr 16) -band 255; $green = ($pixels[$i] -shr 8) -band 255; $blue = $pixels[$i] -band 255
        if ($red -gt 200 -and $green -gt 160 -and $blue -lt 80) { $yellow++ }
    }
    Check 'pointer-halo-drawn' ($yellow -ge 4 * $ring) "yellow pixels around the pointer: $yellow (ring radius $ring px)"
    $farOn = Luma ([LiveInput]::Capture($far[0], $far[1], 1, 1)[0])
    if ($farBefore -gt 60) {
        Check 'pointer-spotlight-dims' ($farOn -lt $farBefore * 0.6) ("canvas far from the pointer: luma {0:F0} before, {1:F0} with Spotlight" -f $farBefore, $farOn)
    }
    else {
        Note ("the canvas far from the pointer is too dark to see Spotlight's dimming (luma {0:F0}); not checked" -f $farBefore)
    }
    Note "click Stop spotlight: $(ClickButton 'Stop spotlight')"
    Check 'pointer-spotlight-stops' (WaitFor { $null -eq (Find 'Stop spotlight') }) "Stop spotlight still shown: $($null -ne (Find 'Stop spotlight'))"
    Note "click Stop cursor halo: $(ClickButton 'Stop cursor halo')"
    Check 'pointer-halo-stops' (WaitFor { $null -eq (Find 'Stop cursor halo') }) "Stop cursor halo still shown: $($null -ne (Find 'Stop cursor halo'))"
    Start-Sleep -Milliseconds 400
    $farOff = Luma ([LiveInput]::Capture($far[0], $far[1], 1, 1)[0])
    Check 'pointer-spotlight-fades-out' ([Math]::Abs($farOff - $farBefore) -le 12) ("canvas far from the pointer: luma {0:F0} before, {1:F0} after Stop" -f $farBefore, $farOff)
    HitTest 'after the pointer effects'

    # Live zoom (WIN-CAP-05, WIN-CAP-06): the Zoom menu turns on the pointer lens, whose own window shows beside the pointer,
    # and full-screen zoom, whose level the strip changes; Stop buttons end both. The live check installs no hooks, so
    # Alt+scroll over other apps and the Escape hook stay with the owner's checks.
    function LensWindow() { [LiveInput]::Windows($appPid) | Where-Object { [LiveInput]::ClassName($_) -eq 'Annotator.Platform.PointerLens' } | Select-Object -First 1 }
    function ChooseZoom([string]$item) {
        Note "click Zoom menu: $(ClickButton 'Zoom menu')"
        if (WaitFor { $null -ne (MenuItem $item) }) {
            $c = Center (MenuItem $item)
            $target = [LiveInput]::TopLevelAt($c[0], $c[1])
            if ([LiveInput]::OwnerOf($target) -eq $appPid) { Note "click ${item}: $([LiveInput]::Click($c[0], $c[1], $target))" }
            else { Note "refused: $item is under $([LiveInput]::Describe($target))" }
        }
        else { Note "the Zoom menu did not show $item" }
    }
    # The lens shows only while the pointer is on its display: the pointer rests on the canvas first.
    $rest = Canvas 300 220
    [void][LiveInput]::SetCursorPos($rest[0], $rest[1])
    ChooseZoom 'Pointer lens'
    Check 'zoom-lens-shows' (WaitFor { $null -ne (LensWindow) -and $null -ne (Find 'Stop pointer lens') } 5000) "lens window $(Show (LensWindow)); Stop pointer lens: $($null -ne (Find 'Stop pointer lens'))"
    # Screen access (WIN-CAP-04): a development build needs no Windows consent, so the lens starts without the explanation
    # that a packaged build shows before Windows asks.
    $explanation = [LiveInput]::Windows($appPid) | Where-Object { [LiveInput]::Title($_) -eq 'Annotator' -and $null -ne (Find 'Allow screen access' $_) } | Select-Object -First 1
    Check 'screen-access-not-asked' ($null -eq $explanation) "explanation $(Show $explanation)"
    Note "click Stop pointer lens: $(ClickButton 'Stop pointer lens')"
    Check 'zoom-lens-stops' (WaitFor { $null -eq (LensWindow) -and $null -eq (Find 'Stop pointer lens') }) "lens window $(Show (LensWindow))"
    [void][LiveInput]::SetCursorPos($rest[0], $rest[1])
    # Screen captures stay in memory (the harness's PNG files are deleted once read): they hold whatever the person has
    # under the canvas.
    function ScreenPixels([int[]]$rect) {
        if (-not $Shot) { return , [LiveInput]::Capture($rect[0], $rect[1], $rect[2], $rect[3]) }
        $png = Join-Path $Out ('zoom-shot-{0}.png' -f [Guid]::NewGuid().ToString('N'))
        try {
            $run = Start-Process -FilePath $Shot -PassThru -NoNewWindow -ArgumentList @('shot', '--area', ('{0},{1},{2},{3}' -f ($rect[0] - $area[0]), ($rect[1] - $area[1]), $rect[2], $rect[3]), '--evidence', $png, '--timeout', '20')
            if (-not $run.WaitForExit(30000)) { Stop-Process -Id $run.Id -Force; [void]$run.WaitForExit(5000); return $null }
            if (-not (Test-Path $png)) { return $null }
            $bitmap = [Drawing.Bitmap]::new($png)
            try {
                $data = $bitmap.LockBits([Drawing.Rectangle]::new(0, 0, $bitmap.Width, $bitmap.Height), [Drawing.Imaging.ImageLockMode]::ReadOnly, [Drawing.Imaging.PixelFormat]::Format32bppRgb)
                $pixels = [int[]]::new($bitmap.Width * $bitmap.Height)
                try { [Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length) } finally { $bitmap.UnlockBits($data) }
                return , $pixels
            }
            finally { $bitmap.Dispose() }
        }
        finally { Remove-Item $png -ErrorAction SilentlyContinue }
    }
    $box = @([int]($rest[0] - 300 * $scale), [int]($rest[1] - 150 * $scale), [int](600 * $scale), [int](300 * $scale))
    $before = if ($ZoomMeasure) { ScreenPixels $box }
    ChooseZoom 'Full-screen zoom'
    Check 'zoom-screen-starts' (WaitFor { $null -ne (Find 'Live zoom level') -and $null -ne (Find 'Stop screen zoom') } 12000) "level: $($null -ne (Find 'Live zoom level')); pending: $($null -ne (Find 'Cancel pending zoom'))"
    Note "click Increase live zoom: $(ClickButton 'Increase live zoom')"
    Check 'zoom-level-increases' (WaitFor { $null -ne (Find '2.5×') }) "2.5×: $($null -ne (Find '2.5×')); 2.0×: $($null -ne (Find '2.0×'))"
    HitTest 'during screen zoom'
    if ($ZoomMeasure) {
        # The pointer back where the captures were taken, so the zoomed capture is anchored at the same point.
        [void][LiveInput]::Hover($rest[0], $rest[1], [LiveInput]::TopLevelAt($rest[0], $rest[1]))
        Start-Sleep -Milliseconds 400
        # What screen sharing sees: the capture during zoom matches the capture before it magnified around the pointer.
        $zoomed = ScreenPixels $box
        $factor = if ($null -ne (Find '2.5×')) { 2.5 } else { 2.0 }
        $match = if ($null -ne $before -and $null -ne $zoomed) { [LiveInput]::ZoomMatch($before, $zoomed, $box[2], $box[3], $rest[0] - $box[0], $rest[1] - $box[1], $factor) } else { 0 }
        $how = if ($Shot) { 'Windows.Graphics.Capture' } else { 'GDI' }
        Check 'zoom-screen-capture-shows-zoom' ($match -ge 0.9) ("{0:P1} of the pixels ($how) match the capture before zoom magnified {1}x around the pointer" -f $match, $factor)
        $appProcess = Get-Process -Id $appPid
        function CpuMs() { $appProcess.Refresh(); $appProcess.TotalProcessorTime.TotalMilliseconds }
        function GpuPercent() {
            try {
                $samples = (Get-Counter "\GPU Engine(pid_$($appPid)_*)\Utilization Percentage" -SampleInterval 1 -MaxSamples 2 -ErrorAction Stop).CounterSamples
                [Math]::Round((($samples | Where-Object { $_.InstanceName -like '*engtype_3d*' } | Measure-Object CookedValue -Sum).Sum) / 2, 2)
            }
            catch { "unavailable ($($_.Exception.Message))" }
        }
        # Pointer moves in circles of 40 pixels around the rest point at about 125 Hz for 3 seconds (relative moves, no
        # button: they only hover whatever is under the click-through canvas).
        $cpu = CpuMs; $watch = [Diagnostics.Stopwatch]::StartNew(); $moves = 0
        while ($watch.ElapsedMilliseconds -lt 3000) {
            $angle = $moves * 0.1
            [void][LiveInput]::Nudge([int][Math]::Round(4 * [Math]::Cos($angle)), [int][Math]::Round(4 * [Math]::Sin($angle)))
            $moves++
            Start-Sleep -Milliseconds 8
        }
        $moving = (CpuMs) - $cpu
        $share = 100 * $moving / $watch.ElapsedMilliseconds
        Note ("zoom measure: pointer moving {0} moves in {1} ms, app CPU {2:F0} ms ({3:F1}% of one core)" -f $moves, $watch.ElapsedMilliseconds, $moving, $share)
        [void][LiveInput]::SetCursorPos($rest[0], $rest[1])
        $cpu = CpuMs; Start-Sleep -Milliseconds 2000; $idle = (CpuMs) - $cpu
        Note ("zoom measure: zoom on, pointer still for 2000 ms, app CPU {0:F0} ms; GPU 3D {1}%" -f $idle, (GpuPercent))
    }
    # Pointer effects during zoom: Cursor halo and Spotlight turn on while full-screen zoom shows. The pointer watcher starts
    # again beside zoom's input (beside its mouse hook too with -ZoomMeasure), and the yellow ring shows at the pointer
    # above the zoomed screen. The Stop buttons turn both off, and zoom stays on.
    function YellowAround([int[]]$point, [int]$box) {
        $pixels = [LiveInput]::Capture($point[0] - [int]($box / 2), $point[1] - [int]($box / 2), $box, $box)
        $count = 0
        for ($i = 0; $i -lt $pixels.Length; $i++) {
            $red = ($pixels[$i] -shr 16) -band 255; $green = ($pixels[$i] -shr 8) -band 255; $blue = $pixels[$i] -band 255
            if ($red -gt 200 -and $green -gt 160 -and $blue -lt 80) { $count++ }
        }
        $count
    }
    $zoomSpot = Canvas 420 200
    $ringPixels = [int][Math]::Round(18 * $scale)
    # Under zoom the canvas lets clicks through to the apps below, so the pointer is only placed there (no click).
    [void][LiveInput]::SetCursorPos($zoomSpot[0], $zoomSpot[1])
    Start-Sleep -Milliseconds 300
    $yellowBefore = YellowAround $zoomSpot (2 * $ringPixels + 24)
    MenuChoose 'Cursor halo'
    MenuChoose 'Spotlight'
    Check 'zoom-pointer-effects-on' (WaitFor { $null -ne (Find 'Stop cursor halo') -and $null -ne (Find 'Stop spotlight') -and $null -ne (Find 'Stop screen zoom') }) "Stop cursor halo $($null -ne (Find 'Stop cursor halo')), Stop spotlight $($null -ne (Find 'Stop spotlight')), Stop screen zoom $($null -ne (Find 'Stop screen zoom'))"
    # SetCursorPos makes no raw input; a relative move (no button) does, so the pointer watcher follows it.
    [void][LiveInput]::SetCursorPos($zoomSpot[0], $zoomSpot[1]); [LiveInput]::Nudge(4, 3)
    Start-Sleep -Milliseconds 500
    $yellowOn = YellowAround $zoomSpot (2 * $ringPixels + 24)
    Check 'zoom-pointer-halo-drawn' ($yellowOn - $yellowBefore -ge 4 * $ringPixels) "yellow pixels around the pointer during zoom: $yellowBefore before the halo, $yellowOn with it (ring radius $ringPixels px)"
    Note "click Stop spotlight: $(ClickButton 'Stop spotlight')"
    Note "click Stop cursor halo: $(ClickButton 'Stop cursor halo')"
    Check 'zoom-pointer-effects-stop' (WaitFor { $null -eq (Find 'Stop spotlight') -and $null -eq (Find 'Stop cursor halo') -and $null -ne (Find 'Stop screen zoom') }) "Stop spotlight $($null -ne (Find 'Stop spotlight')), Stop cursor halo $($null -ne (Find 'Stop cursor halo')), Stop screen zoom $($null -ne (Find 'Stop screen zoom'))"
    Note "click Stop screen zoom: $(ClickButton 'Stop screen zoom')"
    Check 'zoom-screen-stops' (WaitFor { $null -eq (Find 'Live zoom level') -and $null -eq (Find 'Stop screen zoom') }) "level: $($null -ne (Find 'Live zoom level'))"
    if ($ZoomMeasure) {
        Start-Sleep -Milliseconds 500
        $cpu = CpuMs; Start-Sleep -Milliseconds 2000; $after = (CpuMs) - $cpu
        Note ("zoom measure: after zoom stopped, 2000 ms idle, app CPU {0:F0} ms; GPU 3D {1}%" -f $after, (GpuPercent))
    }

    # Toolbar buttons and Fill after the color popover closed with Done after a choice, and with Escape. As on the Mac,
    # the popover stays open after a choice.
    Note "click Ellipse: $(ClickButton 'Ellipse')"
    Check 'toolbar-click-after-stroke' (WaitFor { IsOn 'Ellipse' }) "Ellipse on: $(IsOn 'Ellipse')"
    Note "click Fill color: $(ClickButton 'Fill color')"
    Check 'fill-opens-popover' (WaitFor { $null -ne (Popover) }) "popover: $(Show (Popover))"
    $popover = Popover
    if ($null -ne $popover) {
        $all = ([Windows.Automation.AutomationElement]::FromHandle($popover)).FindAll([Windows.Automation.TreeScope]::Descendants,
            ([Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button)))
        $swatch = $null
        foreach ($candidate in $all) { if ($candidate.Current.Name -like 'Yellow*') { $swatch = $candidate } }
        if ($null -ne $swatch) {
            $c = Center $swatch
            Note "click $($swatch.Current.Name): $([LiveInput]::Click($c[0], $c[1], $popover))"
            Start-Sleep -Milliseconds 400
            Check 'popover-open-after-choice' ($null -ne (Popover)) "popover after the choice: $(Show (Popover))"
        }
        else {
            Check 'popover-open-after-choice' $false 'the popover has no Yellow swatch'
        }
        Note "click Done: $(ClickButton 'Done' $popover)"
        Check 'popover-done-closes' (WaitFor { $null -eq (Popover) }) "popover after Done: $(Show (Popover))"
    }
    Check 'focus-back-to-canvas' (WaitFor { (Foreground) -eq $overlay }) "foreground $([LiveInput]::Describe((Foreground)))"
    HitTest 'after the popover closed with Done'
    Note "click Fill color again: $(ClickButton 'Fill color')"
    Check 'fill-opens-popover-again' (WaitFor { $null -ne (Popover) }) "popover: $(Show (Popover))"
    EscapePopover
    Check 'popover-escape-closes' (WaitFor { $null -eq (Popover) }) "popover after Escape: $(Show (Popover))"
    HitTest 'after the popover closed with Escape'
    Note "click Fill color a third time: $(ClickButton 'Fill color')"
    Check 'fill-opens-popover-third' (WaitFor { $null -ne (Popover) }) "popover: $(Show (Popover))"
    EscapePopover
    [void](WaitFor { $null -eq (Popover) })

    # Hovering shows a tooltip; the next button still takes the click. A press released elsewhere leaves nothing behind.
    $ellipse = Find 'Ellipse'
    $c = Center $ellipse
    Note "hover Ellipse: $([LiveInput]::Hover($c[0], $c[1], $toolbar))"
    Start-Sleep -Milliseconds 1600
    $others = @([LiveInput]::Windows($appPid) | Where-Object { $_ -ne $toolbar -and $_ -ne $overlay })
    Note "windows while hovering: $(($others | ForEach-Object { [LiveInput]::Describe($_) }) -join '; ')"
    $bar = New-Object LiveInput+RECT
    [void][LiveInput]::GetWindowRect($toolbar, [ref]$bar)
    $tips = @($others | Where-Object { [LiveInput]::ClassName($_) -eq 'Microsoft.UI.Content.PopupWindowSiteBridge' })
    foreach ($tip in $tips) {
        $r = New-Object LiveInput+RECT
        [void][LiveInput]::GetWindowRect($tip, [ref]$r)
        $overlaps = $r.Left -lt $bar.Right -and $r.Right -gt $bar.Left -and $r.Top -lt $bar.Bottom -and $r.Bottom -gt $bar.Top
        Check 'tooltip-outside-toolbar' (-not $overlaps) ("tooltip ({0},{1})-({2},{3}), toolbar ({4},{5})-({6},{7})" -f $r.Left, $r.Top, $r.Right, $r.Bottom, $bar.Left, $bar.Top, $bar.Right, $bar.Bottom)
    }
    if ($tips.Count -eq 0) { Note 'no tooltip showed while hovering' }
    HitTest 'while a tooltip shows'
    Note "click Diamond: $(ClickButton 'Diamond')"
    Check 'toolbar-click-after-hover' (WaitFor { IsOn 'Diamond' }) "Diamond on: $(IsOn 'Diamond')"
    $line = Center (Find 'Line')
    $away = Canvas 300 300
    Note "press Line, release on the canvas: $([LiveInput]::Drag(@($line[0], $line[0], $away[0]), @($line[1], ($line[1] + 2), $away[1]), $toolbar, 40))"
    Start-Sleep -Milliseconds 250
    Note "click Pen: $(ClickButton 'Pen')"
    Check 'toolbar-click-after-cancelled-press' (WaitFor { IsOn 'Pen' }) "Pen on: $(IsOn 'Pen')"

    # Escape releases input (Use apps); Resume drawing activates the canvas again.
    [void](WaitFor { (Foreground) -eq $overlay })
    Note "Escape on the canvas: $([LiveInput]::Tap(0x1B, $appPid))"
    Check 'escape-uses-apps' (WaitFor { (([LiveInput]::ExStyle($overlay)) -band 0x20) -ne 0 }) ('canvas ex-style 0x{0:X}' -f [LiveInput]::ExStyle($overlay))
    HitTest 'in Use apps'
    Note "click Resume drawing: $(ClickButton 'Resume drawing')"
    Check 'resume-activates-canvas' (WaitFor { (Foreground) -eq $overlay }) "foreground $([LiveInput]::Describe((Foreground)))"
    HitTest 'after Resume drawing'

    # The keyboard reaches the toolbar from the canvas (WIN-A11Y-02, WIN-A11Y-04): Shift+F10 opens the canvas's context
    # menu, and Up and Enter choose its last item, Focus drawing tools, which gives the toolbar the keyboard; Tab moves
    # on, Escape gives the keyboard back to the canvas while drawing goes on, and Escape there releases input.
    function MenuWindow() { [LiveInput]::Windows($appPid) | Where-Object { [LiveInput]::ClassName($_) -eq '#32768' } | Select-Object -First 1 }
    # The focused element's name while it is inside <window>, else null.
    function FocusedIn([IntPtr]$window) {
        try {
            $focused = [Windows.Automation.AutomationElement]::FocusedElement
            $walker = [Windows.Automation.TreeWalker]::RawViewWalker
            for ($element = $focused; $null -ne $element; $element = $walker.GetParent($element)) {
                if ([long]$element.Current.NativeWindowHandle -eq $window.ToInt64()) { return $focused.Current.Name }
            }
        }
        catch { Note "focused element unknown: $($_.Exception.Message)" }
        return $null
    }
    [void](WaitFor { (Foreground) -eq $overlay })
    Note "Shift+F10 on the canvas: $([LiveInput]::Key(0x10, $false, $appPid)), $([LiveInput]::Tap(0x79, $appPid)), $([LiveInput]::Key(0x10, $true, $appPid))"
    $menu = $null
    Check 'keyboard-canvas-menu' (WaitFor { $null -ne ($script:menu = MenuWindow) }) "menu $(Show $script:menu)"
    if ($null -ne $script:menu) {
        # The menu's items are in the canvas tests (CanvasSelectionTests); only Focus drawing tools gives the toolbar the
        # keyboard, which the next check sees.
        Note "Up and Enter in the menu: $([LiveInput]::Tap(0x26, $appPid)), $([LiveInput]::Tap(0x0D, $appPid))"
    }
    $firstFocus = $null
    Check 'keyboard-reaches-toolbar' (WaitFor { (Foreground) -eq $toolbar -and $null -ne ($script:firstFocus = FocusedIn $toolbar) }) "foreground $([LiveInput]::Describe((Foreground))); focused '$script:firstFocus'"
    Note "Tab in the toolbar: $([LiveInput]::Tap(0x09, $appPid))"
    $nextFocus = $null
    Check 'keyboard-tab-in-toolbar' (WaitFor { $null -ne ($script:nextFocus = FocusedIn $toolbar) -and $script:nextFocus -ne $script:firstFocus }) "from '$script:firstFocus' to '$script:nextFocus'"
    if ((Foreground) -eq $toolbar) { Note "Escape in the toolbar: $([LiveInput]::Tap(0x1B, $appPid))" }
    Check 'keyboard-escape-returns-to-canvas' (WaitFor { (Foreground) -eq $overlay -and (([LiveInput]::ExStyle($overlay)) -band 0x20) -eq 0 }) ("foreground {0}; canvas ex-style 0x{1:X}" -f [LiveInput]::Describe((Foreground)), [LiveInput]::ExStyle($overlay))
    if ((Foreground) -eq $overlay) { Note "Escape on the canvas: $([LiveInput]::Tap(0x1B, $appPid))" }
    Check 'keyboard-escape-releases-input' (WaitFor { (([LiveInput]::ExStyle($overlay)) -band 0x20) -ne 0 }) ('canvas ex-style 0x{0:X}' -f [LiveInput]::ExStyle($overlay))
    Note "click Resume drawing: $(ClickButton 'Resume drawing')"
    Check 'resume-activates-canvas' (WaitFor { (Foreground) -eq $overlay }) "foreground $([LiveInput]::Describe((Foreground)))"

    # Text: the Text tool opens the editor at a click; typing, Backspace and Ctrl+Z edit the text there, UI Automation
    # reads it as the focused edit field, and Escape commits it without releasing input (spec 009, WIN-INP-09).
    function Editor() {
        [Windows.Automation.AutomationElement]::FromHandle($overlay).FindFirst([Windows.Automation.TreeScope]::Descendants,
            ([Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Edit)))
    }
    function EditorValue() {
        $editor = Editor
        $pattern = $null
        if ($null -ne $editor -and $editor.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { return $pattern.Current.Value }
        return $null
    }
    Note "click Text: $(ClickButton 'Text')"
    $spot = Canvas 620 40
    Note "click the canvas: $([LiveInput]::Click($spot[0], $spot[1], $overlay))"
    Check 'text-editor-opens' (WaitFor { $null -ne (Editor) }) "edit field: $(if ($e = Editor) { "'$($e.Current.Name)'" } else { 'none' })"
    [void](WaitFor { (Foreground) -eq $overlay })
    Note "type 'Hi there': $([LiveInput]::Type('Hi there', $appPid))"
    Note "Backspace: $([LiveInput]::Tap(0x08, $appPid))"
    Check 'text-typed' (WaitFor { (EditorValue) -eq 'Hi ther' }) "value '$(EditorValue)'"
    Note "Ctrl+Z: $([LiveInput]::Key(0x11, $false, $appPid)), $([LiveInput]::Tap(0x5A, $appPid)), $([LiveInput]::Key(0x11, $true, $appPid))"
    Check 'text-undo-in-editor' (WaitFor { (EditorValue) -eq 'Hi there' }) "value '$(EditorValue)'"
    $editor = Editor
    Check 'text-editor-focused' ($null -ne $editor -and $editor.Current.HasKeyboardFocus) "has keyboard focus: $(if ($editor) { $editor.Current.HasKeyboardFocus })"
    # The caret blinks at Windows' rate: in captures over three blink times, only a column as tall as the text's line turns
    # off and on, and it changes again about one blink time later. The captures start wherever the blink is (the checks
    # before them take a varying time), so they never rely on its phase. The pixels stay in memory.
    $blink = [LiveInput]::GetCaretBlinkTime()
    if ($null -ne $editor -and $blink -gt 0 -and $blink -lt 5000) {
        $r = $editor.Current.BoundingRectangle
        $box = @([int][Math]::Floor($r.X), [int][Math]::Floor($r.Y), [int][Math]::Ceiling($r.Width), [int][Math]::Ceiling($r.Height))
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $previous = [LiveInput]::Capture($box[0], $box[1], $box[2], $box[3])
        $columns = ''
        $toggles = @()
        while ($clock.ElapsedMilliseconds -lt 3 * $blink -and $toggles.Count -lt 2) {
            Start-Sleep -Milliseconds 40
            $now = $clock.ElapsedMilliseconds
            $capture = [LiveInput]::Capture($box[0], $box[1], $box[2], $box[3])
            $changed = [LiveInput]::ChangedColumns($previous, $capture, $box[2], $box[3], 0.75)
            if ($changed) {
                $toggles += $now
                if (-not $columns) { $columns = $changed }
                $previous = $capture
            }
        }
        $span = if ($columns) { [int]$columns.Split('-')[1] - [int]$columns.Split('-')[0] + 1 } else { 0 }
        $interval = if ($toggles.Count -ge 2) { $toggles[1] - $toggles[0] } else { 0 }
        Check 'text-caret-blinks' ($span -ge 1 -and $span -le 4 -and $interval -ge 0.6 * $blink -and $interval -le 1.4 * $blink) "columns that changed in the $($box[2]) x $($box[3]) edit field: '$columns'; $interval ms between changes (blink $blink ms)"
    }
    else {
        Note "caret blink not checked: blink time $blink ms"
    }
    Note "Escape: $([LiveInput]::Tap(0x1B, $appPid))"
    Check 'text-escape-commits' (WaitFor { $null -eq (Editor) }) "edit field after Escape: $(if (Editor) { 'still open' } else { 'gone' })"
    Check 'text-escape-keeps-drawing' ((([LiveInput]::ExStyle($overlay)) -band 0x20) -eq 0) ('canvas ex-style 0x{0:X}' -f [LiveInput]::ExStyle($overlay))

    # Match outline (spec 001, Mac matchLabelToStroke): the Text tool edits a rectangle's label; while the stroke color popover
    # is open, Match outline shows and is unavailable while the label follows the rectangle's stroke; a palette color stops
    # it following, and Match outline makes it follow again with the stroke's paint on the toolbar. Escape closes the popover
    # and commits the label (the result shows it following the stroke).
    function StrokePopover() { [LiveInput]::Windows($appPid) | Where-Object { [LiveInput]::Title($_) -eq 'Stroke color' } | Select-Object -First 1 }
    function StrokeValue() {
        $named = ([Windows.Automation.AutomationElement]::FromHandle($toolbar)).FindAll([Windows.Automation.TreeScope]::Descendants,
            ([Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, 'Stroke color')))
        foreach ($well in $named) { $value = ValueOf $well; if ($null -ne $value) { return $value } }
        return $null
    }
    function MatchEnabled([IntPtr]$window) { $match = Find 'Match outline' $window; $null -ne $match -and $match.Current.IsEnabled }
    Note "click Rectangle: $(ClickButton 'Rectangle')"
    [void](DrawOnCanvas (Canvas 900 40) (Canvas 1060 130) 'a rectangle for a label')
    Note "click Text: $(ClickButton 'Text')"
    $inside = Canvas 980 85
    Note "click inside the rectangle: $([LiveInput]::Click($inside[0], $inside[1], $overlay))"
    Check 'label-editor-opens' (WaitFor { ($e = Editor) -and $e.Current.Name -like 'Shape label*' }) "edit field: $(if ($e = Editor) { "'$($e.Current.Name)'" } else { 'none' })"
    [void](WaitFor { (Foreground) -eq $overlay })
    Note "type 'API': $([LiveInput]::Type('API', $appPid))"
    [void](WaitFor { (EditorValue) -eq 'API' })
    $outline = StrokeValue
    # Full properties also title the well "Stroke color", so this clicks the button.
    $strokeWell = ([Windows.Automation.AutomationElement]::FromHandle($toolbar)).FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.AndCondition]::new(
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, 'Stroke color'),
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button)))
    $wellCenter = if ($null -ne $strokeWell) { Center $strokeWell } else { $null }
    Note "click Stroke color: $(if ($null -ne $wellCenter) { [LiveInput]::Click($wellCenter[0], $wellCenter[1], $toolbar) } else { 'no Stroke color button' })"
    $strokePopover = if (WaitFor { $null -ne (StrokePopover) }) { StrokePopover } else { $null }
    $match = if ($null -ne $strokePopover) { Find 'Match outline' $strokePopover } else { $null }
    Check 'label-match-outline-unavailable' ($null -ne $match -and -not $match.Current.IsEnabled) "Match outline: $(if ($match) { "enabled $($match.Current.IsEnabled)" } else { 'missing' }); stroke $outline"
    if ($null -ne $strokePopover) {
        # A palette color other than the stroke's own changes the label's paint. The popover shows for the first time: it
        # showed cloaked until its content had drawn, so its first click works.
        $own = if ($outline -like '#2B73E0*') { @('Yellow, hex #F5E338FF', '#F5E338') } else { @('Blue, hex #2B73E0FF', '#2B73E0') }
        # A gradient stroke (new profiles start with Sunset) opens the popover on Gradient; Solid shows the palette.
        if ($null -eq (Find $own[0] $strokePopover) -and $null -ne (Find 'Solid' $strokePopover)) {
            ClickUntil 'Solid' $strokePopover { $null -ne (Find $own[0] $strokePopover) }
        }
        ClickUntil $own[0] $strokePopover { MatchEnabled $strokePopover }
        Check 'label-own-color' ((MatchEnabled $strokePopover) -and (StrokeValue) -like "$($own[1])*") "Match outline enabled $(MatchEnabled $strokePopover); stroke $(StrokeValue)"
        Note "click Match outline: $(ClickButton 'Match outline' $strokePopover)"
        Check 'label-match-outline' (WaitFor { -not (MatchEnabled $strokePopover) -and (StrokeValue) -eq $outline }) "Match outline enabled $(MatchEnabled $strokePopover); stroke $(StrokeValue), the rectangle's $outline"
        if (WaitFor { (Foreground) -eq $strokePopover }) { Note "Escape in the popover: $([LiveInput]::Tap(0x1B, $appPid))" }
        [void](WaitFor { $null -eq (StrokePopover) })
    }
    Check 'label-popover-focus-back' (WaitFor { (Foreground) -eq $overlay -and $null -ne (Editor) }) "foreground $([LiveInput]::Describe((Foreground))); editor $(if (Editor) { 'open' } else { 'closed' })"
    Note "Escape: $([LiveInput]::Tap(0x1B, $appPid))"
    Check 'label-escape-commits' (WaitFor { $null -eq (Editor) }) "edit field after Escape: $(if (Editor) { 'still open' } else { 'gone' })"

    # Settings (WIN-UI-03): Ctrl+, on the canvas opens Settings on General with the keyboard; the share-style switch and the
    # theme change the in-memory profile; tabs switch; Alt+F4 hides the window, and the toolbar's gear opens it again. The
    # live check's Settings never opens a file dialog, a link, the clipboard or Windows Settings.
    function SettingsWindow() { [LiveInput]::Windows($appPid) | Where-Object { [LiveInput]::Title($_) -eq 'Annotator Settings' } | Select-Object -First 1 }
    function Typed([string]$name, $type, [IntPtr]$window) {
        $condition = [Windows.Automation.AndCondition]::new(
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $name),
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, $type))
        ([Windows.Automation.AutomationElement]::FromHandle($window)).FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
    }
    function ClickTyped([string]$name, $type, [IntPtr]$window) {
        $element = Typed $name $type $window
        if ($null -eq $element) { return "no $($type.ProgrammaticName) named '$name'" }
        $c = Center $element
        $result = [LiveInput]::Click($c[0], $c[1], $window)
        Start-Sleep -Milliseconds 250
        return $result
    }
    function Toggled([string]$name, [IntPtr]$window) {
        $element = Typed $name ([Windows.Automation.ControlType]::CheckBox) $window
        $toggle = $null
        $null -ne $element -and $element.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$toggle) -and
            $toggle.Current.ToggleState -eq [Windows.Automation.ToggleState]::On
    }
    function PopUpValue([string]$name, [IntPtr]$window) {
        $element = Typed $name ([Windows.Automation.ControlType]::ComboBox) $window
        $value = $null
        if ($null -ne $element -and $element.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$value)) { return $value.Current.Value }
        return $null
    }
    function ChooseTheme([string]$theme, [IntPtr]$window) {
        Note "click Theme: $(ClickTyped 'Theme' ([Windows.Automation.ControlType]::ComboBox) $window)"
        if (WaitFor { $null -ne (MenuItem $theme) }) {
            $c = Center (MenuItem $theme)
            $target = [LiveInput]::TopLevelAt($c[0], $c[1])
            if ([LiveInput]::OwnerOf($target) -eq $appPid) { Note "click ${theme}: $([LiveInput]::Click($c[0], $c[1], $target))" }
            else { Note "refused: $theme is under $([LiveInput]::Describe($target))" }
        }
        else { Note "the Theme menu did not open" }
    }
    [void](WaitFor { (Foreground) -eq $overlay })
    try { Note "Ctrl+, on the canvas: $([LiveInput]::Key(0x11, $false, $appPid)), $([LiveInput]::Tap(0xBC, $appPid))" }
    finally { [void][LiveInput]::Key(0x11, $true, $appPid) }
    Check 'ctrl-comma-opens-settings' (WaitFor { $null -ne (SettingsWindow) -and (Foreground) -eq (SettingsWindow) } 5000) "Settings $(Show (SettingsWindow)); foreground $([LiveInput]::Describe((Foreground)))"
    $settings = SettingsWindow
    if ($null -ne $settings) {
        Check 'settings-on-general' (WaitFor { Chosen $settings 'General' }) "General chosen: $(Chosen $settings 'General')"
        Check 'settings-app-icon' ([LiveInput]::HasIcons($settings)) "title-bar icons: $([LiveInput]::HasIcons($settings))"
        Note "click Share style between tools: $(ClickTyped 'Share style between tools' ([Windows.Automation.ControlType]::CheckBox) $settings)"
        Check 'settings-share-style-on' (WaitFor { Toggled 'Share style between tools' $settings }) "on: $(Toggled 'Share style between tools' $settings)"
        # Usage counts (WIN-PRV-01): off and unavailable, saying so, while Windows may not send them.
        $usage = Typed 'Share usage counts' ([Windows.Automation.ControlType]::CheckBox) $settings
        Check 'settings-usage-counts-unavailable' ($null -ne $usage -and -not $usage.Current.IsEnabled -and -not (Toggled 'Share usage counts' $settings) -and
            $null -ne (Find "Usage counts aren't available on Windows yet. Nothing is sent." $settings)) "switch: $(if ($usage) { "enabled $($usage.Current.IsEnabled), on $(Toggled 'Share usage counts' $settings)" } else { 'none' })"
        ChooseTheme 'Dark' $settings
        Check 'settings-theme-dark' (WaitFor { (PopUpValue 'Theme' $settings) -eq 'Dark' }) "Theme: '$(PopUpValue 'Theme' $settings)'"
        ChooseTheme 'System' $settings
        Check 'settings-theme-system' (WaitFor { (PopUpValue 'Theme' $settings) -eq 'System' } 4000) "Theme: '$(PopUpValue 'Theme' $settings)'"
        Note "click About: $(ClickTyped 'About' ([Windows.Automation.ControlType]::TabItem) $settings)"
        Check 'settings-about-tab' (WaitFor { (Chosen $settings 'About') -and $null -ne (Find 'Annotator app icon' $settings) }) "About chosen: $(Chosen $settings 'About')"
        # Check for updates (WIN-SYS-03): the live check's Store has an update, never installs it and never calls Windows. The
        # text on the canvas makes Annotator ask first: Not now installs nothing, and Install update reaches the Store, which
        # cancels.
        $updates = Typed 'Check for updates...' ([Windows.Automation.ControlType]::Button) $settings
        Check 'updates-check-enabled' ($null -ne $updates -and $updates.Current.IsEnabled) "enabled: $(if ($updates) { $updates.Current.IsEnabled } else { 'no button' }); Downloads link shown: $($null -ne (Find 'Open Downloads page...' $settings))"
        Check 'updates-store-ready' ($null -ne (Find 'The Microsoft Store installs updates automatically. Checks need an internet connection.' $settings)) 'the status line of a copy from the Store'
        foreach ($answer in 'Not now', 'Install update') {
            Note "click Check for updates...: $(ClickTyped 'Check for updates...' ([Windows.Automation.ControlType]::Button) $settings)"
            Check 'updates-question' (WaitFor { $null -ne (Find 'Install the update now?' $settings) }) "question shown: $($null -ne (Find 'Install the update now?' $settings))"
            Note "click ${answer}: $(ClickTyped $answer ([Windows.Automation.ControlType]::Button) $settings)"
            $status = if ($answer -eq 'Not now') { 'An update is ready in the Microsoft Store. Select Check for updates to install it.' } else { 'The update was not installed. Select Check for updates to try again.' }
            Check "updates-after-$($answer.ToLowerInvariant().Replace(' ', '-'))" (WaitFor { $null -ne (Find $status $settings) -and $null -eq (Find 'Install the update now?' $settings) }) "status: '$status' shown: $($null -ne (Find $status $settings))"
        }
        Note "click Permissions: $(ClickTyped 'Permissions' ([Windows.Automation.ControlType]::TabItem) $settings)"
        Check 'settings-permissions-tab' (WaitFor { (Chosen $settings 'Permissions') -and $null -ne (Find "Drawing needs no permissions. You're good to go." $settings) }) "Permissions chosen: $(Chosen $settings 'Permissions')"
        # Screen access (WIN-CAP-04): a development build has it, so the card offers Windows Settings and shows capture as
        # working or allowed, never Not allowed with Allow screen access.
        $captureStatus = @('Working', 'Allowed. Add a Blur, Pixelate or Distort region to check.', 'Not allowed' | Where-Object { $null -ne (Find $_ $settings) })
        Check 'settings-permissions-screen-access' ($null -ne (Find 'Open screen recording settings' $settings) -and $null -eq (Find 'Allow screen access' $settings) -and
            $captureStatus.Count -eq 1 -and $captureStatus[0] -ne 'Not allowed') "status: '$($captureStatus -join "', '")'; Allow screen access shown: $($null -ne (Find 'Allow screen access' $settings))"
        # The Pointer tab shows the Pointer page the app sets (SettingsWindow.SetPage): Show halo turns the cursor halo on and
        # off, as the toolbar's Spotlight menu does.
        Note "click Pointer: $(ClickTyped 'Pointer' ([Windows.Automation.ControlType]::TabItem) $settings)"
        Check 'settings-pointer-tab' (WaitFor { (Chosen $settings 'Pointer') -and $null -ne (Find 'Show halo' $settings) }) "Pointer chosen: $(Chosen $settings 'Pointer'); Show halo: $($null -ne (Find 'Show halo' $settings))"
        # The page is made when its tab first shows: the switch takes clicks once it is laid out.
        # The switch, not its label, which UI Automation names the same and lists first.
        function HaloSwitch() { Typed 'Show halo' ([Windows.Automation.ControlType]::CheckBox) $settings }
        [void](WaitFor { $e = HaloSwitch; $null -ne $e -and -not $e.Current.BoundingRectangle.IsEmpty -and $e.Current.BoundingRectangle.Width -gt 0 } 2000)
        Start-Sleep -Milliseconds 400
        $halo = HaloSwitch
        if ($null -ne $halo) {
            $c = Center $halo
            Note "click Show halo: $([LiveInput]::Click($c[0], $c[1], $settings))"
            Check 'settings-show-halo-on' (WaitFor { $null -ne (Find 'Stop cursor halo') }) "Stop cursor halo in the toolbar: $($null -ne (Find 'Stop cursor halo'))"
            Note "click Show halo again: $([LiveInput]::Click($c[0], $c[1], $settings))"
            Check 'settings-show-halo-off' (WaitFor { $null -eq (Find 'Stop cursor halo') }) "Stop cursor halo in the toolbar: $($null -ne (Find 'Stop cursor halo'))"
        }
    }

    # Toolbar & Shortcuts (spec 005, ADR 0012), on its tab in the Settings window that is still open. Keys record into the
    # in-memory profile; a global shortcut is recorded only as Ctrl+Alt+Shift+9, which the check's own
    # registrar holds until it quits, and no default shortcut is ever pressed, so a running Annotator keeps its own.
    $appProcess = Get-Process -Id $appPid
    function CpuOver([int]$milliseconds) {
        $appProcess.Refresh(); $before = $appProcess.TotalProcessorTime.TotalMilliseconds
        Start-Sleep -Milliseconds $milliseconds
        $appProcess.Refresh(); [Math]::Round($appProcess.TotalProcessorTime.TotalMilliseconds - $before)
    }
    $appProcess.Refresh(); $memoryBefore = $appProcess.PrivateMemorySize64
    # The Toolbar & Shortcuts tab of the same Settings window shows the page the app sets (SettingsWindow.SetPage).
    $page = [IntPtr]::Zero
    if ($null -ne $settings -and (SettingsWindow) -eq $settings) {
        Note "click Toolbar & Shortcuts: $(ClickTyped 'Toolbar & Shortcuts' ([Windows.Automation.ControlType]::TabItem) $settings)"
        if (WaitFor { (Chosen $settings 'Toolbar & Shortcuts') -and $null -ne (Find 'Pen key' $settings) }) { $page = $settings }
    }
    Check 'shortcuts-page-opens' ($page -ne [IntPtr]::Zero) "page $(Show $page)"
    if ($page -ne [IntPtr]::Zero) {
        Start-Sleep -Milliseconds 500
        $appProcess.Refresh()
        Note ("private memory {0:F1} MB more with the page" -f (($appProcess.PrivateMemorySize64 - $memoryBefore) / 1MB))
        Note "CPU while the page shows and nothing happens: $(CpuOver 3000) ms over 3 s"
        function ById([IntPtr]$window, [string]$id) {
            ([Windows.Automation.AutomationElement]::FromHandle($window)).FindFirst([Windows.Automation.TreeScope]::Descendants,
                ([Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $id)))
        }
        function KeyValue([string]$name) {
            $element = Find $name $page
            $pattern = $null
            if ($null -ne $element -and $element.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { return $pattern.Current.Value }
            return $null
        }
        # The newest toast: one that is fading out can still be in the tree for a moment.
        function Toast() {
            $toasts = ([Windows.Automation.AutomationElement]::FromHandle($page)).FindAll([Windows.Automation.TreeScope]::Descendants,
                ([Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, 'settings-toast')))
            if ($toasts.Count -eq 0) { return '' }
            $texts = $toasts[$toasts.Count - 1].FindAll([Windows.Automation.TreeScope]::Descendants,
                ([Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text)))
            ($texts | ForEach-Object { $_.Current.Name }) -join ' '
        }
        function Record([string]$name, [uint16]$vk) {
            Note "click ${name}: $(ClickPage $name)"
            [void](WaitFor { (KeyValue $name) -like 'Waiting*' })
            Note "key 0x$('{0:X}' -f $vk): $([LiveInput]::Tap($vk, $appPid))"
            Start-Sleep -Milliseconds 300
        }
        function Placed($element) { $null -ne $element -and -not $element.Current.BoundingRectangle.IsEmpty -and $element.Current.BoundingRectangle.Width -gt 0 }
        # Clicks a control of the page once it is laid out and in view.
        function ClickPage([string]$name) {
            if (-not (WaitFor { Placed (Find $name $page) } 2000)) {
                $element = Find $name $page
                return "not placed: '$name' $(if ($null -ne $element) { $element.Current.BoundingRectangle } else { 'missing' })"
            }
            $c = Center (Find $name $page)
            $target = [LiveInput]::TopLevelAt($c[0], $c[1])
            if ([LiveInput]::OwnerOf($target) -ne $appPid) { return "refused: '$name' is under $([LiveInput]::Describe($target))" }
            $result = [LiveInput]::Click($c[0], $c[1], $target)
            Start-Sleep -Milliseconds 250
            return $result
        }
        # The page scrolls: the toolbar's keys and Reset are at the top, the keys that work in any app at the bottom.
        function ScrollPage([double]$percent) {
            $scroller = ([Windows.Automation.AutomationElement]::FromHandle($page)).FindFirst([Windows.Automation.TreeScope]::Descendants,
                ([Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::IsScrollPatternAvailableProperty, $true)))
            $scroll = $null
            if ($null -ne $scroller -and $scroller.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern, [ref]$scroll) -and $scroll.Current.VerticallyScrollable) {
                $scroll.SetScrollPercent([Windows.Automation.ScrollPattern]::NoScroll, $percent)
                Start-Sleep -Milliseconds 400
            }
        }
        function ChooseReset([string]$item) {
            ScrollPage 0
            Note "click Reset: $(ClickPage 'Reset')"
            if (WaitFor { Placed (MenuItem $item) }) {
                $c = Center (MenuItem $item)
                $target = [LiveInput]::TopLevelAt($c[0], $c[1])
                if ([LiveInput]::OwnerOf($target) -eq $appPid) { Note "click ${item}: $([LiveInput]::Click($c[0], $c[1], $target))" }
                else { Note "refused: $item is under $([LiveInput]::Describe($target))" }
            }
            else { Note "the Reset menu did not show $item" }
            Start-Sleep -Milliseconds 400
        }

        # A tool key: J for Pen, then 1, which Select had, with Undo; Delete removes the key.
        Record 'Pen key' 0x4A
        Check 'shortcut-key-records' ((WaitFor { (KeyValue 'Pen key') -eq 'J' -and (Toast) -like 'J is now the key for Pen.*' }) -and (Foreground) -eq $page) "Pen key '$(KeyValue 'Pen key')'; toast '$(Toast)'; foreground $([LiveInput]::Describe((Foreground)))"
        Record 'Pen key' 0x31
        Check 'shortcut-key-moves' (WaitFor { (KeyValue 'Pen key') -eq '1' -and (KeyValue 'Select key') -eq 'None' -and (Toast) -like '*Select has no key now*' }) "Pen key '$(KeyValue 'Pen key')', Select key '$(KeyValue 'Select key')'; toast '$(Toast)'"
        $undo = ById $page 'undo-key-change'
        if ($null -ne $undo) { $c = Center $undo; Note "click Undo: $([LiveInput]::Click($c[0], $c[1], $page))" } else { Note 'no Undo link' }
        Check 'shortcut-key-undo' (WaitFor { (KeyValue 'Pen key') -eq 'J' -and (KeyValue 'Select key') -eq '1' }) "Pen key '$(KeyValue 'Pen key')', Select key '$(KeyValue 'Select key')'; toast '$(Toast)'"
        Record 'Pen key' 0x2E
        Check 'shortcut-key-delete' (WaitFor { (KeyValue 'Pen key') -eq 'None' -and (Toast) -like 'Pen has no key now.*' }) "Pen key '$(KeyValue 'Pen key')'; toast '$(Toast)'"
        # While a key listens its ring pulses (a composition animation); Escape stops listening.
        Note "click Eraser key: $(ClickPage 'Eraser key')"
        [void](WaitFor { (KeyValue 'Eraser key') -like 'Waiting*' })
        Note "CPU while a key listens: $(CpuOver 2000) ms over 2 s"
        Note "Escape: $([LiveInput]::Tap(0x1B, $appPid))"
        Check 'shortcut-escape-stops-listening' (WaitFor { (KeyValue 'Eraser key') -eq '0' }) "Eraser key '$(KeyValue 'Eraser key')'"

        # Dragging Highlighter down to More tools takes it out of the toolbar; Reset puts the default tools and keys back.
        ScrollPage 0
        $tile = ById $page 'toolbar-tool-highlighter'
        $tray = Find 'More tools' $page
        if ($null -ne $tile -and $null -ne $tray) {
            $from = Center $tile
            $r = $tray.Current.BoundingRectangle
            $to = @([int]($r.X + $r.Width / 2), [int]($r.Y + [Math]::Min(120, $r.Height / 2)))
            $xs = @(); $ys = @()
            for ($i = 0; $i -le 12; $i++) { $xs += [int]($from[0] + ($to[0] - $from[0]) * $i / 12); $ys += [int]($from[1] + ($to[1] - $from[1]) * $i / 12) }
            Note "drag Highlighter to More tools: $([LiveInput]::Drag($xs, $ys, $page, 30))"
        }
        Check 'toolbar-drag-removes' (WaitFor { $null -eq (ById $page 'toolbar-tool-highlighter') -and $null -ne (ById $page 'more-tool-highlighter') -and $null -eq (Find 'Highlighter') } 2000) "in the strip: $($null -ne (ById $page 'toolbar-tool-highlighter')); in More tools: $($null -ne (ById $page 'more-tool-highlighter')); toolbar button: $($null -ne (Find 'Highlighter'))"
        ChooseReset 'Default tools and order'
        Check 'reset-default-tools' (WaitFor { $null -ne (ById $page 'toolbar-tool-highlighter') -and $null -ne (Find 'Highlighter') }) "in the strip: $($null -ne (ById $page 'toolbar-tool-highlighter')); toolbar button: $($null -ne (Find 'Highlighter'))"
        ChooseReset 'Default keys for tools and buttons'
        Check 'reset-default-keys' (WaitFor { (KeyValue 'Pen key') -eq '7' }) "Pen key '$(KeyValue 'Pen key')'"
        ScrollPage 0
        Record 'Pen key' 0x4A
        [void](WaitFor { (KeyValue 'Pen key') -eq 'J' })

        # A global shortcut: Ctrl+Alt+Shift+9 for Show / hide. Recording Draw then gets the registered hotkey through the
        # recording gate and refuses it, since Show / hide has it.
        ScrollPage 100
        function Chord([string]$name) {
            Note "click ${name}: $(ClickPage $name)"
            [void](WaitFor { (KeyValue $name) -like 'Waiting*' })
            try {
                foreach ($vk in 0x11, 0x12, 0x10) { [void][LiveInput]::Key([uint16]$vk, $false, $appPid) }
                Note "Ctrl+Alt+Shift+9 for ${name}: $([LiveInput]::Tap(0x39, $appPid))"
            }
            finally { foreach ($vk in 0x10, 0x12, 0x11) { [void][LiveInput]::Key([uint16]$vk, $true, $appPid) } }
            Start-Sleep -Milliseconds 400
        }
        Chord 'Show / hide shortcut'
        Check 'global-shortcut-records' (WaitFor { (KeyValue 'Show / hide shortcut') -eq 'Ctrl + Alt + Shift + 9' }) "Show / hide '$(KeyValue 'Show / hide shortcut')'; toast '$(Toast)'"
        Chord 'Draw shortcut'
        Check 'global-shortcut-clash-refused' (WaitFor { (Toast) -like '*already does Show / hide*' -and (KeyValue 'Draw shortcut') -like 'Waiting*' }) "Draw '$(KeyValue 'Draw shortcut')'; toast '$(Toast)'"
        Note "Escape: $([LiveInput]::Tap(0x1B, $appPid))"
        Check 'global-shortcut-escape-stops' (WaitFor { (KeyValue 'Draw shortcut') -eq 'Ctrl + Shift + 2' }) "Draw '$(KeyValue 'Draw shortcut')'"
    }
    # Alt+F4 hides Settings, and the toolbar's gear opens it again on General.
    if ($null -ne $settings) {
        if (WaitFor { (Foreground) -eq $settings }) {
            try { Note "Alt+F4 in Settings: $([LiveInput]::Key(0x12, $false, $appPid)), $([LiveInput]::Tap(0x73, $appPid))" }
            finally { [void][LiveInput]::Key(0x12, $true, $appPid) }
        }
        Check 'settings-close-hides' (WaitFor { $null -eq (SettingsWindow) }) "Settings $(Show (SettingsWindow))"
        Note "click Settings and shortcuts: $(ClickButton 'Settings and shortcuts')"
        Check 'gear-opens-settings' (WaitFor { (SettingsWindow) -eq $settings -and (Chosen $settings 'General') }) "Settings $(Show (SettingsWindow)); General chosen: $(Chosen $settings 'General')"
        # Settings was hidden by cloaking, so its first click works as soon as it shows again (ClickUntil).
        ClickUntil 'About' $settings { Chosen $settings 'About' }
        if (WaitFor { (Foreground) -eq $settings }) {
            try { Note "Alt+F4 in Settings again: $([LiveInput]::Key(0x12, $false, $appPid)), $([LiveInput]::Tap(0x73, $appPid))" }
            finally { [void][LiveInput]::Key(0x12, $true, $appPid) }
        }
        [void](WaitFor { $null -eq (SettingsWindow) })
    }
    HitTest 'after Settings closed'
    # The canvas uses the page's key: J chooses Pen.
    Note "click Select: $(ClickButton 'Select')"
    [void](WaitFor { (Foreground) -eq $overlay })
    Note "J on the canvas: $([LiveInput]::Tap(0x4A, $appPid))"
    Check 'canvas-uses-new-key' (WaitFor { IsOn 'Pen' }) "Pen on: $(IsOn 'Pen'); foreground $([LiveInput]::Describe((Foreground)))"

    # Boards, PNG export, Insert image and the error window (WIN-SYS-04, spec 005): Ctrl+S saves the board with the Save
    # dialog, the Tools window's File menu exports the PNG, Ctrl+O reports a broken board in the error window and keeps the
    # drawing, then opens the saved board, which replaces a shape drawn after the save; Insert image adds the exported PNG.
    # The dialogs are the app's own windows; they remember their folder under the live check's own ID, never the person's.
    $boardPath = Join-Path $Out 'board-check.annotator'
    $brokenPath = Join-Path $Out 'broken-check.annotator'
    $pngPath = Join-Path $Out 'export-check.png'
    foreach ($stale in $boardPath, $brokenPath, $pngPath) { Remove-Item $stale -ErrorAction SilentlyContinue }
    Set-Content -Path $brokenPath -Value '{ "schemaVersion": 2, "elements": [' -NoNewline
    # The dialogs' Win32 controls have no UI Automation patterns in this client, so the check sets the file name with
    # WM_SETTEXT and chooses OK with WM_COMMAND, messages to the app's own dialog only.
    Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class BoardDialogs
{
    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, string lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, StringBuilder lParam);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    static string ClassOf(IntPtr hwnd) { var b = new StringBuilder(64); GetClassName(hwnd, b, 64); return b.ToString(); }

    static bool Within(IntPtr hwnd, int id) { for (int level = 0; level < 4 && hwnd != IntPtr.Zero; level++, hwnd = GetParent(hwnd)) if (GetDlgCtrlID(hwnd) == id) return true; return false; }

    static IntPtr Child(IntPtr dialog, Func<IntPtr, bool> match)
    {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(dialog, (h, l) => { if (match(h)) { found = h; return false; } return true; }, IntPtr.Zero);
        return found;
    }

    /// <summary>The file name field: the Edit 1001 of a Save dialog or the Edit in the combo box 1148 of an Open dialog.</summary>
    public static IntPtr FileName(IntPtr dialog) { return Child(dialog, h => ClassOf(h) == "Edit" && (Within(h, 1001) || Within(h, 1148))); }

    public static string Text(IntPtr hwnd) { var b = new StringBuilder(1024); SendMessage(hwnd, 0x000D, (IntPtr)b.Capacity, b); return b.ToString(); }

    public static void SetText(IntPtr hwnd, string text) { SendMessage(hwnd, 0x000C, IntPtr.Zero, text); }

    /// <summary>The dialog's OK (Save, Open or Insert), as its button sends it.</summary>
    public static bool Ok(IntPtr dialog)
    {
        IntPtr button = Child(dialog, h => ClassOf(h) == "Button" && GetDlgCtrlID(h) == 1);
        return PostMessage(dialog, 0x0111, (IntPtr)1, button);
    }

    public static bool Cancel(IntPtr dialog) { return PostMessage(dialog, 0x0111, (IntPtr)2, IntPtr.Zero); }
}
'@
    function BoardDialog() { [LiveInput]::Windows($appPid) | Where-Object { [LiveInput]::ClassName($_) -eq '#32770' } | Select-Object -First 1 }
    function BoardError() {
        [LiveInput]::Windows($appPid) | Where-Object { [LiveInput]::Title($_) -eq 'Annotator' -and [LiveInput]::ClassName($_) -eq 'WinUIDesktopWin32WindowClass' } | Select-Object -First 1
    }
    function OnTop([IntPtr]$window) {
        $r = New-Object LiveInput+RECT
        [void][LiveInput]::GetWindowRect($window, [ref]$r)
        $found = [LiveInput]::TopLevelAt([int](($r.Left + $r.Right) / 2), [int](($r.Top + $r.Bottom) / 2))
        @(($found -eq $window), "$([LiveInput]::Describe($window)); at its center $([LiveInput]::Describe($found))")
    }
    # "7 objects, 1 selected. Drawing.": the canvas window's own UI Automation element is the "Annotation canvas" group.
    function BoardObjects() {
        $canvas = [Windows.Automation.AutomationElement]::FromHandle($overlay)
        $value = $null
        if ($null -ne $canvas -and $canvas.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$value) -and $value.Current.Value -match '^(\d+) object') { return [int]$Matches[1] }
        -1
    }
    function BoardCtrl([uint16]$key, [string]$what) {
        try { Note "Ctrl+${what}: $([LiveInput]::Key(0x11, $false, $appPid)), $([LiveInput]::Tap($key, $appPid))" }
        finally { [void][LiveInput]::Key(0x11, $true, $appPid) }
    }
    # Fills in the open dialog's file name and chooses its OK; true once the dialog closed. A dialog left open is cancelled.
    # Note and Check write their line to the output, so here they go to the host and only the result is returned.
    function BoardChoose([string]$path, [string]$what) {
        if (-not (WaitFor { $null -ne (BoardDialog) } 5000)) { Check "$what-dialog-opens" $false 'no file dialog' | Out-Host; return $false }
        $dialog = BoardDialog
        $top = OnTop $dialog
        Check "$what-dialog-above-canvas" $top[0] "'$([LiveInput]::Title($dialog))' $($top[1])" | Out-Host
        [void](WaitFor { [BoardDialogs]::FileName($dialog) -ne [IntPtr]::Zero } 3000)
        $field = [BoardDialogs]::FileName($dialog)
        if ($field -ne [IntPtr]::Zero) {
            Note "$what dialog's file name: '$([BoardDialogs]::Text($field))'" | Out-Host
            [BoardDialogs]::SetText($field, $path)
            Note "$what OK with '$([BoardDialogs]::Text($field))': $([BoardDialogs]::Ok($dialog))" | Out-Host
        }
        else {
            Note "$what dialog has no file name field" | Out-Host
        }
        $closed = WaitFor { $null -eq (BoardDialog) } 5000
        if (-not $closed -and $null -ne (BoardDialog)) {
            Note "the dialog is still open; Cancel: $([BoardDialogs]::Cancel((BoardDialog)))" | Out-Host
            [void](WaitFor { $null -eq (BoardDialog) } 3000)
        }
        $closed
    }
    [void](WaitFor { (Foreground) -eq $overlay })
    $saved = BoardObjects
    BoardCtrl 0x53 'S on the canvas'
    $closed = BoardChoose $boardPath 'save'
    $board = if (WaitFor { Test-Path $boardPath } 5000) { Get-Content $boardPath -Raw | ConvertFrom-Json } else { $null }
    Check 'board-saved' ($closed -and $null -ne $board -and $board.schemaVersion -ge 1 -and @($board.elements).Count -eq $saved) "dialog closed: $closed; saved objects: $(if ($board) { @($board.elements).Count } else { 'no file' }) of $saved"
    Check 'board-save-focus-back' (WaitFor { (Foreground) -eq $overlay }) "foreground $([LiveInput]::Describe((Foreground)))"
    HitTest 'after the Save dialog'
    Note "click Ellipse: $(ClickButton 'Ellipse')"
    [void](DrawOnCanvas (Canvas 660 200) (Canvas 760 260) 'an ellipse after the save')
    Check 'board-drawn-after-save' (WaitFor { (BoardObjects) -eq $saved + 1 }) "objects: $(BoardObjects)"

    # Export PNG from the Tools window's File menu: the PNG has the made-up display's pixel size. The Tools window was hidden
    # by cloaking, so its first click after Ctrl+L works (ClickUntil).
    CtrlL 'for the File menu'
    $tools = ToolsWindow
    if ($null -ne $tools -and (WaitFor { (Foreground) -eq $tools })) {
        ClickUntil 'File' $tools { $null -ne (MenuItem 'Export PNG...') }
        if (WaitFor { $null -ne (MenuItem 'Export PNG...') }) {
            $c = Center (MenuItem 'Export PNG...')
            $target = [LiveInput]::TopLevelAt($c[0], $c[1])
            if ([LiveInput]::OwnerOf($target) -eq $appPid) { Note "click Export PNG...: $([LiveInput]::Click($c[0], $c[1], $target))" }
            else { Note "refused: Export PNG... is under $([LiveInput]::Describe($target))" }
        }
    }
    $closed = BoardChoose $pngPath 'export'
    $size = if (WaitFor { (Test-Path $pngPath) -and (Get-Item $pngPath).Length -gt 24 } 10000) {
        $bytes = [IO.File]::ReadAllBytes($pngPath)
        @(((([int]$bytes[16]) -shl 24) -bor (([int]$bytes[17]) -shl 16) -bor (([int]$bytes[18]) -shl 8) -bor [int]$bytes[19]),
            ((([int]$bytes[20]) -shl 24) -bor (([int]$bytes[21]) -shl 16) -bor (([int]$bytes[22]) -shl 8) -bor [int]$bytes[23]))
    }
    else { @(0, 0) }
    Check 'png-exported' ($closed -and $size[0] -eq $area[2] -and $size[1] -eq $area[3]) "dialog closed: $closed; PNG $($size -join ' x '), display $($area[2]) x $($area[3])"
    Check 'png-export-focus-back' (WaitFor { (Foreground) -eq $overlay }) "foreground $([LiveInput]::Describe((Foreground)))"

    # A broken board: the error window says so above the canvas, OK closes it, and the drawing has not changed.
    BoardCtrl 0x4F 'O on the canvas'
    [void](BoardChoose $brokenPath 'open-broken')
    $panel = if (WaitFor { $null -ne (BoardError) } 5000) { BoardError } else { $null }
    $message = if ($null -ne $panel) { (Find 'Could not complete this action' $panel) } else { $null }
    $texts = if ($null -ne $panel) {
        @(([Windows.Automation.AutomationElement]::FromHandle($panel)).FindAll([Windows.Automation.TreeScope]::Descendants,
                ([Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text))) |
            ForEach-Object { $_.Current.Name })
    }
    else { @() }
    $reason = @($texts | Where-Object { $_ -like 'The drawing could not be opened: Choose another Annotator board.*' })
    Check 'error-window-shows' ($null -ne $message -and $reason.Count -eq 1) "window $(Show $panel); texts: $($texts -join ' | ')"
    if ($null -ne $panel) {
        $top = OnTop $panel
        Check 'error-window-above-canvas' $top[0] $top[1]
        Check 'broken-board-keeps-drawing' ((BoardObjects) -eq $saved + 1) "objects: $(BoardObjects)"
        # No idle work while the error window shows (WIN-PERF-03): the app's CPU time over 3 seconds without input.
        Start-Sleep -Milliseconds 500
        $before = (Get-Process -Id $appPid).TotalProcessorTime
        Start-Sleep -Seconds 3
        Note ("the app's CPU time over 3 s while the error window shows: {0:F0} ms" -f ((Get-Process -Id $appPid).TotalProcessorTime - $before).TotalMilliseconds)
        # The error window shows for the first time here: it showed cloaked until its content had drawn, so its first click works.
        ClickUntil 'OK' $panel { $null -eq (BoardError) }
        Check 'error-window-ok-closes' (WaitFor { $null -eq (BoardError) }) "window $(Show (BoardError))"
    }
    Check 'error-window-focus-back' (WaitFor { (Foreground) -eq $overlay }) "foreground $([LiveInput]::Describe((Foreground)))"

    # The saved board replaces the drawing: the ellipse drawn after the save is gone.
    BoardCtrl 0x4F 'O on the canvas again'
    $closed = BoardChoose $boardPath 'open'
    Check 'board-opened' ($closed -and (WaitFor { (BoardObjects) -eq $saved } 5000)) "dialog closed: $closed; objects: $(BoardObjects) of $saved"

    # Insert image (Tools, Extras): the exported PNG becomes a selected image object.
    CtrlL 'for Insert image'
    $tools = ToolsWindow
    if ($null -ne $tools -and (WaitFor { (Foreground) -eq $tools })) {
        ClickUntil 'Extras' $tools { $null -ne (Find 'Insert image' $tools) }
        Note "click Insert image: $(ClickButton 'Insert image' $tools)"
    }
    $closed = BoardChoose $pngPath 'insert'
    Check 'image-inserted' ($closed -and (WaitFor { (BoardObjects) -eq $saved + 1 } 5000) -and (WaitFor { IsOn 'Select' })) "dialog closed: $closed; objects: $(BoardObjects); Select on: $(IsOn 'Select')"
    Check 'image-focus-back' (WaitFor { (Foreground) -eq $overlay }) "foreground $([LiveInput]::Describe((Foreground)))"
    HitTest 'after the board steps'

    # Shift: lines and arrows snap to 45 degrees, shapes become squares, the highlighter draws straight.
    foreach ($step in @(
            @{ Tool = 'Highlighter'; From = @(60, 235); To = @(190, 280) },
            @{ Tool = 'Line'; From = @(210, 25); To = @(320, 90) },
            @{ Tool = 'Arrow'; From = @(350, 200); To = @(450, 145) },
            @{ Tool = 'Rectangle'; From = @(475, 25); To = @(575, 65) })) {
        Note "click $($step.Tool): $(ClickButton $step.Tool)"
        [void](DrawOnCanvas (Canvas $step.From[0] $step.From[1]) (Canvas $step.To[0] $step.To[1]) "$($step.Tool) with Shift" 0x10)
    }
    # The last shape drawn is selected and the tool is Select, as on the Mac; read now, because later steps (sharing with zoom)
    # change the selection.
    $canvasElement = [Windows.Automation.AutomationElement]::FromHandle($overlay)
    $canvasValue = $null
    $summaryAfterShift = if ($null -ne $canvasElement -and $canvasElement.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$canvasValue)) { $canvasValue.Current.Value } else { '' }
    $selectAfterShift = IsOn 'Select'

    # On-screen tip (spec 009, WIN-OVR-09): it shows for 5 seconds after it wakes, so Use apps and Resume drawing wake it
    # first. The canvas reads it as static text while drawing, then its Turn off tips button; like the Mac's, it shows no
    # status message, so an ink key leaves it as it was.
    function TipText() {
        $tip = [Windows.Automation.AutomationElement]::FromHandle($overlay).FindFirst([Windows.Automation.TreeScope]::Descendants,
            ([Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text)))
        if ($null -eq $tip) { return '' }
        return $tip.Current.Name
    }
    function Flat([string]$text) { $text -replace "`r?`n", ' | ' }
    [void](WaitFor { (Foreground) -eq $overlay })
    Note "Escape on the canvas: $([LiveInput]::Tap(0x1B, $appPid))"
    [void](WaitFor { (([LiveInput]::ExStyle($overlay)) -band 0x20) -ne 0 })
    Note "click Resume drawing: $(ClickButton 'Resume drawing')"
    [void](WaitFor { (Foreground) -eq $overlay })
    Check 'tip-shows-while-drawing' (WaitFor { (TipText) -eq 'Drawing  ·  Escape: use apps' } 4000) "tip '$(Flat (TipText))'"
    $tipClose = [Windows.Automation.AutomationElement]::FromHandle($overlay).FindFirst([Windows.Automation.TreeScope]::Descendants,
        ([Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, 'Turn off tips')))
    Check 'tip-close-button' ($null -ne $tipClose -and $tipClose.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and $tipClose.Current.HelpText -eq 'Turn them back on in Settings > General.') "button $($null -ne $tipClose)"
    $tipBefore = TipText
    Note "R (red ink): $([LiveInput]::Tap(0x52, $appPid))"
    Start-Sleep -Milliseconds 400
    Check 'tip-no-status-line' ((TipText) -eq $tipBefore -and (TipText) -notlike '*Red ink*') "tip '$(Flat (TipText))'"

    # First use (WIN-UI-04, spec 005): after the first-use event the tutorial's welcome card shows as at a launch, without
    # the keyboard. Start tutorial turns drawing on and turns the card into the coach next to the toolbar, which never takes
    # the keyboard: the Rectangle key, a drag on the canvas and the blue ink key each finish a step, while the tutorial's layer
    # shows the ring, the ghost and a step's confetti without taking clicks. End tutorial closes it. Four recorded days
    # of use make the coffee reminder due on the next Draw; Use apps shows it next to the toolbar without the keyboard, and
    # Not now snoozes it.
    function Tutorial() { [LiveInput]::Windows($appPid) | Where-Object { [LiveInput]::Title($_) -eq 'Annotator tutorial' } | Select-Object -First 1 }
    function TutorialLayer() { [LiveInput]::Windows($appPid) | Where-Object { [LiveInput]::ClassName($_) -eq 'Annotator.Platform.TutorialOverlay' } | Select-Object -First 1 }
    function Reminder() { [LiveInput]::Windows($appPid) | Where-Object { [LiveInput]::Title($_) -eq 'Enjoying Annotator?' } | Select-Object -First 1 }
    Note "Escape on the canvas: $([LiveInput]::Tap(0x1B, $appPid))"
    [void](WaitFor { (([LiveInput]::ExStyle($overlay)) -band 0x20) -ne 0 })
    $before = Foreground
    $firstUse = [Threading.EventWaitHandle]::OpenExisting("Local\Annotator.LiveCheck.$appPid.first-use")
    [void]$firstUse.Set()
    $firstUse.Dispose()
    Check 'tutorial-shows' (WaitFor { $null -ne (Tutorial) } 5000) "card $(Show (Tutorial))"
    $card = Tutorial
    Check 'tutorial-without-focus' ($null -ne $card -and (Foreground) -ne $card) "foreground $(Show (Foreground)), before $(Show $before)"
    if ($null -ne $card) {
        $frame = New-Object LiveInput+RECT
        [void][LiveInput]::GetWindowRect($card, [ref]$frame)
        $front = [LiveInput]::TopLevelAt([int](($frame.Left + $frame.Right) / 2), [int](($frame.Top + $frame.Bottom) / 2))
        Check 'tutorial-in-front' ($front -eq $card) "at the card's center: $(Show $front)"
        Check 'tutorial-welcome' (WaitFor { Named $card 'Welcome to Annotator' }) "welcome: $(Named $card 'Welcome to Annotator')"
        # Share usage counts (WIN-PRV-01): off and unavailable on the welcome card, with the Windows note.
        $counts = Typed 'Share usage counts' ([Windows.Automation.ControlType]::CheckBox) $card
        Check 'tutorial-usage-counts-unavailable' ($null -ne $counts -and -not $counts.Current.IsEnabled -and $null -ne (Find 'Not available on Windows yet.' $card)) "check box: $(if ($counts) { "enabled $($counts.Current.IsEnabled)" } else { 'none' })"
        ClickUntil 'Start tutorial' $card { Named $card 'Grab the Rectangle' }
        Check 'tutorial-starts' (WaitFor { Named $card 'Grab the Rectangle' } 4000) "first step: $(Named $card 'Grab the Rectangle')"
        Check 'tutorial-starts-drawing' (WaitFor { (Foreground) -eq $overlay } 3000) "foreground $(Show (Foreground))"
        Check 'tutorial-coach-without-focus' ((Foreground) -ne $card) "foreground $(Show (Foreground))"
        $layer = TutorialLayer
        Check 'tutorial-layer-shows' (WaitFor { $null -ne ($script:layer = TutorialLayer) -and [LiveInput]::IsWindowVisible($script:layer) } 3000) "layer $(Show $layer)"
        Check 'tutorial-layer-passes-clicks' ($null -ne $layer -and ([LiveInput]::ExStyle($layer) -band 0x20) -ne 0) ('ex-style 0x{0:X}' -f $(if ($layer) { [LiveInput]::ExStyle($layer) } else { 0 }))
        [void](WaitFor { (Foreground) -eq $overlay })
        Note "2 (Rectangle): $([LiveInput]::Tap(0x32, $appPid))"
        Check 'tutorial-key-step' (WaitFor { Named $card 'Draw a box' } 5000) "shape step: $(Named $card 'Draw a box')"
        [void](DrawOnCanvas (Canvas 260 40) (Canvas 380 120) 'a rectangle for the tutorial')
        Check 'tutorial-drag-step' (WaitFor { Named $card 'Make it blue' } 5000) "color step: $(Named $card 'Make it blue')"
        Note "B (blue ink): $([LiveInput]::Tap(0x42, $appPid))"
        Check 'tutorial-ink-step' (WaitFor { Named $card 'Draw a curved arrow' } 5000) "arrow step: $(Named $card 'Draw a curved arrow')"
        Check 'tutorial-canvas-keeps-keys' ((Foreground) -eq $overlay) "foreground $(Show (Foreground))"
        ClickUntil 'End tutorial' $card { -not (Named $card 'Draw a curved arrow') }
        Check 'tutorial-ends' (WaitFor { -not (Named $card 'Draw a curved arrow') } 3000) "arrow step still shown: $(Named $card 'Draw a curved arrow')"
        Check 'tutorial-layer-hides' (WaitFor { $null -eq ($l = TutorialLayer) -or -not [LiveInput]::IsWindowVisible($l) } 3000) "layer $(Show (TutorialLayer))"
        foreach ($i in 1..2) { Note "Ctrl+Z (the tutorial's color, then its rectangle): $([LiveInput]::Key(0x11, $false, $appPid)), $([LiveInput]::Tap(0x5A, $appPid)), $([LiveInput]::Key(0x11, $true, $appPid))"; Start-Sleep -Milliseconds 200 }
    }
    Note "click Resume drawing: $(ClickButton 'Resume drawing')"
    [void](WaitFor { (Foreground) -eq $overlay })
    Note "Escape on the canvas: $([LiveInput]::Tap(0x1B, $appPid))"
    Check 'reminder-shows' (WaitFor { $null -ne (Reminder) } 4000) "reminder $(Show (Reminder))"
    $reminder = Reminder
    if ($null -ne $reminder) {
        Check 'reminder-without-focus' ((Foreground) -ne $reminder) "foreground $(Show (Foreground))"
        # Like the Tools window, the reminder takes clicks once it is composed (ClickUntil).
        ClickUntil 'Not now' $reminder { $null -eq (Reminder) }
        Check 'reminder-not-now' (WaitFor { $null -eq (Reminder) }) "reminder $(Show (Reminder))"
        Note "click Resume drawing: $(ClickButton 'Resume drawing')"
        [void](WaitFor { (Foreground) -eq $overlay })
        Note "Escape on the canvas: $([LiveInput]::Tap(0x1B, $appPid))"
        Start-Sleep -Milliseconds 800
        Check 'reminder-snoozed' ($null -eq (Reminder)) "reminder $(Show (Reminder))"
    }
    HitTest 'at the end'

    # Region sharing (WIN-CAP-08, spec 012): the Tools tile opens Share a region; a custom region is drawn on the selection
    # panel; Left half starts sharing into an ordinary output window that turns live, never topmost; Stop covers it.
    function TitledWindow([string]$title) { [LiveInput]::Windows($appPid) | Where-Object { [LiveInput]::Title($_) -like $title } | Select-Object -First 1 }
    Note "click Tool library: $(ClickButton 'Tool library')"
    [void](WaitFor { $null -ne (ToolsWindow) } 5000)
    $tools = ToolsWindow
    if ($null -ne $tools) {
        Start-Sleep -Milliseconds 400
        Note "click Focus: $(ClickButton 'Focus' $tools)"
        Start-Sleep -Milliseconds 300
        Note "click the Share region tile: $(ClickButton 'Share region' $tools)"
    }
    $chooser = $null
    Check 'sharing-chooser-opens' (WaitFor { $null -ne ($script:chooser = TitledWindow 'Share a region') } 5000) "window $(Show $chooser)"
    if ($null -ne $chooser) {
        Start-Sleep -Milliseconds 600
        Note "click Draw a custom region: $(ClickButton 'Draw a custom region' $chooser)"
        $panel = $null
        [void](WaitFor { $null -ne ($script:panel = [LiveInput]::Windows($appPid) | Where-Object { [LiveInput]::ClassName($_) -eq 'Annotator.Platform.RegionSelection' } | Select-Object -First 1) } 3000)
        if ($null -ne $panel) {
            $from = @([int]($area[0] + 300 * $scale), [int]($area[1] + 200 * $scale)); $to = @([int]($area[0] + 700 * $scale), [int]($area[1] + 450 * $scale))
            $xs = @(); $ys = @()
            for ($i = 0; $i -le 8; $i++) { $xs += [int]($from[0] + ($to[0] - $from[0]) * $i / 8); $ys += [int]($from[1] + ($to[1] - $from[1]) * $i / 8) }
            Note "drag a region: $([LiveInput]::Drag($xs, $ys, $panel, 25))"
        }
        $fields = @('Region x in points', 'Region y in points', 'Region width in points', 'Region height in points') | ForEach-Object { ValueOf (Find $_ $chooser) }
        Check 'sharing-custom-region' ($null -ne $panel -and [Math]::Abs([double]$fields[0] - 300) -le 1 -and [Math]::Abs([double]$fields[2] - 400) -le 1 -and [Math]::Abs([double]$fields[3] - 250) -le 1) "panel $(Show $panel); fields $($fields -join ', ')"
        # The chooser hid by cloaking while the region was drawn, so its first click after it shows again works (ClickUntil).
        ClickUntil 'Left half' $chooser { Chosen $chooser 'Left half' }
        Note "click Start region sharing: $(ClickButton 'Start region sharing' $chooser)"
        $output = $null
        [void](WaitFor { $null -ne ($script:output = TitledWindow '*Shared Region') } 5000)
        $image = if ($null -ne $output) { Find 'Shared region. View only.' $output } else { $null }
        Check 'sharing-goes-live' (WaitFor { (ValueOf $image) -eq 'Live region. Choose this window in your meeting app.' } 9000) "output $(Show $output), value '$(ValueOf $image)'"
        Check 'sharing-output-ordinary' ($null -ne $output -and ([LiveInput]::ExStyle($output) -band 0x8) -eq 0 -and (Foreground) -ne $output) ('ex-style 0x{0:X}, foreground {1}' -f $(if ($output) { [LiveInput]::ExStyle($output) } else { 0 }), (Show (Foreground)))
        Check 'sharing-badge-on' ($null -ne (Find 'Region sharing is on' $chooser)) "badge: $($null -ne (Find 'Region sharing is on' $chooser))"
        # Spotlight and Cursor halo in the shared view (Mac SharedRegionPointerView): Cursor halo turns on while the region is
        # live, with the pointer inside the region, and the output draws it with the overlay (two layers show it; the result
        # checks that). Screen zoom then turns on: the shared view stays live and shows the region through zoom's viewport.
        # Copies of the output window (PrintWindow) stay in memory.
        function OutputCopy() {
            if ($null -eq $output) { return $null }
            $r = New-Object LiveInput+RECT; [void][LiveInput]::GetWindowRect($output, [ref]$r)
            return , [LiveInput]::CaptureWindow($output, $r.Right - $r.Left, $r.Bottom - $r.Top)
        }
        $inside = Canvas 200 200
        [void][LiveInput]::SetCursorPos($inside[0], $inside[1])
        Start-Sleep -Milliseconds 300
        $plain = OutputCopy
        MenuChoose 'Cursor halo'
        [void][LiveInput]::SetCursorPos($inside[0], $inside[1]); [LiveInput]::Nudge(4, 3)
        Start-Sleep -Milliseconds 600
        $haloCopy = OutputCopy
        # The ring is scaled down with the region, so its yellow pixels are evidence, not a check; the result checks that
        # the shared view showed the effects.
        if ([LiveInput]::Lit($plain) -and [LiveInput]::Lit($haloCopy)) {
            Note "yellow pixels in a copy of the shared view: $([LiveInput]::Yellow($plain)) before the halo, $([LiveInput]::Yellow($haloCopy)) with it"
        }
        else {
            Note "the shared view could not be copied (PrintWindow)"
        }
        Note "click Stop cursor halo: $(ClickButton 'Stop cursor halo')"
        [void](WaitFor { $null -eq (Find 'Stop cursor halo') })
        Start-Sleep -Milliseconds 300
        $unzoomed = OutputCopy
        ChooseZoom 'Full-screen zoom'
        $zoomOn = WaitFor { $null -ne (Find 'Stop screen zoom') } 12000
        Start-Sleep -Milliseconds 800
        $zoomedCopy = OutputCopy
        Check 'sharing-live-during-zoom' ($zoomOn -and (ValueOf $image) -eq 'Live region. Choose this window in your meeting app.') "zoom on $zoomOn; output value '$(ValueOf $image)'"
        if ([LiveInput]::Lit($unzoomed) -and [LiveInput]::Lit($zoomedCopy) -and $unzoomed.Length -eq $zoomedCopy.Length) {
            Note ("{0:P0} of a copy of the shared view changed with zoom" -f [LiveInput]::Changed($unzoomed, $zoomedCopy))
        }
        else {
            Note "the shared view could not be copied (PrintWindow)"
        }
        Note "click Stop screen zoom: $(ClickButton 'Stop screen zoom')"
        [void](WaitFor { $null -eq (Find 'Stop screen zoom') })
        # Show / hide while sharing (spec 012, Mac SharedWorkspaceTransition): the output plays the hide and the show with
        # copies of the drawings in the region and hands off to the live scene; "Updating the shared view..." shows only as
        # each motion starts, as on the Mac; it stays live, and the result counts the motions, copies, covers and hand-offs.
        # Show / hide is Ctrl+Alt+Shift+9 here, recorded on the Toolbar & Shortcuts tab and held by the check's own registrar.
        function ShowHide([string]$step) {
            if ([LiveInput]::OwnerOf((Foreground)) -ne $appPid) {
                $r = New-Object LiveInput+RECT; [void][LiveInput]::GetWindowRect($chooser, [ref]$r)
                Note "click the chooser's title bar: $([LiveInput]::Click([int](($r.Left + $r.Right) / 2), $r.Top + [int](16 * $scale), $chooser))"
                [void](WaitFor { [LiveInput]::OwnerOf((Foreground)) -eq $appPid } 1500)
            }
            try {
                foreach ($vk in 0x11, 0x12, 0x10) { [void][LiveInput]::Key([uint16]$vk, $false, $appPid) }
                Note "Ctrl+Alt+Shift+9 to ${step}: $([LiveInput]::Tap(0x39, $appPid))"
            }
            finally { foreach ($vk in 0x10, 0x12, 0x11) { [void][LiveInput]::Key([uint16]$vk, $true, $appPid) } }
        }
        # Screen zoom ramps out over 200 ms; the motion starts from the unzoomed view.
        Start-Sleep -Milliseconds 300
        ShowHide 'hide'
        $hiddenTools = WaitFor { -not [LiveInput]::IsWindowVisible($toolbar) } 4000
        Start-Sleep -Milliseconds 400
        ShowHide 'show'
        $shownTools = WaitFor { [LiveInput]::IsWindowVisible($toolbar) } 4000
        Check 'sharing-motion-stays-live' ($hiddenTools -and $shownTools -and (WaitFor { (ValueOf $image) -eq 'Live region. Choose this window in your meeting app.' } 3000)) "tools hidden $hiddenTools, shown again $shownTools; output value '$(ValueOf $image)'"
        # The toolbar shows as the show starts, and the drawings follow: Gather's show plays for at most 1.2 s (0.335 s
        # before the first drawing, 0.26 s of stagger, 0.59 s each). Stopping earlier would cover the output during the
        # motion, so the copies first hand off to the live scene.
        Start-Sleep -Milliseconds 2000
        Note "click Stop region sharing: $(ClickButton 'Stop region sharing' $chooser)"
        Check 'sharing-stop-covers' (WaitFor { (ValueOf $image) -eq 'Region sharing is off' } 3000) "value '$(ValueOf $image)'"
        if ($null -ne $output) {
            [void][LiveInput]::PostClose($output)
            Check 'sharing-output-closes' (WaitFor { $null -eq (TitledWindow '*Shared Region') } 3000) "output $(Show (TitledWindow '*Shared Region'))"
        }
        Note "Escape in the chooser: $([LiveInput]::Tap(0x1B, $appPid))"
    }

    # Idle work (WIN-SYS-07, WIN-PERF-03): with no input and nothing moving, Annotator's own threads and the app's GPU
    # engines stay (almost) idle. Graphics driver threads in the process are reported apart and not counted (D-11).
    Add-Type -Path (Join-Path $PSScriptRoot 'IdleProbe.cs')
    function GpuTime() {
        $samples = (Get-Counter "\GPU Engine(pid_$($appPid)_*)\Running Time" -ErrorAction SilentlyContinue).CounterSamples
        [double](($samples | Measure-Object RawValue -Sum).Sum)
    }
    Start-Sleep -Seconds 2
    $mhz = [double](Get-ItemPropertyValue 'HKLM:\HARDWARE\DESCRIPTION\System\CentralProcessor\0' -Name '~MHz')
    $cyclesBefore = [IdleProbe]::ThreadCycles($appPid)
    $gpuBefore = GpuTime
    $idleClock = [Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds 4
    $cyclesAfter = [IdleProbe]::ThreadCycles($appPid)
    $gpuAfter = GpuTime
    $idleSeconds = $idleClock.Elapsed.TotalSeconds
    $ownCycles = 0.0; $driverCycles = 0.0; $busiest = @()
    foreach ($thread in $cyclesAfter.Keys) {
        $delta = [double]$cyclesAfter[$thread] - $(if ($cyclesBefore.ContainsKey($thread)) { [double]$cyclesBefore[$thread] } else { 0.0 })
        if ($delta -le 0) { continue }
        $start = [IdleProbe]::DescribeThread($appPid, $thread)
        if ($start -match '^(nvwgf2umx|nvd3dumx|atidxx64|amdxx64|igd10iumd64|igd12umd64)\.dll') { $driverCycles += $delta; continue }
        $ownCycles += $delta
        $busiest += [pscustomobject]@{ Cycles = $delta; Start = $start }
    }
    $ownMs = $ownCycles / $idleSeconds / ($mhz * 1000)
    $gpuMs = ($gpuAfter - $gpuBefore) / 1e4 / $idleSeconds
    $top = ($busiest | Sort-Object Cycles -Descending | Select-Object -First 3 | ForEach-Object { '{0:N0} cycles {1}' -f $_.Cycles, $_.Start }) -join ' | '
    Check 'idle-work' ($ownMs -le 10 -and $gpuMs -le 10) ("over {0:N1} s without input: own threads {1:N2} ms of CPU per second, GPU engines {2:N2} ms per second; driver threads {3:N0} cycles per second (not counted); busiest: {4}" -f $idleSeconds, $ownMs, $gpuMs, ($driverCycles / $idleSeconds), $top)
}
finally {
    try {
        $done = [Threading.EventWaitHandle]::OpenExisting("Local\Annotator.LiveCheck.$($process.Id)")
        [void]$done.Set()
        $done.Dispose()
    }
    catch {
        Note "the done event was not there: $($_.Exception.Message)"
    }
    if (-not $process.WaitForExit(15000)) {
        Note "the app did not quit within 15 seconds; stopping process $($process.Id)"
        Stop-Process -Id $process.Id -Force
    }
    [void][LiveInput]::SetCursorPos($pointer.X, $pointer.Y)
}

$resultPath = Join-Path $Out 'result.json'
if (Test-Path $resultPath) {
    $result = Get-Content $resultPath -Raw | ConvertFrom-Json
    $elements = @($result.elements)
    Note "result: mode $($result.mode), tool $($result.tool), $($elements.Count) objects; status '$($result.status)'; error '$($result.error)'"
    if ($ZoomMeasure) { Note "zoom measure: $($result.zoom | ConvertTo-Json -Compress)" }
    function Angle($element) {
        $first = $element.points[0]; $last = $element.points[$element.points.Count - 1]
        [Math]::Atan2($last[1] - $first[1], $last[0] - $first[0]) * 180 / [Math]::PI
    }
    function OnEighth([double]$degrees) { $rest = [Math]::Abs($degrees / 45 - [Math]::Round($degrees / 45)); $rest -lt 0.0005 }
    $shape = $elements | Where-Object { $_.tool -eq 'rectangle' } | Select-Object -First 1
    Check 'first-shape-kept' ($null -ne $shape) "first rectangle: $($shape.frame -join ', ')"
    $typed = $elements | Where-Object { $_.tool -eq 'text' } | Select-Object -Last 1
    Check 'text-committed' ($null -ne $typed -and $typed.text -eq 'Hi there') "text: '$($typed.text)' at $($typed.frame -join ', ')"
    # Match outline left the label following its rectangle's stroke (Mac matchLabelToStroke).
    $labeled = $elements | Where-Object { $_.label -eq 'API' } | Select-Object -Last 1
    Check 'label-follows-outline' ($null -ne $labeled -and $labeled.labelFollowsStroke) "label: $(if ($labeled) { "'$($labeled.label)' on a $($labeled.tool), follows the stroke: $($labeled.labelFollowsStroke)" } else { 'none' })"
    $copies = @($elements | Where-Object { $null -ne $shape -and $_.tool -eq 'rectangle' -and [Math]::Abs($_.frame[2] - $shape.frame[2]) -lt 0.01 -and [Math]::Abs($_.frame[3] - $shape.frame[3]) -lt 0.01 })
    Check 'my-shapes-copy-kept' ($copies.Count -ge 2) "rectangles of the first one's size: $(($copies | ForEach-Object { '[' + ($_.frame -join ', ') + ']' }) -join ' ')"
    $lineElement = $elements | Where-Object { $_.tool -eq 'line' } | Select-Object -Last 1
    Check 'shift-line-45' ($null -ne $lineElement -and (OnEighth (Angle $lineElement))) "line angle $(if ($lineElement) { '{0:F3}' -f (Angle $lineElement) })"
    $arrowElement = $elements | Where-Object { $_.tool -eq 'arrow' } | Select-Object -Last 1
    Check 'shift-arrow-45' ($null -ne $arrowElement -and (OnEighth (Angle $arrowElement))) "arrow angle $(if ($arrowElement) { '{0:F3}' -f (Angle $arrowElement) })"
    $square = $elements | Where-Object { $_.tool -eq 'rectangle' } | Select-Object -Last 1
    Check 'shift-rectangle-square' ($null -ne $square -and [Math]::Abs($square.frame[2] - $square.frame[3]) -lt 0.001) "last rectangle: $($square.frame -join ', ')"
    $highlight = $elements | Where-Object { $_.tool -eq 'highlighter' } | Select-Object -Last 1
    Check 'shift-highlighter-straight' ($null -ne $highlight -and $highlight.points.Count -eq 2 -and (OnEighth (Angle $highlight))) "highlighter points $(if ($highlight) { $highlight.points.Count }), angle $(if ($highlight) { '{0:F3}' -f (Angle $highlight) })"
    Check 'last-shape-selected' ($summaryAfterShift -match ', 1 selected' -and $selectAfterShift) "after the Shift steps: '$summaryAfterShift', Select on $selectAfterShift"
    $pointerEffects = $result.pointerEffects
    # Two watcher starts: the pointer effects' step and the one during zoom; no start failed.
    Check 'pointer-watcher-ran' ($null -ne $pointerEffects -and $pointerEffects.watcherStarts -ge 2 -and $pointerEffects.watcherFailures -eq 0 -and $pointerEffects.activity -ge 1 -and $pointerEffects.clicks -ge 1) "pointer effects: $($pointerEffects | ConvertTo-Json -Compress)"
    Check 'pointer-effects-off-at-end' ($null -ne $pointerEffects -and -not $pointerEffects.spotlightShown -and -not $pointerEffects.haloShown) "spotlight shown $($pointerEffects.spotlightShown), halo shown $($pointerEffects.haloShown)"
    Check 'sharing-output-pointer-effects' ($null -ne $pointerEffects -and $pointerEffects.mostLayersShown -ge 2) "layers that showed an effect at once: $($pointerEffects.mostLayersShown) (the overlay and the shared view)"
    # The hide and the show while sharing played in the output as copies of the drawings in the region, each handed off to
    # the live scene, with no cover while they played besides the one each starts with (no cover or effect was added).
    $sharingMotion = $result.sharingMotion
    Check 'sharing-motion-mirrored' ($null -ne $sharingMotion -and $sharingMotion.transitions -ge 2 -and $sharingMotion.copies -ge 1 -and $sharingMotion.handOffs -ge 2 -and $sharingMotion.coversDuringMotion -eq 0) "shared motion: $($sharingMotion | ConvertTo-Json -Compress)"
    # Capture (WIN-PERF-05, capture change tracking): screen zoom reused the capture that the pointer lens left paused a
    # few seconds before, instead of opening a new Windows.Graphics.Capture session, and nothing takes frames at the end.
    $capture = $result.capture
    Check 'capture-reused-and-stopped' ($null -ne $capture -and $capture.sessionsReused -ge 1 -and -not $capture.capturing) "capture: $($capture | ConvertTo-Json -Compress)"
    # WIN-PERF-01: every press on the canvas asked Windows for the higher refresh rate and gave it back; nothing stays boosted.
    $boost = $result.boost
    Check 'drawing-boost-balanced' ($null -ne $boost -and $boost.requests -ge 1 -and $boost.holds -eq 0 -and -not $boost.boosted -and [string]::IsNullOrEmpty($boost.failure)) "boost: $($boost | ConvertTo-Json -Compress)"
}
else {
    Check 'result-written' $false "no $resultPath"
}
$failed = @($results.Keys | Where-Object { -not $results[$_] })
Note ("summary: {0} passed, {1} failed{2}" -f ($results.Count - $failed.Count), $failed.Count, $(if ($failed.Count) { ': ' + ($failed -join ', ') }))
if ($failed.Count) { exit 1 }
