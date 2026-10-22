using System.Diagnostics;
using Annotator.Core;
using Annotator.Core.Scene;
using Annotator.Platform.Capture;
using Annotator.Platform.Displays;
using Annotator.Rendering.Presentation;

namespace Annotator.RenderHarness;

/// <summary>
/// Mode <c>capture-start</c>: whether the very first frames of a capture that starts are right. The overlay shows a magenta
/// patch in the primary display's top-left corner, which a plain monitor capture sees and the capture service must leave
/// out. Capture then starts <c>--cycles</c> times with a new service each time, and every frame of the first 0.4 s is
/// read under the patch: for the primary display (a frame that shows the patch did not leave Annotator's windows out),
/// and for the display's right half as a made-up display, as the motion run's stage is (a frame that shows the patch
/// there shows the screen's top-left corner instead of the half's).
/// </summary>
internal static partial class CaptureModes
{
    public static void CaptureStart(HarnessOptions options)
    {
        using var session = OverlaySession.Start(LayerKind.VirtualSurface, LayerKind.VirtualSurface);
        var display = session.Display;
        Console.WriteLine($"Display {display.DeviceName}: {display.Bounds.Width} x {display.Bounds.Height} pixels at {display.Scale:P0}.");
        var patch = new Rect(8, 8, 48, 48);
        var area = Inset(Pixels(patch, display.Scale), 4);
        using (var monitor = new MonitorCapture(display))
        {
            var (seen, _, _) = monitor.Expect(new PixelRect(area.Left, area.Top, area.Left + 1, area.Top + 1), Bytes(Magenta), TimeSpan.FromSeconds(2),
                () => session.Run(() =>
                {
                    var start = Stopwatch.GetTimestamp();
                    session.Presenter.ShowScene(Scenes.Patch(patch, Magenta));
                    return (start, Stopwatch.GetTimestamp());
                }, "drawing the patch"));
            if (seen is null)
            {
                throw new NoFramesException("a plain monitor capture never showed the overlay's patch within 2 s; the display may be off or the session locked.");
            }
        }
        PixelRect bounds = display.Bounds;
        var half = new DisplayInfo(0, "capture-start-right-half", PixelRect.FromSize(bounds.Left + bounds.Width / 2, bounds.Top, bounds.Width / 2, bounds.Height),
                                   PixelRect.FromSize(bounds.Left + bounds.Width / 2, bounds.Top, bounds.Width / 2, bounds.Height), display.Dpi, IsPrimary: false);
        var failures = 0;
        void Check(string name, int cycle, CaptureService service, DisplayInfo target)
        {
            var probe = new LeakProbe(area, [Bytes(Magenta)]);
            probe.Start();
            var acquired = Stopwatch.GetTimestamp();
            var lease = service.Acquire(target, probe);
            try
            {
                WaitUntil(() => lease.State is CaptureState.Live or CaptureState.Failed, StartTimeout, "the first captured frame");
                if (lease.State is CaptureState.Failed failed)
                {
                    throw new InvalidOperationException($"Capture failed: {failed.Failure}");
                }
                var live = Stopwatch.GetElapsedTime(acquired).TotalMilliseconds;
                Thread.Sleep(400);
                probe.Stop();
                var fractions = probe.Fractions;
                var leaked = fractions.Select((fraction, index) => (fraction, index)).Where(item => item.fraction >= 0.25).Select(item => item.index).ToArray();
                failures += leaked.Length > 0 ? 1 : 0;
                Console.WriteLine($"  {name}, start {cycle}: live after {live:F0} ms; {fractions.Count} frames read; "
                                  + (leaked.Length == 0 ? "none showed the patch" : $"frames {string.Join(", ", leaked)} showed the patch"));
            }
            finally
            {
                lease.Dispose();
                probe.Dispose();
            }
        }
        foreach (var (name, target) in new[] { ("primary display", display), ("right half", half) })
        {
            for (var cycle = 1; cycle <= options.Cycles; cycle++)
            {
                using var service = CaptureService.Create(new CaptureServiceOptions { Registry = session.Registry, Linger = TimeSpan.Zero });
                Check(name, cycle, service, target);
            }
        }
        // A paused capture taken again, as screen zoom takes the capture that the lens left paused.
        using (var service = CaptureService.Create(new CaptureServiceOptions { Registry = session.Registry, Linger = TimeSpan.FromSeconds(30) }))
        {
            for (var cycle = 1; cycle <= options.Cycles + 1; cycle++)
            {
                Check(cycle == 1 ? "paused capture, first start" : "paused capture taken again", cycle, service, display);
            }
        }
        session.Run(() => session.Presenter.ShowScene(SceneSnapshot.Empty), "clearing the patch");
        if (failures > 0)
        {
            throw new InvalidOperationException($"LEAK: {failures} of {options.Cycles * 3 + 1} starts showed the patch in their first frames.");
        }
        Console.WriteLine("No start showed the patch.");
    }
}
