using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Platform;
using Annotator.Platform.Capture;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Platform.Threading;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Effects;
using Annotator.Rendering.Interop;
using Windows.UI.Composition;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using SystemDispatcherQueue = Windows.System.DispatcherQueue;

namespace Annotator.Rendering.Sharing;

/// <summary>
/// What the shared region shows (the Mac's SharedRegionView.present): the region of the display in points, its covers
/// and pixel effects with the region as the privacy crop, and the drawings with the board behind them. Immutable; a new
/// <see cref="Version"/> asks for a new frame before the output can show it.
/// </summary>
/// <param name="Region">The shared part of the display, in points from its top-left corner.</param>
/// <param name="Effects">The covers and pixel effects at the display's pixel size, with <paramref name="Region"/> as the privacy crop.</param>
/// <param name="Drawings">The drawings' display lists in points, without covers and effects, in drawing order.</param>
/// <param name="Background">The board behind the drawings inside the region, or null for the desktop.</param>
/// <param name="Version">Increases with every scene; the presenter reports the first frame that shows it.</param>
/// <param name="Source">
/// Under screen zoom, the part of the display the region shows magnified (the Mac's <c>region.sourceBounds(through:
/// screenViewport)</c>), in points; <paramref name="Effects"/> then has it as the privacy crop. Null without zoom.
/// </param>
public sealed record SharedRegionScene(Rect Region, EffectList Effects, ImmutableArray<DisplayList> Drawings, RGBA? Background, long Version,
                                       Rect? Source = null);

/// <summary>Everything a <see cref="SharedRegionPresenter"/> needs from its host, on the thread of its compositor.</summary>
public sealed record SharedRegionPresenterOptions
{
    public required Compositor Compositor { get; init; }

    /// <summary>The visual the region image goes into; the presenter adds its visual at the bottom.</summary>
    public required ContainerVisual Root { get; init; }

    /// <summary>The capture service; the presenter holds a lease of <see cref="Display"/> between <see cref="SharedRegionPresenter.Start"/> and Stop.</summary>
    public required IDisplayCapture Capture { get; init; }

    public required DisplayInfo Display { get; init; }

    /// <summary>Resolves the drawings' symbolic colors.</summary>
    public ISystemColorResolver? Colors { get; init; }
}

/// <summary>
/// Draws the shared region from the newest captured frame on the capture device, in the Mac's order (CaptureCompositor
/// with a drawing layer and a privacy crop): the region's pixels with the drawings composited over them in linear light,
/// black outside the region, covers black, pixel effects filtered from that scene and covers on top again. Only the
/// region's pixels and a one-pixel margin are read and filtered, which gives exactly the pixels of filtering the whole
/// display. The result goes to a composition swap chain the size of the region in pixels, which the output's visual
/// scales to its letterboxed frame with nearest-neighbor sampling, as the Mac's Metal view samples it. Frames are drawn
/// by the capture worker that delivers them; the presenter's thread only attaches the swap chain and hears what was shown.
/// Nothing is drawn without a lease, and unfiltered pixels are never shown where an effect or cover is (WIN-CAP-03).
/// </summary>
public sealed unsafe class SharedRegionPresenter : IDisposable
{
    private const int StallFrames = 30;

    private readonly Compositor compositor;
    private readonly ContainerVisual root;
    private readonly SpriteVisual visual;
    private readonly IDisplayCapture capture;
    private readonly DisplayInfo display;
    private readonly ISystemColorResolver colors;
    private readonly SystemDispatcherQueue queue;
    private readonly int threadId = Environment.CurrentManagedThreadId;
    private readonly Consumer consumer;

    // Owner thread.
    private ICaptureLease? lease;
    private ICompositionSurface? surface;
    private CompositionSurfaceBrush? brush;
    private long attachedGeneration = -1;
    private bool disposed;
    private readonly List<SpriteVisual> views = [];

    // Shared with the render worker.
    private readonly Lock renderGate = new();
    private SharedRegionScene? published;
    private ICaptureLease? workerLease;
    private long reportedVersion = -1;
    private int pointerX = int.MinValue;
    private int pointerY = int.MinValue;

    // Render worker, under renderGate.
    private EffectPipeline? pipeline;
    private DisplayListRenderer? renderer;
    private ID2D1DeviceContext* drawContext;
    private IDXGISwapChain2* swapChain;
    private nint backBuffer;
    private HANDLE waitable;
    private int chainWidth;
    private int chainHeight;
    private Texture localSource;
    private Texture localTarget;
    private Texture layer;
    private ID2D1Bitmap1* layerBitmap;
    private ID2D1Bitmap1* chainBitmap;
    private ID2D1Bitmap1* projectedBitmap;
    private nint projectedTexture;
    private SharedRegionScene? layerScene;
    private PixelRect layerArea;
    private bool layerEmpty;
    private ImmutableArray<DisplayList> enteredLists = [];
    private (EffectList List, PixelRect Area, EffectList Local)? localList;
    private long generation;
    private int skippedInARow;

    // What the chain shows (under renderGate): the scene, the captured content and the chain. A frame that changes none of
    // them is not drawn or presented again; the pointer is still followed.
    private SharedRegionScene? drawnScene;
    private long drawnContent = -1;
    private long drawnGeneration = -1;
    private volatile bool renderFailed;
    private bool workerDisposed;

    private SharedRegionPresenter(SharedRegionPresenterOptions options, SystemDispatcherQueue queue)
    {
        compositor = options.Compositor;
        root = options.Root;
        capture = options.Capture;
        display = options.Display;
        colors = options.Colors ?? FixedSystemColors.Light;
        this.queue = queue;
        consumer = new Consumer(this);
        visual = compositor.CreateSpriteVisual();
        visual.IsVisible = false;
    }

    /// <summary>Creates the presenter on its compositor's thread and adds the region image at the bottom of the root.</summary>
    public static SharedRegionPresenter Create(SharedRegionPresenterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Compositor);
        ArgumentNullException.ThrowIfNull(options.Root);
        ArgumentNullException.ThrowIfNull(options.Capture);
        ArgumentNullException.ThrowIfNull(options.Display);
        var queue = SystemDispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("Create the shared region presenter on a thread with a Windows.System dispatcher queue.");
        var presenter = new SharedRegionPresenter(options, queue);
        presenter.root.Children.InsertAtBottom(presenter.visual);
        return presenter;
    }

    /// <summary>The region image. The owner sets its offset and size to the letterboxed frame, in the window's pixels.</summary>
    public SpriteVisual Visual => visual;

    /// <summary>
    /// Another view of the region image (the same swap chain) for a layer above it: the output draws the covers and pixel
    /// effects again above the shared motion's copies, so the copies move behind them (Mac SharedWorkspaceTransition's
    /// protection mask). It shows what <see cref="Visual"/> shows and follows every new swap chain; the owner adds it to its
    /// tree, sizes, places and clips it, and gives it back with <see cref="RemoveView"/>.
    /// </summary>
    public SpriteVisual AddView()
    {
        VerifyAccess();
        SpriteVisual view = compositor.CreateSpriteVisual();
        view.Brush = brush;
        view.IsVisible = visual.IsVisible;
        views.Add(view);
        return view;
    }

    /// <summary>Stops updating a view from <see cref="AddView"/>; the owner removes and disposes it.</summary>
    public void RemoveView(SpriteVisual view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (views.Remove(view))
        {
            view.Brush = null;
            view.IsVisible = false;
        }
    }

    public DisplayInfo Display => display;

    /// <summary>The capture state of the lease, or Stopped without one.</summary>
    public CaptureState CaptureState => lease?.State ?? CaptureState.Stopped.Instance;

    public bool IsRunning => lease is not null;

    /// <summary>Frames drawn and skipped, for measurements.</summary>
    public EffectsStatistics Statistics { get; } = new();

    /// <summary>The first frame of a scene version is on screen (raised on the owner thread).</summary>
    public event Action<long>? Presented;

    /// <summary>Drawing failed; frames stop until <see cref="Show"/> brings a new scene or <see cref="Start"/> runs again.</summary>
    public event Action<Exception>? Failed;

    /// <summary>The capture lease's state changed (raised on the owner thread).</summary>
    public event Action<CaptureState>? CaptureStateChanged;

    /// <summary>The pointer moved, in physical pixels of the desktop (raised on the owner thread while frames arrive).</summary>
    public event Action<int, int>? PointerMoved;

    /// <summary>Shows a new scene. Frames of earlier scenes may still arrive; only frames of this one are reported.</summary>
    public void Show(SharedRegionScene scene)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(scene);
        if (scene.Effects.Width != display.Bounds.Width || scene.Effects.Height != display.Bounds.Height || scene.Effects.Privacy is null)
        {
            throw new ArgumentException("The scene's effects must have the display's size and the region as their privacy crop.", nameof(scene));
        }
        renderFailed = false;
        Volatile.Write(ref published, scene);
        if (lease is not null)
        {
            UpdateInterest(scene);
            RequestRender();
        }
    }

    /// <summary>Starts or joins the capture of the display (WIN-PERF-05). Frames draw as they arrive.</summary>
    public void Start()
    {
        VerifyAccess();
        if (lease is not null)
        {
            return;
        }
        renderFailed = false;
        lease = capture.Acquire(display, consumer);
        Volatile.Write(ref workerLease, lease);
        Interlocked.Exchange(ref reportedVersion, -1);
        if (Volatile.Read(ref published) is { } scene)
        {
            UpdateInterest(scene);
        }
    }

    /// <summary>
    /// Tells the lease which display pixels the region reads (the privacy crop with its one-pixel margin, the zoomed part
    /// under screen zoom), so frames that change nothing there are not drawn; the pointer is followed on every frame anyway.
    /// </summary>
    private void UpdateInterest(SharedRegionScene scene)
    {
        if (lease is not null && scene.Effects.Privacy is { } privacy)
        {
            Span<PixelRect> read = [ReadArea(privacy, scene.Effects.Width, scene.Effects.Height)];
            lease.SetInterest(read);
        }
    }

    /// <summary>Releases the lease, the swap chain and every texture; capture stops when no other consumer needs it.</summary>
    public void Stop()
    {
        VerifyAccess();
        if (lease is not null)
        {
            Volatile.Write(ref workerLease, null);
            lease.Dispose();
            lease = null;
        }
        DetachSurface();
        using (NonPumpingLock.Enter(renderGate))
        {
            ReleaseWorker();
        }
        Interlocked.Exchange(ref reportedVersion, -1);
        pointerX = pointerY = int.MinValue;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        if (Environment.CurrentManagedThreadId != threadId)
        {
            throw new InvalidOperationException("Dispose the shared region presenter on the thread that created it.");
        }
        Stop();
        views.Clear();
        using (NonPumpingLock.Enter(renderGate))
        {
            workerDisposed = true;
        }
        root.Children.Remove(visual);
        visual.Dispose();
        disposed = true;
    }

    /// <summary>
    /// The region in device pixels that the output shows: the Mac's privacy crop, the region rounded inward to whole pixels.
    /// </summary>
    public static PixelRect RegionPixels(Rect region, double scale, int width, int height) =>
        Effects.EffectList.PrivacyRect(region, scale, width, height);

    /// <summary>
    /// Mac region.sourceBounds(through: screenViewport): under screen zoom the region shows the part of the display that zoom
    /// shows there, magnified like the screen, kept inside the display. Null without screen zoom.
    /// </summary>
    public static Rect? ZoomedSource(SharingRegion region, Viewport? view, double scale, int pixelWidth, int pixelHeight)
    {
        if (view is not { } zoom)
        {
            return null;
        }
        Rect source = region.SourceBounds(zoom);
        double maxX = pixelWidth / scale, maxY = pixelHeight / scale;
        double left = Math.Clamp(source.X, 0, maxX), top = Math.Clamp(source.Y, 0, maxY);
        double right = Math.Clamp(source.MaxX, left, maxX), bottom = Math.Clamp(source.MaxY, top, maxY);
        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// The pixels read for the region: the region with one pixel around it, except at the display's edges, so filters near
    /// the region's edge read black there as they do on the whole display.
    /// </summary>
    internal static PixelRect ReadArea(PixelRect privacy, int width, int height) =>
        new(Math.Max(0, privacy.Left - 1), Math.Max(0, privacy.Top - 1), Math.Min(width, privacy.Right + 1), Math.Min(height, privacy.Bottom + 1));

    private void VerifyAccess()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Environment.CurrentManagedThreadId != threadId)
        {
            throw new InvalidOperationException("Use the shared region presenter on the thread that created it.");
        }
    }

    private void DetachSurface()
    {
        visual.Brush = null;
        visual.IsVisible = false;
        foreach (SpriteVisual view in views)
        {
            view.Brush = null;
            view.IsVisible = false;
        }
        brush?.Dispose();
        (surface as IDisposable)?.Dispose();
        brush = null;
        surface = null;
        attachedGeneration = -1;
    }

    private void Post(Windows.System.DispatcherQueueHandler work) => queue.TryEnqueue(work);

    private int renderRequested;

    private void RequestRender()
    {
        if (Interlocked.Exchange(ref renderRequested, 1) == 0)
        {
            ThreadPool.UnsafeQueueUserWorkItem(static presenter => presenter.RunRequestedRender(), this, preferLocal: false);
        }
    }

    private void RunRequestedRender()
    {
        Interlocked.Exchange(ref renderRequested, 0);
        if (Volatile.Read(ref workerLease) is { } source)
        {
            Render(source);
        }
    }

    /// <summary>
    /// Draws the newest frame with the newest scene and presents it (a capture worker or the thread pool). A frame whose
    /// pixels equal what the chain already shows, with the same scene, is not drawn or presented; the pointer is still read.
    /// </summary>
    private void Render(ICaptureLease source)
    {
        if (Volatile.Read(ref published) is null || !ReferenceEquals(source, Volatile.Read(ref workerLease)) || !source.TryAcquireFrame(out var frame))
        {
            return;
        }
        using (frame)
        {
            using (NonPumpingLock.Enter(renderGate))
            {
                if (workerDisposed || renderFailed)
                {
                    return;
                }
                SharedRegionScene scene = Volatile.Read(ref published)!;
                if (frame.Width != scene.Effects.Width || frame.Height != scene.Effects.Height)
                {
                    return;
                }
                if (ReferenceEquals(scene, drawnScene) && frame.ContentVersion == drawnContent && Interlocked.Read(ref generation) == drawnGeneration
                    && Interlocked.Read(ref reportedVersion) == scene.Version)
                {
                    Statistics.Repeat();
                    TrackPointer();
                    return;
                }
                var start = Stopwatch.GetTimestamp();
                try
                {
                    PixelRect privacy = scene.Effects.Privacy!.Value;
                    // Under zoom the chain keeps the region's size, and the zoomed part is projected into it.
                    PixelRect shown = scene.Source is null ? privacy : RegionPixels(scene.Region, scene.Effects.Scale, scene.Effects.Width, scene.Effects.Height);
                    if (pipeline is null || !ReferenceEquals(pipeline.Devices, frame.Devices))
                    {
                        ReleaseWorker();
                        pipeline = new EffectPipeline(frame.Devices);
                    }
                    if (swapChain == null || chainWidth != shown.Width || chainHeight != shown.Height)
                    {
                        CreateSwapChain(frame.Devices, shown.Width, shown.Height);
                    }
                    if (PInvoke.WaitForSingleObject(waitable, 0) != (WAIT_EVENT)0)
                    {
                        Statistics.Skip();
                        if (++skippedInARow == StallFrames)
                        {
                            Interlocked.Exchange(ref reportedVersion, -1);
                        }
                        return;
                    }
                    skippedInARow = 0;
                    using (pipeline.LockContext())
                    {
                        Draw(frame, scene, privacy);
                    }
                    Native.Check(swapChain->Present(0, 0), "IDXGISwapChain::Present(shared region)");
                    Statistics.Drawn(Stopwatch.GetElapsedTime(start));
                    drawnScene = scene;
                    drawnContent = frame.ContentVersion;
                    drawnGeneration = Interlocked.Read(ref generation);
                    if (Interlocked.Exchange(ref reportedVersion, scene.Version) != scene.Version)
                    {
                        PostPresented(scene.Version, generation);
                    }
                    TrackPointer();
                }
                catch (NativeCallException error) when (DeviceLoss.IsDeviceLoss(error.HResult))
                {
                    // The capture device is gone: the capture service replaces it and the next frame starts over.
                    ReleaseWorker();
                    frame.Devices.ReportFailure(error.HResult);
                    Interlocked.Exchange(ref reportedVersion, -1);
                }
                catch (Exception error) when (error is NativeCallException or InvalidOperationException or OutOfMemoryException or RenderFailure or ArgumentException)
                {
                    renderFailed = true;
                    ReleaseWorker();
                    PostFailed(error);
                }
            }
        }
    }

    private void PostPresented(long version, long chain) => Post(() => OnPresented(version, chain));

    private void PostFailed(Exception error) => Post(() =>
    {
        if (!disposed)
        {
            DetachSurface();
            Failed?.Invoke(error);
        }
    });

    private void OnPresented(long version, long chain)
    {
        if (disposed || lease is null || chain != attachedGeneration)
        {
            // The chain is not on screen yet; its attachment reports again.
            Interlocked.CompareExchange(ref reportedVersion, -1, version);
            return;
        }
        visual.IsVisible = true;
        foreach (SpriteVisual view in views)
        {
            view.IsVisible = true;
        }
        Presented?.Invoke(version);
    }

    /// <summary>The pointer's position, read once per frame and told to the owner only when it moved.</summary>
    private void TrackPointer()
    {
        if (!PInvoke.GetCursorPos(out System.Drawing.Point point) || (point.X == pointerX && point.Y == pointerY))
        {
            return;
        }
        pointerX = point.X;
        pointerY = point.Y;
        PostPointer(point.X, point.Y);
    }

    private void PostPointer(int x, int y) => Post(() =>
    {
        if (!disposed && lease is not null)
        {
            PointerMoved?.Invoke(x, y);
        }
    });

    /// <summary>
    /// Copies the region, or filters it with its drawings, covers and effects, into the back buffer. Without drawings and
    /// without covers or effects in reach, the region's pixels are copied as they are. Under screen zoom the zoomed part is
    /// projected into the back buffer instead, nearest-neighbor, as the Mac's view samples the zoomed source.
    /// </summary>
    private void Draw(CaptureFrame frame, SharedRegionScene scene, PixelRect privacy)
    {
        var context = (ID3D11DeviceContext*)frame.Devices.D3D11DeviceContext;
        var device = (ID3D11Device*)frame.Devices.D3D11Device;
        bool zoomed = scene.Source is not null;
        PixelRect area = ReadArea(privacy, scene.Effects.Width, scene.Effects.Height);
        EffectList local = Local(scene.Effects, area);
        SceneLayer? drawingLayer = !scene.Drawings.IsDefaultOrEmpty || scene.Background is not null ? DrawLayer(frame.Devices, scene, area) : null;
        if (drawingLayer is null && local.Regions.IsEmpty && local.Covers.IsEmpty)
        {
            var box = Box(privacy.Left, privacy.Top, privacy.Right, privacy.Bottom);
            if (!zoomed)
            {
                context->CopySubresourceRegion((ID3D11Resource*)backBuffer, 0, 0, 0, 0, (ID3D11Resource*)frame.Texture, 0, &box);
                return;
            }
            localSource.Ensure(device, privacy.Width, privacy.Height, D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE, "shared region source");
            context->CopySubresourceRegion((ID3D11Resource*)localSource.Pointer, 0, 0, 0, 0, (ID3D11Resource*)frame.Texture, 0, &box);
            Project(frame.Devices, localSource.Pointer, new PixelRect(0, 0, privacy.Width, privacy.Height));
            return;
        }
        localSource.Ensure(device, area.Width, area.Height, D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE, "shared region source");
        localTarget.Ensure(device, area.Width, area.Height, D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
                           "shared region target");
        var read = Box(area.Left, area.Top, area.Right, area.Bottom);
        context->CopySubresourceRegion((ID3D11Resource*)localSource.Pointer, 0, 0, 0, 0, (ID3D11Resource*)frame.Texture, 0, &read);
        pipeline!.RenderScene((nint)localSource.Pointer, (nint)localTarget.Pointer, local, drawingLayer);
        if (zoomed)
        {
            Project(frame.Devices, localTarget.Pointer, new PixelRect(privacy.Left - area.Left, privacy.Top - area.Top, privacy.Right - area.Left, privacy.Bottom - area.Top));
            return;
        }
        var shown = Box(privacy.Left - area.Left, privacy.Top - area.Top, privacy.Right - area.Left, privacy.Bottom - area.Top);
        context->CopySubresourceRegion((ID3D11Resource*)backBuffer, 0, 0, 0, 0, (ID3D11Resource*)localTarget.Pointer, 0, &shown);
    }

    /// <summary>The zoomed part, <paramref name="from"/> in <paramref name="texture"/>, fills the back buffer.</summary>
    private void Project(GraphicsDevices devices, ID3D11Texture2D* texture, PixelRect from)
    {
        EnsureDrawContext(devices);
        if (chainBitmap == null)
        {
            chainBitmap = Bitmap(drawContext, (ID3D11Texture2D*)backBuffer, target: true, "shared region back buffer");
        }
        if (projectedBitmap == null || projectedTexture != (nint)texture)
        {
            Native.Release(ref projectedBitmap);
            projectedBitmap = Bitmap(drawContext, texture, target: false, "shared region zoom source");
            projectedTexture = (nint)texture;
        }
        Project(drawContext, projectedBitmap, from, chainBitmap, chainWidth, chainHeight);
    }

    /// <summary>
    /// Mac ViewportProjection: <paramref name="from"/> of <paramref name="source"/> fills <paramref name="target"/>
    /// (<paramref name="width"/> by <paramref name="height"/> pixels), sampled nearest-neighbor and copied, not blended.
    /// </summary>
    internal static void Project(ID2D1DeviceContext* context, ID2D1Bitmap1* source, PixelRect from, ID2D1Bitmap1* target, int width, int height)
    {
        context->SetTarget((ID2D1Image*)target);
        context->BeginDraw();
        try
        {
            var identity = D2D.Identity;
            context->SetTransform(&identity);
            context->SetPrimitiveBlend(D2D1_PRIMITIVE_BLEND.D2D1_PRIMITIVE_BLEND_COPY);
            var destination = new D2D_RECT_F { left = 0, top = 0, right = width, bottom = height };
            var sourceRect = new D2D_RECT_F { left = from.Left, top = from.Top, right = from.Right, bottom = from.Bottom };
            context->DrawBitmap((ID2D1Bitmap*)source, &destination, 1, D2D1_INTERPOLATION_MODE.D2D1_INTERPOLATION_MODE_NEAREST_NEIGHBOR, &sourceRect, null);
        }
        finally
        {
            context->SetPrimitiveBlend(D2D1_PRIMITIVE_BLEND.D2D1_PRIMITIVE_BLEND_SOURCE_OVER);
            HRESULT ended = context->EndDraw(null, null);
            context->SetTarget(null);
            Native.Check(ended, "ID2D1DeviceContext::EndDraw(shared region zoom)");
        }
    }

    /// <summary>A Direct2D bitmap over <paramref name="texture"/>: a drawing target, or a source to draw from.</summary>
    internal static ID2D1Bitmap1* Bitmap(ID2D1DeviceContext* context, ID3D11Texture2D* texture, bool target, string name)
    {
        IDXGISurface* dxgi = Native.Query<IDXGISurface>(texture, IDXGISurface.IID_Guid, $"QueryInterface(IDXGISurface)({name})");
        try
        {
            var properties = new D2D1_BITMAP_PROPERTIES1
            {
                pixelFormat = new D2D1_PIXEL_FORMAT { format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED },
                dpiX = 96,
                dpiY = 96,
                bitmapOptions = target ? D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_CANNOT_DRAW
                    : D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_NONE,
            };
            ID2D1Bitmap1* bitmap;
            Native.Check(context->CreateBitmapFromDxgiSurface(dxgi, &properties, &bitmap), $"ID2D1DeviceContext::CreateBitmapFromDxgiSurface({name})");
            return bitmap;
        }
        finally
        {
            Native.Release(ref dxgi);
        }
    }

    private void EnsureDrawContext(GraphicsDevices devices)
    {
        if (drawContext == null)
        {
            ID2D1DeviceContext* created;
            Native.Check(((ID2D1Device*)devices.D2DDevice)->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS.D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &created),
                         "ID2D1Device::CreateDeviceContext(shared region)");
            drawContext = created;
        }
    }

    private EffectList Local(EffectList list, PixelRect area)
    {
        if (localList is { } cached && ReferenceEquals(cached.List, list) && cached.Area == area)
        {
            return cached.Local;
        }
        EffectList local = list.Within(area);
        localList = (list, area, local);
        return local;
    }

    /// <summary>
    /// The Mac's SharedRegionDrawingLayer: the board and the drawings over the region, drawn once per scene on the display's
    /// pixel grid, in the read area's pixels. The privacy crop removes what they draw outside the region. Null when nothing
    /// is drawn inside the area.
    /// </summary>
    private SceneLayer? DrawLayer(GraphicsDevices devices, SharedRegionScene scene, PixelRect area)
    {
        if (layerScene is { } drawn && layerArea == area && (ReferenceEquals(drawn, scene)
            || (drawn.Drawings == scene.Drawings && drawn.Background == scene.Background && drawn.Region == scene.Region && drawn.Source == scene.Source
                && drawn.Effects.Scale == scene.Effects.Scale)))
        {
            layerScene = scene;
            return layerEmpty ? null : new SceneLayer((nint)layer.Pointer, area.Width, area.Height, 0, 0);
        }
        renderer ??= new DisplayListRenderer(devices, colors);
        ImmutableArray<DisplayList> lists = scene.Drawings.IsDefault ? [] : scene.Drawings;
        foreach (DisplayList list in lists)
        {
            renderer.Enter(list);
        }
        foreach (DisplayList list in enteredLists)
        {
            renderer.Leave(list);
        }
        enteredLists = lists;
        renderer.CollectImages();
        double scale = scene.Effects.Scale;
        var clip = new PixelRect(0, 0, area.Width, area.Height);
        var drawArea = new DrawArea(scale, -area.Left, -area.Top, clip);
        bool any = scene.Background is not null;
        foreach (DisplayList list in lists)
        {
            if (any)
            {
                break;
            }
            any = renderer.Bounds(list) is { } bounds && DisplayListRenderer.Intersects(DisplayListRenderer.PixelBounds(bounds, drawArea), clip);
        }
        layerScene = scene;
        layerArea = area;
        layerEmpty = !any;
        if (!any)
        {
            return null;
        }
        var device = (ID3D11Device*)devices.D3D11Device;
        if (layer.Ensure(device, area.Width, area.Height, D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
                         "shared region drawings"))
        {
            Native.Release(ref layerBitmap);
        }
        EnsureDrawContext(devices);
        if (layerBitmap == null)
        {
            layerBitmap = Bitmap(drawContext, layer.Pointer, target: true, "shared region");
        }
        drawContext->SetTarget((ID2D1Image*)layerBitmap);
        drawContext->BeginDraw();
        try
        {
            var identity = D2D.Identity;
            drawContext->SetTransform(&identity);
            var transparent = default(D2D1_COLOR_F);
            drawContext->Clear(&transparent);
            if (scene.Background is { } background)
            {
                var brush = renderer.SolidBrush;
                var color = D2D.Color(background);
                brush->SetColor(&color);
                brush->SetOpacity(1);
                Rect region = scene.Source ?? scene.Region;
                var fill = D2D.Rect(region.X * scale - area.Left, region.Y * scale - area.Top, region.MaxX * scale - area.Left, region.MaxY * scale - area.Top);
                drawContext->FillRectangle(&fill, (ID2D1Brush*)brush);
            }
            if (!lists.IsEmpty)
            {
                renderer.Draw((nint)drawContext, lists.AsSpan(), drawArea);
            }
        }
        catch
        {
            layerScene = null;
            throw;
        }
        finally
        {
            HRESULT ended = drawContext->EndDraw(null, null);
            drawContext->SetTarget(null);
            if (ended.Failed)
            {
                layerScene = null;
            }
            Native.Check(ended, "ID2D1DeviceContext::EndDraw(shared region)");
        }
        return new SceneLayer((nint)layer.Pointer, area.Width, area.Height, 0, 0);
    }
    private static D3D11_BOX Box(int left, int top, int right, int bottom) => new()
    {
        left = (uint)left,
        top = (uint)top,
        right = (uint)right,
        bottom = (uint)bottom,
        front = 0,
        back = 1,
    };

    private void CreateSwapChain(GraphicsDevices devices, int width, int height)
    {
        ReleaseChain();
        swapChain = (IDXGISwapChain2*)SwapChains.CreateForComposition(devices, width, height, "shared region");
        Native.Check(swapChain->SetMaximumFrameLatency(1), "IDXGISwapChain2::SetMaximumFrameLatency(shared region)");
        waitable = swapChain->GetFrameLatencyWaitableObject();
        if (waitable == HANDLE.Null)
        {
            throw new NativeCallException("IDXGISwapChain2::GetFrameLatencyWaitableObject(shared region)", unchecked((int)0x80004005));
        }
        Guid textureIid = ID3D11Texture2D.IID_Guid;
        void* buffer;
        Native.Check(swapChain->GetBuffer(0, &textureIid, &buffer), "IDXGISwapChain::GetBuffer(shared region)");
        backBuffer = (nint)buffer;
        chainWidth = width;
        chainHeight = height;
        Interlocked.Increment(ref generation);
        Interlocked.Exchange(ref reportedVersion, -1);
        var chain = (nint)swapChain;
        var chainGeneration = generation;
        Marshal.AddRef(chain);
        if (!queue.TryEnqueue(() => OnAttach(chain, chainGeneration)))
        {
            Marshal.Release(chain);
        }
    }

    private void OnAttach(nint chain, long chainGeneration)
    {
        try
        {
            if (disposed || lease is null || chainGeneration != Interlocked.Read(ref generation))
            {
                return;
            }
            var created = Annotator.Platform.Interop.CompositionInterop.CreateCompositionSurfaceForSwapChain(compositor, chain);
            var createdBrush = compositor.CreateSurfaceBrush(created);
            createdBrush.Stretch = CompositionStretch.Fill;
            // The Mac's ViewportProjection samples the region image nearest-neighbor into its view.
            createdBrush.BitmapInterpolationMode = CompositionBitmapInterpolationMode.NearestNeighbor;
            DetachSurface();
            surface = created;
            brush = createdBrush;
            visual.Brush = createdBrush;
            foreach (SpriteVisual view in views)
            {
                view.Brush = createdBrush;
            }
            attachedGeneration = chainGeneration;
            // A frame drawn before the attachment counts once the chain is on screen.
            Interlocked.Exchange(ref reportedVersion, -1);
            RequestRender();
        }
        catch (Exception error) when (error is NativeCallException or InvalidOperationException or COMException)
        {
            Failed?.Invoke(error);
        }
        finally
        {
            Marshal.Release(chain);
        }
    }

    private void ReleaseChain()
    {
        Native.Release(ref chainBitmap);
        if (backBuffer != 0)
        {
            Marshal.Release(backBuffer);
            backBuffer = 0;
        }
        if (waitable != HANDLE.Null)
        {
            PInvoke.CloseHandle(waitable);
            waitable = HANDLE.Null;
        }
        Native.Release(ref swapChain);
        chainWidth = chainHeight = 0;
        Interlocked.Increment(ref generation);
    }

    private void ReleaseWorker()
    {
        ReleaseChain();
        Native.Release(ref layerBitmap);
        Native.Release(ref projectedBitmap);
        projectedTexture = 0;
        if (drawContext != null)
        {
            drawContext->SetTarget(null);
            Native.Release(ref drawContext);
        }
        if (renderer is not null)
        {
            foreach (DisplayList list in enteredLists)
            {
                renderer.Leave(list);
            }
            renderer.Dispose();
            renderer = null;
        }
        enteredLists = [];
        layerScene = null;
        localList = null;
        localSource.Release();
        localTarget.Release();
        layer.Release();
        // Last: disposing the pipeline flushes the device context, which frees what was released above.
        pipeline?.Dispose();
        pipeline = null;
    }

    /// <summary>A texture grown to the largest size asked for, recreated when it is smaller.</summary>
    private struct Texture
    {
        public ID3D11Texture2D* Pointer;
        private int width;
        private int height;

        /// <summary>Makes the texture exactly <paramref name="w"/> by <paramref name="h"/>; true when it was created anew.</summary>
        public bool Ensure(ID3D11Device* device, int w, int h, D3D11_BIND_FLAG bind, string name)
        {
            if (Pointer != null && w == width && h == height)
            {
                return false;
            }
            Release();
            var description = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)w,
                Height = (uint)h,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                BindFlags = bind,
            };
            ID3D11Texture2D* texture;
            Native.Check(device->CreateTexture2D(&description, null, &texture), $"ID3D11Device::CreateTexture2D({name})");
            Pointer = texture;
            width = w;
            height = h;
            return true;
        }

        public void Release()
        {
            Native.Release(ref Pointer);
            width = height = 0;
        }
    }

    /// <summary>Receives the lease's frames and states; frames draw at once, states go to the owner thread.</summary>
    private sealed class Consumer(SharedRegionPresenter owner) : ICaptureConsumer
    {
        public void OnFrameAvailable(ICaptureLease lease) => owner.Render(lease);

        public void OnStateChanged(ICaptureLease lease, CaptureState state) => owner.Post(() =>
        {
            if (!owner.disposed && ReferenceEquals(lease, owner.lease))
            {
                if (!state.ProvidesPixels)
                {
                    // A frame drawn before the pause is stale: the next scene report waits for a new one.
                    Interlocked.Exchange(ref owner.reportedVersion, -1);
                }
                owner.CaptureStateChanged?.Invoke(state);
            }
        });
    }
}

/// <summary>Composition swap chains for the presenters.</summary>
internal static unsafe class SwapChains
{
    /// <summary>
    /// A two-buffer B8G8R8A8 flip-model swap chain for composition with a frame-latency waitable object (IDXGISwapChain2):
    /// opaque, or with premultiplied alpha.
    /// </summary>
    public static nint CreateForComposition(GraphicsDevices devices, int width, int height, string name, bool premultiplied = false)
    {
        var dxgiDevice = (IDXGIDevice*)devices.DxgiDevice;
        IDXGIAdapter* adapter = null;
        IDXGIFactory2* factory = null;
        IDXGISwapChain1* created = null;
        try
        {
            Native.Check(dxgiDevice->GetAdapter(&adapter), $"IDXGIDevice::GetAdapter({name})");
            Guid factoryIid = IDXGIFactory2.IID_Guid;
            void* parent;
            Native.Check(adapter->GetParent(&factoryIid, &parent), $"IDXGIAdapter::GetParent(IDXGIFactory2)({name})");
            factory = (IDXGIFactory2*)parent;
            var description = new DXGI_SWAP_CHAIN_DESC1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                BufferUsage = DXGI_USAGE.DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount = 2,
                Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH,
                SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL,
                AlphaMode = premultiplied ? DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_PREMULTIPLIED : DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE,
                Flags = DXGI_SWAP_CHAIN_FLAG.DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT,
            };
            Native.Check(factory->CreateSwapChainForComposition((Windows.Win32.System.Com.IUnknown*)devices.D3D11Device, &description, null, &created),
                         $"IDXGIFactory2::CreateSwapChainForComposition({name})");
            return (nint)Native.Query<IDXGISwapChain2>(created, IDXGISwapChain2.IID_Guid, $"QueryInterface(IDXGISwapChain2)({name})");
        }
        finally
        {
            Native.Release(ref created);
            Native.Release(ref factory);
            Native.Release(ref adapter);
        }
    }
}
