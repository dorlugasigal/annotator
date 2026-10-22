using System.Runtime.InteropServices;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Platform.Capture;
using Annotator.Platform.Displays;
using Annotator.Platform.Threading;
using Annotator.Rendering.Effects;
using Annotator.Rendering.Imaging;
using Windows.UI.Composition;

namespace Annotator.Rendering.Tests;

/// <summary>A capture source for tests: frames are WARP textures that the test pushes like a running capture.</summary>
internal sealed class FakeCapture : IDisplayCapture
{
    private readonly List<FakeLease> leases = [];

    public FakeLease[] Leases
    {
        get
        {
            lock (leases)
            {
                return [.. leases];
            }
        }
    }

    public FakeLease Current => Leases.Last(lease => !lease.Disposed);

    public ICaptureLease Acquire(DisplayInfo display, ICaptureConsumer consumer)
    {
        var lease = new FakeLease(display, consumer);
        lock (leases)
        {
            leases.Add(lease);
        }
        return lease;
    }
}

internal sealed class FakeLease(DisplayInfo display, ICaptureConsumer consumer) : ICaptureLease
{
    private readonly Lock gate = new();
    private readonly Dictionary<(int Width, int Height, int Index), nint> textures = [];
    private CaptureState state = CaptureState.Starting.Instance;
    private nint newest;
    private int width;
    private int height;
    private long sequence;
    private long contentVersion;
    private bool disposed;
    private int acquired;
    private PixelRect[]? interest;

    public DisplayInfo Display { get; } = display;

    public CaptureState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    public bool Disposed
    {
        get
        {
            lock (gate)
            {
                return disposed;
            }
        }
    }

    /// <summary>Frames handed out by <see cref="TryAcquireFrame"/>.</summary>
    public int Acquired
    {
        get
        {
            lock (gate)
            {
                return acquired;
            }
        }
    }

    /// <summary>The areas the consumer said it reads (<see cref="SetInterest"/>), or null before it said any.</summary>
    public PixelRect[]? Interest
    {
        get
        {
            lock (gate)
            {
                return interest;
            }
        }
    }

    public void SetInterest(ReadOnlySpan<PixelRect> areas)
    {
        lock (gate)
        {
            interest = areas.ToArray();
        }
    }

    /// <summary>
    /// Delivers a frame as a capture worker does: the source turns Live first if needed, then the consumer hears of the
    /// frame on this thread. Three textures per size take turns, so the test never makes more.
    /// </summary>
    public void Push(int frameWidth, int frameHeight) => Push(frameWidth, frameHeight, repeat: false);

    /// <summary>
    /// Delivers a frame; with <paramref name="repeat"/> it shows the pixels of the frame before it, as a capture source
    /// with a frame comparer reports (the same texture and content version).
    /// </summary>
    public void Push(int frameWidth, int frameHeight, bool repeat)
    {
        bool becameLive;
        lock (gate)
        {
            if (repeat && newest != 0 && width == frameWidth && height == frameHeight)
            {
                sequence++;
                becameLive = false;
            }
            else
            {
                becameLive = Store(frameWidth, frameHeight);
            }
        }
        if (becameLive)
        {
            consumer.OnStateChanged(this, CaptureState.Live.Instance);
        }
        consumer.OnFrameAvailable(this);
    }

    /// <summary>Stores a frame with new pixels (under the lock); true when the source turned Live.</summary>
    private bool Store(int frameWidth, int frameHeight)
    {
        var key = (frameWidth, frameHeight, (int)(sequence % 3));
        if (!textures.TryGetValue(key, out var texture))
        {
            var shade = (byte)(40 + key.Item3 * 60);
            texture = EffectTextures.Upload(Warp.Devices, EffectTextures.Image(frameWidth, frameHeight, (x, y) => ((byte)(x * 7 + shade), (byte)(y * 5), shade)));
            textures[key] = texture;
        }
        newest = texture;
        width = frameWidth;
        height = frameHeight;
        sequence++;
        contentVersion++;
        var becameLive = state is not CaptureState.Live;
        state = CaptureState.Live.Instance;
        return becameLive;
    }

    public void SetState(CaptureState next)
    {
        lock (gate)
        {
            state = next;
        }
        consumer.OnStateChanged(this, next);
    }

    public bool TryAcquireFrame(out CaptureFrame frame)
    {
        lock (gate)
        {
            if (disposed || state is not CaptureState.Live || newest == 0)
            {
                frame = default;
                return false;
            }
            acquired++;
            frame = new CaptureFrame(null, 0, newest, width, height, sequence, Warp.Devices, TimeSpan.Zero, contentVersion);
            return true;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
        }
    }

    /// <summary>Releases the textures once nothing reads them any more.</summary>
    public void ReleaseTextures()
    {
        lock (gate)
        {
            foreach (var texture in textures.Values)
            {
                Marshal.Release(texture);
            }
            textures.Clear();
            newest = 0;
        }
    }
}

/// <summary>
/// The effects presenter on WARP with a fake capture source: effect regions stay black until a frame is drawn, the
/// layer hides on every pause, and an overlay without effects holds no capture.
/// </summary>
public sealed class EffectsPresenterTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly DisplayInfo Display = new(1, @"\\.\ANNOTATOR-EFFECTS1", new PixelRect(0, 0, 200, 120), new PixelRect(0, 0, 200, 110), 120, true);

    private sealed class Harness : IAsyncDisposable
    {
        private Harness()
        {
        }

        public DispatcherThread Thread { get; } = DispatcherThread.Start("Annotator effects test");

        public FakeCapture Capture { get; } = new();

        public Compositor Compositor { get; private set; } = null!;

        public ContainerVisual Root { get; private set; } = null!;

        public EffectsPresenter Presenter { get; private set; } = null!;

        public int AvailabilityChanges { get; private set; }

        public static async Task<Harness> Start()
        {
            var harness = new Harness();
            await harness.Run(() =>
            {
                harness.Compositor = new Compositor();
                harness.Root = harness.Compositor.CreateContainerVisual();
                harness.Presenter = EffectsPresenter.Create(new EffectsPresenterOptions
                {
                    Compositor = harness.Compositor,
                    Root = harness.Root,
                    Capture = harness.Capture,
                    Display = Display,
                    Scale = 1.25,
                    PixelWidth = 200,
                    PixelHeight = 120,
                });
                harness.Presenter.PixelsAvailableChanged += (_, _) => harness.AvailabilityChanges++;
            });
            return harness;
        }

        public Task Run(Action work) => Thread.RunAsync(work).WaitAsync(Timeout, TestContext.Current.CancellationToken);

        public Task<T> Run<T>(Func<T> work) => Thread.RunAsync(work).WaitAsync(Timeout, TestContext.Current.CancellationToken);

        /// <summary>
        /// Waits until <paramref name="condition"/> holds on the overlay thread. With <paramref name="feed"/>, a frame
        /// arrives every few milliseconds meanwhile, like a running capture.
        /// </summary>
        public async Task Until(Func<EffectsPresenter, bool> condition, string what, FakeLease? feed = null)
        {
            var deadline = DateTime.UtcNow + Timeout;
            while (!await Run(() => condition(Presenter)))
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"Timed out waiting for {what}; the presenter is {await Run(() => Presenter.State)}.");
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
                    Presenter?.Dispose();
                    Root?.Dispose();
                    Compositor?.Dispose();
                });
            }
            finally
            {
                await Thread.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                // Draws already queued on the thread pool end with the presenter; wait for them to see that.
                await Task.Delay(50, TestContext.Current.CancellationToken);
                foreach (var lease in Capture.Leases)
                {
                    lease.ReleaseTextures();
                }
            }
        }
    }

    private static ElementList Elements(params Element[] elements) => ElementList.Create(elements);

    private static Element Effect(Tool tool, Rect frame, double? amount = null)
    {
        var element = new Element(tool, frame);
        element.Style.EffectAmount = amount;
        return element;
    }

    private static ContainerVisual Fallback(EffectsPresenter presenter) =>
        presenter.Visual.Children.OfType<ContainerVisual>().Single(visual => visual is not SpriteVisual);

    private static SpriteVisual Layer(EffectsPresenter presenter) => presenter.Visual.Children.OfType<SpriteVisual>().Single();

    private static (float X, float Y, float Width, float Height)[] Rectangles(EffectsPresenter presenter) =>
        [.. Fallback(presenter).Children.Where(visual => visual.IsVisible).Select(visual => (visual.Offset.X, visual.Offset.Y, visual.Size.X, visual.Size.Y))];

    [Fact]
    public async Task RegionsStayBlackUntilAFrameIsDrawn()
    {
        await using var harness = await Harness.Start();
        await harness.Run(() =>
        {
            var presenter = harness.Presenter;
            Assert.IsType<EffectsState.Idle>(presenter.State);
            Assert.Equal(1, harness.Root.Children.Count);
            presenter.Show(Elements(Effect(Tool.Blur, new Rect(10, 8, 40, 20))));
            Assert.IsType<EffectsState.Waiting>(presenter.State);
            Assert.False(presenter.PixelsAvailable);
            Assert.False(Layer(presenter).IsVisible);
            // The region at 125%: (12.5, 10)-(62.5, 35), rounded outward.
            Assert.Equal(new[] { (12f, 10f, 51f, 25f) }, Rectangles(presenter));
            Assert.All(Fallback(presenter).Children, visual => Assert.NotNull(((SpriteVisual)visual).Brush));
        });
        var lease = Assert.Single(harness.Capture.Leases);
        Assert.Equal(Display, lease.Display);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await harness.Run(() => Assert.IsType<EffectsState.Waiting>(harness.Presenter.State));

        await harness.Until(presenter => presenter.State is EffectsState.Live, "the first drawn frame", lease);
        await harness.Run(() =>
        {
            var presenter = harness.Presenter;
            Assert.True(presenter.PixelsAvailable);
            Assert.True(Layer(presenter).IsVisible);
            Assert.NotNull(Layer(presenter).Brush);
            // The black rectangle stays underneath, so whatever the layer has not drawn yet is black.
            Assert.Equal(new[] { (12f, 10f, 51f, 25f) }, Rectangles(presenter));
            Assert.Equal(1, harness.AvailabilityChanges);
            Assert.True(presenter.Statistics.Frames >= 1);
        });
    }

    [Fact]
    public async Task PausesHideTheLayerUntilANewFrameIsDrawn()
    {
        await using var harness = await Harness.Start();
        await harness.Run(() => harness.Presenter.Show(Elements(Effect(Tool.Pixelate, new Rect(20, 20, 60, 40)))));
        var lease = harness.Capture.Current;
        await harness.Until(presenter => presenter.State is EffectsState.Live, "live effects", lease);

        foreach (var pause in new CaptureState[]
        {
            new CaptureState.Stalled(TimeSpan.FromMilliseconds(300)),
            new CaptureState.Suspended(CaptureSuspension.SessionLocked),
            new CaptureState.Failed(new CaptureFailure(CaptureFailureKind.StreamClosed, "test")),
        })
        {
            lease.SetState(pause);
            await harness.Until(presenter => presenter.State is EffectsState.Paused, $"the pause for {pause}");
            await harness.Run(() =>
            {
                var presenter = harness.Presenter;
                Assert.Equal(new EffectsState.Paused(pause), presenter.State);
                Assert.Equal(pause, presenter.CaptureState);
                Assert.False(Layer(presenter).IsVisible);
                Assert.False(presenter.PixelsAvailable);
                Assert.Single(Rectangles(presenter));
            });
            await harness.Until(presenter => presenter.State is EffectsState.Live, "live effects again", lease);
        }
        await harness.Run(() => Assert.Equal(7, harness.AvailabilityChanges));
    }

    [Fact]
    public async Task OverlaysWithoutEffectRegionsHoldNoCapture()
    {
        await using var harness = await Harness.Start();
        await harness.Run(() =>
        {
            var presenter = harness.Presenter;
            presenter.Show(Elements(new Element(Tool.Cover, new Rect(50, 50, 20, 20)), new Element(Tool.Rectangle, new Rect(5, 5, 20, 20))));
            Assert.IsType<EffectsState.Idle>(presenter.State);
            Assert.Empty(Rectangles(presenter));
        });
        Assert.Empty(harness.Capture.Leases);

        await harness.Run(() => harness.Presenter.Show(Elements(Effect(Tool.Distort, new Rect(10, 10, 30, 30), 40))));
        var lease = Assert.Single(harness.Capture.Leases);
        await harness.Until(presenter => presenter.State is EffectsState.Live, "live effects", lease);

        await harness.Run(() =>
        {
            var presenter = harness.Presenter;
            presenter.Show(Elements());
            Assert.IsType<EffectsState.Idle>(presenter.State);
            Assert.False(Layer(presenter).IsVisible);
            Assert.Null(Layer(presenter).Brush);
            Assert.Empty(Rectangles(presenter));
            Assert.False(presenter.PixelsAvailable);
        });
        Assert.True(lease.Disposed, "Removing the last region releases the capture.");
        var acquired = lease.Acquired;
        lease.Push(Display.Bounds.Width, Display.Bounds.Height);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(acquired, lease.Acquired);
    }

    // Mac LivePresentation: any pixel-effect element starts capture, and CaptureCompositor skips strength zero.
    [Fact]
    public async Task RegionsOfStrengthZeroShowTheScreenOnceCaptureIsLive()
    {
        await using var harness = await Harness.Start();
        await harness.Run(() =>
        {
            var presenter = harness.Presenter;
            presenter.Show(Elements(Effect(Tool.Blur, new Rect(10, 10, 30, 30), 0)));
            // No rectangle of its own: until capture is live, the scene draws the region black.
            Assert.IsType<EffectsState.Waiting>(presenter.State);
            Assert.False(presenter.PixelsAvailable);
            Assert.Empty(Rectangles(presenter));
        });
        var lease = Assert.Single(harness.Capture.Leases);
        await harness.Until(presenter => presenter.State is EffectsState.Live, "live capture", lease);
        for (var index = 0; index < 10; index++)
        {
            lease.Push(Display.Bounds.Width, Display.Bounds.Height);
        }
        await harness.Run(() =>
        {
            var presenter = harness.Presenter;
            // Live: the scene leaves the region out, so the screen shows unchanged.
            Assert.True(presenter.PixelsAvailable);
            Assert.Empty(Rectangles(presenter));
            // Only the two clearing frames of the new swap chain were drawn; later frames present nothing.
            Assert.True(presenter.Statistics.Frames <= 2, $"{presenter.Statistics.Frames} frames were drawn.");
        });
    }

    [Fact]
    public async Task PastedImagesAreLayersOfTheEffectScene()
    {
        await using var harness = await Harness.Start();
        var art = EffectTextures.Image(4, 4, (_, _) => ((byte)255, (byte)0, (byte)0));
        var image = new Element(Tool.Image, new Rect(12, 12, 20, 20)) { ImagePng = ValueList.Create<byte>(RasterAsset.EncodePng(art)) };
        await harness.Run(() => harness.Presenter.Show(Elements(image, Effect(Tool.Blur, new Rect(10, 10, 30, 30)))));
        Assert.Equal(1, await harness.Run(() => harness.Presenter.Effects.ImageLayers.Length));
        var lease = harness.Capture.Current;
        // The capture worker decodes the image and draws it under the blur.
        await harness.Until(presenter => presenter.State is EffectsState.Live, "live effects over an image", lease);
    }

    [Fact]
    public async Task UnchangedEffectsDoNoWork()
    {
        await using var harness = await Harness.Start();
        var elements = Elements(Effect(Tool.Blur, new Rect(10, 10, 30, 30)), new Element(Tool.Rectangle, new Rect(0, 0, 5, 5)));
        await harness.Run(() => harness.Presenter.Show(elements));
        var lease = harness.Capture.Current;
        await harness.Until(presenter => presenter.State is EffectsState.Live, "live effects", lease);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        var acquired = lease.Acquired;
        var frames = await harness.Run(() => harness.Presenter.Statistics.Frames);
        await harness.Run(() =>
        {
            // The drawings change, the effects do not: nothing is drawn until the next captured frame.
            var moved = elements.ToArray();
            moved[1].Frame = new Rect(1, 1, 5, 5);
            harness.Presenter.Show(Elements(moved));
            Assert.IsType<EffectsState.Live>(harness.Presenter.State);
        });
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(acquired, lease.Acquired);
        Assert.Equal(frames, await harness.Run(() => harness.Presenter.Statistics.Frames));
    }

    [Fact]
    public async Task FramesThatShowNothingNewAreNotDrawnAgain()
    {
        await using var harness = await Harness.Start();
        await harness.Run(() => harness.Presenter.Show(Elements(Effect(Tool.Blur, new Rect(10, 10, 30, 30)))));
        var lease = harness.Capture.Current;
        await harness.Until(presenter => presenter.State is EffectsState.Live, "live effects", lease);
        // A frame that arrived while capture started may not be drawn yet (no free buffer): repeat the newest frame until
        // the presenter skips one, so the layer shows the newest pixels.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var repeated = await harness.Run(() => harness.Presenter.Statistics.Repeated);
        while (await harness.Run(() => harness.Presenter.Statistics.Repeated) == repeated)
        {
            Assert.True(DateTime.UtcNow < deadline, "No repeated frame was skipped within 10 seconds.");
            lease.Push(Display.Bounds.Width, Display.Bounds.Height, repeat: true);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        var frames = await harness.Run(() => harness.Presenter.Statistics.Frames);
        repeated = await harness.Run(() => harness.Presenter.Statistics.Repeated);

        // The screen under the region did not change: no draw and no present, so the compositor has nothing to do.
        for (var index = 0; index < 5; index++)
        {
            lease.Push(Display.Bounds.Width, Display.Bounds.Height, repeat: true);
        }
        Assert.Equal(frames, await harness.Run(() => harness.Presenter.Statistics.Frames));
        Assert.True(await harness.Run(() => harness.Presenter.Statistics.Repeated) >= repeated + 5, "Every repeated frame is skipped.");

        // A moved region is drawn at once from the newest frame, which did not change.
        await harness.Run(() => harness.Presenter.Show(Elements(Effect(Tool.Blur, new Rect(14, 10, 30, 30)))));
        await harness.Until(presenter => presenter.Statistics.Frames > frames, "the moved region to be drawn");
        frames = await harness.Run(() => harness.Presenter.Statistics.Frames);

        // A frame with new pixels is drawn.
        await harness.Until(presenter => presenter.Statistics.Frames > frames, "a changed frame to be drawn", lease);
    }

    [Fact]
    public async Task FramesOfAnotherSizeAreNotDrawn()
    {
        await using var harness = await Harness.Start();
        await harness.Run(() => harness.Presenter.Show(Elements(Effect(Tool.Blur, new Rect(10, 10, 30, 30)))));
        var lease = harness.Capture.Current;
        for (var index = 0; index < 5; index++)
        {
            lease.Push(100, 60);
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await harness.Run(() =>
        {
            Assert.IsType<EffectsState.Waiting>(harness.Presenter.State);
            Assert.False(harness.Presenter.PixelsAvailable);
            Assert.Equal(0, harness.Presenter.Statistics.Frames);
        });
    }

    [Fact]
    public async Task ResizingRebuildsTheRegionsAndWaitsForANewFrame()
    {
        await using var harness = await Harness.Start();
        await harness.Run(() => harness.Presenter.Show(Elements(Effect(Tool.Blur, new Rect(10, 8, 40, 20)))));
        var lease = harness.Capture.Current;
        await harness.Until(presenter => presenter.State is EffectsState.Live, "live effects", lease);
        await harness.Run(() =>
        {
            var presenter = harness.Presenter;
            presenter.Resize(2, 200, 120);
            Assert.Equal(new[] { (20f, 16f, 80f, 40f) }, Rectangles(presenter));
            Assert.IsType<EffectsState.Live>(presenter.State);
            presenter.Resize(2, 160, 100);
            Assert.IsType<EffectsState.Waiting>(presenter.State);
            Assert.False(Layer(presenter).IsVisible);
            Assert.Throws<ArgumentOutOfRangeException>(() => presenter.Resize(double.NaN, 200, 120));
        });
        // The display's frames no longer match the overlay's size, so nothing is drawn.
        for (var index = 0; index < 5; index++)
        {
            lease.Push(Display.Bounds.Width, Display.Bounds.Height);
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        await harness.Run(() => Assert.IsType<EffectsState.Waiting>(harness.Presenter.State));
        await harness.Run(() => harness.Presenter.Resize(1, 200, 120));
        await harness.Until(presenter => presenter.State is EffectsState.Live, "live effects at the new size", lease);
        await harness.Run(() => Assert.Equal(new[] { (10f, 8f, 40f, 20f) }, Rectangles(harness.Presenter)));
    }

    [Fact]
    public async Task DisposingReleasesTheCaptureAndTheVisuals()
    {
        await using var harness = await Harness.Start();
        await harness.Run(() => harness.Presenter.Show(Elements(Effect(Tool.Blur, new Rect(10, 10, 30, 30)))));
        var lease = harness.Capture.Current;
        await harness.Until(presenter => presenter.State is EffectsState.Live, "live effects", lease);
        await harness.Run(() =>
        {
            var presenter = harness.Presenter;
            var states = new List<EffectsState>();
            presenter.StateChanged += (_, args) => states.Add(args.Current);
            presenter.Dispose();
            Assert.IsType<EffectsState.Disposed>(Assert.Single(states));
            Assert.False(presenter.PixelsAvailable);
            Assert.Equal(0, harness.Root.Children.Count);
            Assert.Throws<ObjectDisposedException>(() => presenter.Show(Elements()));
            presenter.Dispose();
        });
        Assert.True(lease.Disposed);
        // A frame that arrives after disposal draws nothing.
        var acquired = lease.Acquired;
        lease.Push(Display.Bounds.Width, Display.Bounds.Height);
        Assert.Equal(acquired, lease.Acquired);
    }

    [Fact]
    public async Task PresentersRefuseOtherThreads()
    {
        await using var harness = await Harness.Start();
        Assert.Throws<InvalidOperationException>(() => harness.Presenter.Show(Elements()));
        Assert.Throws<InvalidOperationException>(() => harness.Presenter.Retry());
        Assert.Throws<InvalidOperationException>(() => harness.Presenter.Dispose());
    }
}
