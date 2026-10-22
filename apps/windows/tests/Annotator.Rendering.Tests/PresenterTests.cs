using System.Diagnostics;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Platform.Input;
using Annotator.Platform.Overlays;
using Annotator.Platform.Threading;
using Annotator.Platform.Windowing;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Export;
using Annotator.Rendering.Imaging;
using Annotator.Rendering.Interop;
using Annotator.Rendering.Presentation;
using Windows.UI.Composition;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Annotator.Rendering.Tests;

/// <summary>
/// A presenter layer that keeps its pixels in a GPU bitmap and can read them back, so tests can compare
/// damage-limited redraws with full renders. It can also refuse frames (a stalled swap chain) or fail a draw.
/// Test-only: the app presents through composition surfaces.
/// </summary>
internal sealed unsafe class BitmapLayer : PresentationLayer
{
    private readonly OffscreenCanvas canvas;
    private ID2D1Bitmap1* target;

    public BitmapLayer(Compositor compositor, GraphicsDevices devices, int width, int height)
        : base(compositor, width, height)
    {
        canvas = new OffscreenCanvas(devices);
        var properties = new D2D1_BITMAP_PROPERTIES1
        {
            pixelFormat = new D2D1_PIXEL_FORMAT { format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED },
            dpiX = 96,
            dpiY = 96,
            bitmapOptions = D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_TARGET,
        };
        ID2D1Bitmap1* bitmap;
        Native.Check(canvas.Context->CreateBitmap(new D2D_SIZE_U { width = (uint)width, height = (uint)height }, null, 0, &properties, &bitmap), "CreateBitmap");
        target = bitmap;
    }

    public override LayerKind Kind => LayerKind.DrawingSurface;

    public int Frames { get; private set; }

    /// <summary>False makes the layer behave like a swap chain whose display takes no frames.</summary>
    public bool FramesAvailable { get; set; } = true;

    /// <summary>An HRESULT the next draw returns instead of drawing; 0 draws normally.</summary>
    public int FailNextDraw { get; set; }

    public override bool TryTakeFrame() => FramesAvailable;

    public override bool DeferFrame() => FramesAvailable;

    public override int Draw(DamageRegion damage, double scale, bool live, ILayerContent content)
    {
        if (FailNextDraw != 0)
        {
            var failure = FailNextDraw;
            FailNextDraw = 0;
            return failure;
        }
        var context = canvas.Context;
        context->SetTarget((ID2D1Image*)target);
        context->BeginDraw();
        try
        {
            foreach (var rect in damage.Rects)
            {
                Clear((ID2D1DeviceContext*)context, rect);
                content.Render((nint)context, new DrawArea(scale, 0, 0, rect, live));
            }
        }
        finally
        {
            var result = context->EndDraw(null, null);
            context->SetTarget(null);
            Native.Check(result, "EndDraw");
        }
        Frames++;
        return 0;
    }

    public RasterImage Read()
    {
        var bitmap = target;
        return canvas.Render(Width, Height, context =>
        {
            var identity = D2D.Identity;
            context->SetTransform(&identity);
            var destination = D2D.Rect(0, 0, Width, Height);
            context->DrawBitmap((ID2D1Bitmap*)bitmap, &destination, 1, D2D1_INTERPOLATION_MODE.D2D1_INTERPOLATION_MODE_NEAREST_NEIGHBOR, null, null);
        });
    }

    public override void Dispose()
    {
        Native.Release(ref target);
        canvas.Dispose();
        base.Dispose();
    }
}

public sealed class PresenterTests
{
    private const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>The newest bitmap layers a presenter created (a device change creates new ones).</summary>
    private sealed class Layers
    {
        public BitmapLayer? Scene { get; set; }

        public BitmapLayer? Live { get; set; }
    }

    private static (OverlayPresenter Presenter, Layers Layers) Create(Compositor compositor, ContainerVisual root, GraphicsDevices? devices, double scale,
                                                                      int width, int height, TimeSpan? stallThreshold = null)
    {
        var layers = new Layers();
        var presenter = OverlayPresenter.Create(new OverlayPresenterOptions
        {
            Compositor = compositor,
            Root = root,
            Devices = devices,
            Scale = scale,
            PixelWidth = width,
            PixelHeight = height,
            FrameStallThreshold = stallThreshold ?? TimeSpan.FromMilliseconds(500),
            LayerFactory = (owner, live) =>
            {
                var layer = new BitmapLayer(owner.Compositor, owner.Devices!, owner.PixelWidth, owner.PixelHeight);
                if (live)
                {
                    layers.Live = layer;
                }
                else
                {
                    layers.Scene = layer;
                }
                return layer;
            },
        });
        return (presenter, layers);
    }

    /// <summary>Runs <paramref name="work"/> on a new overlay-style dispatcher thread with a compositor; every wait is bounded.</summary>
    private static async Task OnDispatcher(Action<Compositor, ContainerVisual> work)
    {
        var thread = DispatcherThread.Start("Annotator presenter test");
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

    private static RasterImage FullRender(SceneSnapshot scene, double scale, int width, int height, bool includeSelection)
    {
        using var exporter = Warp.Exporter();
        return exporter.Render(scene, new Size(width / scale, height / scale), scale, includeSelection: includeSelection);
    }

    private static SpriteVisual[] Sprites(OverlayPresenter presenter) => [.. presenter.Visual.Children.OfType<SpriteVisual>()];

    private static ContainerVisual Fallback(OverlayPresenter presenter) =>
        presenter.Visual.Children.OfType<ContainerVisual>().Single(visual => visual is not SpriteVisual);

    private static HashSet<(float X, float Y, float Width, float Height)> Covers(OverlayPresenter presenter) =>
        [.. Fallback(presenter).Children.Select(visual => (visual.Offset.X, visual.Offset.Y, visual.Size.X, visual.Size.Y))];

    private static Element Stroke(params Point[] points) => new(Tool.Pen, points: ValueList.Create<Point>(points));

    [Fact]
    public async Task IncrementalRedrawsMatchAFullRedraw()
    {
        await OnDispatcher((compositor, root) =>
        {
            const double Scale = 1.25;
            var builder = Warp.Builder();
            var shapes = new List<Element>();
            for (var index = 0; index < 12; index++)
            {
                var element = new Element(index % 3 == 0 ? Tool.Ellipse : Tool.Rectangle, new Rect(10 + index * 13, 12 + index * 7, 60, 40));
                element.Style.Roughness = index % 2 * 2;
                element.Style.Fill = index % 4 == 0 ? new RGBA(0.2, 0.5, 0.9, 0.4) : null;
                shapes.Add(element);
            }
            var text = new Element(Tool.Text, new Rect(30, 120, 200, 40), text: "Damage");
            var cover = new Element(Tool.Cover, new Rect(150.3, 20.6, 30.2, 25.1));
            var first = ElementList.Create([.. shapes, text, cover]);
            var (presenter, layers) = Create(compositor, root, Warp.Devices, Scale, 320, 220);
            using var owned = presenter;
            presenter.ShowScene(builder.Build(first, new HashSet<Guid> { shapes[2].Id }));
            Assert.Equal(1, layers.Scene!.Frames);

            // Move one shape, remove another, add an arrow, select a different shape and reorder two shapes.
            var moved = shapes[5];
            moved.Frame = moved.Frame.Translated(new Point(40, 30));
            moved.InvalidateGeometry();
            var arrow = new Element(Tool.Arrow, points: ValueList.Create<Point>([new Point(20, 200), new Point(240, 150)]));
            var second = ElementList.Create([shapes[0], shapes[1], shapes[3], shapes[2], shapes[4], moved, .. shapes[6..], text, arrow, cover]);
            var next = builder.Build(second, new HashSet<Guid> { shapes[7].Id });
            presenter.ShowScene(next);
            Assert.Equal(2, layers.Scene.Frames);
            Assert.True(presenter.Statistics.LastScenePixels < 320 * 220, "Only damaged areas are redrawn.");
            var full = FullRender(next, Scale, 320, 220, includeSelection: true);
            Assert.True(Pixels.SamePixels(full, layers.Scene.Read()), "The incremental frame differs from a full redraw.");

            presenter.ShowScene(next);
            Assert.Equal(2, layers.Scene.Frames);
        });
    }

    [Fact]
    public async Task GrowingStrokesRedrawOnlyTheirNewSegments()
    {
        await OnDispatcher((compositor, root) =>
        {
            var builder = Warp.Builder();
            var (presenter, layers) = Create(compositor, root, Warp.Devices, 1.5, 600, 300);
            using var owned = presenter;
            foreach (var pressure in new[] { false, true })
            {
                var stroke = Stroke(new Point(10, 100));
                stroke.Style.Width = 6;
                stroke.Style.Color = new RGBA(0.1, 0.3, 0.8, 0.6);
                stroke.Style.Pressure = pressure;
                stroke.Pressures = pressure ? ValueList.Create<double>([0.5]) : default;
                long largest = 0;
                for (var sample = 1; sample <= 300; sample++)
                {
                    stroke.Points = stroke.Points.Add(new Point(10 + sample * 1.2, 100 + Math.Sin(sample * 0.07) * 60));
                    if (pressure)
                    {
                        stroke.Pressures = stroke.Pressures.Add(0.3 + sample % 7 * 0.1);
                    }
                    stroke.Frame = Rect.Enclosing(stroke.Points.AsSpan());
                    stroke.GeometryToken = Guid.NewGuid();
                    presenter.ShowLive(builder.Build(ElementList.Create([stroke])));
                    if (sample > 3)
                    {
                        largest = Math.Max(largest, presenter.Statistics.LastLivePixels);
                    }
                }
                // Each new sample redraws a small area around its segment, not the stroke's growing bounds.
                Assert.True(largest < 60 * 60, $"A sample redrew {largest} pixels (pressure {pressure}).");
                var final = builder.Build(ElementList.Create([stroke]));
                var expected = FullRender(final, 1.5, 600, 300, includeSelection: false);
                Assert.True(Pixels.SamePixels(expected, layers.Live!.Read()), $"The grown stroke differs from a full redraw (pressure {pressure}).");
                presenter.ShowLive(null);
                Assert.Equal(0, Pixels.Painted(layers.Live.Read()));
                builder.Reset();
            }
            // Only the empty selection list of the scene stays entered.
            Assert.Equal(1, presenter.Renderer!.EnteredLists);
        });
    }

    [Fact]
    public async Task CommittingMovesAStrokeFromTheLiveLayerToTheScene()
    {
        await OnDispatcher((compositor, root) =>
        {
            var (presenter, layers) = Create(compositor, root, Warp.Devices, 1, 200, 120);
            using var owned = presenter;
            var stroke = Stroke(new Point(10, 10), new Point(150, 90));
            presenter.ShowLive(Warp.Builder().Build(ElementList.Create([stroke])));
            Assert.True(Pixels.Painted(layers.Live!.Read()) > 0);
            presenter.Show(Warp.Builder().Build(ElementList.Create([stroke])), SceneSnapshot.Empty);
            Assert.Equal(0, Pixels.Painted(layers.Live.Read()));
            Assert.True(Pixels.Painted(layers.Scene!.Read()) > 0);
        });
    }

    [Fact]
    public async Task ResizingRedrawsAtTheNewScale()
    {
        await OnDispatcher((compositor, root) =>
        {
            var (presenter, layers) = Create(compositor, root, Warp.Devices, 1, 300, 200);
            using var owned = presenter;
            var shape = new Element(Tool.Ellipse, new Rect(20, 20, 120, 70));
            var text = new Element(Tool.Text, new Rect(30, 100, 150, 30), text: "Scale");
            var scene = Warp.Builder().Build(ElementList.Create([shape, text]));
            presenter.ShowScene(scene);
            foreach (var scale in new[] { 1.25, 1.5, 1.75 })
            {
                presenter.Resize(scale, 300, 200);
                Assert.Equal(scale, presenter.Scale);
                // Drawn at the new scale in device pixels, never stretched: identical to a full render at that scale.
                Assert.True(Pixels.SamePixels(FullRender(scene, scale, 300, 200, includeSelection: true), layers.Scene!.Read()), $"Scale {scale} differs from a full render.");
            }
        });
    }

    /// <summary>A system color resolver whose theme a test switches, as Windows does when the app theme changes.</summary>
    private sealed class SwitchedColors(ISystemColorResolver current) : ISystemColorResolver
    {
        public ISystemColorResolver Current { get; set; } = current;

        public RGBA Resolve(SystemColor color) => Current.Resolve(color);
    }

    [Fact]
    public async Task ThemeChangesRedrawOnlyListsThatUseSystemColors()
    {
        await OnDispatcher((compositor, root) =>
        {
            const double Scale = 1.25;
            const int Width = 400, Height = 240;
            var colors = new SwitchedColors(FixedSystemColors.Light);
            var layers = new Layers();
            using var presenter = OverlayPresenter.Create(new OverlayPresenterOptions
            {
                Compositor = compositor,
                Root = root,
                Devices = Warp.Devices,
                Scale = Scale,
                PixelWidth = Width,
                PixelHeight = Height,
                Colors = colors,
                LayerFactory = (owner, live) =>
                {
                    var layer = new BitmapLayer(owner.Compositor, owner.Devices!, owner.PixelWidth, owner.PixelHeight);
                    if (live)
                    {
                        layers.Live = layer;
                    }
                    else
                    {
                        layers.Scene = layer;
                    }
                    return layer;
                },
            });
            var shapes = new List<Element>();
            for (var index = 0; index < 8; index++)
            {
                var shape = new Element(index % 2 == 0 ? Tool.Ellipse : Tool.Rectangle, new Rect(12 + index * 38, 20 + index % 3 * 50, 34, 30));
                shape.Style.Roughness = index % 2 * 2;
                shapes.Add(shape);
            }
            var stroke = Stroke(new Point(10, 170), new Point(120, 140), new Point(300, 185));
            var elements = ElementList.Create([.. shapes, stroke]);
            var builder = Warp.Builder();
            var selected = builder.Build(elements, new HashSet<Guid> { shapes[3].Id });
            presenter.ShowScene(selected);
            Assert.True(presenter.Renderer!.UsesSystemColors(selected.Selection));
            Assert.All(selected.Elements.ToArray(), list => Assert.False(presenter.Renderer.UsesSystemColors(list)));

            // The selection uses the accent: only its area is redrawn, and the result equals a full render in the new theme.
            colors.Current = FixedSystemColors.Dark;
            presenter.RedrawSystemColors();
            Assert.Equal(1, presenter.Statistics.ThemeRedraws);
            Assert.True(presenter.Statistics.LastScenePixels < Width * Height / 4, $"A theme change redrew {presenter.Statistics.LastScenePixels} pixels.");
            using (var dark = new PngExporter(Warp.Devices, Warp.Measurer, FixedSystemColors.Dark))
            {
                var expected = dark.Render(selected, new Size(Width / Scale, Height / Scale), Scale, includeSelection: true);
                Assert.True(Pixels.SamePixels(expected, layers.Scene!.Read()), "The redrawn selection differs from a full render in the new theme.");
            }

            // Without a selection nothing on the board uses system colors, so a theme change draws nothing.
            presenter.ShowScene(builder.Build(elements));
            var frames = layers.Scene!.Frames;
            colors.Current = FixedSystemColors.Light;
            presenter.RedrawSystemColors();
            Assert.Equal(frames, layers.Scene.Frames);
        });
    }
    [Fact]
    public async Task NothingIsDrawnWhileNothingChanges()
    {
        var thread = DispatcherThread.Start("Annotator presenter idle test");
        Compositor? compositor = null;
        ContainerVisual? root = null;
        OverlayPresenter? presenter = null;
        var scene = Warp.Builder().Build(ElementList.Create([new Element(Tool.Rectangle, new Rect(10, 10, 80, 50))]));
        (long Scene, long Live, long Deferred) counts = default;
        try
        {
            await thread.RunAsync(() =>
            {
                compositor = new Compositor();
                root = compositor.CreateContainerVisual();
                presenter = OverlayPresenter.Create(new OverlayPresenterOptions
                {
                    Compositor = compositor,
                    Root = root,
                    Devices = Warp.Devices,
                    Scale = 1,
                    PixelWidth = 200,
                    PixelHeight = 120,
                });
                presenter.ShowScene(scene);
                presenter.Show(scene, SceneSnapshot.Empty);
                presenter.ShowLive(null);
                presenter.ShowScene(scene);
                var statistics = presenter.Statistics;
                counts = (statistics.SceneFrames, statistics.LiveFrames, statistics.DeferredFrames);
            }).WaitAsync(Timeout, TestContext.Current.CancellationToken);
            Assert.Equal((1L, 0L, 0L), counts);

            // No timers or queued work: after a quiet period the counters are unchanged.
            await Task.Delay(300, TestContext.Current.CancellationToken);
            await thread.RunAsync(() =>
            {
                var statistics = presenter!.Statistics;
                Assert.Equal(counts, (statistics.SceneFrames, statistics.LiveFrames, statistics.DeferredFrames));
                presenter.Dispose();
                root!.Dispose();
                compositor!.Dispose();
            }).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }
        finally
        {
            await thread.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task LostDevicesShowOpaqueCoversUntilNewDevicesRedrawEverything()
    {
        var thread = DispatcherThread.Start("Annotator presenter loss test");
        var devices = GraphicsDevices.Create(new GraphicsDeviceOptions { UseWarp = true });
        var replacement = GraphicsDevices.Create(new GraphicsDeviceOptions { UseWarp = true });
        Compositor? compositor = null;
        ContainerVisual? root = null;
        OverlayPresenter? presenter = null;
        Layers? layers = null;
        var states = new List<PresenterState>();
        try
        {
            await thread.RunAsync(() =>
            {
                compositor = new Compositor();
                root = compositor.CreateContainerVisual();
                (presenter, layers) = Create(compositor, root, devices, 1, 200, 120);
                presenter.StateChanged += (_, args) => states.Add(args.Current);
                var cover = new Element(Tool.Cover, new Rect(20.5, 10, 40, 30));
                var blur = new Element(Tool.Blur, new Rect(100, 50, 30, 30));
                presenter.ShowScene(Warp.Builder().Build(ElementList.Create([cover, blur])));
                Assert.Equal(1, layers.Scene!.Frames);
                // The bundle reports the loss at once; the presenter learns it through its dispatcher queue.
                devices.ReportFailure(DeviceLoss.D2DERR_RECREATE_TARGET);
                Assert.IsType<PresenterState.Ready>(presenter.State);
            }).WaitAsync(Timeout, TestContext.Current.CancellationToken);
            await thread.RunAsync(() =>
            {
                var lost = Assert.IsType<PresenterState.DevicesLost>(presenter!.State);
                Assert.Equal(DeviceLossKind.RecreateTarget, lost.Loss.Kind);
                var covers = Covers(presenter);
                Assert.Equal(2, covers.Count);
                Assert.Contains((20f, 10f, 41f, 30f), covers);
                Assert.Contains((100f, 50f, 30f, 30f), covers);
                Assert.All(Fallback(presenter).Children, visual => Assert.NotNull(((SpriteVisual)visual).Brush));
                // The covers sit above every layer.
                Assert.Same(Fallback(presenter), presenter.Visual.Children.Last());

                // Nothing draws while the devices are lost.
                var lostLayer = layers!.Scene!;
                presenter.Invalidate();
                Assert.Equal(1, lostLayer.Frames);

                presenter.ReplaceDevices(replacement);
                Assert.IsType<PresenterState.Ready>(presenter.State);
                Assert.Empty(Covers(presenter));
                Assert.Same(replacement, presenter.Renderer!.Devices);
                Assert.Equal(3, presenter.Renderer.EnteredLists);
                Assert.NotSame(lostLayer, layers.Scene);
                Assert.Equal(1, layers.Scene!.Frames);
                Assert.Equal([typeof(PresenterState.DevicesLost), typeof(PresenterState.Ready)], states.Select(state => state.GetType()));
                presenter.Dispose();
                root!.Dispose();
                compositor!.Dispose();
            }).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }
        finally
        {
            await thread.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            replacement.Dispose();
            devices.Dispose();
        }
    }

    [Fact]
    public async Task WithoutDevicesOnlyCoversShowUntilDevicesArrive()
    {
        await OnDispatcher((compositor, root) =>
        {
            var (presenter, layers) = Create(compositor, root, devices: null, 1, 200, 120);
            using var owned = presenter;
            Assert.IsType<PresenterState.DevicesUnavailable>(presenter.State);
            Assert.Null(presenter.Renderer);
            var cover = new Element(Tool.Cover, new Rect(30, 20, 50, 40));
            var shape = new Element(Tool.Rectangle, new Rect(100, 30, 60, 50));
            var scene = Warp.Builder().Build(ElementList.Create([shape, cover]));
            presenter.ShowScene(scene);
            Assert.Equal((30f, 20f, 50f, 40f), Assert.Single(Covers(presenter)));
            Assert.Empty(Sprites(presenter));
            Assert.Null(layers.Scene);

            presenter.ReplaceDevices(Warp.Devices);
            Assert.IsType<PresenterState.Ready>(presenter.State);
            Assert.Empty(Covers(presenter));
            Assert.True(Pixels.SamePixels(FullRender(scene, 1, 200, 120, includeSelection: true), layers.Scene!.Read()), "New devices draw everything.");

            presenter.ReplaceDevices(null);
            Assert.IsType<PresenterState.DevicesUnavailable>(presenter.State);
            Assert.Null(presenter.Renderer);
            Assert.Empty(Sprites(presenter));
            Assert.Equal((30f, 20f, 50f, 40f), Assert.Single(Covers(presenter)));
        });
    }

    [Fact]
    public async Task LostBundlesStartThePresenterLostInsteadOfThrowing()
    {
        using var lost = GraphicsDevices.Create(new GraphicsDeviceOptions { UseWarp = true });
        lost.ReportFailure(DeviceLoss.DXGI_ERROR_DEVICE_REMOVED);
        Assert.IsType<DeviceState.Lost>(lost.State);
        await OnDispatcher((compositor, root) =>
        {
            using var presenter = OverlayPresenter.Create(new OverlayPresenterOptions
            {
                Compositor = compositor,
                Root = root,
                Devices = lost,
                Scale = 1,
                PixelWidth = 100,
                PixelHeight = 100,
            });
            Assert.IsType<PresenterState.DevicesLost>(presenter.State);
            presenter.ShowScene(Warp.Builder().Build(ElementList.Create([new Element(Tool.Cover, new Rect(10, 10, 20, 20))])));
            Assert.Equal((10f, 10f, 20f, 20f), Assert.Single(Covers(presenter)));
            Assert.Equal(0, presenter.Statistics.SceneFrames);
        });
    }

    [Fact]
    public async Task FailedDrawsShowCoversUntilRetry()
    {
        await OnDispatcher((compositor, root) =>
        {
            var (presenter, layers) = Create(compositor, root, Warp.Devices, 1, 200, 120);
            using var owned = presenter;
            var cover = new Element(Tool.Cover, new Rect(10, 10, 30, 30));
            var shape = new Element(Tool.Ellipse, new Rect(60, 20, 100, 60));
            var scene = Warp.Builder().Build(ElementList.Create([shape, cover]));
            layers.Scene!.FailNextDraw = E_OUTOFMEMORY;
            presenter.ShowScene(scene);
            var failed = Assert.IsType<PresenterState.Failed>(presenter.State);
            Assert.Equal(E_OUTOFMEMORY, failed.Error.HResult);
            Assert.Equal((10f, 10f, 30f, 30f), Assert.Single(Covers(presenter)));

            presenter.Retry();
            Assert.IsType<PresenterState.Ready>(presenter.State);
            Assert.Empty(Covers(presenter));
            Assert.True(Pixels.SamePixels(FullRender(scene, 1, 200, 120, includeSelection: true), layers.Scene!.Read()), "Retry draws everything.");
        });
    }

    [Theory]
    [InlineData(LayerKind.VirtualSurface, LayerKind.VirtualSurface)]
    [InlineData(LayerKind.DrawingSurface, LayerKind.DrawingSurface)]
    [InlineData(LayerKind.VirtualSurface, LayerKind.SwapChain)]
    [InlineData(LayerKind.SwapChain, LayerKind.VirtualSurface)]
    public async Task CompositionLayersPresentScenesAndLiveContent(LayerKind sceneKind, LayerKind liveKind)
    {
        await OnDispatcher((compositor, root) =>
        {
            using var presenter = OverlayPresenter.Create(new OverlayPresenterOptions
            {
                Compositor = compositor,
                Root = root,
                Devices = Warp.Devices,
                Scale = 1.25,
                PixelWidth = 400,
                PixelHeight = 300,
                SceneLayer = sceneKind,
                LiveLayer = liveKind,
            });
            Assert.Equal(1, root.Children.Count);
            Assert.IsType<PresenterState.Ready>(presenter.State);
            presenter.ShowScene(Warp.Builder().Build(ElementList.Create([new Element(Tool.Rectangle, new Rect(10, 10, 100, 60))])));
            Assert.Equal(1, presenter.Statistics.SceneFrames + presenter.Statistics.DeferredFrames);
            presenter.ShowLive(Warp.Builder().Build(ElementList.Create([Stroke(new Point(5, 5), new Point(50, 40))])));
            Assert.True(presenter.Statistics.LiveFrames >= 1 || presenter.Statistics.DeferredFrames >= 1);
            Assert.IsType<PresenterState.Ready>(presenter.State);
            presenter.Resize(1.5, 480, 360);
            Assert.Equal(480, presenter.PixelWidth);
            Assert.IsType<PresenterState.Ready>(presenter.State);
            presenter.Dispose();
            Assert.Equal(0, root.Children.Count);
            Assert.IsType<PresenterState.Disposed>(presenter.State);
        });
    }

    [Fact]
    public async Task EmptiedVirtualLayersAreReleasedAndRecreatedOnDemand()
    {
        await OnDispatcher((compositor, root) =>
        {
            using var presenter = OverlayPresenter.Create(new OverlayPresenterOptions
            {
                Compositor = compositor,
                Root = root,
                Devices = Warp.Devices,
                Scale = 1,
                PixelWidth = 320,
                PixelHeight = 200,
            });
            Assert.Equal(LayerKind.VirtualSurface, presenter.SceneLayerKind);
            Assert.Equal(LayerKind.VirtualSurface, presenter.LiveLayerKind);
            // Nothing to show, no surfaces: only the covers' container.
            Assert.Empty(Sprites(presenter));
            var shape = new Element(Tool.Rectangle, new Rect(10, 10, 80, 50));
            var stroke = Stroke(new Point(10, 120), new Point(200, 150));
            presenter.ShowScene(Warp.Builder().Build(ElementList.Create([shape])));
            var sceneVisual = Assert.Single(Sprites(presenter));
            presenter.ShowLive(Warp.Builder().Build(ElementList.Create([stroke])));
            Assert.Equal(2, Sprites(presenter).Length);
            Assert.Same(sceneVisual, Sprites(presenter)[0]);
            Assert.Same(Fallback(presenter), presenter.Visual.Children.Last());

            // Committing releases the emptied live surface in the same work item that draws the stroke into the scene.
            presenter.Show(Warp.Builder().Build(ElementList.Create([shape, stroke])), SceneSnapshot.Empty);
            Assert.Same(sceneVisual, Assert.Single(Sprites(presenter)));
            presenter.ShowScene(SceneSnapshot.Empty);
            Assert.Empty(Sprites(presenter));

            // Recreated on demand, live first: the scene still goes below it.
            presenter.ShowLive(Warp.Builder().Build(ElementList.Create([stroke])));
            var liveVisual = Assert.Single(Sprites(presenter));
            presenter.ShowScene(Warp.Builder().Build(ElementList.Create([shape])));
            Assert.Equal(2, Sprites(presenter).Length);
            Assert.Same(liveVisual, Sprites(presenter)[1]);
            Assert.Same(Fallback(presenter), presenter.Visual.Children.Last());
            Assert.IsType<PresenterState.Ready>(presenter.State);
        });
    }

    [Fact]
    public async Task SwapChainLiveLayersHideAtCommitAndAreReleasedWhenIdle()
    {
        await OnDispatcher((compositor, root) =>
        {
            using var presenter = OverlayPresenter.Create(new OverlayPresenterOptions
            {
                Compositor = compositor,
                Root = root,
                Devices = Warp.Devices,
                Scale = 1,
                PixelWidth = 320,
                PixelHeight = 200,
                LiveLayer = LayerKind.SwapChain,
            });
            // The swap chain exists only while there is live content.
            Assert.Empty(Sprites(presenter));
            var stroke = Stroke(new Point(10, 10), new Point(200, 120));
            var liveBuilder = Warp.Builder();
            presenter.ShowLive(liveBuilder.Build(ElementList.Create([stroke])));
            var liveVisual = Assert.Single(Sprites(presenter));
            Assert.True(liveVisual.IsVisible);
            Assert.Equal(1, presenter.Statistics.LiveFrames);

            // Committing hides the live layer in the same composition commit that draws the stroke in the scene.
            presenter.Show(Warp.Builder().Build(ElementList.Create([stroke])), SceneSnapshot.Empty);
            Assert.False(liveVisual.IsVisible);
            Assert.Equal(1, presenter.Statistics.SceneFrames);
            Assert.Equal(2, Sprites(presenter).Length);

            // The next gesture clears the old pixels and shows the layer again once it is drawn.
            var next = Stroke(new Point(30, 150), new Point(300, 160));
            presenter.ShowLive(liveBuilder.Build(ElementList.Create([next])));
            Assert.True(liveVisual.IsVisible || presenter.Statistics.DeferredFrames > 0);
            Assert.Equal(3, presenter.Renderer!.EnteredLists);

            presenter.ShowLive(null);
            presenter.ReleaseIdleResources();
            Assert.Single(Sprites(presenter));
            Assert.IsType<PresenterState.Ready>(presenter.State);
        });
    }

    [Fact]
    public async Task StalledFramesAreReportedOnceAndDrawnWhenAFrameFrees()
    {
        await OnDispatcher((compositor, root) =>
        {
            var (presenter, layers) = Create(compositor, root, Warp.Devices, 1, 240, 160, stallThreshold: TimeSpan.FromMilliseconds(40));
            using var owned = presenter;
            var stalls = new List<FrameStallEventArgs>();
            presenter.FrameStalled += (_, args) => stalls.Add(args);
            var live = layers.Live!;
            live.FramesAvailable = false;
            var builder = Warp.Builder();
            var stroke = Stroke(new Point(10, 10), new Point(60, 40));
            SceneSnapshot Grow(double x)
            {
                stroke.Points = stroke.Points.Add(new Point(x, 20 + x / 4));
                stroke.Frame = Rect.Enclosing(stroke.Points.AsSpan());
                stroke.GeometryToken = Guid.NewGuid();
                return builder.Build(ElementList.Create([stroke]));
            }

            presenter.ShowLive(Grow(80));
            Assert.Equal((0L, 1L), (presenter.Statistics.LiveFrames, presenter.Statistics.DeferredFrames));
            Thread.Sleep(80);
            presenter.ShowLive(Grow(100));
            var stall = Assert.Single(stalls);
            Assert.True(stall.Live);
            Assert.True(stall.Waited >= TimeSpan.FromMilliseconds(40), $"Waited {stall.Waited}.");
            presenter.ShowLive(Grow(120));
            Assert.Single(stalls);
            Assert.Equal(1, presenter.Statistics.FrameStalls);
            Assert.Equal(0, live.Frames);

            // A frame frees: the newest content is drawn at once, in one frame.
            live.FramesAvailable = true;
            var newest = presenter.Live;
            presenter.FrameReady(live);
            Assert.Equal(1, live.Frames);
            Assert.True(Pixels.SamePixels(FullRender(newest, 1, 240, 160, includeSelection: false), live.Read()), "The deferred frame shows the newest content.");

            // A new wait starts a new stall period.
            live.FramesAvailable = false;
            presenter.ShowLive(Grow(140));
            Assert.Single(stalls);
            Assert.IsType<PresenterState.Ready>(presenter.State);
        });
    }

    [Fact]
    public async Task SwapChainUpdatesNeverBlockWithoutFrames()
    {
        await OnDispatcher((compositor, root) =>
        {
            // This compositor has no window, so nothing may ever take the swap chain's frames.
            using var presenter = OverlayPresenter.Create(new OverlayPresenterOptions
            {
                Compositor = compositor,
                Root = root,
                Devices = Warp.Devices,
                Scale = 1,
                PixelWidth = 320,
                PixelHeight = 200,
                LiveLayer = LayerKind.SwapChain,
            });
            var builder = Warp.Builder();
            var stroke = Stroke(new Point(10, 10));
            var clock = Stopwatch.StartNew();
            const int Updates = 60;
            for (var sample = 1; sample <= Updates; sample++)
            {
                stroke.Points = stroke.Points.Add(new Point(10 + sample * 4, 10 + sample * 2));
                stroke.Frame = Rect.Enclosing(stroke.Points.AsSpan());
                stroke.GeometryToken = Guid.NewGuid();
                presenter.ShowLive(builder.Build(ElementList.Create([stroke])));
            }
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"{Updates} updates took {clock.Elapsed}.");
            Assert.Equal(Updates, presenter.Statistics.LiveFrames + presenter.Statistics.DeferredFrames);
            Assert.IsType<PresenterState.Ready>(presenter.State);
        });
    }

    [Fact]
    public async Task PresentersRefuseOtherThreads()
    {
        var thread = DispatcherThread.Start("Annotator presenter thread test");
        Compositor? compositor = null;
        ContainerVisual? root = null;
        OverlayPresenter? presenter = null;
        try
        {
            await thread.RunAsync(() =>
            {
                compositor = new Compositor();
                root = compositor.CreateContainerVisual();
                presenter = OverlayPresenter.Create(new OverlayPresenterOptions
                {
                    Compositor = compositor,
                    Root = root,
                    Devices = Warp.Devices,
                    Scale = 1,
                    PixelWidth = 100,
                    PixelHeight = 100,
                });
            }).WaitAsync(Timeout, TestContext.Current.CancellationToken);
            Assert.Throws<InvalidOperationException>(() => presenter!.ShowScene(SceneSnapshot.Empty));
            Assert.Throws<InvalidOperationException>(() => presenter!.Dispose());
            await thread.RunAsync(() =>
            {
                presenter!.Dispose();
                root!.Dispose();
                compositor!.Dispose();
            }).WaitAsync(Timeout, TestContext.Current.CancellationToken);
            Assert.Throws<ObjectDisposedException>(() => presenter!.ShowScene(SceneSnapshot.Empty));
        }
        finally
        {
            await thread.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public void DamageMergesNearbyRectanglesAndStaysSmall()
    {
        var damage = new DamageRegion();
        damage.Add(new PixelRect(0, 0, 10, 10), 100, 100);
        damage.Add(new PixelRect(12, 0, 20, 10), 100, 100);
        Assert.Equal(1, damage.Count);
        Assert.Equal(new PixelRect(0, 0, 20, 10), damage.Rects[0]);
        damage.Add(new PixelRect(-5, 90, 5, 120), 100, 100);
        Assert.Equal(new PixelRect(0, 90, 5, 100), damage.Rects[1]);
        for (var index = 0; index < 20; index++)
        {
            damage.Add(new PixelRect(index * 4, 40 + index % 2 * 30, index * 4 + 2, 42 + index % 2 * 30), 100, 100);
        }
        Assert.True(damage.Count <= DamageRegion.Capacity);
        damage.Add(new PixelRect(500, 500, 600, 600), 100, 100);
        Assert.True(damage.Count <= DamageRegion.Capacity);
        damage.AddFull(100, 100);
        Assert.True(damage.IsFull);
        Assert.Equal(10_000, damage.Area);
    }

    /// <summary>
    /// Explicit: needs a real display and shows a small patch in the primary display's top-left corner for a moment.
    /// The presenter on a real overlay root with each layer kind and the hardware device; latency, memory and
    /// performance on the real overlay are measured by tools/Annotator.RenderHarness.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task RealOverlayPresentsWithEveryLayerKind()
    {
        var thread = DispatcherThread.Start("Annotator presenter overlay test");
        try
        {
            await thread.RunAsync(() =>
            {
                using var devices = GraphicsDevices.Create();
                using var manager = OverlayManager.Create(new OverlayManagerOptions
                {
                    Registry = new WindowRegistry(),
                    InputSink = new IgnoredInput(),
                    InitialMode = OverlayInputMode.UseApps,
                });
                var overlay = manager.Overlays.First(candidate => candidate.Display.IsPrimary);
                var patch = TestLists.Scene(TestLists.Create(DisplayItem.FillRect(new Rect(8, 8, 24, 24), Paint.Solid(new RGBA(1, 0, 1)))));
                foreach (var (scene, live) in new[] { (LayerKind.VirtualSurface, LayerKind.VirtualSurface), (LayerKind.DrawingSurface, LayerKind.DrawingSurface), (LayerKind.VirtualSurface, LayerKind.SwapChain) })
                {
                    using var presenter = OverlayPresenter.Create(new OverlayPresenterOptions
                    {
                        Compositor = manager.Compositor,
                        Root = overlay.Root,
                        Devices = devices,
                        Scale = overlay.Display.Scale,
                        PixelWidth = overlay.Display.Bounds.Width,
                        PixelHeight = overlay.Display.Bounds.Height,
                        SceneLayer = scene,
                        LiveLayer = live,
                    });
                    presenter.Show(patch, patch);
                    Assert.IsType<PresenterState.Ready>(presenter.State);
                    Assert.Equal(1, presenter.Statistics.SceneFrames);
                    Assert.Equal(1, presenter.Statistics.LiveFrames);
                    presenter.Show(SceneSnapshot.Empty, SceneSnapshot.Empty);
                    presenter.ReleaseIdleResources();
                }
            }).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }
        finally
        {
            await thread.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
    }

    private sealed class IgnoredInput : IOverlayInputSink
    {
        public void OnPointer(OverlayWindow overlay, in PointerInput input)
        {
        }

        public bool OnKey(OverlayWindow overlay, in KeyInput input) => false;

        public void OnInputReleaseRequested(OverlayWindow overlay, InputReleaseReason reason)
        {
        }

        public void OnFocusChanged(OverlayWindow overlay, bool hasFocus)
        {
        }
    }
}
