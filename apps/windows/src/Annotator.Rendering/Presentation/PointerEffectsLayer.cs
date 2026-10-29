using System.Numerics;
using Annotator.Core;
using Annotator.Core.Scene;
using Annotator.Platform;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Drawing;
using Windows.Graphics.DirectX;
using Windows.UI.Composition;

namespace Annotator.Rendering.Presentation;

/// <summary>Counters for measurements and tests: how the pointer effects reached the screen.</summary>
public sealed class PointerEffectsStatistics
{
    /// <summary>Times the resting halo was drawn into its sprite (settings, scale or devices changed).</summary>
    public long HaloSpriteDraws { get; internal set; }

    /// <summary>Frames of a click drawn into the click layer.</summary>
    public long ClickFrames { get; internal set; }

    /// <summary>Pointer moves shown by moving the halo sprite or the spotlight's center, without drawing.</summary>
    public long Moves { get; internal set; }
}

/// <summary>
/// One overlay's Spotlight and Cursor halo (Mac SelectionOverlayView's pointer part), above everything else on the overlay,
/// as the Mac draws them in its topmost overlay panel. Use it on the overlay thread that created it.
/// <list type="bullet">
/// <item>The spotlight is a composition radial gradient over the whole display: transparent black inside the inner radius
/// to black at alpha 0.62 at the outer radius (<see cref="SpotlightPaint"/>), at the fade's strength. Following the pointer
/// changes only the gradient's center, so nothing is drawn while it moves.</item>
/// <item>The resting halo is drawn once with <see cref="PointerEffectsRenderer"/> into a small surface centered on a pixel
/// corner, and following the pointer moves that surface by whole pixels, which gives the same pixels as drawing it there.
/// </item>
/// <item>A click (Pulse, Ripple, Turn or the static emphasis) is drawn frame by frame into a display-sized virtual surface,
/// only where it changes, and the surface's memory is given back when the click ends.</item>
/// <item>In region sharing's output, the laser pointer's trail (<see cref="ShowLaser"/>) goes between the spotlight and the
/// halo, as Mac SharedRegionPointerView draws it, into its own display-sized virtual surface, again only where it
/// changes. An overlay draws its trail on its canvas instead.</item>
/// </list>
/// Without devices the halo and the trail do not show; the spotlight needs none.
/// </summary>
public sealed unsafe class PointerEffectsLayer : IDisposable, ILayerContent
{
    /// <summary>Black at Core's <see cref="SpotlightPaint.EdgeAlpha"/>, as the Mac's gradient ends.</summary>
    private static readonly Windows.UI.Color Dim = Windows.UI.Color.FromArgb((byte)Math.Round(SpotlightPaint.EdgeAlpha * 255), 0, 0, 0);
    private static readonly Windows.UI.Color Clear = Windows.UI.Color.FromArgb(0, 0, 0, 0);

    private readonly Compositor compositor;
    private readonly ContainerVisual root;
    private readonly ContainerVisual container;
    private readonly SpriteVisual spotlight;
    private readonly CompositionRadialGradientBrush gradient;
    private readonly CompositionColorGradientStop inner;
    private readonly SpriteVisual halo;
    private readonly ContainerVisual clicks;
    private readonly ContainerVisual laser;
    private readonly LaserContent laserContent;
    private readonly ISystemColorResolver? colors;
    private readonly int pixelWidth;
    private readonly int pixelHeight;
    private readonly DamageRegion damage = new();
    private GraphicsDevices? devices;
    private DisplayListRenderer? renderer;
    private PointerEffectsRenderer? effects;
    private CompositionGraphicsDevice? graphics;
    private CompositionDrawingSurface? haloSurface;
    private CompositionSurfaceBrush? haloBrush;
    private SurfaceInterop? haloInterop;
    private DrawingSurfaceLayer? clickLayer;
    private CursorHighlightSettings? spriteSettings;
    private int spriteHalf;
    private CursorHighlightFrame? clickFrame;
    private PixelRect clickDrawn;
    private bool clickShown;
    private DrawingSurfaceLayer? laserLayer;
    private DisplayList? laserList;
    private PixelRect laserDrawn;
    private bool laserShown;
    private Vector2 shownCenter = new(float.NaN);
    private float shownOuter = float.NaN;
    private float shownOpacity = float.NaN;
    private bool spotlightShown;
    private Vector3 haloOffset = new(float.NaN);
    private bool haloShown;
    private double scale;
    private bool disposed;

    /// <param name="scale">The display scale: pixels per point.</param>
    /// <param name="pixelWidth">The overlay's width in device pixels.</param>
    public PointerEffectsLayer(Compositor compositor, ContainerVisual root, GraphicsDevices? devices, double scale, int pixelWidth,
                               int pixelHeight, ISystemColorResolver? colors = null)
    {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(root);
        this.compositor = compositor;
        this.root = root;
        this.devices = devices;
        this.scale = scale;
        this.pixelWidth = pixelWidth;
        this.pixelHeight = pixelHeight;
        this.colors = colors;
        container = compositor.CreateContainerVisual();
        container.Size = new Vector2(pixelWidth, pixelHeight);
        spotlight = compositor.CreateSpriteVisual();
        spotlight.Size = new Vector2(pixelWidth, pixelHeight);
        spotlight.IsVisible = false;
        gradient = compositor.CreateRadialGradientBrush();
        gradient.MappingMode = CompositionMappingMode.Absolute;
        gradient.ExtendMode = CompositionGradientExtendMode.Clamp;
        inner = compositor.CreateColorGradientStop(0, Clear);
        gradient.ColorStops.Add(inner);
        gradient.ColorStops.Add(compositor.CreateColorGradientStop(1, Dim));
        spotlight.Brush = gradient;
        clicks = compositor.CreateContainerVisual();
        clicks.Size = new Vector2(pixelWidth, pixelHeight);
        clicks.IsVisible = false;
        laser = compositor.CreateContainerVisual();
        laser.Size = new Vector2(pixelWidth, pixelHeight);
        laser.IsVisible = false;
        laserContent = new LaserContent(this);
        halo = compositor.CreateSpriteVisual();
        halo.IsVisible = false;
        container.Children.InsertAtTop(spotlight);
        container.Children.InsertAtTop(laser);
        container.Children.InsertAtTop(clicks);
        container.Children.InsertAtTop(halo);
        root.Children.InsertAtTop(container);
    }

    public PointerEffectsStatistics Statistics { get; } = new();

    /// <summary>The last drawing failure, or null once a draw succeeded; the halo stays hidden meanwhile.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>Whether the halo shows now (resting or clicking).</summary>
    public bool HaloShown => haloShown || clickShown;

    /// <summary>Whether the spotlight shows now.</summary>
    public bool SpotlightShown => spotlightShown;

    /// <summary>Whether the laser pointer's trail shows now.</summary>
    public bool LaserShown => laserShown;

    /// <summary>
    /// Shows the spotlight around (<paramref name="x"/>, <paramref name="y"/>) in overlay pixels with a radius of
    /// <paramref name="radius"/> points at <paramref name="opacity"/>: Mac SpotlightRenderer.draw, whose feather is a quarter of
    /// the radius, at least 12 and at most 64 points. Nothing is drawn at opacity 0.
    /// </summary>
    public void ShowSpotlight(double x, double y, double radius, double opacity)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (SpotlightPaint.Create(new Rect(0, 0, pixelWidth / scale, pixelHeight / scale), new Point(x / scale, y / scale), radius, opacity)
            is not { } paint)
        {
            HideSpotlight();
            return;
        }
        // Only what changed goes to the compositor: a moving pointer changes the center alone.
        var center = new Vector2((float)x, (float)y);
        if (center != shownCenter)
        {
            if (spotlightShown)
            {
                Statistics.Moves++;
            }
            gradient.EllipseCenter = center;
            shownCenter = center;
        }
        float outer = (float)(paint.OuterRadius * scale);
        if (outer != shownOuter)
        {
            gradient.EllipseRadius = new Vector2(outer, outer);
            inner.Offset = (float)(paint.InnerRadius / paint.OuterRadius);
            shownOuter = outer;
        }
        if ((float)paint.Opacity != shownOpacity)
        {
            shownOpacity = (float)paint.Opacity;
            spotlight.Opacity = shownOpacity;
        }
        if (!spotlightShown)
        {
            spotlight.IsVisible = true;
            spotlightShown = true;
        }
    }

    public void HideSpotlight()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (spotlightShown)
        {
            spotlight.IsVisible = false;
            spotlightShown = false;
        }
    }

    /// <summary>
    /// Shows the halo around (<paramref name="x"/>, <paramref name="y"/>) in overlay pixels (whole pixels, as the pointer
    /// moves): resting when <paramref name="clickProgress"/> is null, otherwise the click at that progress, with its ripple
    /// around (<paramref name="clickX"/>, <paramref name="clickY"/>) (Mac CursorHighlightRenderer.draw).
    /// </summary>
    public void ShowHalo(in CursorHighlightSettings settings, double x, double y, double? clickProgress, double clickX, double clickY,
                         bool reduceMotion)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (devices is null)
        {
            HideHalo();
            return;
        }
        try
        {
            if (clickProgress is null)
            {
                EndClick();
                ShowResting(settings, x, y);
            }
            else
            {
                SetHaloVisible(false);
                ShowClick(CursorHighlightFrame.Create(settings, new Point(x / scale, y / scale), clickProgress,
                    new Point(clickX / scale, clickY / scale), reduceMotion));
            }
            Failure = null;
        }
        catch (Exception error) when (error is NativeCallException or InvalidOperationException or OutOfMemoryException)
        {
            // A lost device or a failed draw hides the halo until new devices or the next change draw it.
            Failure = error;
            ReleaseDeviceResources();
            SetHaloVisible(false);
            clicks.IsVisible = false;
        }
    }

    public void HideHalo()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        SetHaloVisible(false);
        EndClick();
    }

    /// <summary>Continues on another device bundle, or hides the halo while there is none; the bundle stays the caller's.</summary>
    public void ReplaceDevices(GraphicsDevices? replacement)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ReleaseDeviceResources();
        devices = replacement;
        SetHaloVisible(false);
        clicks.IsVisible = false;
    }

    /// <summary>
    /// Gives back the halo's surfaces and renderer while no pointer effect shows (the host calls it when the effects turn off);
    /// the next halo draws them again. A laser trail that shows keeps them.
    /// </summary>
    public void ReleaseIdleResources()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        SetHaloVisible(false);
        clicks.IsVisible = false;
        if (laserShown)
        {
            EndClick();
            return;
        }
        ReleaseDeviceResources();
    }

    /// <summary>
    /// Shows the laser pointer's trail, <paramref name="trail"/> in the display's points as LaserTrailRenderer draws it
    /// (Mac SharedRegionPointerView), or hides it for null. Only where the old and the new trail paint is drawn again; a
    /// list already shown changes nothing.
    /// </summary>
    public void ShowLaser(DisplayList? trail)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (ReferenceEquals(trail, laserList) && (trail is null || laserShown))
        {
            return;
        }
        if (devices is null)
        {
            laserList = null;
            laserShown = false;
            laser.IsVisible = false;
            return;
        }
        DisplayList? previous = laserList;
        try
        {
            EnsureRenderer();
            if (laserLayer is null)
            {
                graphics ??= Annotator.Platform.Interop.CompositionInterop.CreateGraphicsDevice(compositor, devices.D2DDevice);
                laserLayer = new DrawingSurfaceLayer(compositor, graphics, pixelWidth, pixelHeight, isVirtual: true);
                laser.Children.InsertAtTop(laserLayer.Visual);
            }
            damage.Clear();
            if (laserShown)
            {
                damage.Add(laserDrawn, pixelWidth, pixelHeight);
            }
            PixelRect drawn = default;
            if (trail is not null && renderer!.Enter(trail) is { } bounds)
            {
                drawn = DisplayListRenderer.PixelBounds(bounds, new DrawArea(scale, 0, 0, new PixelRect(0, 0, pixelWidth, pixelHeight)));
                damage.Add(drawn, pixelWidth, pixelHeight);
            }
            laserList = trail;
            int result = laserLayer.Draw(damage, scale, live: false, laserContent);
            if (previous is not null)
            {
                renderer!.Leave(previous);
            }
            Check(result, "Draw the laser pointer's trail");
            laserDrawn = drawn;
            laserShown = trail is not null;
            laser.IsVisible = laserShown;
            if (!laserShown)
            {
                laserLayer.TrimAll();
            }
            Failure = null;
        }
        catch (Exception error) when (error is NativeCallException or InvalidOperationException or OutOfMemoryException)
        {
            // A lost device or a failed draw hides the trail until new devices or the next frame draw it.
            Failure = error;
            ReleaseDeviceResources();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        ReleaseDeviceResources();
        root.Children.Remove(container);
        container.Children.RemoveAll();
        gradient.Dispose();
        spotlight.Dispose();
        halo.Dispose();
        clicks.Dispose();
        laser.Dispose();
        container.Dispose();
    }

    void ILayerContent.Render(nint deviceContext, in DrawArea area)
    {
        if (clickFrame is { } frame)
        {
            effects!.DrawHalo(deviceContext, frame, area);
        }
    }

    private void ShowResting(in CursorHighlightSettings settings, double x, double y)
    {
        if (haloSurface is null || spriteSettings != settings)
        {
            DrawSprite(settings);
        }
        var offset = new Vector3((float)(Math.Round(x) - spriteHalf), (float)(Math.Round(y) - spriteHalf), 0);
        if (offset != haloOffset)
        {
            if (haloShown)
            {
                Statistics.Moves++;
            }
            halo.Offset = offset;
            haloOffset = offset;
        }
        SetHaloVisible(true);
    }

    private void SetHaloVisible(bool visible)
    {
        if (visible != haloShown)
        {
            halo.IsVisible = visible;
            haloShown = visible;
        }
    }

    /// <summary>
    /// Draws the resting halo centered on the pixel corner (half, half) of a square surface just large enough for its
    /// stroke and glow.
    /// </summary>
    private void DrawSprite(in CursorHighlightSettings settings)
    {
        EnsureRenderer();
        // The glow's Gaussian reaches three deviations.
        double reach = (settings.Diameter / 2 + settings.BorderWidth / 2 + settings.ResolvedGlow * CursorHighlightFrame.GlowDeviationPerRadius * 3) * scale + 2;
        int half = (int)Math.Ceiling(reach) + 1;
        if (haloSurface is null || half != spriteHalf)
        {
            ReleaseSprite();
            graphics ??= Annotator.Platform.Interop.CompositionInterop.CreateGraphicsDevice(compositor, devices!.D2DDevice);
            haloSurface = graphics.CreateDrawingSurface(new Windows.Foundation.Size(half * 2, half * 2),
                DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
            haloInterop = new SurfaceInterop(haloSurface);
            haloBrush = compositor.CreateSurfaceBrush(haloSurface);
            haloBrush.Stretch = CompositionStretch.None;
            haloBrush.HorizontalAlignmentRatio = 0;
            haloBrush.VerticalAlignmentRatio = 0;
            haloBrush.BitmapInterpolationMode = CompositionBitmapInterpolationMode.NearestNeighbor;
            halo.Brush = haloBrush;
            halo.Size = new Vector2(half * 2, half * 2);
            spriteHalf = half;
            haloOffset = new Vector3(float.NaN);
        }
        CursorHighlightFrame frame = CursorHighlightFrame.Create(settings, new Point(half / scale, half / scale), null);
        int result = haloInterop!.BeginDraw(new PixelRect(0, 0, half * 2, half * 2), out nint context, out int offsetX, out int offsetY);
        Check(result, "ICompositionDrawingSurfaceInterop::BeginDraw (cursor halo)");
        try
        {
            var device = (Windows.Win32.Graphics.Direct2D.ID2D1DeviceContext*)context;
            var identity = Interop.D2D.Identity;
            device->SetTransform(&identity);
            var transparent = default(Windows.Win32.Graphics.Direct2D.Common.D2D1_COLOR_F);
            device->Clear(&transparent);
            var clip = new PixelRect(offsetX, offsetY, offsetX + half * 2, offsetY + half * 2);
            effects!.DrawHalo(context, frame, new DrawArea(scale, offsetX, offsetY, clip));
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.Release(context);
            result = haloInterop.EndDraw();
        }
        Check(result, "ICompositionDrawingSurfaceInterop::EndDraw (cursor halo)");
        spriteSettings = settings;
        Statistics.HaloSpriteDraws++;
    }

    /// <summary>Draws one click frame where it and the previous one paint, on the display-sized click layer.</summary>
    private void ShowClick(CursorHighlightFrame frame)
    {
        EnsureRenderer();
        if (clickLayer is null)
        {
            graphics ??= Annotator.Platform.Interop.CompositionInterop.CreateGraphicsDevice(compositor, devices!.D2DDevice);
            clickLayer = new DrawingSurfaceLayer(compositor, graphics, pixelWidth, pixelHeight, isVirtual: true);
            clicks.Children.InsertAtTop(clickLayer.Visual);
        }
        Rect bounds = frame.PaintBounds();
        var pixels = new PixelRect((int)Math.Floor(bounds.X * scale) - 1, (int)Math.Floor(bounds.Y * scale) - 1,
                                   (int)Math.Ceiling(bounds.MaxX * scale) + 1, (int)Math.Ceiling(bounds.MaxY * scale) + 1);
        damage.Clear();
        if (clickShown)
        {
            damage.Add(clickDrawn, pixelWidth, pixelHeight);
        }
        damage.Add(pixels, pixelWidth, pixelHeight);
        clickFrame = frame;
        int result = clickLayer.Draw(damage, scale, live: false, this);
        clickFrame = null;
        Check(result, "Draw the click effect");
        clickDrawn = pixels;
        clickShown = true;
        clicks.IsVisible = true;
        Statistics.ClickFrames++;
    }

    /// <summary>Clears the click layer and gives its memory back.</summary>
    private void EndClick()
    {
        if (!clickShown)
        {
            clicks.IsVisible = false;
            return;
        }
        clickShown = false;
        clicks.IsVisible = false;
        if (clickLayer is null)
        {
            return;
        }
        // Trim alone left the last frame in tiles that a later click reuses: outside its BeginDraw rectangle, a reused tile
        // showed old rings, cut at the tile's edges. Draw the clear first, so trimmed tiles hold nothing.
        damage.Clear();
        damage.Add(clickDrawn, pixelWidth, pixelHeight);
        clickFrame = null;
        if (clickLayer.Draw(damage, scale, live: false, this) < 0)
        {
            // The next click starts on a new surface rather than one that may still hold this click.
            clicks.Children.RemoveAll();
            clickLayer.Dispose();
            clickLayer = null;
            return;
        }
        clickLayer.TrimAll();
    }

    private void EnsureRenderer()
    {
        if (renderer is null)
        {
            renderer = new DisplayListRenderer(devices!, colors);
            effects = new PointerEffectsRenderer(renderer);
        }
    }

    private void ReleaseSprite()
    {
        halo.Brush = null;
        haloInterop?.Dispose();
        haloInterop = null;
        haloBrush?.Dispose();
        haloBrush = null;
        haloSurface?.Dispose();
        haloSurface = null;
        spriteSettings = null;
    }

    private void ReleaseDeviceResources()
    {
        ReleaseSprite();
        clicks.Children.RemoveAll();
        clickLayer?.Dispose();
        clickLayer = null;
        clickShown = false;
        laser.Children.RemoveAll();
        laserLayer?.Dispose();
        laserLayer = null;
        laserList = null;
        laserShown = false;
        laser.IsVisible = false;
        graphics?.Dispose();
        graphics = null;
        effects?.Dispose();
        effects = null;
        renderer?.Dispose();
        renderer = null;
    }

    /// <summary>Draws the trail that <see cref="ShowLaser"/> shows, in each damaged rectangle of its surface.</summary>
    private sealed class LaserContent(PointerEffectsLayer owner) : ILayerContent
    {
        public void Render(nint deviceContext, in DrawArea area)
        {
            if (owner.laserList is { } list)
            {
                owner.renderer!.Draw(deviceContext, list, area);
            }
        }
    }

    private void Check(int result, string operation)
    {
        if (result < 0)
        {
            if (DeviceLoss.IsDeviceLoss(result))
            {
                devices?.ReportFailure(result);
            }
            throw new NativeCallException(operation, result);
        }
    }
}
