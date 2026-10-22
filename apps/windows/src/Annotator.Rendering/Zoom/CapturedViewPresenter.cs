using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Platform;
using Annotator.Platform.Capture;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Platform.Threading;
using Annotator.Rendering.Effects;
using Annotator.Rendering.Interop;
using Windows.Graphics.DirectX;
using Windows.UI.Composition;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;
using SystemDispatcherQueue = Windows.System.DispatcherQueue;

namespace Annotator.Rendering.Zoom;

/// <summary>Everything a <see cref="CapturedViewPresenter"/> needs from its host, on the overlay thread.</summary>
public sealed record CapturedViewOptions
{
    public required Compositor Compositor { get; init; }

    /// <summary>The container the view's visual goes into.</summary>
    public required ContainerVisual Root { get; init; }

    /// <summary>
    /// True puts the view below everything else in <see cref="Root"/> (screen zoom sits under the drawings, as the Mac's
    /// screen panel sits under its canvas); false puts it on top.
    /// </summary>
    public bool AtBottom { get; init; }

    /// <summary>The capture service; the presenter holds a lease only while it shows a view.</summary>
    public required IDisplayCapture Capture { get; init; }

    /// <summary>The captured display.</summary>
    public required DisplayInfo Display { get; init; }

    /// <summary>The view's size in device pixels (the display's for screen zoom, the lens's for the lens).</summary>
    public required int PixelWidth { get; init; }

    public required int PixelHeight { get; init; }

    /// <summary>The color under the view until its first frame (the Mac's black cover).</summary>
    public Windows.UI.Color Cover { get; init; } = Windows.UI.Color.FromArgb(255, 0, 0, 0);
}

/// <summary>
/// What a captured view shows: the captured display's frame drawn with <see cref="Effects"/> (covers, Blur, Pixelate and Distort,
/// and image layers when the list has them), and the part inside <see cref="Crop"/> (frame pixels) scaled to the whole view
/// with nearest sampling.
/// </summary>
public sealed record CapturedView(EffectList Effects, Rect Crop);

/// <summary>What a <see cref="CapturedViewPresenter"/> does now.</summary>
public enum CapturedViewState
{
    /// <summary>No view is shown; no capture runs.</summary>
    Idle,

    /// <summary>Capture is starting, or the view has not drawn a frame yet; nothing shows yet.</summary>
    Waiting,

    /// <summary>The view shows the newest captured frame.</summary>
    Live,

    /// <summary>Capture stalled or is suspended: the last frame stays on screen until frames arrive again.</summary>
    Paused,

    /// <summary>Capture or drawing failed: <see cref="CapturedViewPresenter.Failure"/> says why.</summary>
    Failed,

    Disposed,
}

/// <summary>
/// Shows a magnified part of the live screen without Annotator's windows: screen zoom under the drawings and the pointer lens
/// (Mac LivePresentation's screen and lens views, ADR 0004, WIN-CAP-05, WIN-CAP-06). Each captured frame is drawn on the
/// capture device by the capture worker that delivers it, through <see cref="EffectPipeline.RenderView"/>, into a composition
/// drawing surface. A new crop is drawn at once on the calling thread, the overlay thread: surface updates of one work item
/// commit together, so the zoomed pixels and the drawings through the same viewport reach the screen in the same frame,
/// where a swap chain presents on its own and was measured one frame early in a few percent of updates. Nothing shows until
/// the view's first frame, as the Mac makes its zoomed views only once capture has a frame; an opaque cover (the Mac's black
/// cover) stays under the picture, so the view never shows anything but the screen as it was captured, or black.
/// Without a view the presenter holds no capture lease and releases its surface and pipeline.
/// </summary>
public sealed unsafe class CapturedViewPresenter : IDisposable
{
    private readonly Compositor compositor;
    private readonly ContainerVisual root;
    private readonly ContainerVisual group;
    private readonly SpriteVisual cover;
    private readonly SpriteVisual content;
    private readonly CompositionColorBrush coverBrush;
    private readonly IDisplayCapture capture;
    private readonly DisplayInfo display;
    private readonly SystemDispatcherQueue queue;
    private readonly int threadId = Environment.CurrentManagedThreadId;
    private readonly Consumer consumer;
    private readonly List<PixelRect> interest = [];

    // Overlay thread.
    private ICaptureLease? lease;
    private CapturedView? view;
    private int version;
    private CaptureState captureState = CaptureState.Stopped.Instance;
    private CapturedViewState state = CapturedViewState.Idle;
    private Exception? failure;
    private CompositionGraphicsDevice? graphics;
    private CompositionDrawingSurface? surface;
    private CompositionSurfaceBrush? brush;
    private long attachedGeneration = -1;
    private bool presented;

    // The view drew a frame since it started: it shows from then on, its cover under a surface that waits for a frame.
    private bool revealed;
    private bool disposed;

    // Shared with the render worker.
    private readonly Lock renderGate = new();
    private Published? published;
    private ICaptureLease? workerLease;
    private int renderRequested;
    private int reportWanted;

    // Under renderGate.
    private EffectPipeline? pipeline;
    private nint surfaceInterop;
    private GraphicsDevices? surfaceDevices;
    private GraphicsDevices? requestedDevices;
    private int surfaceWidth;
    private int surfaceHeight;
    private long generation;
    private volatile bool renderFailed;
    private bool workerDisposed;

    // What the surface shows: the view, the captured content and the surface. A frame that changes none of them is not drawn.
    private Published? drawnWork;
    private long drawnContent = -1;
    private nint drawnSurface;

    private CapturedViewPresenter(CapturedViewOptions options, SystemDispatcherQueue queue)
    {
        compositor = options.Compositor;
        root = options.Root;
        capture = options.Capture;
        display = options.Display;
        PixelWidth = options.PixelWidth;
        PixelHeight = options.PixelHeight;
        this.queue = queue;
        consumer = new Consumer(this);
        group = compositor.CreateContainerVisual();
        cover = compositor.CreateSpriteVisual();
        content = compositor.CreateSpriteVisual();
        coverBrush = compositor.CreateColorBrush(options.Cover);
    }

    /// <summary>Creates the presenter on the overlay thread and adds its (hidden) visual to <see cref="CapturedViewOptions.Root"/>.</summary>
    public static CapturedViewPresenter Create(CapturedViewOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Compositor);
        ArgumentNullException.ThrowIfNull(options.Root);
        ArgumentNullException.ThrowIfNull(options.Capture);
        ArgumentNullException.ThrowIfNull(options.Display);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.PixelWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.PixelHeight);
        var queue = SystemDispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("Create the captured view on a thread with a Windows.System dispatcher queue, such as a DispatcherThread.");
        var presenter = new CapturedViewPresenter(options, queue);
        var size = new Vector2(options.PixelWidth, options.PixelHeight);
        presenter.group.Size = size;
        presenter.cover.Size = size;
        presenter.content.Size = size;
        presenter.cover.Brush = presenter.coverBrush;
        presenter.content.IsVisible = false;
        presenter.group.IsVisible = false;
        presenter.group.Children.InsertAtTop(presenter.cover);
        presenter.group.Children.InsertAtTop(presenter.content);
        if (options.AtBottom)
        {
            presenter.root.Children.InsertAtBottom(presenter.group);
        }
        else
        {
            presenter.root.Children.InsertAtTop(presenter.group);
        }
        return presenter;
    }

    /// <summary>The view's visuals: the cover, and the zoomed screen above it.</summary>
    public ContainerVisual Visual => group;

    public int PixelWidth { get; private set; }

    public int PixelHeight { get; private set; }

    public CapturedViewState State => state;

    /// <summary>Why the view failed, while <see cref="State"/> is <see cref="CapturedViewState.Failed"/>.</summary>
    public Exception? Failure => failure;

    /// <summary>The capture state behind <see cref="State"/>, for messages.</summary>
    public CaptureState CaptureState => captureState;

    /// <summary>True once the shown view drew a captured frame; false while capture starts (nothing shows) or after a resize.</summary>
    public bool Presented => presented;

    /// <summary>Raised on the overlay thread when <see cref="State"/> or <see cref="Presented"/> changes. A handler must not throw.</summary>
    public event EventHandler? Changed;

    public EffectsStatistics Statistics { get; } = new();

    /// <summary>
    /// Shows <paramref name="next"/>, or nothing with null (the lease, surface and pipeline go). A view of the same effects
    /// with another crop is drawn at once with the newest frame (<paramref name="renderNow"/>), so it changes with the drawings.
    /// A view that starts shows nothing until its first frame.
    /// </summary>
    public void Show(CapturedView? next, bool renderNow = true)
    {
        VerifyAccess();
        if (next is null)
        {
            if (view is null)
            {
                return;
            }
            view = null;
            Volatile.Write(ref published, null);
            ReleaseLease();
            ReleaseResources();
            presented = false;
            revealed = false;
            failure = null;
            Update();
            return;
        }
        if (next.Effects.Width <= 0 || next.Effects.Height <= 0)
        {
            throw new ArgumentException("The view's effects need the display's size.", nameof(next));
        }
        bool starting = view is null;
        if (next.Equals(view) && failure is null)
        {
            return;
        }
        view = next;
        Publish();
        if (starting)
        {
            failure = null;
            renderFailed = false;
            presented = false;
            revealed = false;
        }
        EnsureLease();
        UpdateInterest(next);
        if (renderNow && lease is { } source && captureState is CaptureState.Live)
        {
            Render(source);
        }
        else
        {
            RequestRender();
        }
        Update();
    }

    /// <summary>After <see cref="CapturedViewState.Failed"/>, tries again with the same view.</summary>
    public void Retry()
    {
        VerifyAccess();
        renderFailed = false;
        failure = null;
        if (view is not null)
        {
            EnsureLease();
            UpdateInterest(view);
            RequestRender();
        }
        Update();
    }

    /// <summary>Follows a size change of the view: the surface is made again, and the cover shows until the next frame.</summary>
    public void Resize(int pixelWidth, int pixelHeight)
    {
        VerifyAccess();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelHeight);
        if (pixelWidth == PixelWidth && pixelHeight == PixelHeight)
        {
            return;
        }
        Volatile.Write(ref published, null);
        ReleaseResources();
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        var size = new Vector2(pixelWidth, pixelHeight);
        group.Size = size;
        cover.Size = size;
        content.Size = size;
        presented = false;
        if (view is not null)
        {
            Publish();
            RequestRender();
        }
        Update();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        if (Environment.CurrentManagedThreadId != threadId)
        {
            throw new InvalidOperationException("Dispose the captured view on the thread that created it.");
        }
        ReleaseLease();
        using (NonPumpingLock.Enter(renderGate))
        {
            workerDisposed = true;
        }
        ReleaseResources();
        root.Children.Remove(group);
        content.Dispose();
        cover.Dispose();
        coverBrush.Dispose();
        group.Dispose();
        disposed = true;
        presented = false;
        SetState(CapturedViewState.Disposed);
    }

    private void VerifyAccess()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Environment.CurrentManagedThreadId != threadId)
        {
            throw new InvalidOperationException("Use the captured view on the thread that created it.");
        }
    }

    private void Publish()
    {
        version++;
        Volatile.Write(ref published, new Published(view!, version, PixelWidth, PixelHeight));
    }

    private void EnsureLease()
    {
        if (lease is not null)
        {
            return;
        }
        lease = capture.Acquire(display, consumer);
        Volatile.Write(ref workerLease, lease);
        Volatile.Write(ref reportWanted, 1);
        Follow(lease.State);
    }

    /// <summary>
    /// Tells the lease which display pixels the view reads: the crop with a pixel around it for nearest sampling, and the
    /// filter footprint of every effect region in it, so frames that change nothing there are not drawn.
    /// </summary>
    private void UpdateInterest(CapturedView shown)
    {
        if (lease is null)
        {
            return;
        }
        var effects = shown.Effects;
        var crop = shown.Crop;
        var cropped = new PixelRect(Math.Max(0, (int)Math.Floor(crop.X) - 1), Math.Max(0, (int)Math.Floor(crop.Y) - 1),
                                    Math.Min(effects.Width, (int)Math.Ceiling(crop.MaxX) + 1), Math.Min(effects.Height, (int)Math.Ceiling(crop.MaxY) + 1));
        interest.Clear();
        interest.Add(cropped);
        foreach (var region in effects.Regions)
        {
            if (region.IsVisible && region.Rect.Left < cropped.Right && region.Rect.Right > cropped.Left && region.Rect.Top < cropped.Bottom
                && region.Rect.Bottom > cropped.Top)
            {
                interest.Add(EffectPipeline.Footprint(region, effects));
            }
        }
        lease.SetInterest(CollectionsMarshal.AsSpan(interest));
    }

    private void ReleaseLease()
    {
        if (lease is null)
        {
            return;
        }
        Volatile.Write(ref workerLease, null);
        lease.Dispose();
        lease = null;
        captureState = CaptureState.Stopped.Instance;
    }

    /// <summary>Releases the surface, its graphics device and the pipeline (overlay thread).</summary>
    private void ReleaseResources()
    {
        using (NonPumpingLock.Enter(renderGate))
        {
            ReleaseSurfaceInterop();
            requestedDevices = null;
            // Disposing the pipeline flushes the device context, which frees what it released.
            pipeline?.Dispose();
            pipeline = null;
        }
        DetachSurface();
    }

    private void ReleaseSurfaceInterop()
    {
        if (surfaceInterop != 0)
        {
            Marshal.Release(surfaceInterop);
            surfaceInterop = 0;
            drawnWork = null;
        }
        surfaceDevices = null;
        generation++;
    }

    private void DetachSurface()
    {
        content.Brush = null;
        content.IsVisible = false;
        brush?.Dispose();
        surface?.Dispose();
        graphics?.Dispose();
        brush = null;
        surface = null;
        graphics = null;
        attachedGeneration = -1;
    }

    private void Update()
    {
        if (disposed)
        {
            return;
        }
        // Mac LivePresentation makes its zoomed views only once capture has a frame: nothing shows while capture starts, so
        // the person keeps seeing the screen as it is. The cover stays under the picture, so a lost or resized surface shows
        // black until its next frame.
        group.IsVisible = view is not null && revealed;
        content.IsVisible = view is not null && presented && brush is not null && failure is null;
        CapturedViewState next = view is null ? CapturedViewState.Idle
            : failure is not null || captureState is CaptureState.Failed ? CapturedViewState.Failed
            : captureState is CaptureState.Stalled or CaptureState.Suspended ? CapturedViewState.Paused
            : presented ? CapturedViewState.Live
            : CapturedViewState.Waiting;
        SetState(next);
    }

    private void SetState(CapturedViewState next)
    {
        if (state == next)
        {
            return;
        }
        state = next;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnCaptureState(ICaptureLease from, CaptureState next)
    {
        if (disposed || !ReferenceEquals(from, lease))
        {
            return;
        }
        Follow(next);
        if (next is CaptureState.Live)
        {
            Volatile.Write(ref reportWanted, 1);
            RequestRender();
        }
        Update();
    }

    /// <summary>A failed capture fails the view (the Mac stops zoom with a message); a new view or Retry tries again.</summary>
    private void Follow(CaptureState next)
    {
        captureState = next;
        if (next is CaptureState.Failed failed)
        {
            failure = failed.Failure.Error ?? new InvalidOperationException($"{failed.Failure.Kind}: {failed.Failure.Operation}");
        }
    }

    /// <summary>
    /// A drawing surface of the view's size on a composition graphics device made from the capture device, so frames are
    /// drawn where they are captured (overlay thread).
    /// </summary>
    private void Attach(GraphicsDevices devices, int width, int height)
    {
        if (disposed || view is null || width != PixelWidth || height != PixelHeight || devices.State is not DeviceState.Ready)
        {
            using (NonPumpingLock.Enter(renderGate))
            {
                if (ReferenceEquals(requestedDevices, devices))
                {
                    requestedDevices = null;
                }
            }
            return;
        }
        try
        {
            DetachSurface();
            using (NonPumpingLock.Enter(renderGate))
            {
                ReleaseSurfaceInterop();
            }
            graphics = Annotator.Platform.Interop.CompositionInterop.CreateGraphicsDevice(compositor, devices.D3D11Device);
            surface = graphics.CreateDrawingSurface(new Windows.Foundation.Size(width, height), DirectXPixelFormat.B8G8R8A8UIntNormalized,
                                                    DirectXAlphaMode.Premultiplied);
            brush = compositor.CreateSurfaceBrush(surface);
            brush.Stretch = CompositionStretch.Fill;
            content.Brush = brush;
            nint interop = Annotator.Platform.Interop.CompositionInterop.DrawingSurfaceInterop(surface);
            using (NonPumpingLock.Enter(renderGate))
            {
                surfaceInterop = interop;
                drawnWork = null;
                surfaceDevices = devices;
                surfaceWidth = width;
                surfaceHeight = height;
                attachedGeneration = ++generation;
            }
            Volatile.Write(ref reportWanted, 1);
            RequestRender();
        }
        catch (Exception error) when (error is NativeCallException or InvalidOperationException or COMException or ArgumentException)
        {
            DetachSurface();
            failure = error;
        }
        Update();
    }

    private void OnPresented(long drawnGeneration)
    {
        if (disposed || drawnGeneration != attachedGeneration || view is null)
        {
            return;
        }
        bool first = !presented;
        presented = true;
        revealed = true;
        Update();
        if (first)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnRenderStopped(Exception? error)
    {
        if (disposed)
        {
            return;
        }
        if (error is not null)
        {
            failure = error;
        }
        else
        {
            // The capture device was lost: the surface on it goes, and the next frame on new devices makes another.
            DetachSurface();
        }
        Update();
    }

    private void Post(Windows.System.DispatcherQueueHandler work) => queue.TryEnqueue(work);

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
    /// Draws the newest frame with the newest view into the surface (a capture worker, the thread pool or the overlay thread).
    /// The first frame on a device asks the overlay thread for a surface on it; frames until then are not drawn.
    /// </summary>
    private void Render(ICaptureLease source)
    {
        var work = Volatile.Read(ref published);
        if (work is null || !ReferenceEquals(source, Volatile.Read(ref workerLease)) || !source.TryAcquireFrame(out var frame))
        {
            return;
        }
        using (frame)
        {
            if (!renderGate.TryEnter())
            {
                if (Environment.CurrentManagedThreadId == threadId)
                {
                    // A worker is drawing. The overlay thread never waits for it (a wait would run its dispatcher queue
                    // inside this work item, which ends the process); a worker draws the newest view right after.
                    RequestRender();
                    return;
                }
                NonPumpingLock.EnterWithoutPumping(renderGate);
            }
            try
            {
                if (workerDisposed || renderFailed)
                {
                    return;
                }
                work = Volatile.Read(ref published);
                if (work is null || frame.Width != work.View.Effects.Width || frame.Height != work.View.Effects.Height)
                {
                    return;
                }
                if (ReferenceEquals(work, drawnWork) && frame.ContentVersion == drawnContent && surfaceInterop != 0 && surfaceInterop == drawnSurface
                    && Volatile.Read(ref reportWanted) == 0)
                {
                    Statistics.Repeat();
                    return;
                }
                var start = Stopwatch.GetTimestamp();
                try
                {
                    if (pipeline is null || !ReferenceEquals(pipeline.Devices, frame.Devices))
                    {
                        pipeline?.Dispose();
                        pipeline = new EffectPipeline(frame.Devices);
                    }
                    if (surfaceInterop == 0 || !ReferenceEquals(surfaceDevices, frame.Devices) || surfaceWidth != work.Width || surfaceHeight != work.Height)
                    {
                        RequestSurface(frame.Devices, work.Width, work.Height);
                        return;
                    }
                    Guid textureIid = ID3D11Texture2D.IID_Guid;
                    Native.Check(Annotator.Platform.Interop.CompositionInterop.BeginDraw(surfaceInterop, textureIid, out nint target, out var offset),
                                 "ICompositionDrawingSurfaceInterop::BeginDraw(captured view)");
                    int ended;
                    try
                    {
                        pipeline.RenderView(frame.Texture, target, work.View.Effects,
                                            new PixelRect(offset.X, offset.Y, offset.X + work.Width, offset.Y + work.Height), work.View.Crop, regionsOnly: false);
                    }
                    finally
                    {
                        Marshal.Release(target);
                        ended = Annotator.Platform.Interop.CompositionInterop.EndDraw(surfaceInterop);
                    }
                    Native.Check(ended, "ICompositionDrawingSurfaceInterop::EndDraw(captured view)");
                    Statistics.Drawn(Stopwatch.GetElapsedTime(start));
                    drawnWork = work;
                    drawnContent = frame.ContentVersion;
                    drawnSurface = surfaceInterop;
                    if (Interlocked.Exchange(ref reportWanted, 0) == 1)
                    {
                        PostPresented(generation);
                    }
                }
                catch (NativeCallException error) when (DeviceLoss.IsDeviceLoss(error.HResult))
                {
                    ReleaseSurfaceInterop();
                    requestedDevices = null;
                    pipeline?.Dispose();
                    pipeline = null;
                    frame.Devices.ReportFailure(error.HResult);
                    Volatile.Write(ref reportWanted, 1);
                    Post(() => OnRenderStopped(null));
                }
                catch (Exception error) when (error is NativeCallException or InvalidOperationException or OutOfMemoryException or RenderFailure)
                {
                    renderFailed = true;
                    ReleaseSurfaceInterop();
                    pipeline?.Dispose();
                    pipeline = null;
                    Post(() => OnRenderStopped(error));
                }
            }
            finally
            {
                renderGate.Exit();
            }
        }
    }

    /// <summary>Asks the overlay thread, once per device and size, for a surface (under renderGate).</summary>
    private void RequestSurface(GraphicsDevices devices, int width, int height)
    {
        if (ReferenceEquals(requestedDevices, devices) && surfaceWidth == width && surfaceHeight == height)
        {
            return;
        }
        requestedDevices = devices;
        surfaceWidth = width;
        surfaceHeight = height;
        Post(() => Attach(devices, width, height));
    }

    /// <summary>Its own method, so the closure is made only when something is posted (see EffectsPresenter.PostPresented).</summary>
    private void PostPresented(long drawnGeneration) => Post(() => OnPresented(drawnGeneration));

    /// <summary>The newest view, its version and the surface's size, swapped as one immutable object.</summary>
    private sealed record Published(CapturedView View, int Version, int Width, int Height);

    /// <summary>Receives the lease's frames and states; frames draw at once, states go to the overlay thread.</summary>
    private sealed class Consumer(CapturedViewPresenter owner) : ICaptureConsumer
    {
        public void OnFrameAvailable(ICaptureLease lease) => owner.Render(lease);

        public void OnStateChanged(ICaptureLease lease, CaptureState state) => owner.Post(() => owner.OnCaptureState(lease, state));
    }
}