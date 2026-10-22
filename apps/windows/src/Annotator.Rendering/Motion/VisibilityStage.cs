using System.Numerics;
using System.Runtime.InteropServices;
using Annotator.Core;
using Annotator.Core.Scene;
using Annotator.Platform;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Interop;
using Annotator.Rendering.Presentation;
using Annotator.Workspace.Motion;
using Windows.Graphics.DirectX;
using Windows.UI.Composition;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Annotator.Rendering.Motion;

/// <summary>
/// One snapshot on a <see cref="VisibilityStage"/>: an image visual at its resting pixel rectangle inside a container
/// centered on it. The layer motion animates the image (scale and rotation about its center, offset and opacity); the
/// container carries effects around the image, such as the toolbar's absorb pulses.
/// </summary>
public sealed class StageImage
{
    internal StageImage(ContainerVisual container, SpriteVisual image, PixelRect pixels)
    {
        Container = container;
        Image = image;
        Pixels = pixels;
    }

    public ContainerVisual Container { get; }

    public SpriteVisual Image { get; }

    /// <summary>Where the image rests, in the overlay's pixels.</summary>
    public PixelRect Pixels { get; }

    internal CompositionDrawingSurface? Surface { get; set; }

    internal CompositionBrush? Brush { get; set; }

    /// <summary>Visuals added around the image (trails, sparks, glow), removed with it.</summary>
    internal List<Visual> Extras { get; } = [];

    /// <summary>What one play of the image keeps (trail geometry and the like); a new play releases it.</summary>
    internal List<IDisposable> Resources { get; } = [];

    /// <summary>Soft focus: the unblurred pixels, which stay for every play until the image is removed.</summary>
    internal BlurSource? Blur { get; set; }
}

/// <summary>
/// The visibility motion's layer on one overlay (spec 009, WIN-MOT-02): temporary snapshots of drawings and the toolbar
/// above the canvas, rendered once with the Direct2D renderer and moved only by compositor animations, so nothing runs
/// on the overlay thread while they play. Use it on the overlay thread; dispose it with the overlay.
/// </summary>
public sealed unsafe class VisibilityStage : IDisposable
{
    private readonly Compositor _compositor;
    private readonly ContainerVisual _root;
    private readonly ISystemColorResolver _colors;
    private readonly List<StageImage> _images = [];
    private GraphicsDevices? _devices;
    private CompositionGraphicsDevice? _graphics;
    private DisplayListRenderer? _renderer;
    private ParticleBursts? _bursts;
    private bool _disposed;

    /// <param name="scale">Pixels per point of the overlay's display.</param>
    public VisibilityStage(Compositor compositor, ContainerVisual root, GraphicsDevices? devices, ISystemColorResolver colors, double scale,
                           MotionAnimations animations)
    {
        _compositor = compositor ?? throw new ArgumentNullException(nameof(compositor));
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _colors = colors ?? throw new ArgumentNullException(nameof(colors));
        Animations = animations ?? throw new ArgumentNullException(nameof(animations));
        _devices = devices;
        Scale = scale;
        Visual = compositor.CreateContainerVisual();
        Visual.RelativeSizeAdjustment = Vector2.One;
        Visual.IsVisible = false;
        root.Children.InsertAtTop(Visual);
    }

    /// <summary>The stage's container, above everything else on the overlay.</summary>
    public ContainerVisual Visual { get; }

    public double Scale { get; }

    public MotionAnimations Animations { get; }

    public Compositor Compositor => _compositor;

    public GraphicsDevices? Devices => _devices;

    /// <summary>Snapshots on the stage.</summary>
    public int ImageCount => _images.Count;

    /// <summary>Dissolve's particle bursts on this overlay, created with the first one.</summary>
    public ParticleBursts? Bursts => _bursts;

    /// <summary>The particle bursts, created on first use (Dissolve).</summary>
    /// <exception cref="InvalidOperationException">There are no graphics devices.</exception>
    /// <exception cref="NativeCallException">The shaders or states could not be created.</exception>
    public ParticleBursts UseBursts(PixelRect bounds)
    {
        ThrowIfDisposed();
        if (_devices is null)
        {
            throw new InvalidOperationException("There are no graphics devices to draw the particles.");
        }
        return _bursts ??= new ParticleBursts(_compositor, Visual, _devices, Scale, bounds);
    }

    /// <summary>
    /// Renders <paramref name="lists"/> into a texture for a particle burst at <paramref name="pixels"/>, keeping only what
    /// <paramref name="clip"/> keeps.
    /// </summary>
    public ParticleSource ParticleLists(ParticleBursts bursts, ReadOnlySpan<DisplayList> lists, PixelRect pixels, StageClip? clip = null)
    {
        ArgumentNullException.ThrowIfNull(bursts);
        return bursts.Lists(Renderer(), lists, pixels, clip);
    }

    /// <summary>
    /// Renders <paramref name="lists"/> (points from the display's top-left) into a new snapshot at <paramref name="pixels"/>;
    /// with <paramref name="clip"/>, only what it keeps (a copy for the shared region).
    /// </summary>
    /// <exception cref="InvalidOperationException">There are no graphics devices.</exception>
    /// <exception cref="NativeCallException">Drawing failed, for example because the device was lost.</exception>
    public StageImage AddLists(ReadOnlySpan<DisplayList> lists, PixelRect pixels, StageClip? clip = null)
    {
        var (image, surface) = CreateSurfaceImage(pixels);
        var renderer = Renderer();
        foreach (var list in lists)
        {
            renderer.Enter(list);
        }
        renderer.CollectImages();
        try
        {
            using var interop = new SurfaceInterop(surface);
            var context = Begin(interop, pixels, out var offsetX, out var offsetY);
            try
            {
                var bounds = new PixelRect(offsetX, offsetY, offsetX + pixels.Width, offsetY + pixels.Height);
                int originX = pixels.Left - offsetX, originY = pixels.Top - offsetY;
                var area = new DrawArea(Scale, offsetX - pixels.Left, offsetY - pixels.Top, clip?.KeepIn(bounds, originX, originY) ?? bounds);
                renderer.Draw(context, lists, area);
                clip?.EraseIn((ID2D1DeviceContext*)context, bounds, originX, originY);
            }
            finally
            {
                End(interop, context);
            }
        }
        catch
        {
            Remove(image);
            throw;
        }
        finally
        {
            foreach (var list in lists)
            {
                renderer.Leave(list);
            }
            renderer.CollectImages();
        }
        return image;
    }

    /// <summary>
    /// A plain color snapshot, such as a whiteboard or blackboard filling the display; with <paramref name="clip"/>, a drawn
    /// copy of only what the clip keeps.
    /// </summary>
    public StageImage AddColor(RGBA color, PixelRect pixels, StageClip? clip = null)
    {
        ThrowIfDisposed();
        if (clip is not null)
        {
            var (clipped, surface) = CreateSurfaceImage(pixels);
            try
            {
                using var interop = new SurfaceInterop(surface);
                var context = Begin(interop, pixels, out var offsetX, out var offsetY);
                try
                {
                    var device = (ID2D1DeviceContext*)context;
                    var bounds = new PixelRect(offsetX, offsetY, offsetX + pixels.Width, offsetY + pixels.Height);
                    int originX = pixels.Left - offsetX, originY = pixels.Top - offsetY;
                    clip.BeginKeep(device, bounds, originX, originY);
                    var fill = Interop.D2D.Color(color);
                    device->Clear(&fill);
                    clip.EndKeep(device, bounds, originX, originY);
                }
                finally
                {
                    End(interop, context);
                }
            }
            catch
            {
                Remove(clipped);
                throw;
            }
            return clipped;
        }
        var brush = _compositor.CreateColorBrush(Windows.UI.Color.FromArgb((byte)Math.Round(color.Alpha * 255),
            (byte)Math.Round(color.Red * 255), (byte)Math.Round(color.Green * 255), (byte)Math.Round(color.Blue * 255)));
        var image = CreateImage(pixels);
        image.Image.Brush = brush;
        image.Brush = brush;
        return image;
    }

    /// <summary>
    /// A snapshot from premultiplied BGRA pixels (the toolbar), placed at <paramref name="pixels"/>; the bitmap is drawn at
    /// its own size from the rectangle's top-left corner, and with <paramref name="clip"/> only where the clip keeps it.
    /// </summary>
    public StageImage AddBitmap(ReadOnlySpan<byte> bgra, int width, int height, int stride, PixelRect pixels, StageClip? clip = null)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 || bgra.Length < (long)stride * (height - 1) + width * 4)
        {
            throw new ArgumentException("The bitmap does not match its size.", nameof(bgra));
        }
        var (image, surface) = CreateSurfaceImage(pixels);
        try
        {
            using var interop = new SurfaceInterop(surface);
            var context = Begin(interop, pixels, out var offsetX, out var offsetY);
            try
            {
                var device = (ID2D1DeviceContext*)context;
                var properties = new D2D1_BITMAP_PROPERTIES
                {
                    pixelFormat = new D2D1_PIXEL_FORMAT { format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED },
                    dpiX = 96,
                    dpiY = 96,
                };
                ID2D1Bitmap* bitmap;
                fixed (byte* bytes = bgra)
                {
                    Interop.Native.Check(device->CreateBitmap(new D2D_SIZE_U { width = (uint)width, height = (uint)height }, bytes, (uint)stride,
                                                              &properties, &bitmap), "ID2D1DeviceContext::CreateBitmap(toolbar snapshot)");
                }
                var bounds = new PixelRect(offsetX, offsetY, offsetX + pixels.Width, offsetY + pixels.Height);
                int originX = pixels.Left - offsetX, originY = pixels.Top - offsetY;
                try
                {
                    clip?.BeginKeep(device, bounds, originX, originY);
                    var destination = Interop.D2D.Rect(offsetX, offsetY, offsetX + width, offsetY + height);
                    device->DrawBitmap(bitmap, &destination, 1, D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_NEAREST_NEIGHBOR, null);
                    clip?.EndKeep(device, bounds, originX, originY);
                }
                finally
                {
                    Interop.Native.Release(ref bitmap);
                }
            }
            finally
            {
                End(interop, context);
            }
        }
        catch
        {
            Remove(image);
            throw;
        }
        return image;
    }

    /// <summary>
    /// Plays <paramref name="motion"/> on <paramref name="image"/>: scale and rotation about its center, movement and
    /// opacity, starting <paramref name="shift"/> seconds into the plan (a late start keeps its delay shorter).
    /// </summary>
    public void Animate(StageImage image, LayerMotion motion, double shift = 0)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(motion);
        var delay = Math.Max(0, motion.Started - shift);
        var duration = motion.EffectiveDuration;
        var origin = new Vector3(image.Pixels.Left, image.Pixels.Top, 0);
        var scale = Scale;
        var visual = image.Image;
        var start = motion.Transform.Values[0].ToScreen();
        visual.Scale = new Vector3((float)start.Scale, (float)start.Scale, 1);
        visual.RotationAngle = (float)start.Angle;
        visual.Offset = origin + new Vector3((float)(start.Tx * scale), (float)(start.Ty * scale), 0);
        visual.Opacity = (float)motion.Opacity.Values[0];
        using (var scaleAnimation = Animations.Vector(motion.Transform, transform =>
               new Vector3((float)transform.Scale, (float)transform.Scale, 1), delay, duration))
        {
            visual.StartAnimation("Scale", scaleAnimation);
        }
        if (Rotates(motion.Transform))
        {
            using var rotation = Animations.Angle(motion.Transform, static transform => (float)transform.ToScreen().Angle, delay, duration);
            visual.StartAnimation("RotationAngle", rotation);
        }
        using (var offset = Animations.Vector(motion.Transform, transform =>
               {
                   var screen = transform.ToScreen();
                   return origin + new Vector3((float)(screen.Tx * scale), (float)(screen.Ty * scale), 0);
               }, delay, duration))
        {
            visual.StartAnimation("Offset", offset);
        }
        using (var opacity = Animations.Scalar(motion.Opacity, static value => (float)value, delay, duration))
        {
            visual.StartAnimation("Opacity", opacity);
        }
    }

    /// <summary>
    /// The toolbar's absorb pulses as gathered shapes land (Mac VisibilityImageView.absorbPulses): 3.5% scale bumps
    /// around the image center at <paramref name="times"/>, sampled 120 times a second.
    /// </summary>
    public void Pulse(StageImage image, IReadOnlyList<double> times, double shift = 0, double amplitude = 0.035, double width = 0.13)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (times.Count == 0)
        {
            return;
        }
        double first = times[0], last = times[^1];
        var span = last + width - first;
        var steps = Math.Max(2, (int)(span * 120));
        var values = new Vector3[steps + 1];
        for (var step = 0; step <= steps; step++)
        {
            var time = first + span * step / steps;
            var value = (float)(1 + amplitude * ToolbarSlideMotion.Pulse(time, times, width));
            values[step] = new Vector3(value, value, 1);
        }
        using var pulse = Animations.Sampled(values, first - shift, span);
        image.Container.StartAnimation("Scale", pulse);
    }

    /// <summary>Removes what was added around <paramref name="image"/> (a reversal builds its trails and sparks again).</summary>
    public void ClearExtras(StageImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        foreach (var extra in image.Extras)
        {
            image.Container.Children.Remove(extra);
            extra.Dispose();
        }
        image.Extras.Clear();
        foreach (var resource in image.Resources)
        {
            resource.Dispose();
        }
        image.Resources.Clear();
        image.Container.StopAnimation("Scale");
        image.Container.Scale = Vector3.One;
    }

    /// <summary>Removes one snapshot and what was added around it.</summary>
    public void Remove(StageImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!_images.Remove(image))
        {
            return;
        }
        Release(image);
        Visual.IsVisible = _images.Count > 0 || (_bursts?.Count ?? 0) > 0;
    }

    /// <summary>Sizes the particle layer, draws the bursts' first frame and returns whether they still move.</summary>
    public bool PrepareBursts(double now)
    {
        if (_bursts is null)
        {
            return false;
        }
        var moving = _bursts.Prepare(now);
        Visual.IsVisible = _images.Count > 0 || _bursts.Count > 0;
        return moving;
    }

    /// <summary>Removes every particle burst in this commit.</summary>
    public void ClearBursts()
    {
        _bursts?.Clear();
        Visual.IsVisible = _images.Count > 0;
    }

    /// <summary>
    /// Soft focus: renders <paramref name="lists"/> once into a GPU bitmap and shows them at <paramref name="pixels"/> in a
    /// snapshot <paramref name="padding"/> pixels larger on each side, so <see cref="SetBlur"/> can blur them past their edges
    /// (Mac: a CIGaussianBlur on the snapshot's layer); with <paramref name="clip"/>, only what the clip keeps.
    /// </summary>
    public StageImage AddBlurredLists(ReadOnlySpan<DisplayList> lists, PixelRect pixels, int padding, StageClip? clip = null)
    {
        var renderer = Renderer();
        foreach (var list in lists)
        {
            renderer.Enter(list);
        }
        renderer.CollectImages();
        try
        {
            var bounds = new PixelRect(0, 0, pixels.Width + padding * 2, pixels.Height + padding * 2);
            int originX = pixels.Left - padding, originY = pixels.Top - padding;
            var area = new DrawArea(Scale, padding - pixels.Left, padding - pixels.Top, clip?.KeepIn(bounds, originX, originY) ?? bounds);
            DisplayList[] copy = lists.ToArray();
            return AddBlurred(pixels, padding, context =>
            {
                renderer.Draw((nint)context, copy, area);
                clip?.EraseIn(context, bounds, originX, originY);
            });
        }
        finally
        {
            foreach (var list in lists)
            {
                renderer.Leave(list);
            }
            renderer.CollectImages();
        }
    }

    /// <summary>
    /// Soft focus for the toolbar: premultiplied BGRA pixels (drawn at their own size from the top-left corner of
    /// <paramref name="pixels"/>) in a snapshot <paramref name="padding"/> pixels larger on each side, for <see cref="SetBlur"/>;
    /// with <paramref name="clip"/>, only what the clip keeps.
    /// </summary>
    public StageImage AddBlurredBitmap(ReadOnlySpan<byte> bgra, int width, int height, int stride, PixelRect pixels, int padding, StageClip? clip = null)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 || bgra.Length < (long)stride * (height - 1) + width * 4)
        {
            throw new ArgumentException("The bitmap does not match its size.", nameof(bgra));
        }
        byte[] copy = bgra.ToArray();
        var bounds = new PixelRect(0, 0, pixels.Width + padding * 2, pixels.Height + padding * 2);
        int originX = pixels.Left - padding, originY = pixels.Top - padding;
        return AddBlurred(pixels, padding, context =>
        {
            var properties = new D2D1_BITMAP_PROPERTIES
            {
                pixelFormat = new D2D1_PIXEL_FORMAT { format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED },
                dpiX = 96,
                dpiY = 96,
            };
            ID2D1Bitmap* bitmap;
            fixed (byte* bytes = copy)
            {
                Check(context->CreateBitmap(new D2D_SIZE_U { width = (uint)width, height = (uint)height }, bytes, (uint)stride, &properties, &bitmap),
                      "ID2D1DeviceContext::CreateBitmap (toolbar soft focus)");
            }
            try
            {
                clip?.BeginKeep(context, bounds, originX, originY);
                var destination = Interop.D2D.Rect(padding, padding, padding + width, padding + height);
                context->DrawBitmap(bitmap, &destination, 1, D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_NEAREST_NEIGHBOR, null);
                clip?.EndKeep(context, bounds, originX, originY);
            }
            finally
            {
                Native.Release(ref bitmap);
            }
        });
    }

    /// <summary>A padded snapshot whose content <paramref name="draw"/> renders once into the blur's source bitmap.</summary>
    private StageImage AddBlurred(PixelRect pixels, int padding, DrawContent draw)
    {
        var padded = new PixelRect(pixels.Left - padding, pixels.Top - padding, pixels.Right + padding, pixels.Bottom + padding);
        var (image, _) = CreateSurfaceImage(padded);
        ID2D1DeviceContext* context = null;
        ID2D1Bitmap1* bitmap = null;
        try
        {
            Check(((ID2D1Device*)_devices!.D2DDevice)->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS.D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &context),
                  "ID2D1Device::CreateDeviceContext (soft focus)");
            var properties = new D2D1_BITMAP_PROPERTIES1
            {
                pixelFormat = new D2D1_PIXEL_FORMAT { format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED },
                dpiX = 96,
                dpiY = 96,
                bitmapOptions = D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_TARGET,
            };
            var size = new D2D_SIZE_U { width = (uint)padded.Width, height = (uint)padded.Height };
            Check(context->CreateBitmap(size, null, 0, &properties, &bitmap), "ID2D1DeviceContext::CreateBitmap (soft focus)");
            context->SetTarget((ID2D1Image*)bitmap);
            context->BeginDraw();
            HRESULT ended;
            try
            {
                var transparent = default(D2D1_COLOR_F);
                context->Clear(&transparent);
                draw(context);
            }
            finally
            {
                ended = context->EndDraw(null, null);
                context->SetTarget(null);
            }
            Check(ended.Value, "ID2D1DeviceContext::EndDraw (soft focus)");
            image.Blur = new BlurSource(bitmap);
            bitmap = null;
        }
        catch
        {
            Remove(image);
            throw;
        }
        finally
        {
            Native.Release(ref bitmap);
            Native.Release(ref context);
        }
        SetBlur(image, 0);
        return image;
    }

    private delegate void DrawContent(ID2D1DeviceContext* context);

    /// <summary>Redraws a soft-focus snapshot with a Gaussian blur of <paramref name="deviation"/> pixels.</summary>
    public void SetBlur(StageImage image, double deviation)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Blur is not { } source || image.Surface is not { } surface)
        {
            return;
        }
        deviation = Math.Max(0, deviation);
        if (source.Drawn is { } drawn && Math.Abs(drawn - deviation) < 0.01)
        {
            return;
        }
        using var interop = new SurfaceInterop(surface);
        var pixels = image.Pixels;
        var context = Begin(interop, pixels, out var offsetX, out var offsetY);
        try
        {
            var device = (ID2D1DeviceContext*)context;
            var offset = new D2D_POINT_2F { x = offsetX, y = offsetY };
            if (deviation < 0.01)
            {
                device->DrawImage((ID2D1Image*)source.Bitmap, &offset, null, D2D1_INTERPOLATION_MODE.D2D1_INTERPOLATION_MODE_LINEAR,
                                  D2D1_COMPOSITE_MODE.D2D1_COMPOSITE_MODE_SOURCE_OVER);
            }
            else
            {
                var effect = source.Effect(device);
                var value = (float)deviation;
                // D2D1_GAUSSIANBLUR_PROP_STANDARD_DEVIATION.
                Check(effect->SetValue(0, D2D1_PROPERTY_TYPE.D2D1_PROPERTY_TYPE_FLOAT, (byte*)&value, sizeof(float)), "ID2D1Effect::SetValue (blur)");
                ID2D1Image* output;
                effect->GetOutput(&output);
                device->DrawImage(output, &offset, null, D2D1_INTERPOLATION_MODE.D2D1_INTERPOLATION_MODE_LINEAR,
                                  D2D1_COMPOSITE_MODE.D2D1_COMPOSITE_MODE_SOURCE_OVER);
                Native.Release(ref output);
            }
        }
        finally
        {
            End(interop, context);
        }
        source.Drawn = deviation;
    }

    /// <summary>Removes every snapshot. Visuals leave the tree in this commit; their held end states never flash back.</summary>
    public void Clear()
    {
        foreach (var image in _images)
        {
            Release(image);
        }
        _images.Clear();
        _bursts?.Clear();
        Visual.IsVisible = false;
        ReleaseRenderer();
    }

    /// <summary>Continues on other devices, or none; existing snapshots stay as they are.</summary>
    public void ReplaceDevices(GraphicsDevices? devices)
    {
        ReleaseRenderer();
        _bursts?.Dispose();
        _bursts = null;
        _graphics?.Dispose();
        _graphics = null;
        _devices = devices;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        Clear();
        _bursts?.Dispose();
        _bursts = null;
        _graphics?.Dispose();
        _graphics = null;
        _root.Children.Remove(Visual);
        Visual.Dispose();
        _disposed = true;
    }

    /// <summary>Adds a visual behind the image inside its container (trails and sparks fly below the shape).</summary>
    public void AddBelow(StageImage image, Visual extra)
    {
        image.Container.Children.InsertBelow(extra, image.Image);
        image.Extras.Add(extra);
    }

    /// <summary>Adds a visual above the image inside its container.</summary>
    public void AddAbove(StageImage image, Visual extra)
    {
        image.Container.Children.InsertAtTop(extra);
        image.Extras.Add(extra);
    }

    /// <summary>Keeps <paramref name="resource"/> until the image is removed.</summary>
    public void Keep(StageImage image, IDisposable resource) => image.Resources.Add(resource);

    private static bool Rotates(TransformTrack track)
    {
        foreach (var value in track.Values)
        {
            if (value.B != 0)
            {
                return true;
            }
        }
        return false;
    }

    private StageImage CreateImage(PixelRect pixels)
    {
        ThrowIfDisposed();
        var container = _compositor.CreateContainerVisual();
        container.RelativeSizeAdjustment = Vector2.One;
        container.CenterPoint = new Vector3(pixels.Left + pixels.Width / 2f, pixels.Top + pixels.Height / 2f, 0);
        var sprite = _compositor.CreateSpriteVisual();
        sprite.Size = new Vector2(pixels.Width, pixels.Height);
        sprite.Offset = new Vector3(pixels.Left, pixels.Top, 0);
        sprite.CenterPoint = new Vector3(pixels.Width / 2f, pixels.Height / 2f, 0);
        container.Children.InsertAtTop(sprite);
        Visual.Children.InsertAtTop(container);
        var image = new StageImage(container, sprite, pixels);
        _images.Add(image);
        Visual.IsVisible = true;
        return image;
    }

    private (StageImage Image, CompositionDrawingSurface Surface) CreateSurfaceImage(PixelRect pixels)
    {
        ThrowIfDisposed();
        if (_devices is null)
        {
            throw new InvalidOperationException("There are no graphics devices to draw the snapshot.");
        }
        if (pixels.Width <= 0 || pixels.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixels), "A snapshot needs at least one pixel.");
        }
        _graphics ??= Annotator.Platform.Interop.CompositionInterop.CreateGraphicsDevice(_compositor, _devices.D2DDevice);
        var surface = _graphics.CreateDrawingSurface(new Windows.Foundation.Size(pixels.Width, pixels.Height),
            DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
        var brush = _compositor.CreateSurfaceBrush(surface);
        brush.Stretch = CompositionStretch.Fill;
        brush.BitmapInterpolationMode = CompositionBitmapInterpolationMode.Linear;
        var image = CreateImage(pixels);
        image.Image.Brush = brush;
        image.Brush = brush;
        image.Surface = surface;
        return (image, surface);
    }

    private nint Begin(SurfaceInterop interop, PixelRect pixels, out int offsetX, out int offsetY)
    {
        var result = interop.BeginDraw(new PixelRect(0, 0, pixels.Width, pixels.Height), out var context, out offsetX, out offsetY);
        Check(result, "ICompositionDrawingSurfaceInterop::BeginDraw (visibility snapshot)");
        var device = (ID2D1DeviceContext*)context;
        var identity = Interop.D2D.Identity;
        device->SetTransform(&identity);
        var transparent = default(D2D1_COLOR_F);
        device->Clear(&transparent);
        return context;
    }

    private void End(SurfaceInterop interop, nint context)
    {
        Marshal.Release(context);
        Check(interop.EndDraw(), "ICompositionDrawingSurfaceInterop::EndDraw (visibility snapshot)");
    }

    private DisplayListRenderer Renderer()
    {
        if (_devices is null)
        {
            throw new InvalidOperationException("There are no graphics devices to draw the snapshot.");
        }
        return _renderer ??= new DisplayListRenderer(_devices, _colors);
    }

    private void ReleaseRenderer()
    {
        _renderer?.Dispose();
        _renderer = null;
    }

    private void Release(StageImage image)
    {
        Visual.Children.Remove(image.Container);
        foreach (var extra in image.Extras)
        {
            extra.Dispose();
        }
        image.Extras.Clear();
        foreach (var resource in image.Resources)
        {
            resource.Dispose();
        }
        image.Resources.Clear();
        image.Blur?.Dispose();
        image.Blur = null;
        image.Image.Brush = null;
        image.Brush?.Dispose();
        image.Surface?.Dispose();
        image.Image.Dispose();
        image.Container.Dispose();
    }

    private void Check(int result, string operation)
    {
        if (result < 0)
        {
            if (DeviceLoss.IsDeviceLoss(result))
            {
                _devices?.ReportFailure(result);
            }
            throw new NativeCallException(operation, result);
        }
    }

    private void Check(HRESULT result, string operation) => Check(result.Value, operation);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

/// <summary>
/// A soft-focus snapshot's unblurred pixels and the Gaussian blur that draws them. An effect belongs to the device
/// context that created it, so it is made again when the surface hands out another context.
/// </summary>
internal sealed unsafe class BlurSource : IDisposable
{
    private static readonly Guid GaussianBlur = new("1FEB6D69-2FE6-4AC9-8C58-1D7F93E7A6A5");
    private ID2D1Bitmap1* _bitmap;
    private ID2D1Effect* _effect;
    private ID2D1DeviceContext* _owner;

    public BlurSource(ID2D1Bitmap1* bitmap) => _bitmap = bitmap;

    public ID2D1Bitmap1* Bitmap => _bitmap;

    /// <summary>The deviation the surface shows now, in pixels.</summary>
    public double? Drawn { get; set; }

    public ID2D1Effect* Effect(ID2D1DeviceContext* context)
    {
        if (_effect != null && _owner == context)
        {
            return _effect;
        }
        Native.Release(ref _effect);
        Native.Release(ref _owner);
        Guid id = GaussianBlur;
        ID2D1Effect* created;
        Native.Check(context->CreateEffect(&id, &created), "ID2D1DeviceContext::CreateEffect(blur)");
        try
        {
            // D2D1_GAUSSIANBLUR_PROP_OPTIMIZATION balanced, D2D1_GAUSSIANBLUR_PROP_BORDER_MODE soft (edges fade, as in Core Image).
            uint optimization = 1;
            Native.Check(created->SetValue(1, D2D1_PROPERTY_TYPE.D2D1_PROPERTY_TYPE_ENUM, (byte*)&optimization, sizeof(uint)), "ID2D1Effect::SetValue(blur optimization)");
            uint border = 0;
            Native.Check(created->SetValue(2, D2D1_PROPERTY_TYPE.D2D1_PROPERTY_TYPE_ENUM, (byte*)&border, sizeof(uint)), "ID2D1Effect::SetValue(blur border)");
            created->SetInput(0, (ID2D1Image*)_bitmap, true);
        }
        catch
        {
            Native.Release(ref created);
            throw;
        }
        _effect = created;
        _owner = Native.AddRef(context);
        return _effect;
    }

    public void Dispose()
    {
        Native.Release(ref _effect);
        Native.Release(ref _owner);
        Native.Release(ref _bitmap);
    }
}
