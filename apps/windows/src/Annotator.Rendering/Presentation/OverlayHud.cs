using System.Diagnostics;
using System.Numerics;
using Annotator.Core;
using Annotator.Core.Motion;
using Annotator.Core.Scene;
using Annotator.Platform;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Drawing;
using Windows.Graphics.DirectX;
using Windows.UI.Composition;
using Windows.Win32.Graphics.Direct2D;

namespace Annotator.Rendering.Presentation;

/// <summary>
/// Small controls drawn above an overlay's drawings, such as the canvas's Use apps button: one display list on its own
/// composition drawing surface, drawn when its content, the theme or the devices change, and shown or hidden with fades
/// that the compositor runs, so a resting control costs nothing. Without devices it shows nothing. Use it on the overlay
/// thread that created it.
/// </summary>
public sealed unsafe class OverlayHud : IDisposable
{
    private readonly Compositor compositor;
    private readonly ContainerVisual root;
    private readonly SpriteVisual visual;
    private readonly ISystemColorResolver colors;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private GraphicsDevices? devices;
    private DisplayListRenderer? renderer;
    private CompositionGraphicsDevice? graphics;
    private CompositionDrawingSurface? surface;
    private CompositionSurfaceBrush? brush;
    private SurfaceInterop? interop;
    private DisplayList content = DisplayList.Empty;
    private Rect bounds;
    private PixelRect pixels;
    private double scale;
    private bool drawn;
    private bool disposed;
    private double opacityFrom;
    private double opacityTo;
    private double fadeStart;
    private double fadeSeconds;

    /// <param name="scale">The display scale: pixels per point.</param>
    public OverlayHud(Compositor compositor, ContainerVisual root, GraphicsDevices? devices, ISystemColorResolver colors, double scale)
    {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(colors);
        this.compositor = compositor;
        this.root = root;
        this.colors = colors;
        this.devices = devices;
        this.scale = scale;
        visual = compositor.CreateSpriteVisual();
        visual.Opacity = 0;
        visual.IsVisible = false;
        root.Children.InsertAtTop(visual);
    }

    /// <summary>Whether the control is shown or fading in: the Mac's HUDFader.acceptsInput, false while it fades out.</summary>
    public bool IsVisible { get; private set; }

    /// <summary>The last drawing failure, or null once a draw succeeded; the control stays hidden meanwhile.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>Sets what to draw: <paramref name="list"/> in points, within <paramref name="area"/> (points).</summary>
    public void Show(DisplayList list, Rect area)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(list);
        renderer?.Leave(content);
        content = list;
        renderer?.Enter(content);
        if (area != bounds)
        {
            bounds = area;
            ReleaseSurface();
        }
        drawn = false;
        Draw();
    }

    /// <summary>
    /// Shows or hides the control: at once, or with the Mac's fades (0.16 s in with a 3-point rise unless motion is
    /// reduced, 0.12 s out), starting from the opacity it has now.
    /// </summary>
    public void SetVisible(bool shown, bool animate, bool reduceMotion) => SetVisible(shown, animate, reduceMotion, crossfade: null);

    /// <summary>
    /// <see cref="SetVisible(bool, bool, bool)"/>, or with <paramref name="crossfade"/> a fade of that many seconds without
    /// the rise, for a text that replaces another (Mac WorkspaceHintView's CATransition).
    /// </summary>
    public void SetVisible(bool shown, bool animate, bool reduceMotion, double? crossfade)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        // A change at once also ends a fade toward the same state, so the control is where it settles.
        if (shown == IsVisible && (animate || fadeSeconds <= 0))
        {
            return;
        }
        IsVisible = shown;
        double now = clock.Elapsed.TotalSeconds;
        double current = CurrentOpacity(now);
        double target = shown ? 1 : 0;
        double seconds = (crossfade ?? (shown ? MotionTiming.HintFadeInDuration : MotionTiming.HintFadeOutDuration)) * Math.Abs(target - current);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Offset.Y");
        visual.Offset = new Vector3(pixels.Left, pixels.Top, 0);
        if (!animate || seconds <= 0.001)
        {
            visual.Opacity = (float)target;
            opacityFrom = opacityTo = target;
            fadeSeconds = 0;
            UpdateShown();
            return;
        }
        opacityFrom = current;
        opacityTo = target;
        fadeStart = now;
        fadeSeconds = seconds;
        UpdateShown();
        CubicBezierEasingFunction fade = Easing(compositor, MotionCurves.Fade);
        using ScalarKeyFrameAnimation opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0, (float)current);
        opacity.InsertKeyFrame(1, (float)target, fade);
        opacity.Duration = TimeSpan.FromSeconds(seconds);
        visual.StartAnimation("Opacity", opacity);
        if (shown && current == 0 && !reduceMotion && crossfade is null)
        {
            CubicBezierEasingFunction enter = Easing(compositor, MotionCurves.Enter);
            using ScalarKeyFrameAnimation rise = compositor.CreateScalarKeyFrameAnimation();
            rise.InsertKeyFrame(0, (float)(pixels.Top + 3 * scale));
            rise.InsertKeyFrame(1, pixels.Top, enter);
            rise.Duration = TimeSpan.FromSeconds(MotionTiming.HintFadeInDuration);
            visual.StartAnimation("Offset.Y", rise);
        }
    }

    private static CubicBezierEasingFunction Easing(Compositor compositor, CubicBezier curve) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2((float)curve.X1, (float)curve.Y1), new Vector2((float)curve.X2, (float)curve.Y2));

    /// <summary>Continues on another device bundle, or shows nothing while there is none; the bundle stays the caller's.</summary>
    public void ReplaceDevices(GraphicsDevices? replacement)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ReleaseDeviceResources();
        devices = replacement;
        drawn = false;
        Draw();
    }

    /// <summary>Draws again, for example after an accent color change.</summary>
    public void Invalidate()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        drawn = false;
        Draw();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        ReleaseDeviceResources();
        root.Children.Remove(visual);
        visual.Dispose();
    }

    private double CurrentOpacity(double now)
    {
        if (fadeSeconds <= 0)
        {
            return opacityTo;
        }
        double progress = Math.Clamp((now - fadeStart) / fadeSeconds, 0, 1);
        return opacityFrom + (opacityTo - opacityFrom) * progress;
    }

    /// <summary>The visual shows only with drawn content; a fade out keeps it until the next change.</summary>
    private void UpdateShown() => visual.IsVisible = drawn && (IsVisible || fadeSeconds > 0);

    private void Draw()
    {
        if (drawn || devices is null || content.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
        {
            UpdateShown();
            return;
        }
        try
        {
            if (renderer is null)
            {
                renderer = new DisplayListRenderer(devices, colors);
                renderer.Enter(content);
            }
            if (surface is null)
            {
                CreateSurface();
            }
            int result = interop!.BeginDraw(new PixelRect(0, 0, pixels.Width, pixels.Height), out nint context, out int offsetX, out int offsetY);
            Check(result, "ICompositionDrawingSurfaceInterop::BeginDraw (canvas controls)");
            try
            {
                var device = (ID2D1DeviceContext*)context;
                var clip = new PixelRect(offsetX, offsetY, offsetX + pixels.Width, offsetY + pixels.Height);
                var identity = Interop.D2D.Identity;
                device->SetTransform(&identity);
                var transparent = default(Windows.Win32.Graphics.Direct2D.Common.D2D1_COLOR_F);
                device->Clear(&transparent);
                renderer.Draw(context, content, new DrawArea(scale, offsetX - pixels.Left, offsetY - pixels.Top, clip));
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.Release(context);
                result = interop.EndDraw();
            }
            Check(result, "ICompositionDrawingSurfaceInterop::EndDraw (canvas controls)");
            drawn = true;
            Failure = null;
        }
        catch (Exception error) when (error is NativeCallException or InvalidOperationException or OutOfMemoryException)
        {
            // A lost device or a failed draw hides the controls until new devices or the next change draw them.
            Failure = error;
            ReleaseDeviceResources();
        }
        UpdateShown();
    }

    private void CreateSurface()
    {
        pixels = new PixelRect((int)Math.Floor(bounds.X * scale), (int)Math.Floor(bounds.Y * scale),
                               (int)Math.Ceiling(bounds.MaxX * scale), (int)Math.Ceiling(bounds.MaxY * scale));
        graphics ??= Annotator.Platform.Interop.CompositionInterop.CreateGraphicsDevice(compositor, devices!.D2DDevice);
        surface = graphics.CreateDrawingSurface(new Windows.Foundation.Size(pixels.Width, pixels.Height),
            DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
        interop = new SurfaceInterop(surface);
        brush = compositor.CreateSurfaceBrush(surface);
        brush.Stretch = CompositionStretch.None;
        brush.HorizontalAlignmentRatio = 0;
        brush.VerticalAlignmentRatio = 0;
        brush.BitmapInterpolationMode = CompositionBitmapInterpolationMode.NearestNeighbor;
        visual.Brush = brush;
        visual.Size = new Vector2(pixels.Width, pixels.Height);
        visual.Offset = new Vector3(pixels.Left, pixels.Top, 0);
    }

    private void ReleaseSurface()
    {
        visual.Brush = null;
        interop?.Dispose();
        interop = null;
        brush?.Dispose();
        brush = null;
        surface?.Dispose();
        surface = null;
        drawn = false;
    }

    private void ReleaseDeviceResources()
    {
        ReleaseSurface();
        graphics?.Dispose();
        graphics = null;
        if (renderer is not null)
        {
            renderer.Leave(content);
            renderer.Dispose();
            renderer = null;
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
