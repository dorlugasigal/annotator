using System.Runtime.InteropServices;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Platform.Capture;
using Annotator.Platform.Displays;
using Annotator.Platform.Threading;
using Annotator.Rendering.Effects;
using Annotator.Rendering.Imaging;
using Annotator.Rendering.Presentation;
using Annotator.Rendering.Zoom;
using Windows.UI.Composition;

namespace Annotator.Rendering.Tests;

/// <summary>
/// Live zoom's drawing (WIN-CAP-05, WIN-CAP-06): the captured frame through the viewport with nearest sampling, as the Mac's
/// ViewportProjection does, the drawings and the effect layer through the same viewport, and the captured view's capture use.
/// </summary>
public sealed class ZoomRenderingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly DisplayInfo Display = new(1, @"\\.\ANNOTATOR-ZOOM1", new PixelRect(0, 0, 200, 120), new PixelRect(0, 0, 200, 110), 120, true);

    private static RasterImage Source() =>
        EffectTextures.Image(64, 48, (x, y) => ((byte)(x * 4), (byte)(y * 5), (byte)((x + y) % 7 * 30)));

    private static RasterImage Render(RasterImage source, Action<EffectPipeline, nint, nint> draw)
    {
        var devices = Warp.Devices;
        using var pipeline = new EffectPipeline(devices);
        var input = EffectTextures.Upload(devices, source);
        var target = EffectTextures.Target(devices, source.Width, source.Height);
        try
        {
            draw(pipeline, input, target);
            return EffectTextures.Read(devices, target, source.Width, source.Height);
        }
        finally
        {
            Marshal.Release(target);
            Marshal.Release(input);
        }
    }

    private static RasterImage View(RasterImage source, EffectList list, Rect crop, bool regionsOnly) =>
        Render(source, (pipeline, input, target) =>
            pipeline.RenderView(input, target, list, new PixelRect(0, 0, source.Width, source.Height), crop, regionsOnly));

    /// <summary>The texel each view pixel shows: the one under the pixel's center, mapped into the crop.</summary>
    private static (int X, int Y) Texel(RasterImage image, Rect crop, int x, int y) =>
        (Math.Clamp((int)Math.Floor(crop.X + (x + 0.5) * crop.Width / image.Width), 0, image.Width - 1),
         Math.Clamp((int)Math.Floor(crop.Y + (y + 0.5) * crop.Height / image.Height), 0, image.Height - 1));

    private static void AssertProjection(RasterImage expected, RasterImage view, Rect crop, bool opaque)
    {
        for (var y = 0; y < view.Height; y++)
        {
            for (var x = 0; x < view.Width; x++)
            {
                var (sx, sy) = Texel(expected, crop, x, y);
                var want = expected[sx, sy];
                if (opaque)
                {
                    want.Alpha = 255;
                }
                Assert.True(want == view[x, y], $"Pixel ({x}, {y}) of crop {crop} shows {view[x, y]}, not texel ({sx}, {sy}) {want}.");
            }
        }
    }

    // Mac ViewportProjection.imageForMetal: samplingNearest() through the viewport, at whole and fractional crops.
    [Fact]
    public void ViewsShowTheCropWithNearestSampling()
    {
        var source = Source();
        var list = EffectList.From(ElementList.Empty, 1, 64, 48);
        foreach (var crop in new[] { new Rect(16, 12, 32, 24), new Rect(10.25, 5.5, 25.6, 19.2), new Rect(0, 0, 64, 48), new Rect(40.7, 30.1, 4, 3) })
        {
            AssertProjection(source, View(source, list, crop, regionsOnly: false), crop, opaque: true);
        }
    }

    // Mac LivePresentation: the screen view shows covers and pixel effects, and the pasted images of the effect scene.
    [Fact]
    public void ViewsShowTheProtectedSceneThroughTheCrop()
    {
        var source = Source();
        var cover = new Element(Tool.Cover, new Rect(20, 16, 8, 8));
        var blur = new Element(Tool.Pixelate, new Rect(30, 10, 12, 12));
        var list = EffectList.From(ElementList.Create([cover, blur]), 1, 64, 48);
        var scene = Render(source, (pipeline, input, target) => pipeline.RenderScene(input, target, list));
        var crop = new Rect(16, 12, 32, 24);
        var view = View(source, list, crop, regionsOnly: false);
        AssertProjection(scene, view, crop, opaque: true);
        // The cover (20, 16)-(28, 24) fills (8, 8)-(24, 24) of the view at twice the size.
        Assert.Equal((0, 0, 0, 255), view[8, 8]);
        Assert.Equal((0, 0, 0, 255), view[23, 23]);
        Assert.NotEqual((0, 0, 0, 255), view[24, 24]);
    }

    // Mac effectsView.present with the screen viewport: the effect layer keeps its transparency outside the regions.
    [Fact]
    public void TheEffectLayerKeepsItsTransparencyThroughTheView()
    {
        var source = Source();
        var list = EffectList.From(ElementList.Create([new Element(Tool.Pixelate, new Rect(16, 12, 16, 12))]), 1, 64, 48);
        var layer = Render(source, (pipeline, input, target) => pipeline.Render(input, target, list, [new PixelRect(0, 0, 64, 48)]));
        var crop = new Rect(16, 12, 32, 24);
        var view = View(source, list, crop, regionsOnly: true);
        AssertProjection(layer, view, crop, opaque: false);
        Assert.Equal(255, view[31, 23].Alpha);
        Assert.Equal(0, view[32, 24].Alpha);
        Assert.Equal(0, view[63, 47].Alpha);
    }

    private static (OverlayPresenter Presenter, Func<BitmapLayer?> Scene) Presenter(Compositor compositor, ContainerVisual root, double scale, int width, int height,
                                                                                    bool devices = true)
    {
        BitmapLayer? scene = null;
        var presenter = OverlayPresenter.Create(new OverlayPresenterOptions
        {
            Compositor = compositor,
            Root = root,
            Devices = devices ? Warp.Devices : null,
            Scale = scale,
            PixelWidth = width,
            PixelHeight = height,
            LayerFactory = (owner, live) =>
            {
                var layer = new BitmapLayer(owner.Compositor, owner.Devices!, owner.PixelWidth, owner.PixelHeight);
                if (!live)
                {
                    scene = layer;
                }
                return layer;
            },
        });
        return (presenter, () => scene);
    }

    private static async Task OnDispatcher(Action<Compositor, ContainerVisual> work)
    {
        var thread = DispatcherThread.Start("Annotator zoom test");
        try
        {
            await thread.RunAsync(() =>
            {
                using var compositor = new Compositor();
                using var root = compositor.CreateContainerVisual();
                work(compositor, root);
            }).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }
        finally
        {
            await thread.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
    }

    // Mac DrawingCanvas under screen zoom: drawings magnify with the screen, through the same viewport.
    [Fact]
    public async Task DrawingsAreDrawnThroughTheZoomView()
    {
        await OnDispatcher((compositor, root) =>
        {
            const double Scale = 1.25;
            var builder = Warp.Builder();
            var shape = new Element(Tool.Ellipse, new Rect(30, 20, 60, 40));
            var text = new Element(Tool.Text, new Rect(40, 70, 120, 30), text: "Zoom");
            var scene = builder.Build(ElementList.Create([shape, text]), new HashSet<Guid> { shape.Id });
            var (presenter, layer) = Presenter(compositor, root, Scale, 300, 200);
            using var owned = presenter;
            presenter.ShowScene(scene);
            presenter.SetView(2, 0, 0);
            Assert.Equal(2, presenter.ViewZoom);
            Assert.Equal(1, presenter.Statistics.ViewRedraws);
            // At the origin, zoom 2 is the scene drawn at twice the display scale.
            using (var exporter = Warp.Exporter())
            {
                var doubled = exporter.Render(scene, new Size(300 / (Scale * 2), 200 / (Scale * 2)), Scale * 2, includeSelection: true);
                Assert.True(Pixels.SamePixels(doubled, layer()!.Read()), "Zoom 2 differs from a render at twice the scale.");
            }

            // Changes under zoom redraw only what changed, through the view: identical to a fresh presenter with that view.
            presenter.SetView(3.5, 22.25, 13.5);
            shape.Frame = shape.Frame.Translated(new Point(6, 4));
            shape.InvalidateGeometry();
            var moved = builder.Build(ElementList.Create([shape, text]), new HashSet<Guid> { text.Id });
            presenter.ShowScene(moved);
            var (fresh, freshLayer) = Presenter(compositor, root, Scale, 300, 200);
            using (fresh)
            {
                fresh.SetView(3.5, 22.25, 13.5);
                fresh.ShowScene(moved);
                Assert.True(Pixels.SamePixels(freshLayer()!.Read(), layer()!.Read()), "The incremental frame through the view differs from a full redraw.");
            }

            // Leaving zoom clears every zoomed pixel.
            presenter.SetView(1, 0, 0);
            using (var exporter = Warp.Exporter())
            {
                var plain = exporter.Render(moved, new Size(300 / Scale, 200 / Scale), Scale, includeSelection: true);
                Assert.True(Pixels.SamePixels(plain, layer()!.Read()), "Pixels drawn through the view were left behind.");
            }
            presenter.SetView(1, 0, 0);
            Assert.Equal(3, presenter.Statistics.ViewRedraws);
            Assert.Throws<ArgumentOutOfRangeException>(() => presenter.SetView(double.NaN, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => presenter.SetView(0, 0, 0));
        });
    }

    // A cover stays opaque through the view, also while the overlay has no devices (WIN-REND-05).
    [Fact]
    public async Task CoversFollowTheViewWithoutDevices()
    {
        await OnDispatcher((compositor, root) =>
        {
            var (presenter, _) = Presenter(compositor, root, 1, 200, 120, devices: false);
            using var owned = presenter;
            presenter.ShowScene(Warp.Builder().Build(ElementList.Create([new Element(Tool.Cover, new Rect(60, 30, 20, 10))])));
            presenter.SetView(2, 50, 25);
            var fallback = presenter.Visual.Children.OfType<ContainerVisual>().Single(visual => visual is not SpriteVisual);
            var cover = Assert.Single(fallback.Children);
            Assert.Equal((20f, 10f, 40f, 20f), (cover.Offset.X, cover.Offset.Y, cover.Size.X, cover.Size.Y));
        });
    }

    private sealed class Harness : IAsyncDisposable
    {
        public DispatcherThread Thread { get; } = DispatcherThread.Start("Annotator captured view test");

        public FakeCapture Capture { get; } = new();

        public Compositor Compositor { get; private set; } = null!;

        public ContainerVisual Root { get; private set; } = null!;

        public CapturedViewPresenter View { get; private set; } = null!;

        public EffectsPresenter Effects { get; private set; } = null!;

        public int Changes { get; private set; }

        public static async Task<Harness> Start()
        {
            var harness = new Harness();
            await harness.Run(() =>
            {
                harness.Compositor = new Compositor();
                harness.Root = harness.Compositor.CreateContainerVisual();
                harness.Root.Children.InsertAtTop(harness.Compositor.CreateSpriteVisual());
                harness.View = CapturedViewPresenter.Create(new CapturedViewOptions
                {
                    Compositor = harness.Compositor,
                    Root = harness.Root,
                    AtBottom = true,
                    Capture = harness.Capture,
                    Display = Display,
                    PixelWidth = 200,
                    PixelHeight = 120,
                });
                harness.View.Changed += (_, _) => harness.Changes++;
                harness.Effects = EffectsPresenter.Create(new EffectsPresenterOptions
                {
                    Compositor = harness.Compositor,
                    Root = harness.Root,
                    Capture = harness.Capture,
                    Display = Display,
                    Scale = 1.25,
                    PixelWidth = 200,
                    PixelHeight = 120,
                });
            });
            return harness;
        }

        public Task Run(Action work) => Thread.RunAsync(work).WaitAsync(Timeout, TestContext.Current.CancellationToken);

        public Task<T> Run<T>(Func<T> work) => Thread.RunAsync(work).WaitAsync(Timeout, TestContext.Current.CancellationToken);

        public async Task Until(Func<bool> condition, string what, FakeLease? feed = null)
        {
            var deadline = DateTime.UtcNow + Timeout;
            while (!await Run(condition))
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"Timed out waiting for {what}; the view is {await Run(() => View.State)}.");
                }
                feed?.Push(Display.Bounds.Width, Display.Bounds.Height);
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Run(() =>
                {
                    View?.Dispose();
                    Effects?.Dispose();
                    Root?.Dispose();
                    Compositor?.Dispose();
                });
            }
            finally
            {
                await Thread.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                await Task.Delay(50, TestContext.Current.CancellationToken);
                foreach (var lease in Capture.Leases)
                {
                    lease.ReleaseTextures();
                }
            }
        }
    }

    private static CapturedView ViewOf(Rect crop) => new(EffectList.From(ElementList.Empty, 1.25, 200, 120), crop);

    // Mac LivePresentation makes its zoomed views only once capture has a frame: nothing shows while capture starts, the
    // black cover then stays under the picture (a resized surface shows it until its next frame), and no capture runs
    // without zoom.
    [Fact]
    public async Task CapturedViewsShowNothingUntilTheirFirstFrame()
    {
        await using var harness = await Harness.Start();
        await harness.Run(() =>
        {
            var view = harness.View;
            Assert.Equal(CapturedViewState.Idle, view.State);
            Assert.Empty(harness.Capture.Leases);
            Assert.False(view.Visual.IsVisible);
            // Under everything else in the root: the drawings stay above the zoomed screen.
            Assert.Same(view.Visual, harness.Root.Children.First());
            view.Show(ViewOf(new Rect(50, 30, 100, 60)));
            Assert.Equal(CapturedViewState.Waiting, view.State);
            Assert.False(view.Visual.IsVisible);
            Assert.False(view.Presented);
            Assert.False(view.Visual.Children.Last().IsVisible);
        });
        var lease = Assert.Single(harness.Capture.Leases);
        Assert.Equal(Display, lease.Display);

        await harness.Until(() => harness.View.Presented, "the first zoomed frame", lease);
        await harness.Run(() =>
        {
            var view = harness.View;
            Assert.Equal(CapturedViewState.Live, view.State);
            Assert.True(view.Visual.IsVisible);
            Assert.True(view.Visual.Children.Last().IsVisible);
            Assert.True(view.Statistics.Frames >= 1);
            // A new crop is drawn at once on the calling thread, so the zoomed screen moves with the drawings.
            var drawn = view.Statistics.Frames;
            view.Show(ViewOf(new Rect(60, 30, 100, 60)));
            Assert.True(view.Statistics.Frames > drawn || view.Statistics.Skipped > 0, "A new crop was not drawn at once.");
            view.Resize(220, 120);
            Assert.False(view.Presented);
            Assert.True(view.Visual.IsVisible);
            Assert.True(((SpriteVisual)view.Visual.Children.First()).IsVisible);
            Assert.False(view.Visual.Children.Last().IsVisible);
        });
        await harness.Until(() => harness.View.Presented, "a frame after the resize", lease);
        await harness.Run(() =>
        {
            var view = harness.View;
            Assert.True(view.Visual.Children.Last().IsVisible);
            view.Show(null);
            Assert.Equal(CapturedViewState.Idle, view.State);
            Assert.False(view.Visual.IsVisible);
            Assert.False(view.Presented);
        });
        Assert.True(lease.Disposed);
    }

    // The owner's rule: no steady-state per-frame allocation. A captured view at a still pointer draws every captured frame.
    // Unoptimized (Debug) code allocates in the pipeline's native calls, so the count is checked in optimized builds only.
    [Fact]
    public async Task CapturedViewsDrawFramesWithoutAllocating()
    {
        if (typeof(EffectPipeline).Assembly.GetCustomAttributes(typeof(System.Diagnostics.DebuggableAttribute), false)
                is [System.Diagnostics.DebuggableAttribute { IsJITOptimizerDisabled: true }])
        {
            Assert.Skip("Allocation counts need an optimized build (validate.ps1 runs the tests in Release).");
        }
        await using var harness = await Harness.Start();
        await harness.Run(() => harness.View.Show(ViewOf(new Rect(20, 10, 100, 60))));
        var lease = harness.Capture.Current;
        await harness.Until(() => harness.View.Presented, "the first zoomed frame", lease);
        for (int warm = 0; warm < 20; warm++)
        {
            lease.Push(Display.Bounds.Width, Display.Bounds.Height);
        }
        long drawn = await harness.Run(() => harness.View.Statistics.Frames);
        const int Frames = 200;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < Frames; index++)
        {
            lease.Push(Display.Bounds.Width, Display.Bounds.Height);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        long frames = await harness.Run(() => harness.View.Statistics.Frames) - drawn;
        Assert.True(frames >= Frames - 1, $"Only {frames} of {Frames} frames were drawn.");
        Assert.True(allocated / (double)Frames < 1, $"{allocated} bytes for {Frames} frames.");
    }

    // Mac reportScreenZoomFailure: a failed capture fails the view; a new view tries again with a new lease.
    [Fact]
    public async Task FailedCaptureFailsTheViewUntilItStartsAgain()
    {
        await using var harness = await Harness.Start();
        await harness.Run(() => harness.View.Show(ViewOf(new Rect(0, 0, 100, 60))));
        var lease = harness.Capture.Current;
        await harness.Until(() => harness.View.Presented, "the first zoomed frame", lease);
        lease.SetState(new CaptureState.Stalled(TimeSpan.FromMilliseconds(400)));
        await harness.Until(() => harness.View.State == CapturedViewState.Paused, "a paused view");
        // A stall keeps the last zoomed frame on screen.
        await harness.Run(() => Assert.True(harness.View.Visual.Children.Last().IsVisible));
        lease.SetState(new CaptureState.Failed(new CaptureFailure(CaptureFailureKind.StreamClosed, "test")));
        await harness.Until(() => harness.View.State == CapturedViewState.Failed, "a failed view");
        await harness.Run(() =>
        {
            Assert.NotNull(harness.View.Failure);
            Assert.False(harness.View.Visual.Children.Last().IsVisible);
            harness.View.Show(null);
            harness.View.Show(ViewOf(new Rect(0, 0, 100, 60)));
            Assert.Null(harness.View.Failure);
            Assert.Equal(CapturedViewState.Waiting, harness.View.State);
        });
        Assert.Equal(2, harness.Capture.Leases.Length);
        await harness.Until(() => harness.View.Presented, "a frame after the restart", harness.Capture.Current);
    }

    // Mac effectsView.present(viewport:): effect regions, and their opaque stand-ins, follow screen zoom's viewport.
    [Fact]
    public async Task EffectRegionsFollowTheZoomViewport()
    {
        await using var harness = await Harness.Start();
        await harness.Run(() =>
        {
            var effects = harness.Effects;
            effects.Show(ElementList.Create([new Element(Tool.Blur, new Rect(10, 8, 40, 20))]));
            // At 125%: (12.5, 10)-(62.5, 35), rounded outward to (12, 10)-(63, 35); through a 2x crop at the origin.
            effects.SetViewport(new Rect(0, 0, 100, 60));
            var fallback = effects.Visual.Children.OfType<ContainerVisual>().Single(visual => visual is not SpriteVisual);
            var rectangle = Assert.Single(fallback.Children, visual => visual.IsVisible);
            Assert.Equal((24f, 20f, 102f, 50f), (rectangle.Offset.X, rectangle.Offset.Y, rectangle.Size.X, rectangle.Size.Y));
            effects.SetViewport(null);
            rectangle = Assert.Single(fallback.Children, visual => visual.IsVisible);
            Assert.Equal((12f, 10f, 51f, 25f), (rectangle.Offset.X, rectangle.Offset.Y, rectangle.Size.X, rectangle.Size.Y));
            effects.SetViewport(new Rect(6, 5, 100, 60));
        });
        var lease = harness.Capture.Leases.Single(candidate => !candidate.Disposed);
        await harness.Until(() => harness.Effects.State is EffectsState.Live, "live effects through the viewport", lease);
    }
}
