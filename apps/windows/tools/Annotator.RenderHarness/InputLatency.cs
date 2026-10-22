using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Annotator.Platform.Displays;

namespace Annotator.RenderHarness;

/// <summary>
/// Input-to-visible latency of the running Annotator app (mode <c>input</c>): the time from a SendInput mouse move that
/// extends a pen stroke on Annotator's own overlay to the composition time of the first Windows.Graphics.Capture frame
/// that shows the new segment (the S-1 color-code method). It covers Windows input delivery, the app's processing and
/// composition; it does not cover the mouse, USB polling or the display's scan-out. Before the run, the app must be in
/// Draw with the Pen tool on the whiteboard (Ctrl+W); <c>--ink</c> gives the pen color (default coral, the pen's default).
/// Every event goes only to a window of the given process, checked with WindowFromPoint first.
/// </summary>
internal static partial class InputLatency
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromMilliseconds(500);
    private const int Warmup = 5;
    private const int Step = 18;
    private const int RowSamples = 40;
    private const int MissedFramesLimit = 5;

    public static void Run(HarnessOptions options)
    {
        var pid = options.Pid ?? throw new ArgumentException("--pid names the running Annotator process.");
        var display = DisplayEnumerator.GetDisplays().First(candidate => candidate.IsPrimary);
        using var capture = new MonitorCapture(display);
        var latencies = new List<double>();
        var phases = new List<(double StartPhase, double Latency, double FramePhase)>();
        var measured = new List<(int X, int Y, long Sent, long Frame)>();
        var rows = new List<string> { "sample,x,y,send_qpc,frame_qpc,latency_ms,send_phase_ms,frame_phase_ms,period_ms" };
        var missed = 0;
        var consecutive = 0;
        var total = options.Samples + Warmup;
        var left = display.Bounds.Left + 300;
        var top = display.Bounds.Top + display.Bounds.Height - 360;
        var refresh = CompositionTiming.Read();
        Console.WriteLine($"Input latency: SendInput move to the composition time of the first captured frame that shows the new pen segment; {options.Samples} samples after {Warmup} warm-up samples.");
        Console.WriteLine($"Display {display.Bounds.Width} x {display.Bounds.Height} at {display.Scale:P0}, {refresh?.RefreshHz ?? 0:F3} Hz; strokes from ({left}, {top}).");
        for (var sample = 0; sample < total;)
        {
            var y = top + sample / RowSamples * 28;
            Press(left, y, pid);
            var x = left;
            try
            {
                Thread.Sleep(60);
                for (var column = 0; column < RowSamples && sample < total; column++, sample++)
                {
                    x += Step;
                    var seen = Measure(capture, display, x, y, pid, options.Ink);
                    if (seen is null)
                    {
                        missed++;
                        if (++consecutive >= MissedFramesLimit)
                        {
                            throw new NoFramesException($"{consecutive} pen segments in a row never reached the screen; the display may be off, the session locked, or the app not drawing with that ink on the whiteboard.");
                        }
                    }
                    else
                    {
                        consecutive = 0;
                        if (sample >= Warmup)
                        {
                            var (sent, frame) = seen.Value;
                            var latency = CompositionTiming.Ms(frame - sent);
                            latencies.Add(latency);
                            measured.Add((x, y, sent, frame));
                            if (CompositionTiming.Read() is { } grid)
                            {
                                phases.Add((grid.PhaseMs(sent), latency, grid.PhaseMs(frame)));
                                rows.Add(FormattableString.Invariant($"{sample},{x},{y},{sent},{frame},{latency:F3},{grid.PhaseMs(sent):F3},{grid.PhaseMs(frame):F3},{grid.PeriodMs:F4}"));
                            }
                        }
                    }
                    Thread.Sleep(20 + sample % 13);
                }
            }
            finally
            {
                // The overlay holds the mouse capture during the stroke, so the release reaches it.
                Release(x, y, pid);
            }
            Thread.Sleep(120);
        }
        Console.WriteLine($"  input to composition: {Stats.Summary(latencies)}; missed {missed}");
        CompositionTiming.PrintPhases("input to composition", phases, refresh?.PeriodMs ?? 0);
        if (options.Evidence is { } evidence)
        {
            Directory.CreateDirectory(evidence);
            var path = Path.Combine(evidence, "input.csv");
            File.WriteAllLines(path, rows);
            Console.WriteLine($"  samples: {path}");
        }
        if (options.Probe is { } probeFile)
        {
            // The app appends a stroke's times once the stroke ends; give the last one a moment.
            Thread.Sleep(500);
            Breakdown(probeFile, measured);
        }
    }

    /// <summary>
    /// Splits each measured sample with the app's own times (ANNOTATOR_LATENCY_LOG): from the send to the start of the
    /// overlay's handling, the handling (the canvas and the presenter), and from the end of the handling to the frame;
    /// then the end of the handling by its place in the refresh, which shows when an update must be done to make a frame.
    /// </summary>
    private static void Breakdown(string file, List<(int X, int Y, long Sent, long Frame)> measured)
    {
        if (!File.Exists(file))
        {
            Console.WriteLine($"  probe: {file} does not exist; the app was not started with ANNOTATOR_LATENCY_LOG.");
            return;
        }
        var records = new List<(int X, int Y, long Event, long Dispatch, long Handled)>();
        foreach (var line in File.ReadLines(file).Skip(1))
        {
            var parts = line.Split(',');
            if (parts.Length >= 7 && parts[0] == "2")
            {
                records.Add((int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture),
                             long.Parse(parts[3], CultureInfo.InvariantCulture), long.Parse(parts[4], CultureInfo.InvariantCulture),
                             long.Parse(parts[5], CultureInfo.InvariantCulture)));
            }
        }
        var toEvent = new List<double>();
        var toDispatch = new List<double>();
        var handling = new List<double>();
        var toFrame = new List<double>();
        var handledPhases = new List<(double StartPhase, double Latency, double FramePhase)>();
        var grid = CompositionTiming.Read();
        var unmatched = 0;
        foreach (var sample in measured)
        {
            var match = records.Where(record => record.X == sample.X && record.Y == sample.Y && record.Dispatch >= sample.Sent && record.Dispatch <= sample.Frame)
                               .OrderBy(record => record.Dispatch).Take(1).ToList();
            if (match.Count == 0)
            {
                unmatched++;
                continue;
            }
            var record = match[0];
            if (record.Event > 0)
            {
                toEvent.Add(CompositionTiming.Ms(record.Event - sample.Sent));
            }
            toDispatch.Add(CompositionTiming.Ms(record.Dispatch - sample.Sent));
            handling.Add(CompositionTiming.Ms(record.Handled - record.Dispatch));
            toFrame.Add(CompositionTiming.Ms(sample.Frame - record.Handled));
            if (grid is { } refresh)
            {
                handledPhases.Add((refresh.PhaseMs(record.Handled), CompositionTiming.Ms(sample.Frame - record.Handled), refresh.PhaseMs(sample.Frame)));
            }
        }
        Console.WriteLine($"  probe: {measured.Count - unmatched} of {measured.Count} samples matched the app's pointer messages");
        if (toEvent.Count > 0)
        {
            Console.WriteLine($"    send to the pointer event's time stamp: {Stats.Summary(toEvent)}");
        }
        Console.WriteLine($"    send to the overlay's handling: {Stats.Summary(toDispatch)}");
        Console.WriteLine($"    handling (canvas and presenter): {Stats.Summary(handling)}");
        Console.WriteLine($"    end of handling to the frame: {Stats.Summary(toFrame)}");
        CompositionTiming.PrintPhases("    end of handling to the frame", handledPhases, grid?.PeriodMs ?? 0);
    }

    /// <summary>
    /// Moves the pointer to (<paramref name="x"/>, <paramref name="y"/>) and returns the QPC times of the send and of the
    /// composition of the first frame that showed it, or null when no frame showed it.
    /// </summary>
    private static (long Sent, long Frame)? Measure(MonitorCapture capture, DisplayInfo display, int x, int y, int pid, (byte Red, byte Green, byte Blue) ink)
    {
        // The new segment's pixel just behind the pointer: no earlier segment painted it.
        var probe = new PixelRect(x - 4 - display.Bounds.Left, y - display.Bounds.Top, x - 3 - display.Bounds.Left, y + 1 - display.Bounds.Top);
        var (seen, start, _) = capture.Expect(probe, pixel => Near(pixel, ink), FrameTimeout, () =>
        {
            var begin = Stopwatch.GetTimestamp();
            MoveTo(x, y, pid);
            return (begin, Stopwatch.GetTimestamp());
        });
        return seen is { } time ? (start, CompositionTiming.Qpc(time)) : null;
    }

    private static bool Near((byte Red, byte Green, byte Blue) pixel, (byte Red, byte Green, byte Blue) ink) =>
        Math.Abs(pixel.Red - ink.Red) + Math.Abs(pixel.Green - ink.Green) + Math.Abs(pixel.Blue - ink.Blue) <= 45;

    internal static void Press(int x, int y, int pid)
    {
        MoveTo(x, y, pid);
        Thread.Sleep(30);
        Send(x, y, LeftDown, pid);
    }

    /// <summary>
    /// Releases the left button. When another window came over the point (another check started), the release goes into a
    /// small window of the harness's own under the pointer instead, so the button never stays down and no other app gets
    /// input; then the run stops as before.
    /// </summary>
    internal static void Release(int x, int y, int pid)
    {
        try
        {
            Send(x, y, LeftUp, pid);
        }
        catch (InvalidOperationException error) when ((GetAsyncKeyState(0x01) & 0x8000) != 0)
        {
            throw new InvalidOperationException($"{error.Message} {ReleaseIntoOwnWindow()}", error);
        }
    }

    /// <summary>A left-button release into a topmost, never-activated 40 x 40 window of this process under the pointer.</summary>
    private static string ReleaseIntoOwnWindow()
    {
        GetCursorPos(out var at);
        // WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE, WS_POPUP; alpha 1 keeps it hit-testable.
        var helper = CreateWindowExW(0x8 | 0x80 | 0x80000 | 0x08000000, "STATIC", "", 0x80000000, at.X - 20, at.Y - 20, 40, 40, 0, 0, 0, 0);
        if (helper == 0)
        {
            return "The left button may still be down: the harness could not create its own window to release it.";
        }
        try
        {
            SetLayeredWindowAttributes(helper, 0, 1, 2);
            ShowWindow(helper, 4);
            if (GetAncestor(WindowFromPoint(at), 2) != helper)
            {
                return "The left button may still be down: the harness's own window did not come under the pointer.";
            }
            var input = new INPUT { Type = 0, Mouse = new MOUSEINPUT { Flags = LeftUp } };
            SendInput(1, ref input, Marshal.SizeOf<INPUT>());
            Thread.Sleep(50);
            return (GetAsyncKeyState(0x01) & 0x8000) == 0
                ? "The left button was released into the harness's own window."
                : "The left button may still be down after a release into the harness's own window.";
        }
        finally
        {
            DestroyWindow(helper);
        }
    }

    private static void MoveTo(int x, int y, int pid) => Send(x, y, Move, pid);

    private const uint Move = 0x1, LeftDown = 0x2, LeftUp = 0x4, Absolute = 0x8000, VirtualDesk = 0x4000;

    /// <summary>Sends one absolute mouse event, only when the window at the point belongs to <paramref name="pid"/>.</summary>
    private static void Send(int x, int y, uint flags, int pid)
    {
        var window = WindowFromPoint(new POINT { X = x, Y = y });
        var root = window == 0 ? 0 : GetAncestor(window, 2);
        GetWindowThreadProcessId(root, out var owner);
        if (root == 0 || owner != pid)
        {
            throw new InvalidOperationException($"The window at {x},{y} belongs to process {owner}, not Annotator ({pid}); no input was sent.");
        }
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        var input = new INPUT
        {
            Type = 0,
            Mouse = new MOUSEINPUT
            {
                X = (int)Math.Round((x - vx) * 65535.0 / (vw - 1)),
                Y = (int)Math.Round((y - vy) * 65535.0 / (vh - 1)),
                Flags = flags | Absolute | VirtualDesk,
            },
        };
        if (SendInput(1, ref input, Marshal.SizeOf<INPUT>()) != 1)
        {
            throw new InvalidOperationException($"SendInput failed with error {Marshal.GetLastPInvokeError()}.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int X;
        public int Y;
        public uint Data;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public MOUSEINPUT Mouse;
        // The union's keyboard and hardware members are smaller than MOUSEINPUT on x64.
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint count, ref INPUT input, int size);

    [LibraryImport("user32.dll")]
    private static partial nint WindowFromPoint(POINT point);

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint window, uint flags);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out int processId);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
                                                nint parent, nint menu, nint instance, nint parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetLayeredWindowAttributes(nint window, uint key, byte alpha, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);
}
