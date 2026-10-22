using System.Diagnostics;
using System.Globalization;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Platform.Displays;
using Annotator.Rendering.Presentation;

namespace Annotator.RenderHarness;

/// <summary>
/// Mode <c>timing</c>: what a captured frame's composition time is in the compositor's own frame statistics, and when an
/// update must reach the compositor to make a frame. A patch on the harness's click-through overlay changes color
/// (<c>--samples</c> times); for each change, the start of the submit and the time of the first captured frame that shows
/// it are placed in the display's refresh (DwmGetCompositionTimingInfo), and the frame time is matched with the start
/// and target times of the compositor's recent frames (DCompositionGetStatistics, Windows 11).
/// </summary>
internal static unsafe class FrameTiming
{
    private static readonly RGBA[] Colors = [new(1, 0, 0), new(0, 0.8, 0), new(0, 0, 1)];
    private const int Warmup = 5;

    public static void Run(HarnessOptions options)
    {
        using var session = OverlaySession.Start(LayerKind.VirtualSurface, options.Live);
        using var capture = new MonitorCapture(session.Display);
        var scale = session.Display.Scale;
        var patch = new Rect(24, 24, 32, 32);
        var probe = new PixelRect((int)Math.Floor(40 * scale), (int)Math.Floor(40 * scale), (int)Math.Floor(40 * scale) + 1, (int)Math.Floor(40 * scale) + 1);
        var grid = CompositionTiming.Read() ?? throw new NoFramesException("The compositor reports no refresh timing.");
        var boost = options.Boost;
        if (options.GpuPriority is { } priority)
        {
            var device = (Windows.Win32.Graphics.Dxgi.IDXGIDevice*)session.Devices.DxgiDevice;
            var set = device->SetGPUThreadPriority(priority);
            int now;
            device->GetGPUThreadPriority(&now);
            Console.WriteLine($"  IDXGIDevice::SetGPUThreadPriority({priority}): 0x{set.Value:X8}; the device's priority is now {now}");
        }
        if (boost)
        {
            Console.WriteLine($"  DCompositionBoostCompositorClock(TRUE): 0x{CompositionTiming.Boost(true):X8}");
            Thread.Sleep(200);
            grid = CompositionTiming.Read() ?? grid;
        }
        Console.WriteLine($"Timing: display {session.Display.Bounds.Width} x {session.Display.Bounds.Height} at {scale:P0}, {grid.RefreshHz:F3} Hz (period {grid.PeriodMs:F3} ms); live layer {options.Live}; {options.Samples} samples after {Warmup} warm-up samples.");
        Console.WriteLine($"  hardware overlay planes: {DesktopDuplication.OverlaySupport(session.Devices, session.Display)}");
        var phases = new List<(double StartPhase, double Latency, double FramePhase)>();
        var matchedTarget = 0;
        var matchedStart = 0;
        var unmatched = 0;
        var startToTarget = new List<double>();
        var startPhases = new List<double>();
        var rows = new List<string> { "sample,start_phase_ms,latency_ms,frame_phase_ms,frame_minus_stats_start_ms,frame_minus_stats_target_ms,stats_start_phase_ms" };
        var missed = 0;
        for (var sample = 0; sample < options.Samples + Warmup; sample++)
        {
            var color = Colors[sample % Colors.Length];
            var snapshot = Scenes.Patch(patch, color);
            var expected = ((byte)Math.Round(color.Red * 255), (byte)Math.Round(color.Green * 255), (byte)Math.Round(color.Blue * 255));
            var (seen, start, _) = capture.Expect(probe, expected, TimeSpan.FromMilliseconds(500), () => session.Run(() =>
            {
                var begin = Stopwatch.GetTimestamp();
                session.Presenter.ShowLive(snapshot);
                return (begin, Stopwatch.GetTimestamp());
            }, "drawing a timing patch"));
            if (seen is null)
            {
                if (++missed >= 5)
                {
                    throw new NoFramesException("Five timing patches never reached the screen; the display may be off or the session locked.");
                }
                continue;
            }
            var frame = CompositionTiming.Qpc(seen.Value);
            var now = CompositionTiming.Read() ?? grid;
            // The compositor's recent frames: the one whose start or target time equals the captured frame's time. The frame
            // that drew it may not have reached the display yet, so the search starts at the newest frame it created.
            CompositionTiming.FrameStats? match = null;
            if (CompositionTiming.FrameId(0) is { } newest)
            {
                for (ulong back = 0; back < 12 && back <= newest; back++)
                {
                    if (CompositionTiming.Statistics(newest - back) is not { } stats)
                    {
                        continue;
                    }
                    if (Math.Abs(CompositionTiming.Ms(frame - stats.Target)) < 0.3 || Math.Abs(CompositionTiming.Ms(frame - stats.Start)) < 0.3)
                    {
                        match = stats;
                        break;
                    }
                    if (match is null || Math.Abs(frame - stats.Target) < Math.Abs(frame - match.Value.Target))
                    {
                        match = stats;
                    }
                }
            }
            if (sample < Warmup)
            {
                continue;
            }
            var latency = CompositionTiming.Ms(frame - start);
            var startPhase = now.PhaseMs(start);
            var framePhase = now.PhaseMs(frame);
            phases.Add((startPhase, latency, framePhase));
            double? minusStart = null, minusTarget = null, statsStartPhase = null;
            if (match is { } found)
            {
                minusStart = CompositionTiming.Ms(frame - found.Start);
                minusTarget = CompositionTiming.Ms(frame - found.Target);
                statsStartPhase = now.PhaseMs(found.Start);
                startToTarget.Add(CompositionTiming.Ms(found.Target - found.Start));
                startPhases.Add(statsStartPhase.Value);
                if (Math.Abs(minusTarget.Value) < 0.3)
                {
                    matchedTarget++;
                }
                else if (Math.Abs(minusStart.Value) < 0.3)
                {
                    matchedStart++;
                }
                else
                {
                    unmatched++;
                }
            }
            else
            {
                unmatched++;
            }
            rows.Add(string.Join(',', sample.ToString(CultureInfo.InvariantCulture), F(startPhase), F(latency), F(framePhase), F(minusStart), F(minusTarget), F(statsStartPhase)));
            Thread.Sleep(20 + sample % 13);
        }
        session.Run(() => session.Presenter.ShowLive(null), "clearing the timing patch");
        if (boost)
        {
            Console.WriteLine($"  DCompositionBoostCompositorClock(FALSE): 0x{CompositionTiming.Boost(false):X8}");
        }
        Console.WriteLine($"  latency: {Stats.Summary(phases.Select(sample => sample.Latency).ToList())}; missed {missed}");
        CompositionTiming.PrintPhases("latency", phases, grid.PeriodMs);
        Console.WriteLine($"  captured frame time equals the compositor frame's target time in {matchedTarget}, its start time in {matchedStart}, neither in {unmatched} of {phases.Count}");
        if (startToTarget.Count > 0)
        {
            Console.WriteLine($"  compositor frames: target minus start {Stats.Summary(startToTarget)}; start, ms after the vertical blank: {Stats.Summary(startPhases)}");
        }
        if (options.Evidence is { } evidence)
        {
            Directory.CreateDirectory(evidence);
            var path = Path.Combine(evidence, $"timing-{options.Live}.csv");
            File.WriteAllLines(path, rows);
            Console.WriteLine($"  samples: {path}");
        }
    }

    private static string F(double? value) => value is { } number ? number.ToString("F3", CultureInfo.InvariantCulture) : string.Empty;
}
