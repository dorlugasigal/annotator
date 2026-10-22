using System.Diagnostics;
using System.Globalization;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Platform.Displays;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Export;
using Annotator.Rendering.Imaging;
using Annotator.Rendering.Presentation;
using Annotator.Rendering.Text;

namespace Annotator.RenderHarness;

/// <summary>
/// Manual harness for the overlay presenter on the real overlay of the primary display (click-through, so the desktop
/// stays usable). See README.md for the modes and how to run them safely. Every wait is bounded: overlay-thread steps,
/// GPU queries and capture checks time out, and a watchdog ends the process after <c>--timeout</c> seconds. Measurement
/// code spins and allocates freely; the presenter it measures does not.
/// </summary>
internal static class Program
{
    private static readonly RGBA[] Colors = [new(1, 0, 0), new(0, 0.8, 0), new(0, 0, 1)];
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromMilliseconds(500);
    private const int MissedFramesLimit = 5;
    private const int Warmup = 5;

    public static int Main(string[] args)
    {
        HarnessOptions options;
        try
        {
            options = HarnessOptions.Parse(args);
        }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine(error.Message);
            Console.Error.WriteLine(HarnessOptions.Usage);
            return 2;
        }
        using var watchdog = Watchdog.Start(options.Timeout, options.Mode);
        var clock = Stopwatch.StartNew();
        try
        {
            switch (options.Mode)
            {
                case "frames":
                    Frames();
                    break;
                case "show":
                    Show(options);
                    break;
                case "latency":
                    Latency(options);
                    break;
                case "race":
                    Race(options);
                    break;
                case "memory":
                    Memory();
                    break;
                case "surfaces":
                    SurfaceMemory();
                    break;
                case "perf":
                    Perf(options);
                    break;
                case "input":
                    InputLatency.Run(options);
                    break;
                case "timing":
                    FrameTiming.Run(options);
                    break;
                case "ink":
                    InkTrailTiming.Run(options);
                    break;
                case "shot":
                    ScreenShot.Run(options);
                    break;
                case "capture":
                    CaptureModes.Capture(options);
                    break;
                case "capture-cycles":
                    CaptureModes.CaptureCycles(options);
                    break;
                case "capture-start":
                    CaptureModes.CaptureStart(options);
                    break;
                case "effects":
                    CaptureModes.Effects(options);
                    break;
                case "motion":
                    MotionCapture.Run(options);
                    break;
                case "motion-live":
                    MotionLiveCapture.Run(options);
                    break;
                case "pointer":
                    PointerPictures.Run(options);
                    break;
                case "video-check":
                    VideoCheck.Run(options);
                    break;
                case "live-compare":
                    LiveCompare.Run(options);
                    break;
            }
            Console.WriteLine($"Done: '{options.Mode}' finished in {clock.Elapsed.TotalSeconds:F1} s.");
            return 0;
        }
        catch (NoFramesException error)
        {
            Console.Error.WriteLine($"NO FRAMES: {error.Message}");
            return 3;
        }
        catch (TimeoutException error)
        {
            Console.Error.WriteLine($"TIMEOUT: {error.Message}");
            return 124;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    /// <summary>The device pixel under a point, for capture probes.</summary>
    private static PixelRect Probe(Point point, double scale)
    {
        int x = (int)Math.Floor(point.X * scale), y = (int)Math.Floor(point.Y * scale);
        return new PixelRect(x, y, x + 1, y + 1);
    }

    private static (byte, byte, byte) Bytes(RGBA color) =>
        ((byte)Math.Round(color.Red * 255), (byte)Math.Round(color.Green * 255), (byte)Math.Round(color.Blue * 255));

    /// <summary>
    /// Draws a small magenta patch and waits up to two seconds for a composed frame that shows it. Without one, the
    /// display is off or the session locked: report it and stop instead of measuring nothing.
    /// </summary>
    private static void RequireFrames(OverlaySession session, MonitorCapture capture)
    {
        var magenta = new RGBA(1, 0, 1);
        var probe = Probe(new Point(12, 12), session.Display.Scale);
        var patch = Scenes.Patch(new Rect(4, 4, 16, 16), magenta);
        var (seen, _, _) = capture.Expect(probe, Bytes(magenta), TimeSpan.FromSeconds(2), () => session.Run(() =>
        {
            var start = Stopwatch.GetTimestamp();
            session.Presenter.ShowScene(patch);
            return (start, Stopwatch.GetTimestamp());
        }, "drawing the frame check"));
        session.Run(() => session.Presenter.ShowScene(SceneSnapshot.Empty), "clearing the frame check");
        if (seen is null)
        {
            throw new NoFramesException($"no composed frame showed the check patch within 2 s ({capture.Frames} capture frames arrived). "
                                        + "The display may be off or the session locked; nothing was measured.");
        }
    }

    private static void Frames()
    {
        using var session = OverlaySession.Start(LayerKind.VirtualSurface, LayerKind.VirtualSurface);
        using var capture = new MonitorCapture(session.Display);
        RequireFrames(session, capture);
        Console.WriteLine($"Composition frames arrive on {session.Display.DeviceName} ({session.Display.Bounds.Width} x {session.Display.Bounds.Height} at {session.Display.Scale:P0}).");
    }

    private static void Show(HarnessOptions options)
    {
        using var measurer = new DirectWriteTextMeasurer();
        using var session = OverlaySession.Start(LayerKind.VirtualSurface, options.Live);
        using var capture = new MonitorCapture(session.Display);
        RequireFrames(session, capture);
        byte[] image;
        using (var exporter = new PngExporter(session.Devices, measurer))
        {
            var art = Scenes.Demo([]).Where(element => element.Tool is Tool.Ellipse or Tool.Diamond).Select(element =>
            {
                element.Frame = element.Frame.Translated(new Point(-element.Frame.X + 10, -element.Frame.Y + 10));
                element.InvalidateGeometry();
                return element;
            }).ToArray();
            image = exporter.Data(ElementList.Create(art), new Size(220, 160), background: Palette.White);
        }
        // An opaque backdrop under the board, so screenshots never contain what is behind the overlay.
        var board = new Rect(0, 0, 1240, 690);
        var elements = ElementList.Create([Scenes.Backdrop(board), .. Scenes.Demo(image)]);
        var display = session.Display;
        Console.WriteLine($"Display {display.DeviceName}: {display.Bounds.Width} x {display.Bounds.Height} pixels at {display.Scale:P0}");
        foreach (var scale in new[] { display.Scale, 1.5, 1.75 })
        {
            var builder = new SceneBuilder(measurer);
            var selected = new HashSet<Guid> { elements[2].Id };
            session.Run(() =>
            {
                session.Presenter.Resize(scale, display.Bounds.Width, display.Bounds.Height);
                session.Presenter.ShowScene(builder.Build(elements, selected, selectionScale: 1));
            }, "drawing the demo board");
            // A live stroke grows sample by sample on the live layer, then moves to the scene.
            var stroke = new Element(Tool.Pen, points: Scenes.Points(new Point(60, 600)));
            stroke.Style.Width = 6;
            stroke.Style.Color = new RGBA(0.1, 0.6, 0.3, 0.8);
            var liveBuilder = new SceneBuilder(measurer);
            for (var sample = 1; sample <= 240; sample++)
            {
                stroke.Points = stroke.Points.Add(new Point(60 + sample * 4, 600 + Math.Sin(sample * 0.08) * 40));
                stroke.Frame = Rect.Enclosing(stroke.Points.AsSpan());
                stroke.GeometryToken = Guid.NewGuid();
                var live = liveBuilder.Build(ElementList.Create([stroke]));
                session.Run(() => session.Presenter.ShowLive(live), "drawing a live sample");
                Thread.Sleep(4);
            }
            var statistics = session.Run(() => (session.Presenter.Statistics.LastLivePixels, session.Presenter.Statistics.LiveFrames), "reading statistics");
            var committed = elements.Add(stroke);
            session.Run(() => session.Presenter.Show(builder.Build(committed, selected), SceneSnapshot.Empty), "committing the stroke");
            Console.WriteLine($"Scale {scale:P0}: last live sample redrew {statistics.LastLivePixels} pixels; {statistics.LiveFrames} live frames so far");
            if (options.Evidence is { } evidence)
            {
                Directory.CreateDirectory(evidence);
                var area = new PixelRect(0, 0, Math.Min(display.Bounds.Width, (int)Math.Floor(board.Width * scale)), Math.Min(display.Bounds.Height, (int)Math.Floor(board.Height * scale)));
                var shot = capture.Snapshot(area);
                var path = Path.Combine(evidence, $"presenter-demo-{(int)Math.Round(scale * 100)}.png");
                File.WriteAllBytes(path, RasterAsset.EncodePng(shot));
                Console.WriteLine($"Saved {path}");
            }
            Thread.Sleep(500);
        }
        var state = session.Run(() => session.Presenter.State, "reading the state");
        Console.WriteLine($"Presenter state: {state}");
    }

    /// <summary>
    /// Submit-to-composition latency of each layer kind: a patch changes color, and Windows.Graphics.Capture reports the
    /// composition time of the first frame that shows the new color (the S-1 color-code method).
    /// </summary>
    private static void Latency(HarnessOptions options)
    {
        using var session = OverlaySession.Start(LayerKind.VirtualSurface, LayerKind.VirtualSurface);
        using var capture = new MonitorCapture(session.Display);
        RequireFrames(session, capture);
        var scale = session.Display.Scale;
        var patch = new Rect(24, 24, 32, 32);
        var probe = Probe(new Point(40, 40), scale);
        Console.WriteLine($"Latency: from the start of the submit to the composition time of the first captured frame that shows the patch; {options.Samples} samples per case after {Warmup} warm-up samples.");
        Console.WriteLine($"Display {session.Display.Bounds.Width} x {session.Display.Bounds.Height} at {scale:P0}.");
        foreach (var kind in new[] { LayerKind.VirtualSurface, LayerKind.DrawingSurface, LayerKind.SwapChain })
        {
            session.Recreate(kind, kind);
            foreach (var live in new[] { false, true })
            {
                var latencies = new List<double>();
                var submits = new List<double>();
                var phases = new List<(double StartPhase, double Latency, double FramePhase)>();
                var missed = 0;
                var consecutive = 0;
                for (var sample = 0; sample < options.Samples + Warmup; sample++)
                {
                    var color = Colors[sample % Colors.Length];
                    var snapshot = Scenes.Patch(patch, color);
                    var (seen, start, end) = capture.Expect(probe, Bytes(color), FrameTimeout, () => session.Run(() =>
                    {
                        var begin = Stopwatch.GetTimestamp();
                        if (live)
                        {
                            session.Presenter.ShowLive(snapshot);
                        }
                        else
                        {
                            session.Presenter.ShowScene(snapshot);
                        }
                        return (begin, Stopwatch.GetTimestamp());
                    }, "drawing a latency patch"));
                    if (seen is null)
                    {
                        missed++;
                        if (++consecutive >= MissedFramesLimit)
                        {
                            throw new NoFramesException($"{consecutive} patches in a row never reached the screen ({kind}, {(live ? "live" : "scene")}); the display may be off or the session locked.");
                        }
                        continue;
                    }
                    consecutive = 0;
                    if (sample >= Warmup)
                    {
                        latencies.Add((seen.Value - TimeSpan.FromSeconds((double)start / Stopwatch.Frequency)).TotalMilliseconds);
                        submits.Add((double)(end - start) * 1000 / Stopwatch.Frequency);
                        if (CompositionTiming.Read() is { } grid)
                        {
                            phases.Add((grid.PhaseMs(start), latencies[^1], grid.PhaseMs(CompositionTiming.Qpc(seen.Value))));
                        }
                    }
                    Thread.Sleep(20 + sample % 7);
                }
                session.Run(() =>
                {
                    session.Presenter.ShowLive(null);
                    session.Presenter.ShowScene(SceneSnapshot.Empty);
                }, "clearing the patches");
                Console.WriteLine($"  {kind,-15} {(live ? "live " : "scene")}: latency {Stats.Summary(latencies)}; submit CPU {Stats.Summary(submits)}; missed {missed}");
                CompositionTiming.PrintPhases("    latency", phases, CompositionTiming.Read()?.PeriodMs ?? 0);
            }
        }
    }

    /// <summary>
    /// The scene layer and the live layer race in the same captured frames: each step draws the same new color into
    /// both in one overlay-thread work item; the difference of the first frames that show it compares the two kinds
    /// without relying on clocks.
    /// </summary>
    private static void Race(HarnessOptions options)
    {
        using var session = OverlaySession.Start(options.Scene, options.Live);
        using var capture = new MonitorCapture(session.Display);
        RequireFrames(session, capture);
        var scale = session.Display.Scale;
        var sceneProbe = Probe(new Point(40, 40), scale);
        var liveProbe = Probe(new Point(90, 40), scale);
        var differences = new List<double>();
        var sceneLatencies = new List<double>();
        var liveLatencies = new List<double>();
        var missed = 0;
        var consecutive = 0;
        Console.WriteLine($"Race: scene layer {options.Scene} against live layer {options.Live}, {options.Samples} steps after {Warmup} warm-up steps.");
        for (var step = 0; step < options.Samples + Warmup; step++)
        {
            var color = Colors[step % Colors.Length];
            var expected = Bytes(color);
            var scene = Scenes.Patch(new Rect(24, 24, 32, 32), color);
            var live = Scenes.Patch(new Rect(74, 24, 32, 32), color);
            TimeSpan? sceneSeen = null, liveSeen = null;
            using var both = new ManualResetEventSlim();
            capture.OnFrame((texture, time) =>
            {
                var scenePixel = capture.Read(texture, sceneProbe)[0, 0];
                var livePixel = capture.Read(texture, liveProbe)[0, 0];
                if (sceneSeen is null && (scenePixel.Red, scenePixel.Green, scenePixel.Blue) == expected)
                {
                    sceneSeen = time;
                }
                if (liveSeen is null && (livePixel.Red, livePixel.Green, livePixel.Blue) == expected)
                {
                    liveSeen = time;
                }
                if (sceneSeen is not null && liveSeen is not null)
                {
                    both.Set();
                }
            });
            var start = session.Run(() =>
            {
                var begin = Stopwatch.GetTimestamp();
                session.Presenter.Show(scene, live);
                return begin;
            }, "drawing a race step");
            var arrived = both.Wait(FrameTimeout);
            capture.OnFrame(null);
            if (!arrived)
            {
                missed++;
                if (++consecutive >= MissedFramesLimit)
                {
                    throw new NoFramesException($"{consecutive} race steps in a row never reached the screen; the display may be off or the session locked.");
                }
                continue;
            }
            consecutive = 0;
            if (step >= Warmup)
            {
                var submitted = TimeSpan.FromSeconds((double)start / Stopwatch.Frequency);
                differences.Add((liveSeen!.Value - sceneSeen!.Value).TotalMilliseconds);
                sceneLatencies.Add((sceneSeen.Value - submitted).TotalMilliseconds);
                liveLatencies.Add((liveSeen.Value - submitted).TotalMilliseconds);
            }
            Thread.Sleep(20 + step % 7);
        }
        session.Run(() => session.Presenter.Show(SceneSnapshot.Empty, SceneSnapshot.Empty), "clearing the race patches");
        var earlier = differences.Count(value => value < -0.5);
        var later = differences.Count(value => value > 0.5);
        Console.WriteLine($"  scene {options.Scene}: latency {Stats.Summary(sceneLatencies)}");
        Console.WriteLine($"  live  {options.Live}: latency {Stats.Summary(liveLatencies)}");
        Console.WriteLine($"  live minus scene, same capture stream: {Stats.Summary(differences)}; live earlier in {earlier}, later in {later}, same frame in {differences.Count - earlier - later} of {differences.Count}; missed {missed}");
    }

    /// <summary>
    /// Isolates what composition surfaces allocate: one surface of the display's size, updated in small and full
    /// rectangles, then trimmed and released; and the cost of creating a swap-chain layer.
    /// </summary>
    private static void SurfaceMemory()
    {
        using var session = OverlaySession.Start(LayerKind.VirtualSurface, LayerKind.VirtualSurface);
        var display = session.Display;
        int width = display.Bounds.Width, height = display.Bounds.Height;
        Console.WriteLine($"Composition surfaces of {width} x {height} pixels: local video memory of the process (IDXGIAdapter3::QueryVideoMemoryInfo), 700 ms settle per step.");
        session.Run(() => session.Presenter.Dispose(), "releasing the presenter");
        foreach (var isVirtual in new[] { true, false })
        {
            var compositor = session.Run(() => session.Overlay.Root.Compositor, "reading the compositor");
            var graphics = session.Run(() => Annotator.Platform.Interop.CompositionInterop.CreateGraphicsDevice(compositor, session.Devices.D2DDevice), "creating a graphics device");
            // Each step is its own dispatcher work item, so the compositor commits it before the reading.
            ulong Step(Action action)
            {
                session.Run(action, "a surface step");
                Thread.Sleep(700);
                return session.Run(() => GpuTimer.LocalVideoMemory(session.Devices), "reading video memory");
            }
            var content = new ClearOnly();
            DrawingSurfaceLayer? layer = null;
            var damage = new DamageRegion();
            var baseline = Step(() => { });
            var created = Step(() =>
            {
                layer = new DrawingSurfaceLayer(compositor, graphics, width, height, isVirtual);
                session.Overlay.Root.Children.InsertAtTop(layer.Visual);
            });
            var small = Step(() =>
            {
                damage.Clear();
                damage.Add(new PixelRect(100, 100, 350, 350), width, height);
                layer!.Draw(damage, 1, false, content);
            });
            var second = Step(() =>
            {
                damage.Clear();
                damage.Add(new PixelRect(4000, 1000, 4250, 1250), width, height);
                layer!.Draw(damage, 1, false, content);
            });
            var full = Step(() =>
            {
                damage.AddFull(width, height);
                layer!.Draw(damage, 1, false, content);
            });
            var trimmed = Step(() => layer!.TrimAll());
            var deviceTrimmed = Step(() => graphics.Trim());
            var surfaceReleased = Step(() =>
            {
                session.Overlay.Root.Children.Remove(layer!.Visual);
                layer!.Dispose();
            });
            var trimTime = 0.0;
            var deviceTrimmedAfter = Step(() =>
            {
                var start = Stopwatch.GetTimestamp();
                graphics.Trim();
                trimTime = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            });
            var released = Step(() => graphics.Dispose());
            static string MiB(ulong value, ulong reference) => $"{((double)value - reference) / 1_048_576:F1}";
            Console.WriteLine($"  {(isVirtual ? "CompositionVirtualDrawingSurface" : "CompositionDrawingSurface"),-32}: MiB after create {MiB(created, baseline)}, "
                              + $"250 x 250 update {MiB(small, baseline)}, second distant update {MiB(second, baseline)}, full update {MiB(full, baseline)}, "
                              + $"surface trim {MiB(trimmed, baseline)}, graphics device trim {MiB(deviceTrimmed, baseline)}, surface released {MiB(surfaceReleased, baseline)}, "
                              + $"then graphics device trim {MiB(deviceTrimmedAfter, baseline)} (took {trimTime:F3} ms), graphics device released {MiB(released, baseline)}");
        }
        session.Run(() =>
        {
            var compositor = session.Overlay.Root.Compositor;
            var times = new List<double>();
            for (var run = 0; run < 20; run++)
            {
                var start = Stopwatch.GetTimestamp();
                var device = Annotator.Platform.Interop.CompositionInterop.CreateGraphicsDevice(compositor, session.Devices.D2DDevice);
                times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                device.Dispose();
            }
            Console.WriteLine($"  Creating a composition graphics device: {Stats.Summary(times)}");
        }, "timing graphics-device creation");
        session.Run(() =>
        {
            var compositor = session.Overlay.Root.Compositor;
            var graphics = Annotator.Platform.Interop.CompositionInterop.CreateGraphicsDevice(compositor, session.Devices.D2DDevice);
            var content = new ClearOnly();
            var damage = new DamageRegion();
            damage.Add(new PixelRect(200, 200, 264, 264), width, height);
            var times = new List<double>();
            for (var run = 0; run < 20; run++)
            {
                var start = Stopwatch.GetTimestamp();
                var layer = new DrawingSurfaceLayer(compositor, graphics, width, height, isVirtual: true);
                session.Overlay.Root.Children.InsertAtTop(layer.Visual);
                layer.Draw(damage, 1, true, content);
                times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                session.Overlay.Root.Children.Remove(layer.Visual);
                layer.Dispose();
            }
            graphics.Dispose();
            Console.WriteLine($"  Creating a {width} x {height} virtual-surface layer and drawing 64 x 64 pixels: {Stats.Summary(times)}");
        }, "timing virtual-surface creation");
        session.Run(() =>
        {
            var compositor = session.Overlay.Root.Compositor;
            var queue = Windows.System.DispatcherQueue.GetForCurrentThread();
            var times = new List<double>();
            for (var run = 0; run < 10; run++)
            {
                var start = Stopwatch.GetTimestamp();
                var layer = new SwapChainLayer(compositor, session.Devices, width, height, queue, static _ => { });
                times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                layer.Dispose();
            }
            Console.WriteLine($"  Creating a {width} x {height} swap-chain layer: {Stats.Summary(times)}");
        }, "timing swap-chain creation");
    }

    private sealed class ClearOnly : ILayerContent
    {
        public void Render(nint deviceContext, in DrawArea area)
        {
        }
    }

    private static void Memory()
    {
        Console.WriteLine("Local video memory of the process (IDXGIAdapter3::QueryVideoMemoryInfo), each kind in a fresh overlay session, 700 ms settle per step.");
        foreach (var (kind, liveKind) in new[] { (LayerKind.VirtualSurface, LayerKind.VirtualSurface), (LayerKind.DrawingSurface, LayerKind.DrawingSurface), (LayerKind.SwapChain, LayerKind.SwapChain), (LayerKind.VirtualSurface, LayerKind.SwapChain) })
        {
            using var session = OverlaySession.Start(kind, liveKind);
            var display = session.Display;
            var points = new Size(display.Bounds.Width / display.Scale, display.Bounds.Height / display.Scale);
            ulong Measure(Action step)
            {
                session.Run(step, "a memory step");
                Thread.Sleep(700);
                return session.Run(() => GpuTimer.LocalVideoMemory(session.Devices), "reading video memory");
            }
            var baseline = Measure(() => { });
            var smallScene = Measure(() => session.Presenter.ShowScene(Scenes.Patch(new Rect(100, 100, 200, 200), Colors[0])));
            var smallLive = Measure(() => session.Presenter.ShowLive(Scenes.Patch(new Rect(400, 100, 200, 200), Colors[1])));
            var fullScene = Measure(() => session.Presenter.ShowScene(Scenes.Patch(new Rect(0, 0, points.Width, points.Height), new RGBA(0, 0, 0, 0.05))));
            var fullLive = Measure(() => session.Presenter.ShowLive(Scenes.Patch(new Rect(0, 0, points.Width, points.Height), new RGBA(0, 0, 0, 0.05))));
            var cleared = Measure(() =>
            {
                session.Presenter.ShowLive(null);
                session.Presenter.ShowScene(SceneSnapshot.Empty);
            });
            var released = Measure(() => session.Presenter.ReleaseIdleResources());
            static string MiB(ulong now, ulong before) => $"{((double)now - before) / 1_048_576:F1}";
            Console.WriteLine($"  scene {kind,-15} live {liveKind,-15} {display.Bounds.Width} x {display.Bounds.Height}: baseline {baseline / 1_048_576.0:F1} MiB; added MiB: small scene patch {MiB(smallScene, baseline)}, "
                              + $"plus small live patch {MiB(smallLive, baseline)}, full scene {MiB(fullScene, baseline)}, full scene and live {MiB(fullLive, baseline)}, "
                              + $"both cleared {MiB(cleared, baseline)}, idle resources released {MiB(released, baseline)}");
        }
        MemorySteps();
    }

    /// <summary>The default scene layer with a swap-chain live layer, one change at a time.</summary>
    private static void MemorySteps()
    {
        using var session = OverlaySession.Start(LayerKind.VirtualSurface, LayerKind.SwapChain);
        var display = session.Display;
        var points = new Size(display.Bounds.Width / display.Scale, display.Bounds.Height / display.Scale);
        var steps = new List<string>();
        var baseline = 0UL;
        void Measure(string name, Action step)
        {
            session.Run(step, name);
            Thread.Sleep(700);
            var now = session.Run(() => GpuTimer.LocalVideoMemory(session.Devices), "reading video memory");
            if (steps.Count == 0)
            {
                baseline = now;
            }
            steps.Add($"{name} {((double)now - baseline) / 1_048_576:F1}");
        }
        var full = new Rect(0, 0, points.Width, points.Height);
        Measure("baseline", () => { });
        Measure("full scene", () => session.Presenter.ShowScene(Scenes.Patch(full, new RGBA(0, 0, 0, 0.05))));
        Measure("full live", () => session.Presenter.ShowLive(Scenes.Patch(full, new RGBA(0, 0, 0, 0.05))));
        Measure("scene cleared", () => session.Presenter.ShowScene(SceneSnapshot.Empty));
        Measure("idle release with live content", () => session.Presenter.ReleaseIdleResources());
        Measure("live cleared", () => session.Presenter.ShowLive(null));
        Measure("idle release", () => session.Presenter.ReleaseIdleResources());
        Console.WriteLine($"  scene VirtualSurface, live SwapChain, step by step (MiB added): {string.Join("; ", steps)}");
    }

    private static void Perf(HarnessOptions options)
    {
        using var measurer = new DirectWriteTextMeasurer();
        var rendererOptions = new RendererOptions { UseRealizations = options.Realizations };
        using var session = OverlaySession.Start(options.Scene, options.Live, rendererOptions);
        var display = session.Display;
        // Drawing costs do not depend on the group's opacity; a faint overlay keeps the desktop usable during the run.
        session.Run(() => session.Presenter.Visual.Opacity = (float)options.Opacity, "setting the opacity");
        Console.WriteLine($"WIN-PERF-01 workloads on {session.Devices.Adapter.Description}, {display.Bounds.Width} x {display.Bounds.Height} at {display.Scale:P0}; "
                          + $"scene {options.Scene}, live {options.Live}, realizations {(options.Realizations ? "on" : "off")}, overlay opacity {options.Opacity:P0}.");
        // The fixtures span 1,920 x 1,080 points; stretch them over this display's points.
        var scaleX = display.Bounds.Width / display.Scale / 1_920;
        var scaleY = display.Bounds.Height / display.Scale / 1_080;
        var workloads = new[] { ("standard", 200, 20_000), ("stress", 999, 200_000) }.Where(workload => options.Workload is "all" || options.Workload == workload.Item1);
        foreach (var (name, mixed, samples) in workloads)
        {
            var elements = ElementList.Create(Scenes.Mixed(mixed, scaleX, scaleY));
            var stroke = Scenes.Stroke(samples, scaleX, scaleY);
            using var gpu = session.Run(() => new GpuTimer(session.Devices), "creating GPU queries");
            var builder = new SceneBuilder(measurer);
            var clock = Stopwatch.StartNew();
            var withStroke = builder.Build(elements.Add(stroke));
            var buildMs = clock.Elapsed.TotalMilliseconds;
            session.Run(() => session.Presenter.ShowScene(SceneSnapshot.Empty), "clearing the scene");

            // Full redraw of the committed scene (the first one also creates every cached resource).
            var fullCpu = new List<double>();
            var fullGpu = new List<double>();
            var fullDone = new List<double>();
            for (var run = 0; run < 12; run++)
            {
                var first = run == 0;
                var result = session.Run(() =>
                {
                    var start = Stopwatch.GetTimestamp();
                    gpu.Begin();
                    if (first)
                    {
                        session.Presenter.ShowScene(withStroke);
                    }
                    else
                    {
                        session.Presenter.Invalidate();
                    }
                    gpu.End();
                    var cpu = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    var (gpuMs, done) = gpu.Read(start);
                    return (cpu, gpuMs, done, pixels: session.Presenter.Statistics.LastScenePixels);
                }, "a full redraw");
                if (first)
                {
                    Console.WriteLine($"  {name}: first full draw (builds every resource) CPU {result.cpu:F2} ms, GPU {result.gpuMs:F2} ms, done after {result.done:F2} ms; "
                                      + $"{result.pixels} pixels; scene build on the app thread {buildMs:F1} ms");
                    continue;
                }
                fullCpu.Add(result.cpu);
                fullGpu.Add(result.gpuMs);
                fullDone.Add(result.done);
            }
            Console.WriteLine($"  {name}: full redraw CPU submit {Stats.Summary(fullCpu)}");
            Console.WriteLine($"  {name}: full redraw GPU work {Stats.Summary(fullGpu)}; GPU completion after submit start {Stats.Summary(fullDone)}");

            // Selection changes: the outline and handles move from element to element.
            var selectionCpu = new List<double>();
            var selectionFrame = new List<double>();
            var selectionGpu = new List<double>();
            var selectionDone = new List<double>();
            var selectionPixels = new List<double>();
            for (var run = 0; run < 60; run++)
            {
                var selected = new HashSet<Guid> { elements[(run * 37) % elements.Count].Id };
                var snapshot = builder.Build(elements.Add(stroke), selected);
                var result = session.Run(() =>
                {
                    var start = Stopwatch.GetTimestamp();
                    gpu.Begin();
                    session.Presenter.ShowScene(snapshot);
                    gpu.End();
                    var cpu = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    var statistics = session.Presenter.Statistics;
                    var (gpuMs, done) = gpu.Read(start);
                    return (cpu, gpuMs, done, pixels: (double)statistics.LastScenePixels, frame: statistics.LastSceneFrameTime.TotalMilliseconds);
                }, "a selection change");
                selectionCpu.Add(result.cpu);
                selectionFrame.Add(result.frame);
                selectionGpu.Add(result.gpuMs);
                selectionDone.Add(result.done);
                selectionPixels.Add(result.pixels);
            }
            Console.WriteLine($"  {name}: selection change CPU submit {Stats.Summary(selectionCpu)}; of which drawing {Stats.Summary(selectionFrame)}");
            Console.WriteLine($"  {name}: selection change GPU work {Stats.Summary(selectionGpu)}; GPU completion {Stats.Summary(selectionDone)}; redrawn pixels p50 {Stats.Percentile(selectionPixels, 0.5):F0}");

            // The long stroke in progress on the live layer: one new sample at a time over the committed scene.
            var committed = builder.Build(elements);
            session.Run(() => session.Presenter.ShowScene(committed), "showing the committed scene");
            var liveBuilder = new SceneBuilder(measurer);
            var growing = stroke;
            var firstLive = liveBuilder.Build(ElementList.Create([growing]));
            session.Run(() => session.Presenter.ShowLive(firstLive), "showing the live stroke");
            var buildTimes = new List<double>();
            var liveCpu = new List<double>();
            var liveFrame = new List<double>();
            var liveGpu = new List<double>();
            var liveDone = new List<double>();
            var livePixels = new List<double>();
            long allocated = 0;
            var deferred = 0;
            var deferredWaits = new List<double>();
            for (var sample = 0; sample < options.Samples; sample++)
            {
                var last = growing.Points[^1];
                // The fixture stroke ends near the right edge; new samples run back across the display, so every one stays visible.
                growing.Points = growing.Points.Add(new Point(last.X - 0.4 * scaleX, 540 * scaleY + Math.Sin(sample * 0.05) * 300 * scaleY));
                growing.Frame = growing.Frame.Union(new Rect(growing.Points[^1], growing.Points[^1]));
                growing.GeometryToken = Guid.NewGuid();
                var buildStart = Stopwatch.GetTimestamp();
                var live = liveBuilder.Build(ElementList.Create([growing]));
                buildTimes.Add(Stopwatch.GetElapsedTime(buildStart).TotalMilliseconds);
                var result = session.Run(() =>
                {
                    var statistics = session.Presenter.Statistics;
                    var frames = statistics.LiveFrames;
                    var deferrals = statistics.DeferredFrames;
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    var start = Stopwatch.GetTimestamp();
                    gpu.Begin();
                    session.Presenter.ShowLive(live);
                    gpu.End();
                    var cpu = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                    var drawn = statistics.LiveFrames > frames;
                    var (gpuMs, done) = drawn ? gpu.Read(start) : (double.NaN, double.NaN);
                    return (cpu, gpuMs, done, drawn, deferred: statistics.DeferredFrames > deferrals, bytes, frames: statistics.LiveFrames,
                            frame: statistics.LastLiveFrameTime.TotalMilliseconds, pixels: (double)statistics.LastLivePixels, state: session.Presenter.State);
                }, "a live sample");
                if (result.state is not PresenterState.Ready)
                {
                    throw new InvalidOperationException($"The presenter left Ready during the live samples: {result.state}.");
                }
                if (!result.drawn && !result.deferred)
                {
                    throw new InvalidOperationException($"Live sample {sample} changed nothing on the live layer.");
                }
                if (!result.drawn)
                {
                    // No free frame slot: the presenter draws the newest sample when the display takes the last frame.
                    deferred++;
                    var waited = Stopwatch.StartNew();
                    while (session.Run(() => session.Presenter.Statistics.LiveFrames == result.frames, "checking a deferred sample"))
                    {
                        if (waited.Elapsed > FrameTimeout)
                        {
                            throw new NoFramesException($"a deferred live sample was not drawn within {FrameTimeout.TotalMilliseconds:F0} ms: the swap chain got no free frame "
                                                        + "(display off or session locked?). Measurements so far are incomplete.");
                        }
                        Thread.Sleep(1);
                    }
                    deferredWaits.Add(waited.Elapsed.TotalMilliseconds);
                    continue;
                }
                liveCpu.Add(result.cpu);
                liveFrame.Add(result.frame);
                liveGpu.Add(result.gpuMs);
                liveDone.Add(result.done);
                livePixels.Add(result.pixels);
                allocated += result.bytes;
                // Pen input arrives about once per display frame (4.2 ms at 240 Hz).
                Thread.Sleep(4);
            }
            Console.WriteLine($"  {name}: live sample scene build (app thread) {Stats.Summary(buildTimes)}");
            Console.WriteLine($"  {name}: live sample CPU submit {Stats.Summary(liveCpu)}; of which drawing and present {Stats.Summary(liveFrame)}");
            Console.WriteLine($"  {name}: live sample GPU work {Stats.Summary(liveGpu)}; GPU completion {Stats.Summary(liveDone)}; redrawn pixels p50 {Stats.Percentile(livePixels, 0.5):F0}");
            Console.WriteLine($"  {name}: managed bytes allocated on the overlay thread per drawn sample {allocated / Math.Max(1, liveCpu.Count)}; "
                              + $"deferred samples {deferred} (wait {Stats.Summary(deferredWaits)}); GPU read timeouts {gpu.Timeouts}");
            var counters = session.Run(() => session.Presenter.Renderer?.Counters, "reading renderer counters");
            if (counters is not null)
            {
                Console.WriteLine($"  {name}: renderer: {counters.ListsDrawn} lists drawn, {counters.ListsSkipped} skipped, {counters.CulledStrokes} culled strokes, {counters.RealizationsBuilt} realizations");
            }
            session.Run(() =>
            {
                session.Presenter.ShowLive(null);
                session.Presenter.ShowScene(SceneSnapshot.Empty);
            }, "clearing the workload");
        }
    }
}

/// <summary>Command line options. Every run has a time limit.</summary>
internal sealed record HarnessOptions(string Mode, TimeSpan Timeout, int Samples, LayerKind Scene, LayerKind Live, bool Realizations, string Workload, string? Evidence,
                                      double Opacity, int? Pid = null, PixelRect? Area = null, (byte Red, byte Green, byte Blue) Ink = default,
                                      string? App = null, string[]? Styles = null, bool ReduceMotion = false, bool Dark = false, int Objects = 6,
                                      bool Offscreen = false)
{
    /// <summary>motion-live: the Mac's live recordings (default reference/ux-reference/macos-live).</summary>
    public string? Reference { get; init; }

    /// <summary>motion-live: an ffmpeg to read the Mac's backdrop and encode the videos instead of Media Foundation.</summary>
    public string? Ffmpeg { get; init; }

    /// <summary>motion-live: the Mac recording's desktop as a PNG (default: a frame of the Mac's Off take).</summary>
    public string? Backdrop { get; init; }

    /// <summary>motion-live: the takes to replay (default all ten show and hide takes), and the measurements on the stage.</summary>
    public string[]? Takes { get; init; }

    /// <summary>Whether --theme was given; motion-live otherwise uses each Mac take's appearance.</summary>
    public bool ThemeGiven { get; init; }

    /// <summary>capture-cycles: how many times live effects start and stop (default 4).</summary>
    public int Cycles { get; init; } = 4;

    /// <summary>capture-cycles: how long the paused capture stays open after each stop (default the app's).</summary>
    public TimeSpan? Linger { get; init; }

    /// <summary>The app's ANNOTATOR_LATENCY_LOG file (input): the app's own times of each pointer message.</summary>
    public string? Probe { get; init; }

    /// <summary>Hold DCompositionBoostCompositorClock during the run (timing).</summary>
    public bool Boost { get; init; }

    /// <summary>IDXGIDevice::SetGPUThreadPriority for the presenter's device, -7 to 7 (timing).</summary>
    public int? GpuPriority { get; init; }

    /// <summary>Host the ink mode's swap chain in a DirectComposition tree of the overlay window (ink).</summary>
    public bool DirectComposition { get; init; }

    /// <summary>Look for the ink mode's samples in DXGI desktop duplication instead of Windows.Graphics.Capture (ink).</summary>
    public bool Duplication { get; init; }

    /// <summary>Put the overlay in Draw with the left button held on it during the ink mode's samples (ink).</summary>
    public bool Press { get; init; }

    public const string Usage = """
        Usage: Annotator.RenderHarness <mode> [options]
          modes: frames | show | latency | race | memory | surfaces | perf | input | timing | ink | shot | capture | capture-cycles |
                 capture-start | effects | motion | motion-live | live-compare | pointer | video-check
          --timeout <seconds>   end the run after this time (default 120)
          --samples <count>     latency, timing and race samples (default 60), perf live samples (default 1000),
                                capture frames read for the leak check (default 1000), effects frames per workload (default 100)
          --scene <kind>        VirtualSurface, DrawingSurface or SwapChain (race, perf; default VirtualSurface)
          --live <kind>         VirtualSurface, DrawingSurface or SwapChain (show, race, perf, timing; default VirtualSurface;
                                ink: SwapChain presents a dot instead of the trail)
          --workload <name>     standard, stress or all (perf; default all)
          --realizations        draw committed paths through geometry realizations (perf)
          --opacity <0..1>      opacity of the presenter's visuals during perf (default 0.05; drawing work is the same)
          --evidence <folder>   save screenshots there (show), the PNG file to write (shot), or the folder for sheets and timings (motion),
                                or for the pointer pictures and their comparison with reference/ux-reference/macos/pointer (pointer), or
                                for the samples of input and timing (CSV)
          --area <x,y,w,h>      the monitor area in pixels to save (shot; default the whole monitor) or to play the motion in
                                (motion; default 1,600 x 900 in the middle)
          --styles <a,b,...>    the show and hide styles to play (motion; default dissolve,slide,magic,corners,pop,drop,focus,fade)
          --app <path>          the development build of Annotator.exe (motion; default the newest under src/Annotator.App/bin)
          --reduce-motion       play the motion as with Animation effects off (motion)
          --theme <dark|light>  the sample board's appearance (motion; default light)
          --objects <count>     objects on the sample board, 0 to 64 (motion; default 6)
          --takes <a,b,...>     the Mac takes to replay (motion-live; default the ten show-hide-* takes; also sharing-live with
                                sharing-output, sharing-controls for its toolbar steps alone, effects-live, zoom-screen, zoom-lens,
                                and effects-measure, sharing-measure and zoom-measure to measure the app's effects, region sharing
                                and screen zoom on the stage), or to compare (live-compare; default every take under --evidence)
          --reference <folder>  the Mac's live recordings (motion-live, live-compare, video-check; default reference/ux-reference/macos-live)
          --ffmpeg <path>       an ffmpeg to read the Mac's backdrop and encode the videos (motion-live; default Media Foundation)
          --backdrop <png>      the Mac's desktop for the stage (motion-live; default a frame of show-hide-none.mp4)
          --pid <id>            the running Annotator app (input; Draw with the Pen tool on the whiteboard)
          --probe <file>        the app's ANNOTATOR_LATENCY_LOG file (input): splits the time into delivery, the app's work and composition
          --boost               hold DCompositionBoostCompositorClock during the run (timing)
          --gpu-priority <n>    IDXGIDevice::SetGPUThreadPriority of the presenter's device, -7 to 7 (timing)
          --dcomp               host the swap chain in a DirectComposition tree of the overlay window (ink)
          --duplication         look for the samples in DXGI desktop duplication instead of Windows.Graphics.Capture (ink)
          --press               the overlay in Draw with the left button held on it during the samples (ink)
          --ink <r,g,b>         the pen color to look for (input; default 240,69,56, the pen's coral)
          --offscreen           effects: only the pipeline timings with synthetic frames; nothing appears on screen
          --cycles <count>      capture-cycles: starts and stops of live effects, 1 to 100 (default 4); capture-start: starts
                                per display (default 4)
          --linger-ms <ms>      capture-cycles: how long the paused capture stays open after a stop (default the app's 60 s;
                                0 ends capture with each stop)
        """;

    private static readonly string[] Modes = ["frames", "show", "latency", "race", "memory", "surfaces", "perf", "input", "timing", "ink", "shot", "capture", "capture-cycles",
                                              "capture-start", "effects", "motion", "motion-live", "live-compare", "pointer", "video-check"];

    public static HarnessOptions Parse(string[] args)
    {
        if (args.Length == 0 || !Modes.Contains(args[0]))
        {
            throw new ArgumentException(args.Length == 0 ? "Choose a mode." : $"Unknown mode '{args[0]}'.");
        }
        var mode = args[0];
        var timeout = 120;
        int? samples = null;
        var scene = LayerKind.VirtualSurface;
        var live = LayerKind.VirtualSurface;
        var realizations = false;
        var workload = "all";
        string? evidence = null;
        var opacity = 0.05;
        int? pid = null;
        PixelRect? area = null;
        (byte, byte, byte) ink = (240, 69, 56);
        string? app = null;
        string[]? styles = null;
        var reduceMotion = false;
        var dark = false;
        var themeGiven = false;
        var objects = 6;
        string? reference = null, ffmpeg = null, backdrop = null;
        string[]? takes = null;
        string? probe = null;
        bool boost = false, directComposition = false, duplication = false, press = false;
        int? gpuPriority = null;
        var offscreen = false;
        var cycles = 4;
        TimeSpan? linger = null;
        for (var index = 1; index < args.Length; index++)
        {
            string Value() => index + 1 < args.Length ? args[++index] : throw new ArgumentException($"{args[index]} needs a value.");
            switch (args[index])
            {
                case "--cycles":
                    cycles = int.Parse(Value(), CultureInfo.InvariantCulture);
                    if (cycles is < 1 or > 100)
                    {
                        throw new ArgumentException("--cycles must be between 1 and 100.");
                    }
                    break;
                case "--linger-ms":
                    var milliseconds = int.Parse(Value(), CultureInfo.InvariantCulture);
                    if (milliseconds is < 0 or > 600_000)
                    {
                        throw new ArgumentException("--linger-ms must be between 0 and 600000.");
                    }
                    linger = TimeSpan.FromMilliseconds(milliseconds);
                    break;
                case "--takes":
                    takes = Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                case "--probe":
                    probe = Value();
                    break;
                case "--boost":
                    boost = true;
                    break;
                case "--gpu-priority":
                    gpuPriority = int.Parse(Value(), CultureInfo.InvariantCulture);
                    break;
                case "--dcomp":
                    directComposition = true;
                    break;
                case "--duplication":
                    duplication = true;
                    break;
                case "--press":
                    press = true;
                    break;
                case "--reference":
                    reference = Value();
                    break;
                case "--ffmpeg":
                    ffmpeg = Value();
                    break;
                case "--backdrop":
                    backdrop = Value();
                    break;
                case "--offscreen":
                    offscreen = true;
                    break;
                case "--opacity":
                    opacity = double.Parse(Value(), CultureInfo.InvariantCulture);
                    if (!(opacity is > 0 and <= 1))
                    {
                        throw new ArgumentException("--opacity must be above 0 and at most 1.");
                    }
                    break;
                case "--timeout":
                    timeout = int.Parse(Value(), CultureInfo.InvariantCulture);
                    break;
                case "--samples":
                    samples = int.Parse(Value(), CultureInfo.InvariantCulture);
                    break;
                case "--scene":
                    scene = Enum.Parse<LayerKind>(Value());
                    break;
                case "--live":
                    live = Enum.Parse<LayerKind>(Value());
                    break;
                case "--workload":
                    workload = Value();
                    if (workload is not ("standard" or "stress" or "all"))
                    {
                        throw new ArgumentException($"Unknown workload '{workload}'.");
                    }
                    break;
                case "--realizations":
                    realizations = true;
                    break;
                case "--area":
                    var parts = Value().Split(',').Select(part => int.Parse(part, CultureInfo.InvariantCulture)).ToArray();
                    if (parts.Length != 4)
                    {
                        throw new ArgumentException("--area needs x,y,width,height.");
                    }
                    area = PixelRect.FromSize(parts[0], parts[1], parts[2], parts[3]);
                    break;
                case "--ink":
                    var channels = Value().Split(',').Select(part => byte.Parse(part, CultureInfo.InvariantCulture)).ToArray();
                    if (channels.Length != 3)
                    {
                        throw new ArgumentException("--ink needs red,green,blue.");
                    }
                    ink = (channels[0], channels[1], channels[2]);
                    break;
                case "--pid":
                    pid = int.Parse(Value(), CultureInfo.InvariantCulture);
                    break;
                case "--evidence":
                    evidence = Value();
                    break;
                case "--app":
                    app = Value();
                    break;
                case "--styles":
                    styles = Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                case "--reduce-motion":
                    reduceMotion = true;
                    break;
                case "--theme":
                    themeGiven = true;
                    dark = Value() switch
                    {
                        "dark" => true,
                        "light" => false,
                        _ => throw new ArgumentException("--theme is dark or light."),
                    };
                    break;
                case "--objects":
                    objects = int.Parse(Value(), CultureInfo.InvariantCulture);
                    if (objects is < 0 or > 64)
                    {
                        throw new ArgumentException("--objects must be between 0 and 64.");
                    }
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{args[index]}'.");
            }
        }
        if (timeout is < 1 or > 3_600)
        {
            throw new ArgumentException("--timeout must be between 1 and 3600 seconds.");
        }
        if (samples is < 1 or > 100_000)
        {
            throw new ArgumentException("--samples must be between 1 and 100000.");
        }
        var defaultSamples = mode switch
        {
            "perf" or "capture" => 1_000,
            "effects" => 100,
            _ => 60,
        };
        return new HarnessOptions(mode, TimeSpan.FromSeconds(timeout), samples ?? defaultSamples, scene, live, realizations, workload, evidence, opacity, pid, area, ink,
                                  app, styles, reduceMotion, dark, objects, offscreen)
        {
            Reference = reference,
            Ffmpeg = ffmpeg,
            Backdrop = backdrop,
            Takes = takes,
            ThemeGiven = themeGiven,
            Cycles = cycles,
            Linger = linger,
            Probe = probe,
            Boost = boost,
            GpuPriority = gpuPriority,
            DirectComposition = directComposition,
            Duplication = duplication,
            Press = press,
        };
    }
}
