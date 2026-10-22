using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Effects;
using Annotator.Rendering.Imaging;
using Annotator.Rendering.Interop;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Annotator.Rendering.Export;

/// <summary>Swift <c>ExportBackdrop</c>: an optional padded, rounded frame around a snapshot.</summary>
public readonly record struct ExportBackdrop
{
    public ExportBackdrop()
    {
    }

    public ExportBackdropKind Kind { get; init; } = ExportBackdropKind.None;

    public double Padding { get; init; } = 36;

    public double Radius { get; init; } = 16;

    public RGBA Color { get; init; } = new(0.17, 0.2, 0.3);

    public double Scale { get; init; } = 1;
}

public enum ExportBackdropKind
{
    None,
    Solid,
    Gradient,
}

/// <summary>What a snapshot could not reproduce exactly. Every case fails closed.</summary>
[Flags]
public enum SnapshotIssues : byte
{
    None = 0,

    /// <summary>An image element could not be shown; its replacement outline was drawn.</summary>
    ImageUnavailable = 2,
}

/// <summary>
/// Port of Swift <c>SnapshotExporter</c>: a captured screen image with the drawings rendered on it, then covers and
/// pixel effects applied as the Mac's <c>CaptureCompositor</c> does (covers are opaque black and win over every
/// effect), then cropped and framed. Coordinates of <c>elements</c> are the source's pixels, as on the Mac. Use it on
/// one thread.
/// </summary>
public sealed unsafe class SnapshotExporter : IDisposable
{
    private static readonly RGBA GradientEnd = new(0.6, 0.38, 0.75);

    private readonly PngExporter exporter;
    private readonly ITextMeasurer measurer;
    private readonly GraphicsDevices devices;
    private EffectPipeline? effects;

    public SnapshotExporter(GraphicsDevices devices, ITextMeasurer measurer, ISystemColorResolver? colors = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(measurer);
        this.measurer = measurer;
        this.devices = devices;
        exporter = new PngExporter(devices, measurer, colors);
    }

    /// <summary>What the last call could not reproduce exactly.</summary>
    public SnapshotIssues LastIssues { get; private set; }

    public DisplayListRenderer Renderer => exporter.Renderer;

    /// <summary>
    /// Swift <c>flattenedSource</c>: with no protective element the source is returned unchanged; otherwise covers turn
    /// their pixel rectangles (rounded outward) opaque black, Blur, Pixelate and Distort filter their rectangles from
    /// the covered source, and covers are drawn again on top (<see cref="EffectPipeline.RenderScene"/>).
    /// </summary>
    /// <exception cref="Annotator.Platform.NativeCallException">The graphics device failed, for example because it was lost.</exception>
    public RasterImage FlattenedSource(RasterImage source, ElementList elements)
    {
        ArgumentNullException.ThrowIfNull(source);
        LastIssues = SnapshotIssues.None;
        return Flatten(source, elements);
    }

    /// <summary>
    /// Swift <c>SnapshotExporter.image(source:elements:crop:backdrop:)</c>.
    /// </summary>
    /// <exception cref="RenderFailure">The crop is not inside the source or smaller than one pixel, the backdrop is out of
    /// range (padding 0 to 256, radius 0 to 100, scale 0.25 to 2, a valid color), or the result is larger than 16,384
    /// pixels on a side or 40 million pixels (<see cref="RenderFailureKind.InvalidSize"/>).</exception>
    public RasterImage Image(RasterImage source, ElementList elements, Rect? crop = null, ExportBackdrop? backdrop = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        foreach (ref readonly var element in elements)
        {
            element.Validate();
        }
        LastIssues = SnapshotIssues.None;
        var frameStyle = backdrop ?? new ExportBackdrop();
        var full = new Rect(0, 0, source.Width, source.Height);
        var area = crop ?? full;
        if (!area.IsValid || !RectMath.Contains(full, area) || !(area.Width >= 1) || !(area.Height >= 1)
            || !double.IsFinite(frameStyle.Padding) || !(frameStyle.Padding >= 0 && frameStyle.Padding <= 256)
            || !double.IsFinite(frameStyle.Radius) || !(frameStyle.Radius >= 0 && frameStyle.Radius <= 100)
            || !double.IsFinite(frameStyle.Scale) || !(frameStyle.Scale >= 0.25 && frameStyle.Scale <= 2) || !frameStyle.Color.IsValid)
        {
            throw new RenderFailure(RenderFailureKind.InvalidSize);
        }
        var padding = frameStyle.Kind == ExportBackdropKind.None ? 0 : frameStyle.Padding;
        var size = new Size(area.Width + padding * 2, area.Height + padding * 2);
        double w = Math.Ceiling(size.Width * frameStyle.Scale), h = Math.Ceiling(size.Height * frameStyle.Scale);
        if (!(w * h <= PngExporter.MaximumPixels) || !(w <= PngExporter.MaximumSide) || !(h <= PngExporter.MaximumSide))
        {
            throw new RenderFailure(RenderFailureKind.InvalidSize);
        }
        var content = AnnotatedSource(source, elements);
        var flattened = Flatten(content, elements);
        var integral = RectMath.Intersection(RectMath.Integral(area), new Rect(0, 0, flattened.Width, flattened.Height));
        if (integral is not { } cropped || RectMath.IsEmpty(cropped))
        {
            throw new RenderFailure(RenderFailureKind.ImageUnavailable);
        }
        var frame = new Rect(padding, padding, area.Width, area.Height);
        var radius = frameStyle.Kind == ExportBackdropKind.None ? 0 : Math.Min(frameStyle.Radius, Math.Min(frame.Width / 2, frame.Height / 2));
        var canvas = exporter.Canvas;
        var renderer = exporter.Renderer;
        var bitmap = canvas.Upload(flattened);
        PathData? mask = radius > 0 ? new PathBuilder(10, 21).AddRoundedRect(frame, radius, radius).ToPathData() : null;
        var builder = new PathGeometryBuilder();
        ID2D1PathGeometry1* maskGeometry = mask is null ? null : builder.Build(renderer.Factory, mask, FillRule.NonZero);
        try
        {
            return canvas.Render((int)w, (int)h, context =>
            {
                var scale = AffineTransform.Scale(frameStyle.Scale, frameStyle.Scale);
                var matrix = D2D.Matrix(scale);
                context->SetTransform(&matrix);
                context->SetAntialiasMode(D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
                if (frameStyle.Kind != ExportBackdropKind.None)
                {
                    var brush = renderer.SolidBrush;
                    var color = D2D.Color(frameStyle.Color);
                    brush->SetColor(&color);
                    brush->SetOpacity(1);
                    var whole = D2D.Rect(0, 0, size.Width, size.Height);
                    context->FillRectangle(&whole, (ID2D1Brush*)brush);
                    if (frameStyle.Kind == ExportBackdropKind.Gradient)
                    {
                        FillGradient(context, frameStyle.Color, size);
                    }
                }
                if (maskGeometry != null)
                {
                    var parameters = new D2D1_LAYER_PARAMETERS1
                    {
                        contentBounds = D2D.Rect(frame),
                        geometricMask = (ID2D1Geometry*)maskGeometry,
                        maskAntialiasMode = D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_PER_PRIMITIVE,
                        maskTransform = D2D.Identity,
                        opacity = 1,
                        layerOptions = D2D1_LAYER_OPTIONS1.D2D1_LAYER_OPTIONS1_NONE,
                    };
                    context->PushLayer(&parameters, null);
                }
                else
                {
                    var clip = D2D.Rect(frame);
                    context->PushAxisAlignedClip(&clip, D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
                }
                var destination = D2D.Rect(frame);
                var sourceRect = D2D.Rect(cropped);
                context->DrawBitmap((ID2D1Bitmap*)bitmap, &destination, 1, D2D1_INTERPOLATION_MODE.D2D1_INTERPOLATION_MODE_HIGH_QUALITY_CUBIC,
                                    &sourceRect, null);
                if (maskGeometry != null)
                {
                    context->PopLayer();
                }
                else
                {
                    context->PopAxisAlignedClip();
                }
            });
        }
        finally
        {
            Native.Release(ref maskGeometry);
            Native.Release(ref bitmap);
        }
    }

    public void Dispose()
    {
        effects?.Dispose();
        effects = null;
        exporter.Dispose();
    }

    /// <summary>Swift <c>annotatedSource</c>: the drawings (no covers or effects) rendered over the source at one point per pixel.</summary>
    private RasterImage AnnotatedSource(RasterImage source, ElementList elements)
    {
        var drawings = elements.Filter(element => !element.Tool.IsProtection);
        if (drawings.IsEmpty)
        {
            return source;
        }
        var scene = new SceneBuilder(measurer).Build(drawings);
        var renderer = exporter.Renderer;
        foreach (var list in scene.Elements)
        {
            renderer.Enter(list);
        }
        var bitmap = exporter.Canvas.Upload(source);
        try
        {
            renderer.CollectImages();
            var area = OffscreenCanvas.Area(source.Width, source.Height, 1);
            var image = exporter.Canvas.Render(source.Width, source.Height, context =>
            {
                var identity = D2D.Identity;
                context->SetTransform(&identity);
                var destination = D2D.Rect(0, 0, source.Width, source.Height);
                context->DrawBitmap((ID2D1Bitmap*)bitmap, &destination, 1, D2D1_INTERPOLATION_MODE.D2D1_INTERPOLATION_MODE_NEAREST_NEIGHBOR, null, null);
                renderer.Draw((nint)context, scene.Elements, area);
            });
            if ((renderer.TakeIssues() & RenderIssues.ImageUnavailable) != 0)
            {
                LastIssues |= SnapshotIssues.ImageUnavailable;
            }
            return image;
        }
        finally
        {
            Native.Release(ref bitmap);
            foreach (var list in scene.Elements)
            {
                renderer.Leave(list);
            }
            renderer.CollectImages();
        }
    }

    /// <summary>
    /// Swift <c>flattenedSource</c> through <c>CaptureCompositor</c> (no image layers: the drawings, images included,
    /// are already in <paramref name="source"/>): covers and pixel effects on the GPU in the Mac's order.
    /// </summary>
    private RasterImage Flatten(RasterImage source, ElementList elements)
    {
        var protects = false;
        foreach (ref readonly var element in elements)
        {
            if (!element.Tool.IsProtection)
            {
                continue;
            }
            if (element.Tool.IsPixelEffect)
            {
                element.Validate();
            }
            if (!element.Frame.IsValid)
            {
                throw new RenderFailure(RenderFailureKind.InvalidSize);
            }
            protects = true;
        }
        if (!protects)
        {
            return source;
        }
        var list = EffectList.From(elements, 1, source.Width, source.Height);
        effects ??= new EffectPipeline(devices);
        var device = (ID3D11Device*)devices.D3D11Device;
        ID3D11Texture2D* input = null;
        ID3D11Texture2D* target = null;
        ID3D11Texture2D* staging = null;
        try
        {
            var description = TextureDescription(source.Width, source.Height, D3D11_USAGE.D3D11_USAGE_IMMUTABLE, D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE);
            fixed (byte* pixels = source.Buffer)
            {
                var data = new D3D11_SUBRESOURCE_DATA { pSysMem = pixels, SysMemPitch = (uint)source.Stride };
                Native.Check(device->CreateTexture2D(&description, &data, &input), "ID3D11Device::CreateTexture2D(snapshot source)");
            }
            description = TextureDescription(source.Width, source.Height, D3D11_USAGE.D3D11_USAGE_DEFAULT,
                                             D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE);
            Native.Check(device->CreateTexture2D(&description, null, &target), "ID3D11Device::CreateTexture2D(snapshot effects)");
            description = TextureDescription(source.Width, source.Height, D3D11_USAGE.D3D11_USAGE_STAGING, 0);
            description.CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
            Native.Check(device->CreateTexture2D(&description, null, &staging), "ID3D11Device::CreateTexture2D(snapshot readback)");
            using var held = effects.LockContext();
            effects.RenderScene((nint)input, (nint)target, list);
            var context = (ID3D11DeviceContext*)devices.D3D11DeviceContext;
            context->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)target);
            D3D11_MAPPED_SUBRESOURCE mapped;
            Native.Check(context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped), "ID3D11DeviceContext::Map(snapshot readback)");
            try
            {
                var stride = source.Width * 4;
                var output = GC.AllocateUninitializedArray<byte>(stride * source.Height);
                for (var row = 0; row < source.Height; row++)
                {
                    new ReadOnlySpan<byte>((byte*)mapped.pData + (long)row * mapped.RowPitch, stride).CopyTo(output.AsSpan(row * stride, stride));
                }
                return new RasterImage(source.Width, source.Height, stride, output);
            }
            finally
            {
                context->Unmap((ID3D11Resource*)staging, 0);
            }
        }
        finally
        {
            Native.Release(ref staging);
            Native.Release(ref target);
            Native.Release(ref input);
        }
    }

    private static D3D11_TEXTURE2D_DESC TextureDescription(int width, int height, D3D11_USAGE usage, D3D11_BIND_FLAG bind) => new()
    {
        Width = (uint)width,
        Height = (uint)height,
        MipLevels = 1,
        ArraySize = 1,
        Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
        SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
        Usage = usage,
        BindFlags = bind,
    };

    private void FillGradient(ID2D1DeviceContext1* context, RGBA start, Size size)
    {
        var resources = exporter.Renderer.ResourceContext;
        var stops = stackalloc D2D1_GRADIENT_STOP[2];
        stops[0] = new D2D1_GRADIENT_STOP { position = 0, color = D2D.Color(start) };
        stops[1] = new D2D1_GRADIENT_STOP { position = 1, color = D2D.Color(GradientEnd) };
        ID2D1GradientStopCollection* collection = null;
        ID2D1LinearGradientBrush* brush = null;
        try
        {
            Native.Check(resources->CreateGradientStopCollection(stops, 2, D2D1_GAMMA.D2D1_GAMMA_2_2, D2D1_EXTEND_MODE.D2D1_EXTEND_MODE_CLAMP, &collection),
                         "ID2D1RenderTarget::CreateGradientStopCollection");
            var properties = new D2D1_LINEAR_GRADIENT_BRUSH_PROPERTIES { startPoint = D2D.Point(0, 0), endPoint = D2D.Point(size.Width, size.Height) };
            Native.Check(resources->CreateLinearGradientBrush(&properties, null, collection, &brush), "ID2D1RenderTarget::CreateLinearGradientBrush");
            var whole = D2D.Rect(0, 0, size.Width, size.Height);
            context->FillRectangle(&whole, (ID2D1Brush*)brush);
        }
        finally
        {
            Native.Release(ref brush);
            Native.Release(ref collection);
        }
    }
}
