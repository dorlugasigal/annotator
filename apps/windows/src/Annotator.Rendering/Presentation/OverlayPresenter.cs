using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Platform;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Drawing;
using Windows.UI.Composition;
using SystemDispatcherQueue = Windows.System.DispatcherQueue;

namespace Annotator.Rendering.Presentation;

/// <summary>
/// Presents one overlay's content: a committed-scene layer (element lists and the selection) and, above it, a live
/// layer for the gesture in progress. Both are in device pixels at the display scale, so nothing is stretched at any
/// scale (WIN-REND-06). Each update redraws only the damaged rectangles: lists that appeared, disappeared or moved,
/// and for a growing stroke only its new segments. Nothing runs while nothing changes: no timers, no polling and no
/// presents. Use it on the overlay thread that created it (a thread with a Windows.System dispatcher queue).
/// </summary>
/// <remarks>
/// <para>Typical use from an overlay canvas: <see cref="Show"/> with the committed scene and the live gesture for every
/// frame the app thread sends, <see cref="ShowLive"/> for each pointer sample, <see cref="ReplaceDevices"/> when the
/// host replaces the device bundle (or has none), <see cref="ReleaseIdleResources"/> when the overlay leaves Draw or
/// hides, and <see cref="Dispose"/> with the overlay.</para>
/// <para>Failures are typed states (<see cref="State"/>, <see cref="StateChanged"/>). Outside
/// <see cref="PresenterState.Ready"/>, every cover and pixel-effect region is shown by opaque composition color
/// visuals that need no GPU device of Annotator's, so a lost device never reveals what a cover hides. New devices bring
/// a full redraw.</para>
/// <para>Surface layers updated in one dispatcher work item reach the screen in the same composition frame. A
/// swap-chain layer presents on its own and never without a free frame: an update that finds none waits, without
/// blocking, for the next one and is reported through <see cref="FrameStalled"/> when that takes too long.</para>
/// </remarks>
public sealed unsafe class OverlayPresenter : IDisposable
{
    private readonly Compositor compositor;
    private readonly ContainerVisual root;
    private readonly ContainerVisual group;
    private readonly ContainerVisual fallback;
    private readonly ISystemColorResolver colors;
    private readonly RendererOptions rendererOptions;
    private readonly LayerKind sceneKind;
    private readonly LayerKind liveKind;
    private readonly TimeSpan stallThreshold;
    private readonly SystemDispatcherQueue queue;
    private readonly int threadId = Environment.CurrentManagedThreadId;
    private readonly LayerState scene;
    private readonly LayerState live;
    private readonly Dictionary<DisplayList, int> previousIndexes = new(ReferenceEqualityComparer.Instance);
    private readonly List<DisplayList> added = [];
    private readonly List<DisplayList> removed = [];
    private readonly Func<OverlayPresenter, bool, PresentationLayer>? layerFactory;
    private readonly Action<PresentationLayer> frameReady;
    private GraphicsDevices? devices;
    private DisplayListRenderer? renderer;
    private CompositionGraphicsDevice? graphics;
    private PresentationLayer? sceneLayer;
    private PresentationLayer? liveLayer;
    private PresenterState state = new PresenterState.Ready();
    private bool fallbackShown;
    private bool liveHidden;
    private double viewZoom = 1;
    private double viewX;
    private double viewY;

    private OverlayPresenter(OverlayPresenterOptions options, SystemDispatcherQueue queue)
    {
        compositor = options.Compositor;
        root = options.Root;
        colors = options.Colors ?? FixedSystemColors.Light;
        rendererOptions = options.Renderer ?? new RendererOptions();
        sceneKind = options.SceneLayer;
        liveKind = options.LiveLayer;
        stallThreshold = options.FrameStallThreshold;
        Scale = options.Scale;
        PixelWidth = options.PixelWidth;
        PixelHeight = options.PixelHeight;
        this.queue = queue;
        layerFactory = options.LayerFactory;
        frameReady = FrameReady;
        scene = new LayerState(this, live: false);
        live = new LayerState(this, live: true);
        group = compositor.CreateContainerVisual();
        fallback = compositor.CreateContainerVisual();
    }

    /// <summary>
    /// Creates the presenter and its visuals on top of <see cref="OverlayPresenterOptions.Root"/>, on the overlay
    /// thread. Invalid options throw. Device problems never do: without devices the presenter starts in
    /// <see cref="PresenterState.DevicesUnavailable"/>, and a bundle that fails starts it in
    /// <see cref="PresenterState.DevicesLost"/> or <see cref="PresenterState.Failed"/>.
    /// </summary>
    public static OverlayPresenter Create(OverlayPresenterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Compositor);
        ArgumentNullException.ThrowIfNull(options.Root);
        ValidateSize(options.Scale, options.PixelWidth, options.PixelHeight);
        if (!Enum.IsDefined(options.SceneLayer) || !Enum.IsDefined(options.LiveLayer))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Unknown layer kind.");
        }
        if (options.FrameStallThreshold <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.FrameStallThreshold, "The frame stall threshold must be positive.");
        }
        var queue = SystemDispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("Create the overlay presenter on a thread with a Windows.System dispatcher queue, such as a DispatcherThread.");
        var presenter = new OverlayPresenter(options, queue);
        try
        {
            presenter.root.Children.InsertAtTop(presenter.group);
            presenter.group.Children.InsertAtTop(presenter.fallback);
            if (options.Devices is { } devices)
            {
                presenter.TryAttach(devices);
            }
            else
            {
                presenter.state = new PresenterState.DevicesUnavailable();
            }
            return presenter;
        }
        catch
        {
            presenter.Dispose();
            throw;
        }
    }

    public PresenterState State => state;

    /// <summary>Raised on the overlay thread when <see cref="State"/> changes. A handler must not throw.</summary>
    public event EventHandler<PresenterStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Raised on the overlay thread, once per stall, when an update finds a swap-chain layer still without a free frame
    /// after <see cref="OverlayPresenterOptions.FrameStallThreshold"/>. A handler must not throw.
    /// </summary>
    public event EventHandler<FrameStallEventArgs>? FrameStalled;

    public PresenterStatistics Statistics { get; } = new();

    public double Scale { get; private set; }

    public int PixelWidth { get; private set; }

    public int PixelHeight { get; private set; }

    public LayerKind SceneLayerKind => sceneKind;

    public LayerKind LiveLayerKind => liveKind;

    /// <summary>The renderer of the current devices, for its counters and image cache; null without devices.</summary>
    public DisplayListRenderer? Renderer => renderer;

    /// <summary>The device bundle in use, or null.</summary>
    public GraphicsDevices? Devices => devices;

    internal Compositor Compositor => compositor;

    /// <summary>The scene and live content now shown (or kept for redrawing after a device change).</summary>
    public SceneSnapshot Scene => scene.Snapshot;

    public SceneSnapshot Live => live.Snapshot;

    /// <summary>The visuals the presenter added to the overlay root.</summary>
    public ContainerVisual Visual => group;

    /// <summary>The zoom of the view; 1 without screen zoom.</summary>
    public double ViewZoom => viewZoom;

    /// <summary>The scene point drawn at the overlay's top-left corner; the origin without screen zoom.</summary>
    public Point ViewOrigin => new(viewX, viewY);

    /// <summary>
    /// Shows the content through screen zoom's viewport, as the Mac canvas draws under its screen view: a scene point
    /// <c>p</c> is drawn at <c>(p - origin) * zoom</c> points, so the drawings, the selection and their covers magnify with
    /// the zoomed screen under them. Zoom 1 at the origin is the overlay without zoom. A change redraws what is shown at
    /// once, in the same composition commit as the caller's other changes.
    /// </summary>
    public void SetView(double zoom, double originX, double originY)
    {
        VerifyAccess();
        if (!double.IsFinite(zoom) || !(zoom > 0) || !double.IsFinite(originX) || !double.IsFinite(originY))
        {
            throw new ArgumentOutOfRangeException(nameof(zoom), zoom, "The view needs a finite, positive zoom and a finite origin.");
        }
        if (zoom == viewZoom && originX == viewX && originY == viewY)
        {
            return;
        }
        var ready = state is PresenterState.Ready;
        DropHiddenLive(draw: ready);
        if (ready)
        {
            // What was drawn through the old view goes; then everything is drawn through the new one.
            DamageContent(scene);
            DamageContent(live);
        }
        viewZoom = zoom;
        viewX = originX;
        viewY = originY;
        if (!ready)
        {
            if (fallbackShown)
            {
                ShowFallback();
            }
            return;
        }
        DamageContent(scene);
        DamageContent(live);
        Statistics.ViewRedraws++;
        Flush(scene);
        FlushLive();
        if (state is not PresenterState.Ready)
        {
            ShowFallback();
        }
    }

    /// <summary>Shows a new committed scene. Unchanged lists keep their pixels.</summary>
    public void ShowScene(SceneSnapshot snapshot) => Show(snapshot, null);

    /// <summary>Shows the gesture in progress (null or <see cref="SceneSnapshot.Empty"/> for none).</summary>
    public void ShowLive(SceneSnapshot? snapshot)
    {
        VerifyAccess();
        Update(live, snapshot ?? SceneSnapshot.Empty);
        FlushLive();
        if (state is not PresenterState.Ready)
        {
            ShowFallback();
        }
    }

    /// <summary>
    /// Shows a committed scene and, unless <paramref name="liveSnapshot"/> is null, the live content in one step. When
    /// a stroke moves from the live layer to the scene, it appears in the scene in the same composition frame as it
    /// leaves the live layer, never twice and never missing: surface layers commit together, and a swap-chain live layer
    /// is hidden through a composition property in that same commit (its pixels are cleared with the next live content).
    /// </summary>
    public void Show(SceneSnapshot snapshot, SceneSnapshot? liveSnapshot)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(snapshot);
        var sceneChanged = !ReferenceEquals(scene.Snapshot, snapshot);
        Update(scene, snapshot);
        var hideLive = liveSnapshot is not null && liveSnapshot.Elements.IsEmpty && sceneChanged && !liveHidden
            && liveLayer is SwapChainLayer && !live.Snapshot.Elements.IsEmpty && state is PresenterState.Ready;
        if (hideLive)
        {
            liveLayer!.Visual.IsVisible = false;
            liveHidden = true;
        }
        else if (liveSnapshot is not null)
        {
            Update(live, liveSnapshot);
        }
        Flush(scene);
        if (liveSnapshot is not null && !hideLive)
        {
            FlushLive();
        }
        if (state is not PresenterState.Ready)
        {
            ShowFallback();
        }
    }

    /// <summary>
    /// Redraws everything that is shown, for example after the theme or accent color changed. Areas without content
    /// stay untouched (and unallocated on virtual surfaces).
    /// </summary>
    public void Invalidate()
    {
        VerifyAccess();
        if (state is not PresenterState.Ready)
        {
            return;
        }
        DamageContent(scene);
        DamageContent(live);
        Statistics.FullRedraws++;
        Flush(scene);
        FlushLive();
    }

    /// <summary>
    /// Redraws what a new theme or accent color changes: only the lists that paint with system colors (the selection,
    /// shape label backgrounds, image fallbacks, accent hints), because every other list draws the same pixels in every
    /// theme. A board of 1,000 drawings with a 200,000-sample stroke would otherwise be drawn again in full.
    /// </summary>
    public void RedrawSystemColors()
    {
        VerifyAccess();
        if (state is not PresenterState.Ready)
        {
            return;
        }
        DamageSystemColors(scene);
        DamageSystemColors(live);
        Statistics.ThemeRedraws++;
        Flush(scene);
        FlushLive();
    }

    /// <summary>
    /// Releases what an idle overlay does not need: a swap-chain live layer (two display-sized buffers), recreated with
    /// the next live content, and the pooled memory of surfaces released while drawing (emptied virtual surfaces are
    /// released at once and their tiles reused by later gestures). Call it when the overlay leaves Draw or hides.
    /// </summary>
    public void ReleaseIdleResources()
    {
        VerifyAccess();
        DropHiddenLive(draw: false);
        if (live.Snapshot.Elements.IsEmpty && liveLayer is SwapChainLayer && layerFactory is null)
        {
            ReleaseLayer(live);
            live.Damage.Clear();
        }
        TrimPool();
    }

    /// <summary>Follows a display scale or size change: new layers at the new size, then a redraw of the content.</summary>
    public void Resize(double scale, int pixelWidth, int pixelHeight)
    {
        VerifyAccess();
        ValidateSize(scale, pixelWidth, pixelHeight);
        var sized = pixelWidth != PixelWidth || pixelHeight != PixelHeight;
        var ready = state is PresenterState.Ready;
        DropHiddenLive(draw: ready && !sized);
        if (ready && !sized)
        {
            // Old pixels at the old scale must go: clear what was drawn, then draw at the new scale.
            DamageContent(scene);
            DamageContent(live);
        }
        Scale = scale;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        if (!ready)
        {
            return;
        }
        if (sized)
        {
            DisposeLayers();
            try
            {
                CreateLayers();
            }
            catch (NativeCallException error) when (DeviceLoss.IsDeviceLoss(error.HResult))
            {
                Lose(error.HResult);
                return;
            }
            catch (Exception error) when (error is NativeCallException or InvalidOperationException or OutOfMemoryException)
            {
                Fail(error);
                return;
            }
        }
        Invalidate();
    }

    /// <summary>
    /// Continues on another device bundle: after <see cref="PresenterState.DevicesLost"/>, on a new adapter, or with
    /// null when the host has no bundle (<see cref="PresenterState.DevicesUnavailable"/>). Recreates every device
    /// resource and surface and redraws everything. The bundle stays the host's to dispose. This is
    /// <c>ICanvasPresenter.DevicesChanged</c> for an overlay canvas.
    /// </summary>
    public void ReplaceDevices(GraphicsDevices? replacement)
    {
        VerifyAccess();
        DropHiddenLive(draw: false);
        ReleaseDeviceResources();
        scene.Damage.Clear();
        live.Damage.Clear();
        if (replacement is null)
        {
            SetState(new PresenterState.DevicesUnavailable());
            ShowFallback();
            return;
        }
        if (!TryAttach(replacement))
        {
            return;
        }
        SetState(new PresenterState.Ready());
        HideFallback();
        Statistics.FullRedraws++;
        Flush(scene);
        FlushLive();
        if (state is not PresenterState.Ready)
        {
            ShowFallback();
        }
    }

    /// <summary>After <see cref="PresenterState.Failed"/> or <see cref="PresenterState.DevicesLost"/>, tries again on the same devices.</summary>
    public void Retry() => ReplaceDevices(devices);

    public void Dispose()
    {
        if (state is PresenterState.Disposed)
        {
            return;
        }
        if (Environment.CurrentManagedThreadId != threadId)
        {
            throw new InvalidOperationException("Dispose the overlay presenter on the thread that created it.");
        }
        ReleaseDeviceResources();
        HideFallback();
        root.Children.Remove(group);
        group.Dispose();
        fallback.Dispose();
        SetState(new PresenterState.Disposed());
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
        ObjectDisposedException.ThrowIf(state is PresenterState.Disposed, this);
        if (Environment.CurrentManagedThreadId != threadId)
        {
            throw new InvalidOperationException("Use the overlay presenter on the thread that created it.");
        }
    }

    /// <summary>
    /// Attaches <paramref name="bundle"/>; on failure releases what was made and moves to
    /// <see cref="PresenterState.DevicesLost"/> or <see cref="PresenterState.Failed"/> with covers shown.
    /// </summary>
    private bool TryAttach(GraphicsDevices bundle)
    {
        devices = bundle;
        if (bundle.State is DeviceState.Lost lost)
        {
            Lose(lost.HResult, lost);
            return false;
        }
        try
        {
            Attach(bundle);
        }
        catch (NativeCallException error) when (DeviceLoss.IsDeviceLoss(error.HResult))
        {
            ReleaseDeviceResources();
            Lose(error.HResult);
            return false;
        }
        catch (Exception error) when (error is NativeCallException or InvalidOperationException or ObjectDisposedException or OutOfMemoryException)
        {
            ReleaseDeviceResources();
            Fail(error);
            return false;
        }
        if (bundle.State is DeviceState.Lost lostMeanwhile)
        {
            ReleaseDeviceResources();
            Lose(lostMeanwhile.HResult, lostMeanwhile);
            return false;
        }
        return true;
    }

    /// <summary>Creates the renderer and layers for <paramref name="bundle"/> and enters every shown list.</summary>
    private void Attach(GraphicsDevices bundle)
    {
        renderer = new DisplayListRenderer(bundle, colors, rendererOptions);
        foreach (var list in scene.Snapshot.Elements)
        {
            renderer.Enter(list);
        }
        renderer.Enter(scene.Snapshot.Selection);
        foreach (var list in live.Snapshot.Elements)
        {
            renderer.Enter(list);
        }
        renderer.CollectImages();
        bundle.DeviceLost += OnDeviceLost;
        CreateLayers();
    }

    /// <summary>Drops the layers, the composition graphics device and the renderer of the current bundle.</summary>
    private void ReleaseDeviceResources()
    {
        if (devices is not null)
        {
            devices.DeviceLost -= OnDeviceLost;
        }
        DisposeLayers();
        renderer?.Dispose();
        renderer = null;
    }

    /// <summary>
    /// Builds the layers that exist from the start: drawing surfaces, a swap-chain scene and test layers. Virtual
    /// surfaces and a swap-chain live layer are created with their first content (see <see cref="CreatedOnDemand"/>).
    /// </summary>
    private void CreateLayers()
    {
        if (!CreatedOnDemand(isLive: false) || !scene.ShowsNothing)
        {
            EnsureLayer(scene);
        }
        if (!CreatedOnDemand(isLive: true) || !live.ShowsNothing)
        {
            EnsureLayer(live);
        }
    }

    /// <summary>
    /// Virtual surfaces exist only while their layer has content: an emptied one is released (trimming a surface keeps
    /// its tile memory, measured; see the README), its tiles go back to the graphics device's pool for the next gesture,
    /// and creating one costs about 0.05 ms. A swap-chain live layer is created with the first live content and
    /// released by <see cref="ReleaseIdleResources"/>.
    /// </summary>
    private bool CreatedOnDemand(bool isLive) =>
        layerFactory is null && (isLive ? liveKind : sceneKind) is var kind
        && (kind == LayerKind.VirtualSurface || (isLive && kind == LayerKind.SwapChain));

    private PresentationLayer EnsureLayer(LayerState layer)
    {
        if ((layer.IsLive ? liveLayer : sceneLayer) is { } existing)
        {
            return existing;
        }
        var created = MakeLayer(layer.IsLive);
        if (layer.IsLive)
        {
            liveLayer = created;
            if (sceneLayer is not null)
            {
                group.Children.InsertAbove(created.Visual, sceneLayer.Visual);
            }
            else
            {
                group.Children.InsertAtBottom(created.Visual);
            }
        }
        else
        {
            sceneLayer = created;
            group.Children.InsertAtBottom(created.Visual);
        }
        StartLayer(layer, created);
        return created;
    }

    /// <summary>A new virtual surface reads as transparent, so only content is drawn; other kinds start with a full clear.</summary>
    private void StartLayer(LayerState layer, PresentationLayer target)
    {
        if (target.Kind == LayerKind.VirtualSurface)
        {
            DamageContent(layer);
        }
        else
        {
            layer.Damage.AddFull(PixelWidth, PixelHeight);
        }
    }

    private PresentationLayer MakeLayer(bool isLive)
    {
        if (layerFactory is not null)
        {
            return layerFactory(this, isLive);
        }
        var kind = isLive ? liveKind : sceneKind;
        if (kind == LayerKind.SwapChain)
        {
            return new SwapChainLayer(compositor, devices!, PixelWidth, PixelHeight, queue, frameReady);
        }
        graphics ??= Annotator.Platform.Interop.CompositionInterop.CreateGraphicsDevice(compositor, devices!.D2DDevice);
        return new DrawingSurfaceLayer(compositor, graphics, PixelWidth, PixelHeight, isVirtual: kind == LayerKind.VirtualSurface);
    }

    /// <summary>Removes and releases one layer (a swap chain also ends its frame wait).</summary>
    private void ReleaseLayer(LayerState layer)
    {
        var target = layer.IsLive ? liveLayer : sceneLayer;
        if (target is null)
        {
            return;
        }
        group.Children.Remove(target.Visual);
        target.Dispose();
        if (layer.IsLive)
        {
            liveLayer = null;
            liveHidden = false;
        }
        else
        {
            sceneLayer = null;
        }
        layer.ResetWaiting();
    }

    /// <summary>Removes and releases both layers and the composition graphics device.</summary>
    private void DisposeLayers()
    {
        ReleaseLayer(live);
        ReleaseLayer(scene);
        graphics?.Dispose();
        graphics = null;
    }

    /// <summary>
    /// Forgets live content that was hidden when it moved to the scene; with <paramref name="draw"/>, also clears its
    /// pixels from the (still hidden) live layer.
    /// </summary>
    private void DropHiddenLive(bool draw)
    {
        if (!liveHidden)
        {
            return;
        }
        liveHidden = false;
        Update(live, SceneSnapshot.Empty);
        if (draw)
        {
            FlushLive();
        }
        else
        {
            live.Damage.Clear();
        }
    }

    /// <summary>Draws the live layer's damage and shows a hidden swap-chain layer again once it shows the new content.</summary>
    private void FlushLive()
    {
        Flush(live);
        if (liveHidden && live.Damage.IsEmpty && !live.Snapshot.Elements.IsEmpty && state is PresenterState.Ready && liveLayer is not null)
        {
            liveLayer.Visual.IsVisible = true;
            liveHidden = false;
        }
    }

    /// <summary>Adds the bounds of everything a layer shows to its damage.</summary>
    private void DamageContent(LayerState layer)
    {
        foreach (var list in layer.Snapshot.Elements)
        {
            Damage(layer, list);
        }
        if (!layer.IsLive)
        {
            Damage(layer, layer.Snapshot.Selection);
        }
    }

    /// <summary>Adds the bounds of the lists a layer shows that paint with system colors to its damage.</summary>
    private void DamageSystemColors(LayerState layer)
    {
        var renderer = this.renderer!;
        foreach (var list in layer.Snapshot.Elements)
        {
            if (renderer.UsesSystemColors(list))
            {
                Damage(layer, list);
            }
        }
        if (!layer.IsLive && renderer.UsesSystemColors(layer.Snapshot.Selection))
        {
            Damage(layer, layer.Snapshot.Selection);
        }
    }

    /// <summary>
    /// Enters new lists, leaves old ones and adds the pixels that changed to the layer's damage. Without a working
    /// renderer it only records the snapshot; <see cref="ReplaceDevices"/> enters everything later.
    /// </summary>
    private void Update(LayerState layer, SceneSnapshot next)
    {
        var old = layer.Snapshot;
        if (ReferenceEquals(old, next))
        {
            return;
        }
        layer.Snapshot = next;
        if (renderer is null || state is not PresenterState.Ready)
        {
            return;
        }
        try
        {
            Diff(layer, old, next);
        }
        catch (NativeCallException error) when (DeviceLoss.IsDeviceLoss(error.HResult))
        {
            Lose(error.HResult);
        }
        catch (Exception error) when (error is NativeCallException or InvalidOperationException or OutOfMemoryException)
        {
            Fail(error);
        }
    }

    private void Diff(LayerState layer, SceneSnapshot old, SceneSnapshot next)
    {
        var renderer = this.renderer!;
        previousIndexes.Clear();
        added.Clear();
        removed.Clear();
        var oldLists = old.Elements;
        for (var index = 0; index < oldLists.Length; index++)
        {
            previousIndexes[oldLists[index]] = index;
        }
        var furthest = -1;
        foreach (var list in next.Elements)
        {
            if (previousIndexes.Remove(list, out var previousIndex))
            {
                // A list that kept its place keeps its pixels; one that moved in the stacking order is redrawn.
                if (previousIndex < furthest)
                {
                    Damage(layer, list);
                }
                furthest = Math.Max(furthest, previousIndex);
            }
            else
            {
                renderer.Enter(list);
                added.Add(list);
            }
        }
        foreach (var list in previousIndexes.Keys)
        {
            removed.Add(list);
        }
        foreach (var list in added)
        {
            var grown = false;
            if (layer.IsLive && list.ElementId != Guid.Empty)
            {
                for (var index = 0; index < removed.Count; index++)
                {
                    if (removed[index].ElementId == list.ElementId && TryGrowthDamage(removed[index], list, out var growth))
                    {
                        if (growth is { } area)
                        {
                            layer.Damage.Add(DisplayListRenderer.PixelBounds(area, AreaAtScale()), PixelWidth, PixelHeight);
                        }
                        renderer.Leave(removed[index]);
                        removed.RemoveAt(index);
                        grown = true;
                        break;
                    }
                }
            }
            if (!grown)
            {
                Damage(layer, list);
            }
        }
        foreach (var list in removed)
        {
            Damage(layer, list);
            renderer.Leave(list);
        }
        if (!layer.IsLive && !ReferenceEquals(old.Selection, next.Selection))
        {
            renderer.Enter(next.Selection);
            Damage(layer, old.Selection);
            Damage(layer, next.Selection);
            renderer.Leave(old.Selection);
        }
        added.Clear();
        removed.Clear();
        renderer.CollectImages();
    }

    private DrawArea AreaAtScale() => Through(new DrawArea(Scale, 0, 0, new PixelRect(0, 0, PixelWidth, PixelHeight)));

    /// <summary>A draw area of the layers, through screen zoom's view.</summary>
    private DrawArea Through(in DrawArea area) =>
        viewZoom == 1 && viewX == 0 && viewY == 0
            ? area
            : area with
            {
                Scale = area.Scale * viewZoom,
                OffsetX = area.OffsetX - viewX * viewZoom * area.Scale,
                OffsetY = area.OffsetY - viewY * viewZoom * area.Scale,
            };

    private void Damage(LayerState layer, DisplayList list)
    {
        if (renderer!.Bounds(list) is { } bounds)
        {
            layer.Damage.Add(DisplayListRenderer.PixelBounds(bounds, AreaAtScale()), PixelWidth, PixelHeight);
        }
    }

    /// <summary>
    /// Whether <paramref name="next"/> only extends the strokes of <paramref name="previous"/> (the same items, with
    /// paths or pressure runs that grew in their shared storage). <paramref name="damage"/> is then the area of the new
    /// segments in scene points, or null when nothing changed.
    /// </summary>
    internal static bool TryGrowthDamage(DisplayList previous, DisplayList next, out Rect? damage)
    {
        damage = null;
        var before = previous.Items;
        var after = next.Items;
        if (before.Length != after.Length)
        {
            return false;
        }
        for (var index = 0; index < before.Length; index++)
        {
            ref readonly var a = ref before[index];
            ref readonly var b = ref after[index];
            if (a.Op != b.Op)
            {
                return false;
            }
            switch (a.Op)
            {
                case DisplayOp.StrokePath when !ReferenceEquals(a.Path, b.Path):
                    if (!Equals(a.Stroke, b.Stroke) || !Equals(a.Paint, b.Paint) || Extension(a.Path!, b.Path!) is not { } grown)
                    {
                        return false;
                    }
                    var extent = DisplayListRenderer.StrokeExtent(b.Stroke!);
                    damage = RectMath.Union(damage, RectMath.Outset(grown, extent, extent));
                    break;
                case DisplayOp.StrokePath:
                    if (!Equals(a.Stroke, b.Stroke) || !Equals(a.Paint, b.Paint))
                    {
                        return false;
                    }
                    break;
                case DisplayOp.StrokeSegments when !ReferenceEquals(a.Segments, b.Segments):
                    if (SegmentExtension(a.Segments!, b.Segments!) is not { } segments)
                    {
                        return false;
                    }
                    damage = RectMath.Union(damage, segments);
                    break;
                case DisplayOp.PushTransform when !b.Transform.IsIdentity:
                    // Growth inside a transform would need the damage transformed; redraw the lists instead.
                    return false;
                case DisplayOp.PushLayer or DisplayOp.PushClip or DisplayOp.FillPath or DisplayOp.FillRect or DisplayOp.DrawText
                    or DisplayOp.DrawImage when !a.ContentEquals(b):
                    return false;
            }
        }
        return true;
    }

    /// <summary>The bounds of the lines a path gained since <paramref name="before"/>, from its old last point on.</summary>
    private static Rect? Extension(PathData before, PathData after)
    {
        if (before.PointCount == 0 || after.PointCount < before.PointCount || after.VerbCount < before.VerbCount
            || before.Verbs[^1] == PathVerb.Close)
        {
            return null;
        }
        if (!SharesPrefix(before.Points, after.Points) || !SharesPrefix(before.Verbs, after.Verbs))
        {
            return null;
        }
        foreach (var verb in after.Verbs[before.VerbCount..])
        {
            if (verb != PathVerb.LineTo)
            {
                return null;
            }
        }
        return Rect.Enclosing(after.Points[(before.PointCount - 1)..]);
    }

    /// <summary>
    /// Whether <paramref name="after"/> starts with <paramref name="before"/>: at once when both views share storage
    /// (an in-progress stroke appends in place), otherwise by comparing bytes (after its storage grew).
    /// </summary>
    private static bool SharesPrefix<T>(ReadOnlySpan<T> before, ReadOnlySpan<T> after)
        where T : unmanaged =>
        before.Length <= after.Length
        && (Unsafe.AreSame(ref MemoryMarshal.GetReference(before), ref MemoryMarshal.GetReference(after))
            || MemoryMarshal.AsBytes(before).SequenceEqual(MemoryMarshal.AsBytes(after[..before.Length])));

    private static Rect? SegmentExtension(StrokeSegments before, StrokeSegments after)
    {
        if (before.Points.Count == 0 || after.Points.Count < before.Points.Count || before.BaseWidth != after.BaseWidth
            || !before.Style.Equals(after.Style) || !before.Paint.Equals(after.Paint))
        {
            return null;
        }
        ReadOnlySpan<Point> oldPoints = before.Points.AsSpan(), newPoints = after.Points.AsSpan();
        ReadOnlySpan<double> oldPressures = before.Pressures.AsSpan(), newPressures = after.Pressures.AsSpan();
        if (!SharesPrefix(oldPoints, newPoints) || !SharesPrefix(oldPressures, newPressures))
        {
            return null;
        }
        var first = oldPoints.Length - 1;
        var widest = 0.15;
        foreach (var pressure in newPressures[first..])
        {
            widest = Math.Max(widest, pressure);
        }
        var extent = DisplayListRenderer.StrokeExtent(after.Style.WithWidth(after.BaseWidth * widest));
        return RectMath.Outset(Rect.Enclosing(newPoints[first..]), extent, extent);
    }

    private void Flush(LayerState layer)
    {
        if (layer.Damage.IsEmpty || state is not PresenterState.Ready)
        {
            return;
        }
        var target = layer.IsLive ? liveLayer : sceneLayer;
        if (target is null && layer.ShowsNothing)
        {
            layer.Damage.Clear();
            return;
        }
        var start = Stopwatch.GetTimestamp();
        if (layer.ShowsNothing && target!.Kind == LayerKind.VirtualSurface && CreatedOnDemand(layer.IsLive))
        {
            // An emptied virtual surface is released instead of cleared: no drawing, and its tiles go back to the
            // graphics device's pool (trimming the surface keeps them). It leaves the screen in the same composition
            // commit as the work item's other changes.
            ReleaseLayer(layer);
            Completed(layer, start);
            if (sceneLayer is null && liveLayer is null)
            {
                TrimPool();
            }
            return;
        }
        if (target is null)
        {
            try
            {
                target = EnsureLayer(layer);
            }
            catch (NativeCallException error) when (DeviceLoss.IsDeviceLoss(error.HResult))
            {
                Lose(error.HResult);
                return;
            }
            catch (Exception error) when (error is NativeCallException or InvalidOperationException or OutOfMemoryException)
            {
                Fail(error);
                return;
            }
        }
        if (!target.TryTakeFrame() && !target.DeferFrame())
        {
            Deferred(layer);
            return;
        }
        int result;
        try
        {
            result = target.Draw(layer.Damage, Scale, layer.IsLive, layer);
        }
        catch (NativeCallException error) when (DeviceLoss.IsDeviceLoss(error.HResult))
        {
            result = error.HResult;
        }
        catch (Exception error) when (error is NativeCallException or InvalidOperationException or OutOfMemoryException)
        {
            Fail(error);
            return;
        }
        if (result < 0)
        {
            Lose(result);
            return;
        }
        Completed(layer, start);
    }

    private void Completed(LayerState layer, long start)
    {
        var elapsed = Stopwatch.GetElapsedTime(start);
        layer.ResetWaiting();
        if (layer.IsLive)
        {
            Statistics.LiveFrames++;
            Statistics.LastLivePixels = layer.Damage.Area;
            Statistics.LastLiveFrameTime = elapsed;
        }
        else
        {
            Statistics.SceneFrames++;
            Statistics.LastScenePixels = layer.Damage.Area;
            Statistics.LastSceneFrameTime = elapsed;
        }
        layer.Damage.Clear();
    }

    /// <summary>
    /// Returns the memory that released surfaces left in the composition graphics device's pool, then asks Direct3D to
    /// destroy released resources now (it defers destruction until the immediate context is flushed). Measured: a
    /// display-sized virtual surface's 30 MiB come back only after the surface is released and the device trimmed.
    /// </summary>
    private void TrimPool()
    {
        if (state is not PresenterState.Ready || devices is not { State: DeviceState.Ready } bundle)
        {
            return;
        }
        graphics?.Trim();
        ((Windows.Win32.Graphics.Direct3D11.ID3D11DeviceContext*)bundle.D3D11DeviceContext)->Flush();
    }

    /// <summary>A swap-chain layer had no free frame: the damage waits for <see cref="FrameReady"/>; a long wait is reported.</summary>
    private void Deferred(LayerState layer)
    {
        Statistics.DeferredFrames++;
        var now = Stopwatch.GetTimestamp();
        if (layer.WaitingSince == 0)
        {
            layer.WaitingSince = now;
            return;
        }
        var waited = Stopwatch.GetElapsedTime(layer.WaitingSince, now);
        if (!layer.StallReported && waited >= stallThreshold)
        {
            layer.StallReported = true;
            Statistics.FrameStalls++;
            FrameStalled?.Invoke(this, new FrameStallEventArgs(layer.IsLive, waited));
        }
    }

    /// <summary>
    /// A deferred swap-chain frame can be drawn (on the overlay thread; tests call it for their layers). Layers that
    /// were replaced are ignored.
    /// </summary>
    internal void FrameReady(PresentationLayer layer)
    {
        if (state is not PresenterState.Ready)
        {
            return;
        }
        if (ReferenceEquals(layer, liveLayer))
        {
            FlushLive();
        }
        else if (ReferenceEquals(layer, sceneLayer))
        {
            Flush(scene);
        }
    }

    private void OnDeviceLost(object? sender, DeviceLostEventArgs args)
    {
        // Raised on the thread that noticed, possibly a thread-pool thread.
        queue.TryEnqueue(() =>
        {
            if (ReferenceEquals(sender, devices) && state is PresenterState.Ready)
            {
                Lose(args.State.HResult, args.State);
            }
        });
    }

    private void Lose(int hresult, DeviceState.Lost? known = null)
    {
        if (DeviceLoss.IsDeviceLoss(hresult) || known is not null)
        {
            var reported = known ?? devices?.ReportFailure(hresult) as DeviceState.Lost ?? new DeviceState.Lost(DeviceLoss.Classify(hresult), hresult);
            SetState(new PresenterState.DevicesLost(reported));
        }
        else
        {
            SetState(new PresenterState.Failed(new NativeCallException("Drawing the overlay", hresult)));
        }
        ShowFallback();
    }

    private void Fail(Exception error)
    {
        SetState(new PresenterState.Failed(error));
        ShowFallback();
    }

    private void SetState(PresenterState next)
    {
        var previous = state;
        if (previous == next)
        {
            return;
        }
        state = next;
        StateChanged?.Invoke(this, new PresenterStateChangedEventArgs(previous, next));
    }

    /// <summary>Opaque black composition visuals over every cover of both layers, rounded outward to whole pixels.</summary>
    private void ShowFallback()
    {
        HideFallback();
        fallbackShown = true;
        CompositionColorBrush? black = null;
        foreach (var layer in liveHidden ? new[] { scene } : new[] { scene, live })
        {
            foreach (var list in layer.Snapshot.Elements)
            {
                foreach (var cover in Covers(list))
                {
                    black ??= compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0));
                    var visual = compositor.CreateSpriteVisual();
                    visual.Brush = black;
                    visual.Offset = new System.Numerics.Vector3(cover.Left, cover.Top, 0);
                    visual.Size = new System.Numerics.Vector2(cover.Width, cover.Height);
                    fallback.Children.InsertAtTop(visual);
                }
            }
        }
    }

    private void HideFallback()
    {
        if (!fallbackShown)
        {
            return;
        }
        fallbackShown = false;
        foreach (var child in fallback.Children)
        {
            child.Dispose();
        }
        fallback.Children.RemoveAll();
    }

    /// <summary>The device rectangles of a list's snapped (protective) rectangles, through its transforms.</summary>
    private List<PixelRect> Covers(DisplayList list)
    {
        var covers = new List<PixelRect>();
        var stack = new Stack<AffineTransform>();
        var current = AreaAtScale().PointsToPixels;
        foreach (ref readonly var item in list.Items)
        {
            switch (item.Op)
            {
                case DisplayOp.PushTransform:
                    stack.Push(current);
                    current = AffineTransform.Concat(item.Transform, current);
                    break;
                case DisplayOp.PopTransform:
                    current = stack.Pop();
                    break;
                case DisplayOp.FillRect when item.Snap == RectSnap.OutwardToPixels:
                    var box = current.TransformBounds(item.Rect);
                    var left = (int)Math.Floor(box.X);
                    var top = (int)Math.Floor(box.Y);
                    covers.Add(new PixelRect(left, top, Math.Max(left + 1, (int)Math.Ceiling(box.MaxX)), Math.Max(top + 1, (int)Math.Ceiling(box.MaxY))));
                    break;
            }
        }
        return covers;
    }

    /// <summary>One layer's content, pending damage and frame wait.</summary>
    private sealed class LayerState(OverlayPresenter owner, bool live) : ILayerContent
    {
        public SceneSnapshot Snapshot { get; set; } = SceneSnapshot.Empty;

        public DamageRegion Damage { get; } = new();

        public bool IsLive { get; } = live;

        /// <summary>Whether the layer has nothing to draw (the scene's selection counts as content).</summary>
        public bool ShowsNothing => Snapshot.Elements.IsEmpty && (IsLive || Snapshot.Selection.IsEmpty);

        /// <summary>When the oldest update still waiting for a swap-chain frame arrived (Stopwatch ticks), or 0.</summary>
        public long WaitingSince { get; set; }

        public bool StallReported { get; set; }

        public void ResetWaiting()
        {
            WaitingSince = 0;
            StallReported = false;
        }

        public void Render(nint deviceContext, in DrawArea area)
        {
            var renderer = owner.renderer!;
            var shown = owner.Through(area);
            renderer.Draw(deviceContext, Snapshot.Elements, shown);
            if (!IsLive)
            {
                renderer.Draw(deviceContext, Snapshot.Selection, shown);
            }
        }
    }
}
