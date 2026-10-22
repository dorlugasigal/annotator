using System.Diagnostics;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Platform.Graphics;
using Annotator.Platform.Threading;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Presentation;
using Annotator.Rendering.Text;
using Windows.UI.Composition;
using static Annotator.Benchmarks.Measurement;

namespace Annotator.Benchmarks;

/// <summary>
/// WIN-PERF-01 app frame work: the overlay presenter on a dedicated dispatcher thread with its own compositor and real
/// composition virtual surfaces, at the reference display's size (5,120 x 1,440 pixels at 125%), without a window, so
/// nothing appears on screen. The 003 fixtures are stretched over the display's points, as the render harness's perf mode
/// does. Each sample is one frame: from the start of the presenter call on the overlay thread until the thread runs its
/// next work item, which includes the composition commit at the end of the frame's work item (Direct2D work on composition
/// surfaces can finish there). The presenter call alone and the GPU completion of an event query issued after the commit
/// are counters.
/// </summary>
internal static class PresenterWorkloads
{
    public const int PixelWidth = 5_120;
    public const int PixelHeight = 1_440;
    public const double Scale = 1.25;

    /// <summary>The zoom of the zoom-pan workloads: half the display's points in each direction are in view.</summary>
    private const double ViewZoom = 2;

    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(60);

    public static readonly string[] Scenarios =
    [
        "presenter.standard.full-redraw", "presenter.standard.theme-change", "presenter.standard.selection", "presenter.standard.live-sample",
        "presenter.standard.zoom-pan",
        "presenter.stress.full-redraw", "presenter.stress.theme-change", "presenter.stress.selection", "presenter.stress.live-sample",
        "presenter.stress.zoom-pan",
    ];

    public static bool IsPresenter(string scenario) => scenario.StartsWith("presenter.", StringComparison.Ordinal);

    public static int Frames(string scenario, int samples) => Kind(scenario) switch
    {
        "full-redraw" => samples,
        "theme-change" or "selection" => Math.Max(samples, 60),
        "live-sample" => Math.Max(samples, 600),
        "zoom-pan" => Math.Max(samples, 240),
        _ => throw new BenchmarkFailure($"Unknown presenter scenario {scenario}."),
    };

    private static int WarmupFrames(string scenario, int warmup) => Kind(scenario) is "live-sample" or "zoom-pan" ? Math.Max(warmup, 30) : warmup;

    private static string Kind(string scenario) => scenario[(scenario.LastIndexOf('.') + 1)..];

    public static Result Run(string scenario, int samples, int warmup)
    {
        var kind = Kind(scenario);
        var stress = scenario.StartsWith("presenter.stress.", StringComparison.Ordinal);
        var (mixed, strokeSamples) = stress ? (999, 200_000) : (200, 20_000);
        double scaleX = PixelWidth / Scale / Fixtures.Width, scaleY = PixelHeight / Scale / Fixtures.Height;
        var elements = ElementList.Create(Fixtures.Elements(mixed, scaleX: scaleX, scaleY: scaleY));
        var stroke = Fixtures.Stroke(strokeSamples, scaleX, scaleY);
        var frames = Frames(scenario, samples);
        var warmupFrames = WarmupFrames(scenario, warmup);
        using var measurer = new DirectWriteTextMeasurer();
        var builder = new SceneBuilder(measurer);
        var withStroke = builder.Build(elements.Add(stroke));
        var thread = DispatcherThread.Start("Annotator benchmark overlay");
        Session? session = null;
        try
        {
            var current = On(thread, Session.Create);
            session = current;
            var committed = kind switch
            {
                "live-sample" => builder.Build(elements),
                "theme-change" => builder.Build(elements.Add(stroke), new HashSet<Guid> { elements[elements.Count / 2].Id }),
                _ => withStroke,
            };
            var first = Frame(thread, current, presenter => presenter.ShowScene(committed));
            var workload = Workload(("elements", elements.Count + (kind == "live-sample" ? 0 : 1)), ("longStrokePoints", strokeSamples),
                                    ("pixelWidth", PixelWidth), ("pixelHeight", PixelHeight), ("scalePercent", (long)(Scale * 100)),
                                    ("frames", frames), ("firstFrameMicroseconds", (long)Math.Round(first.FrameMs * 1_000)));
            var results = new List<Sample>(frames);
            var liveBuilder = new SceneBuilder(measurer);
            var growing = stroke;
            if (kind == "live-sample")
            {
                var firstLive = liveBuilder.Build(ElementList.Create([growing]));
                Frame(thread, current, presenter => presenter.ShowLive(firstLive));
            }
            for (var frame = 0; frame < warmupFrames + frames; frame++)
            {
                Action<OverlayPresenter> step;
                double buildMs = 0;
                switch (kind)
                {
                    case "full-redraw":
                        step = static presenter => presenter.Invalidate();
                        break;
                    case "theme-change":
                        step = presenter =>
                        {
                            current.Colors.Current = current.Colors.Current == FixedSystemColors.Light ? FixedSystemColors.Dark : FixedSystemColors.Light;
                            presenter.RedrawSystemColors();
                        };
                        break;
                    case "selection":
                    {
                        var selected = new HashSet<Guid> { elements[frame * 37 % elements.Count].Id };
                        var snapshot = builder.Build(elements.Add(stroke), selected);
                        step = presenter => presenter.ShowScene(snapshot);
                        break;
                    }
                    case "live-sample":
                    {
                        var last = growing.Points[^1];
                        // The fixture stroke ends near the right edge; new samples run back across the display.
                        growing.Points = growing.Points.Add(new Point(last.X - 0.4 * scaleX, 540 * scaleY + Math.Sin(frame * 0.05) * 300 * scaleY));
                        growing.Frame = growing.Frame.Union(new Rect(growing.Points[^1], growing.Points[^1]));
                        growing.GeometryToken = Guid.NewGuid();
                        var start = Stopwatch.GetTimestamp();
                        var live = liveBuilder.Build(ElementList.Create([growing]));
                        buildMs = Milliseconds(start, Stopwatch.GetTimestamp());
                        step = presenter => presenter.ShowLive(live);
                        break;
                    }
                    default:
                    {
                        // A pointer moving 8 points a frame to the right and back over a 2x view of the display's middle.
                        double viewWidth = PixelWidth / Scale / ViewZoom, viewHeight = PixelHeight / Scale / ViewZoom;
                        var travel = PixelWidth / Scale - viewWidth;
                        var position = frame * 8.0 % (2 * travel);
                        var originX = position <= travel ? position : 2 * travel - position;
                        var originY = (PixelHeight / Scale - viewHeight) / 2;
                        step = presenter => presenter.SetView(ViewZoom, originX, originY);
                        break;
                    }
                }
                var measured = Frame(thread, current, step);
                if (frame >= warmupFrames)
                {
                    results.Add(new Sample(measured.FrameMs, measured.Before, measured.After,
                                           Counters(("gpuCompletionMicroseconds", (ulong)Math.Round(measured.CompletionMs * 1_000)),
                                                    ("presenterCallMicroseconds", (ulong)Math.Round(measured.CallMs * 1_000)),
                                                    ("redrawnPixels", (ulong)measured.Pixels), ("allocatedBytes", (ulong)measured.Allocated),
                                                    ("sceneBuildMicroseconds", (ulong)Math.Round(buildMs * 1_000)),
                                                    ("listsDrawn", (ulong)measured.ListsDrawn))));
                }
            }
            var state = On(thread, () => current.Presenter.State);
            BenchmarkFailure.Require(state is PresenterState.Ready, $"The presenter left Ready: {state}.");
            var scope = kind switch
            {
                "full-redraw" => "OverlayPresenter.Invalidate on the overlay thread: the committed scene redrawn in full (scale or device change).",
                "theme-change" => "OverlayPresenter.RedrawSystemColors after the light and dark themes swap, with one element selected: what a theme change redraws.",
                "selection" => "OverlayPresenter.ShowScene with another selected element: the old and new outline areas redrawn.",
                "live-sample" => "OverlayPresenter.ShowLive with one more sample on the long stroke over the committed scene; the scene build is a counter.",
                _ => "OverlayPresenter.SetView at 2x with the origin 8 points further: everything in view drawn through screen zoom's new viewport.",
            };
            return Finish(scenario, workload, scope + " Duration runs until the overlay thread's next work item (commit included); the presenter call and GPU completion are counters. Virtual surfaces, hardware device, no window.",
                          warmupFrames, results);
        }
        finally
        {
            if (session is not null)
            {
                On(thread, () =>
                {
                    session.Dispose();
                    return true;
                });
            }
            if (!((IAsyncResult)thread.ShutdownAsync()).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(10)))
            {
                Console.Error.WriteLine("WARNING: the benchmark overlay thread did not shut down within 10 s; the process exit removes it.");
            }
        }
    }

    private static T On<T>(DispatcherThread thread, Func<T> work)
    {
        var task = thread.RunAsync(work);
        if (!((IAsyncResult)task).AsyncWaitHandle.WaitOne(StepTimeout))
        {
            throw new BenchmarkFailure($"The overlay thread did not finish a step within {StepTimeout.TotalSeconds:F0} s.");
        }
        return task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// One frame in its own work item, which queues a second one before it returns: the second starts once the first
    /// returned and the compositor committed, ends the frame's time and waits for the GPU.
    /// </summary>
    private static FrameResult Frame(DispatcherThread thread, Session session, Action<OverlayPresenter> step)
    {
        var done = new TaskCompletionSource<FrameResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        On(thread, () =>
        {
            var begun = session.Begin(step);
            var queued = thread.Queue.TryEnqueue(() =>
            {
                try
                {
                    var ended = session.End();
                    done.SetResult(begun with { FrameMs = ended.FrameMs, CompletionMs = ended.CompletionMs, After = ended.After });
                }
                catch (Exception error)
                {
                    done.SetException(error);
                }
            });
            if (!queued)
            {
                done.SetException(new BenchmarkFailure("The overlay thread did not accept the end of a frame."));
            }
            return true;
        });
        if (!((IAsyncResult)done.Task).AsyncWaitHandle.WaitOne(StepTimeout))
        {
            throw new BenchmarkFailure($"The overlay thread did not end a frame within {StepTimeout.TotalSeconds:F0} s.");
        }
        return done.Task.GetAwaiter().GetResult();
    }

    /// <summary>One measured frame.</summary>
    private readonly record struct FrameResult(double FrameMs, double CallMs, double CompletionMs, long Pixels, long Allocated, long ListsDrawn,
                                               MemoryReading Before, MemoryReading After);

    /// <summary>The overlay thread's devices, compositor and presenter. Use it on that thread only.</summary>
    private sealed class Session : IDisposable
    {
        private readonly GraphicsDevices devices;
        private readonly Compositor compositor;
        private readonly ContainerVisual root;
        private readonly GpuFence fence;

        private Session(GraphicsDevices devices, Compositor compositor, ContainerVisual root, OverlayPresenter presenter, GpuFence fence, ThemeColors colors)
        {
            this.devices = devices;
            this.compositor = compositor;
            this.root = root;
            Presenter = presenter;
            this.fence = fence;
            Colors = colors;
        }

        public OverlayPresenter Presenter { get; }

        /// <summary>The system colors the presenter draws with; theme-change workloads swap them.</summary>
        public ThemeColors Colors { get; }

        public static Session Create()
        {
            var devices = GraphicsDevices.Create();
            var compositor = new Compositor();
            var root = compositor.CreateContainerVisual();
            var colors = new ThemeColors();
            var presenter = OverlayPresenter.Create(new OverlayPresenterOptions
            {
                Compositor = compositor,
                Root = root,
                Devices = devices,
                Scale = Scale,
                PixelWidth = PixelWidth,
                PixelHeight = PixelHeight,
                Colors = colors,
            });
            BenchmarkFailure.Require(presenter.State is PresenterState.Ready, $"The presenter did not start: {presenter.State}.");
            return new Session(devices, compositor, root, presenter, new GpuFence(devices), colors);
        }

        private long frameStart;

        /// <summary>Runs one frame's presenter call; the frame ends in <see cref="End"/>, the next work item.</summary>
        public FrameResult Begin(Action<OverlayPresenter> step)
        {
            var statistics = Presenter.Statistics;
            var drawn = Presenter.Renderer?.Counters.ListsDrawn ?? 0;
            long sceneFrames = statistics.SceneFrames, liveFrames = statistics.LiveFrames;
            var before = MemoryReading.Read();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            step(Presenter);
            var submitted = Stopwatch.GetTimestamp();
            var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            frameStart = start;
            var pixels = (statistics.SceneFrames > sceneFrames ? statistics.LastScenePixels : 0) + (statistics.LiveFrames > liveFrames ? statistics.LastLivePixels : 0);
            return new FrameResult(0, Milliseconds(start, submitted), 0, pixels, bytes, (Presenter.Renderer?.Counters.ListsDrawn ?? 0) - drawn, before, before);
        }

        /// <summary>The work item after a frame: the commit is done; waits for the GPU.</summary>
        public FrameResult End()
        {
            var frame = Milliseconds(frameStart, Stopwatch.GetTimestamp());
            fence.Signal();
            fence.Wait();
            var completed = Milliseconds(frameStart, Stopwatch.GetTimestamp());
            return new FrameResult(frame, 0, completed, 0, 0, 0, default, MemoryReading.Read());
        }

        public void Dispose()
        {
            fence.Dispose();
            Presenter.Dispose();
            root.Dispose();
            compositor.Dispose();
            devices.Dispose();
        }
    }
}

/// <summary>System colors that a workload switches between the light and dark themes, as Windows does.</summary>
internal sealed class ThemeColors : ISystemColorResolver
{
    public FixedSystemColors Current { get; set; } = FixedSystemColors.Light;

    public Annotator.Core.RGBA Resolve(SystemColor color) => Current.Resolve(color);
}
