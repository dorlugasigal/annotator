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
using Annotator.Rendering.Interop;
using Windows.UI.Composition;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;
using SystemDispatcherQueue = Windows.System.DispatcherQueue;

namespace Annotator.Rendering.Effects;

/// <summary>Everything an <see cref="EffectsPresenter"/> needs from its host, on the overlay thread.</summary>
public sealed record EffectsPresenterOptions
{
    /// <summary>The compositor that owns <see cref="Root"/>.</summary>
    public required Compositor Compositor { get; init; }

    /// <summary>
    /// Where the effect layer goes: the overlay's root visual. The presenter adds its visual on top, so create it after
    /// the <see cref="Presentation.OverlayPresenter"/>: effects cover drawings, as on the Mac.
    /// </summary>
    public required ContainerVisual Root { get; init; }

    /// <summary>The capture service; the presenter holds a lease only while a region needs pixels.</summary>
    public required IDisplayCapture Capture { get; init; }

    /// <summary>The overlay's display.</summary>
    public required DisplayInfo Display { get; init; }

    /// <summary>Device pixels per point.</summary>
    public required double Scale { get; init; }

    /// <summary>The overlay's size in device pixels (the display's).</summary>
    public required int PixelWidth { get; init; }

    public required int PixelHeight { get; init; }
}

/// <summary>
/// Shows one overlay's Blur, Pixelate and Distort regions from the newest captured frame (the Mac's effects panel).
/// Every visible region has an opaque black composition rectangle underneath the effect layer, and the layer is shown
/// only while capture is live and has drawn a frame since, so a region is black until the first frame and whenever
/// capture or drawing fails, stalls or pauses, and never shows unfiltered or stale pixels (WIN-CAP-03). Effects are
/// drawn on the capture device by the capture worker that delivers each frame and presented through a composition swap
/// chain, so the overlay thread does no per-frame work.
/// </summary>
/// <remarks>
/// <para>Use it on the overlay thread that created it. Call <see cref="Show(ElementList)"/> with the document of every
/// frame; an unchanged effect list does nothing. Build the overlay's scene with
/// <c>pixelsAvailableForBlur: <see cref="PixelsAvailable"/></c> and build it again on
/// <see cref="PixelsAvailableChanged"/>, like the Mac canvas with <c>effectsReady</c>.</para>
/// <para>Without effect regions the presenter holds no capture lease and releases its swap chain, pipeline and scratch
/// textures: an overlay without effects costs nothing. As on the Mac, a region of strength zero needs capture too,
/// although nothing is drawn for it.</para>
/// </remarks>
public sealed unsafe class EffectsPresenter : IDisposable
{
    private const int StallFrames = 30;

    private readonly Compositor compositor;
    private readonly ContainerVisual root;
    private readonly ContainerVisual group;
    private readonly ContainerVisual fallback;
    private readonly SpriteVisual effect;
    private readonly CompositionColorBrush black;
    private readonly IDisplayCapture capture;
    private readonly DisplayInfo display;
    private readonly SystemDispatcherQueue queue;
    private readonly int threadId = Environment.CurrentManagedThreadId;
    private readonly Consumer consumer;
    private readonly List<SpriteVisual> rectangles = [];
    private readonly List<PixelRect> interest = [];

    // Overlay thread.
    private ICaptureLease? lease;
    private EffectList list = EffectList.Empty;
    private ElementList? elements;
    private Rect? viewport;
    private int version;
    private CaptureState captureState = CaptureState.Stopped.Instance;
    private EffectsState state = EffectsState.Idle.Instance;
    private Exception? failure;
    private bool pixelsAvailable;
    private ICompositionSurface? surface;
    private CompositionSurfaceBrush? brush;
    private long attachedGeneration = -1;
    private bool presented;

    // Shared with the render worker.
    private readonly Lock renderGate = new();
    private Published? published;
    private ICaptureLease? workerLease;
    private int renderRequested;
    private int reportWanted;

    // Render worker, under renderGate.
    private readonly List<PixelRect> current = [];
    private readonly List<PixelRect> drawnLast = [];
    private readonly List<PixelRect> drawnBefore = [];
    private readonly List<PixelRect> clear = [];
    private RECT[] dirty = new RECT[16];
    private EffectPipeline? pipeline;
    private IDXGISwapChain2* swapChain;
    private nint backBuffer;
    private HANDLE waitable;
    private long generation;
    private int presents;
    private int skippedInARow;

    // What the chain shows (under renderGate): the effect list and viewport, the captured content and the chain. A frame
    // that changes none of them is not drawn or presented again.
    private Published? drawnWork;
    private long drawnContent = -1;
    private long drawnGeneration = -1;
    private volatile bool renderFailed;
    private bool workerDisposed;

    private EffectsPresenter(EffectsPresenterOptions options, SystemDispatcherQueue queue)
    {
        compositor = options.Compositor;
        root = options.Root;
        capture = options.Capture;
        display = options.Display;
        Scale = options.Scale;
        PixelWidth = options.PixelWidth;
        PixelHeight = options.PixelHeight;
        this.queue = queue;
        consumer = new Consumer(this);
        group = compositor.CreateContainerVisual();
        fallback = compositor.CreateContainerVisual();
        effect = compositor.CreateSpriteVisual();
        black = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0));
    }

    /// <summary>Creates the presenter on the overlay thread and adds its visual on top of <see cref="EffectsPresenterOptions.Root"/>.</summary>
    public static EffectsPresenter Create(EffectsPresenterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Compositor);
        ArgumentNullException.ThrowIfNull(options.Root);
        ArgumentNullException.ThrowIfNull(options.Capture);
        ArgumentNullException.ThrowIfNull(options.Display);
        ValidateSize(options.Scale, options.PixelWidth, options.PixelHeight);
        var queue = SystemDispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("Create the effects presenter on a thread with a Windows.System dispatcher queue, such as a DispatcherThread.");
        var presenter = new EffectsPresenter(options, queue);
        presenter.effect.Size = new Vector2(options.PixelWidth, options.PixelHeight);
        presenter.effect.IsVisible = false;
        presenter.group.Children.InsertAtTop(presenter.fallback);
        presenter.group.Children.InsertAtTop(presenter.effect);
        presenter.root.Children.InsertAtTop(presenter.group);
        return presenter;
    }

    /// <summary>
    /// True while effect regions show live pixels. Build the scene with <c>pixelsAvailableForBlur</c> set to this, so
    /// the scene draws effect regions black whenever this layer cannot show them (the Mac's <c>effectsReady</c>).
    /// Regions of strength zero have no layer content: the scene draws them black until this is true and leaves them
    /// out after, so they show the screen unchanged, as on the Mac.
    /// </summary>
    public bool PixelsAvailable => pixelsAvailable;

    /// <summary>Raised on the overlay thread when <see cref="PixelsAvailable"/> changes. A handler must not throw.</summary>
    public event EventHandler? PixelsAvailableChanged;

    public EffectsState State => state;

    /// <summary>Raised on the overlay thread when <see cref="State"/> changes. A handler must not throw.</summary>
    public event EventHandler<EffectsStateChangedEventArgs>? StateChanged;

    /// <summary>The capture state behind <see cref="State"/>, for messages.</summary>
    public CaptureState CaptureState => captureState;

    public EffectsStatistics Statistics { get; } = new();

    public double Scale { get; private set; }

    public int PixelWidth { get; private set; }

    public int PixelHeight { get; private set; }

    /// <summary>The visuals the presenter added to the root.</summary>
    public ContainerVisual Visual => group;

    /// <summary>The effect list shown now.</summary>
    public EffectList Effects => list;

    /// <summary>
    /// Shows the effect regions and covers of a document's elements at the presenter's scale. As on the Mac's live
    /// effect view, image elements are layers of the scene the effects read, so a region over a pasted image shows it
    /// filtered.
    /// </summary>
    public void Show(ElementList elements)
    {
        VerifyAccess();
        this.elements = elements;
        EffectList next;
        try
        {
            next = EffectList.From(elements, Scale, PixelWidth, PixelHeight, includeImageLayers: true);
        }
        catch (ArgumentException error)
        {
            Fail(error);
            return;
        }
        Show(next);
    }

    /// <summary>Shows <paramref name="effects"/>. An equal list does nothing; a different one also ends a failure.</summary>
    public void Show(EffectList effects)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(effects);
        if (effects.Equals(list) && failure is null)
        {
            return;
        }
        failure = null;
        renderFailed = false;
        list = effects;
        version++;
        Volatile.Write(ref published, new Published(effects, version, viewport));
        UpdateFallback();
        if (effects.NeedsPixels)
        {
            EnsureLease();
            UpdateInterest();
            RequestRender();
        }
        else
        {
            ReleaseLease();
            ReleaseResources();
        }
        Update();
    }

    /// <summary>
    /// Shows the effect layer through screen zoom's viewport (Mac effectsView.present with the screen viewport):
    /// <paramref name="crop"/> is the part of the display that fills the overlay, in display pixels, or null without zoom. A
    /// changed crop is drawn at once with the newest frame (<paramref name="renderNow"/>), so the regions move with the
    /// drawings, and their opaque fallbacks move with them.
    /// </summary>
    public void SetViewport(Rect? crop, bool renderNow = true)
    {
        VerifyAccess();
        if (crop == viewport)
        {
            return;
        }
        viewport = crop;
        version++;
        Volatile.Write(ref published, new Published(list, version, crop));
        UpdateFallback();
        if (!list.NeedsPixels)
        {
            return;
        }
        if (renderNow && lease is { } source && captureState is CaptureState.Live)
        {
            Render(source);
        }
        else
        {
            RequestRender();
        }
    }

    /// <summary>After <see cref="EffectsState.Failed"/>, tries again with the same effects.</summary>
    public void Retry()
    {
        VerifyAccess();
        renderFailed = false;
        failure = null;
        if (list.NeedsPixels)
        {
            EnsureLease();
            UpdateInterest();
            RequestRender();
        }
        Update();
    }

    /// <summary>Follows a scale or size change of the overlay; the swap chain is created again at the new size.</summary>
    public void Resize(double scale, int pixelWidth, int pixelHeight)
    {
        VerifyAccess();
        ValidateSize(scale, pixelWidth, pixelHeight);
        if (pixelWidth != PixelWidth || pixelHeight != PixelHeight)
        {
            // Workers that start from now on find nothing to draw, so none makes resources at the old size again.
            Volatile.Write(ref published, new Published(EffectList.Empty, ++version, null));
            ReleaseResources();
            effect.Size = new Vector2(pixelWidth, pixelHeight);
        }
        Scale = scale;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        if (elements is { } last)
        {
            list = EffectList.Empty;
            Show(last);
        }
    }

    public void Dispose()
    {
        if (state is EffectsState.Disposed)
        {
            return;
        }
        if (Environment.CurrentManagedThreadId != threadId)
        {
            throw new InvalidOperationException("Dispose the effects presenter on the thread that created it.");
        }
        ReleaseLease();
        DetachSurface();
        using (NonPumpingLock.Enter(renderGate))
        {
            workerDisposed = true;
            ReleaseWorker();
        }
        root.Children.Remove(group);
        foreach (var rectangle in rectangles)
        {
            rectangle.Dispose();
        }
        rectangles.Clear();
        effect.Dispose();
        fallback.Dispose();
        group.Dispose();
        black.Dispose();
        pixelsAvailable = false;
        SetState(EffectsState.Disposed.Instance);
    }

    private static void ValidateSize(double scale, int width, int height)
    {
        if (!double.IsFinite(scale) || !(scale > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "The display scale must be finite and positive.");
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
    }

    private void VerifyAccess()
    {
        ObjectDisposedException.ThrowIf(state is EffectsState.Disposed, this);
        if (Environment.CurrentManagedThreadId != threadId)
        {
            throw new InvalidOperationException("Use the effects presenter on the thread that created it.");
        }
    }

    private void EnsureLease()
    {
        if (lease is not null)
        {
            return;
        }
        lease = capture.Acquire(display, consumer);
        Volatile.Write(ref workerLease, lease);
        captureState = lease.State;
        presented = false;
        Volatile.Write(ref reportWanted, 1);
    }

    /// <summary>
    /// Tells the lease which display pixels the layer reads, each visible region's filter footprint, so frames that change
    /// nothing there are not drawn (zoom's viewport only moves the result).
    /// </summary>
    private void UpdateInterest()
    {
        if (lease is null)
        {
            return;
        }
        interest.Clear();
        foreach (var region in list.Regions)
        {
            if (region.IsVisible)
            {
                interest.Add(EffectPipeline.Footprint(region, list));
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
        presented = false;
    }

    /// <summary>Drops the swap chain, pipeline and scratch textures; they are made again with the next frame.</summary>
    private void ReleaseResources()
    {
        // The composition surface goes first, so the flush in ReleaseWorker frees the swap chain at once.
        DetachSurface();
        using (NonPumpingLock.Enter(renderGate))
        {
            ReleaseWorker();
        }
    }

    private void DetachSurface()
    {
        effect.Brush = null;
        brush?.Dispose();
        (surface as IDisposable)?.Dispose();
        brush = null;
        surface = null;
        attachedGeneration = -1;
        presented = false;
    }

    /// <summary>One opaque black rectangle under the effect layer for every visible region.</summary>
    private void UpdateFallback()
    {
        var count = 0;
        foreach (var region in list.Regions)
        {
            if (!region.IsVisible)
            {
                continue;
            }
            if (count == rectangles.Count)
            {
                var visual = compositor.CreateSpriteVisual();
                visual.Brush = black;
                fallback.Children.InsertAtTop(visual);
                rectangles.Add(visual);
            }
            var rectangle = rectangles[count++];
            PixelRect shown = viewport is { } crop ? Through(region.Rect, crop) : region.Rect;
            rectangle.Offset = new Vector3(shown.Left, shown.Top, 0);
            rectangle.Size = new Vector2(shown.Width, shown.Height);
            rectangle.IsVisible = true;
        }
        for (var index = count; index < rectangles.Count; index++)
        {
            rectangles[index].IsVisible = false;
        }
    }

    /// <summary>A display rectangle as screen zoom shows it, rounded outward to whole overlay pixels.</summary>
    private PixelRect Through(PixelRect rect, Rect crop)
    {
        double scaleX = PixelWidth / crop.Width, scaleY = PixelHeight / crop.Height;
        int left = (int)Math.Floor((rect.Left - crop.X) * scaleX), top = (int)Math.Floor((rect.Top - crop.Y) * scaleY);
        int right = (int)Math.Ceiling((rect.Right - crop.X) * scaleX), bottom = (int)Math.Ceiling((rect.Bottom - crop.Y) * scaleY);
        return new PixelRect(left, top, right, bottom);
    }

    /// <summary>Shows the effect layer only while capture is live and a frame was drawn since; updates the state.</summary>
    private void Update()
    {
        var live = failure is null && list.NeedsPixels && captureState is CaptureState.Live && presented && brush is not null;
        effect.IsVisible = live;
        EffectsState next;
        if (failure is { } error)
        {
            next = new EffectsState.Failed(error);
        }
        else if (!list.NeedsPixels)
        {
            next = EffectsState.Idle.Instance;
        }
        else if (live)
        {
            next = EffectsState.Live.Instance;
        }
        else if (captureState is CaptureState.Starting or CaptureState.Live or CaptureState.Stopped)
        {
            next = EffectsState.Waiting.Instance;
        }
        else
        {
            next = new EffectsState.Paused(captureState);
        }
        SetState(next);
        if (pixelsAvailable != live)
        {
            pixelsAvailable = live;
            PixelsAvailableChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetState(EffectsState next)
    {
        var previous = state;
        if (previous.Equals(next))
        {
            return;
        }
        state = next;
        StateChanged?.Invoke(this, new EffectsStateChangedEventArgs(previous, next));
    }

    private void Fail(Exception error)
    {
        failure = error;
        Update();
    }

    private void OnCaptureState(ICaptureLease from, CaptureState next)
    {
        if (state is EffectsState.Disposed || !ReferenceEquals(from, lease))
        {
            return;
        }
        captureState = next;
        // A frame drawn before the pause is stale: wait for a new one.
        presented = false;
        if (next is CaptureState.Live)
        {
            Volatile.Write(ref reportWanted, 1);
            RequestRender();
        }
        Update();
    }

    private void OnAttach(nint chain, long chainGeneration)
    {
        try
        {
            // A chain the worker already replaced or released is never shown.
            if (state is EffectsState.Disposed || !list.NeedsPixels || chainGeneration != Interlocked.Read(ref generation))
            {
                return;
            }
            var created = Annotator.Platform.Interop.CompositionInterop.CreateCompositionSurfaceForSwapChain(compositor, chain);
            var createdBrush = compositor.CreateSurfaceBrush(created);
            createdBrush.Stretch = CompositionStretch.Fill;
            DetachSurface();
            surface = created;
            brush = createdBrush;
            effect.Brush = createdBrush;
            attachedGeneration = chainGeneration;
            Volatile.Write(ref reportWanted, 1);
            RequestRender();
        }
        catch (Exception error) when (error is NativeCallException or InvalidOperationException or COMException)
        {
            Fail(error);
        }
        finally
        {
            Marshal.Release(chain);
        }
        Update();
    }

    private void OnPresented(long chainGeneration)
    {
        if (state is EffectsState.Disposed || chainGeneration != attachedGeneration || captureState is not CaptureState.Live)
        {
            return;
        }
        presented = true;
        Update();
    }

    private void OnRenderStopped(Exception? error)
    {
        if (state is EffectsState.Disposed)
        {
            return;
        }
        presented = false;
        if (error is not null)
        {
            failure = error;
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
    /// Draws the newest frame with the newest effect list and presents it (a capture worker or the thread pool). Draws
    /// that find no free swap-chain buffer are skipped; the next frame draws again. A frame whose pixels equal what the
    /// chain already shows, with the same effects, is not drawn or presented (<see cref="CaptureFrame.ContentVersion"/>).
    /// </summary>
    private void Render(ICaptureLease source)
    {
        var work = Volatile.Read(ref published);
        // Frames of a released lease are never drawn.
        if (work is null || !work.List.NeedsPixels || !ReferenceEquals(source, Volatile.Read(ref workerLease)) || !source.TryAcquireFrame(out var frame))
        {
            return;
        }
        using (frame)
        {
            NonPumpingLock.Held held;
            if (Environment.CurrentManagedThreadId != threadId)
            {
                held = NonPumpingLock.Enter(renderGate);
            }
            else if (!NonPumpingLock.TryEnter(renderGate, out held))
            {
                // A worker is drawing. The overlay thread never waits for it (see CapturedViewPresenter.Render); a worker
                // draws the newest effects right after.
                RequestRender();
                return;
            }
            using (held)
            {
                if (workerDisposed || renderFailed)
                {
                    return;
                }
                work = Volatile.Read(ref published)!;
                var effects = work.List;
                if (!effects.NeedsPixels || frame.Width != effects.Width || frame.Height != effects.Height)
                {
                    return;
                }
                if (ReferenceEquals(work, drawnWork) && frame.ContentVersion == drawnContent && Interlocked.Read(ref generation) == drawnGeneration
                    && Volatile.Read(ref reportWanted) == 0)
                {
                    Statistics.Repeat();
                    return;
                }
                var start = Stopwatch.GetTimestamp();
                try
                {
                    if (pipeline is null || swapChain == null || !ReferenceEquals(pipeline.Devices, frame.Devices))
                    {
                        Recreate(frame.Devices, effects.Width, effects.Height);
                    }
                    if (presents >= 2 && drawnLast.Count == 0 && drawnBefore.Count == 0 && !effects.HasVisibleRegions)
                    {
                        // Only regions of strength zero: both buffers are clear and stay so. No frame is presented,
                        // but the frame still tells the overlay that capture delivers, so the screen may show there.
                        ReportPresented();
                        return;
                    }
                    if (PInvoke.WaitForSingleObject(waitable, 0) != (WAIT_EVENT)0)
                    {
                        Statistics.Skip();
                        if (++skippedInARow == StallFrames)
                        {
                            // The display takes no frames: what is shown is getting old, so show the fallback until
                            // a later frame is presented and reported.
                            Volatile.Write(ref reportWanted, 1);
                            Post(() => OnRenderStopped(null));
                        }
                        return;
                    }
                    skippedInARow = 0;
                    using (pipeline!.LockContext())
                    {
                        if (work.Crop is { } crop)
                        {
                            DrawThrough(frame.Texture, effects, crop);
                        }
                        else
                        {
                            Draw(frame.Texture, effects);
                        }
                    }
                    Statistics.Drawn(Stopwatch.GetElapsedTime(start));
                    drawnWork = work;
                    drawnContent = frame.ContentVersion;
                    drawnGeneration = Interlocked.Read(ref generation);
                    ReportPresented();
                }
                catch (NativeCallException error) when (DeviceLoss.IsDeviceLoss(error.HResult))
                {
                    // The capture device is gone: the capture service replaces it and the next frame starts over.
                    ReleaseWorker();
                    frame.Devices.ReportFailure(error.HResult);
                    Post(() => OnRenderStopped(null));
                }
                catch (Exception error) when (error is NativeCallException or InvalidOperationException or OutOfMemoryException or RenderFailure)
                {
                    renderFailed = true;
                    ReleaseWorker();
                    Post(() => OnRenderStopped(error));
                }
            }
        }
    }

    /// <summary>Tells the overlay thread, once per request, that the current chain shows a frame of live capture.</summary>
    private void ReportPresented()
    {
        if (Interlocked.Exchange(ref reportWanted, 0) == 1)
        {
            PostPresented(generation);
        }
    }

    /// <summary>
    /// Its own method: inside <see cref="ReportPresented"/>, the compiler allocated the lambda's closure on every call,
    /// also when nothing was posted (32 bytes per effect frame, measured).
    /// </summary>
    private void PostPresented(long drawnGeneration) => Post(() => OnPresented(drawnGeneration));

    /// <summary>Clears what the back buffer drew two presents ago, draws the effects and presents the changed area.</summary>
    private void Draw(nint texture, EffectList effects)
    {
        current.Clear();
        foreach (var region in effects.Regions)
        {
            if (region.IsVisible)
            {
                current.Add(region.Rect);
            }
        }
        clear.Clear();
        var full = presents < 2;
        if (full)
        {
            clear.Add(new PixelRect(0, 0, effects.Width, effects.Height));
        }
        else
        {
            // A flip-model back buffer holds the frame from two presents ago.
            clear.AddRange(drawnBefore);
            clear.AddRange(drawnLast);
        }
        pipeline!.Render(texture, backBuffer, effects, CollectionsMarshal.AsSpan(clear));

        var count = 0;
        if (!full)
        {
            if (dirty.Length < current.Count + drawnLast.Count)
            {
                dirty = new RECT[current.Count + drawnLast.Count];
            }
            foreach (var rect in current)
            {
                dirty[count++] = new RECT { left = rect.Left, top = rect.Top, right = rect.Right, bottom = rect.Bottom };
            }
            foreach (var rect in drawnLast)
            {
                dirty[count++] = new RECT { left = rect.Left, top = rect.Top, right = rect.Right, bottom = rect.Bottom };
            }
        }
        HRESULT result;
        fixed (RECT* rects = dirty)
        {
            var parameters = new DXGI_PRESENT_PARAMETERS { DirtyRectsCount = (uint)count, pDirtyRects = count == 0 ? null : rects };
            result = swapChain->Present1(0, 0, &parameters);
        }
        Native.Check(result, "IDXGISwapChain1::Present1(effects)");
        presents++;
        drawnBefore.Clear();
        drawnBefore.AddRange(drawnLast);
        drawnLast.Clear();
        drawnLast.AddRange(current);
    }

    /// <summary>
    /// Under screen zoom every pixel can change with the viewport: the whole layer is drawn through it and presented. The next
    /// draw without zoom clears both buffers.
    /// </summary>
    private void DrawThrough(nint texture, EffectList effects, Rect crop)
    {
        pipeline!.RenderView(texture, backBuffer, effects, new PixelRect(0, 0, effects.Width, effects.Height), crop, regionsOnly: true);
        var parameters = default(DXGI_PRESENT_PARAMETERS);
        Native.Check(swapChain->Present1(0, 0, &parameters), "IDXGISwapChain1::Present1(effects through zoom)");
        presents = 0;
        drawnBefore.Clear();
        drawnLast.Clear();
    }

    /// <summary>A new pipeline and composition swap chain on the capture device; the overlay thread attaches the chain.</summary>
    private void Recreate(GraphicsDevices devices, int width, int height)
    {
        ReleaseWorker();
        pipeline = new EffectPipeline(devices);
        swapChain = CreateSwapChain(devices, width, height);
        Native.Check(swapChain->SetMaximumFrameLatency(1), "IDXGISwapChain2::SetMaximumFrameLatency(effects)");
        waitable = swapChain->GetFrameLatencyWaitableObject();
        if (waitable == HANDLE.Null)
        {
            throw new NativeCallException("IDXGISwapChain2::GetFrameLatencyWaitableObject(effects)", unchecked((int)0x80004005));
        }
        Guid textureIid = ID3D11Texture2D.IID_Guid;
        void* buffer;
        Native.Check(swapChain->GetBuffer(0, &textureIid, &buffer), "IDXGISwapChain::GetBuffer(effects)");
        backBuffer = (nint)buffer;
        presents = 0;
        drawnLast.Clear();
        drawnBefore.Clear();
        Interlocked.Increment(ref generation);
        var chain = (nint)swapChain;
        var chainGeneration = generation;
        Marshal.AddRef(chain);
        if (!queue.TryEnqueue(() => OnAttach(chain, chainGeneration)))
        {
            Marshal.Release(chain);
        }
    }

    private void ReleaseWorker()
    {
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
        // Last: disposing the pipeline flushes the device context, which frees the swap chain released above too.
        pipeline?.Dispose();
        pipeline = null;
        Interlocked.Increment(ref generation);
    }

    private static IDXGISwapChain2* CreateSwapChain(GraphicsDevices devices, int width, int height)
    {
        var dxgiDevice = (IDXGIDevice*)devices.DxgiDevice;
        IDXGIAdapter* adapter = null;
        IDXGIFactory2* factory = null;
        IDXGISwapChain1* created = null;
        try
        {
            Native.Check(dxgiDevice->GetAdapter(&adapter), "IDXGIDevice::GetAdapter(effects)");
            Guid factoryIid = IDXGIFactory2.IID_Guid;
            void* parent;
            Native.Check(adapter->GetParent(&factoryIid, &parent), "IDXGIAdapter::GetParent(IDXGIFactory2)(effects)");
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
                AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_PREMULTIPLIED,
                Flags = DXGI_SWAP_CHAIN_FLAG.DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT,
            };
            Native.Check(factory->CreateSwapChainForComposition((IUnknown*)devices.D3D11Device, &description, null, &created),
                         "IDXGIFactory2::CreateSwapChainForComposition(effects)");
            return Native.Query<IDXGISwapChain2>(created, IDXGISwapChain2.IID_Guid, "QueryInterface(IDXGISwapChain2)(effects)");
        }
        finally
        {
            Native.Release(ref created);
            Native.Release(ref factory);
            Native.Release(ref adapter);
        }
    }

    /// <summary>The newest effect list, its version and screen zoom's crop (display pixels), swapped as one immutable object.</summary>
    private sealed record Published(EffectList List, int Version, Rect? Crop);

    /// <summary>Receives the lease's frames and states; frames draw at once, states go to the overlay thread.</summary>
    private sealed class Consumer(EffectsPresenter owner) : ICaptureConsumer
    {
        public void OnFrameAvailable(ICaptureLease lease) => owner.Render(lease);

        public void OnStateChanged(ICaptureLease lease, CaptureState state) => owner.Post(() => owner.OnCaptureState(lease, state));
    }
}
