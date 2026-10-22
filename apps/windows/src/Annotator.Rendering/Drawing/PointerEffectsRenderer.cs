using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Rendering.Interop;
using Windows.Win32;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;

namespace Annotator.Rendering.Drawing;

/// <summary>
/// Draws the pointer effects that Annotator.Core describes: the cursor halo (Swift <c>CursorHighlightRenderer</c>)
/// and the spotlight dimming (Swift <c>SpotlightRenderer</c>). Shares the device resources of a
/// <see cref="DisplayListRenderer"/>; use it on the same thread.
/// </summary>
/// <remarks>
/// The halo's glow is Core Graphics' shadow with zero offset: a Gaussian blur of the shape's coverage in the halo
/// color at alpha 0.75, under the shape, all inside one layer at the color's alpha. Core Graphics blurs in device pixels
/// with a standard deviation of about half the blur value; the Mac draws at 2x, so the glow's deviation is a quarter of
/// its radius in points (<see cref="CursorHighlightFrame.GlowDeviationPerRadius"/>), fitted against the Mac's
/// pointer/halo-*-glow references (the RenderHarness pointer mode).
/// </remarks>
public sealed unsafe class PointerEffectsRenderer : IDisposable
{
    private const uint ShadowStandardDeviation = 0;
    private const uint ShadowColor = 1;
    private const uint ShadowOptimization = 2;

    private readonly DisplayListRenderer renderer;
    private readonly PathGeometryBuilder builder = new();
    private readonly Dictionary<StrokeStyle, nint> strokes = [];
    private PathData? shapePath;
    private ID2D1PathGeometry1* shapeGeometry;
    private PathData? ripplePath;
    private ID2D1PathGeometry1* rippleGeometry;
    private ID2D1Effect* shadow;
    private ID2D1RadialGradientBrush* spotlight;
    private double spotlightRatio = double.NaN;

    public PointerEffectsRenderer(DisplayListRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        this.renderer = renderer;
    }

    /// <summary>Command lists recorded for glows; one per halo frame drawn with a glow.</summary>
    public long GlowRecordings { get; private set; }

    /// <summary>
    /// Draws one halo frame (positions in points) into an ID2D1DeviceContext between BeginDraw and EndDraw.
    /// <paramref name="glowScale"/> is the glow's pixels per point when it differs from the area's scale: Core Graphics
    /// blurs shadows in device space, so a halo drawn under an extra scale (the Pointer page preview) keeps its glow size.
    /// </summary>
    public void DrawHalo(nint deviceContext, CursorHighlightFrame frame, in DrawArea area, double? glowScale = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var context = renderer.Begin(deviceContext, area);
        try
        {
            var transform = AffineTransform.Concat(frame.Transform, area.PointsToPixels);
            var shape = Geometry(frame.Shape, ref shapePath, ref shapeGeometry);
            var layer = new D2D1_LAYER_PARAMETERS1
            {
                contentBounds = D2D.Infinite,
                maskAntialiasMode = D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_PER_PRIMITIVE,
                maskTransform = D2D.Identity,
                opacity = (float)frame.Opacity,
                layerOptions = D2D1_LAYER_OPTIONS1.D2D1_LAYER_OPTIONS1_NONE,
            };
            context->PushLayer(&layer, null);
            var brush = renderer.SolidBrush;
            var color = D2D.Color(frame.Color);
            brush->SetColor(&color);
            brush->SetOpacity(1);
            if (frame.GlowRadius > 0)
            {
                DrawGlow(context, frame, shape, transform, glowScale ?? area.Scale);
                brush->SetColor(&color);
                brush->SetOpacity(1);
            }
            var matrix = D2D.Matrix(transform);
            context->SetTransform(&matrix);
            if (frame.Filled)
            {
                context->FillGeometry((ID2D1Geometry*)shape, (ID2D1Brush*)brush, null);
            }
            else
            {
                context->DrawGeometry((ID2D1Geometry*)shape, (ID2D1Brush*)brush, (float)frame.Stroke.Width, Stroke(frame.Stroke));
            }
            if (frame.Ripple is { } ripple && frame.RippleStroke is { } rippleStroke)
            {
                var geometry = Geometry(ripple, ref ripplePath, ref rippleGeometry);
                brush->SetOpacity((float)frame.RippleOpacity);
                context->DrawGeometry((ID2D1Geometry*)geometry, (ID2D1Brush*)brush, (float)rippleStroke.Width, Stroke(rippleStroke));
                brush->SetOpacity(1);
            }
            context->PopLayer();
        }
        finally
        {
            context->PopAxisAlignedClip();
        }
    }

    /// <summary>
    /// Swift <c>SpotlightRenderer.draw</c>: inside the paint's clip, black from alpha 0 at the inner radius to 0.62 at
    /// the outer radius, extended on both sides, at the paint's opacity.
    /// </summary>
    public void DrawSpotlight(nint deviceContext, SpotlightPaint paint, in DrawArea area)
    {
        var context = renderer.Begin(deviceContext, area);
        try
        {
            var matrix = D2D.Matrix(area.PointsToPixels);
            context->SetTransform(&matrix);
            var clip = D2D.Rect(paint.Clip);
            context->PushAxisAlignedClip(&clip, D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
            var brush = SpotlightBrush(paint.InnerRadius / paint.OuterRadius);
            brush->SetCenter(D2D.Point(paint.Center));
            brush->SetRadiusX((float)paint.OuterRadius);
            brush->SetRadiusY((float)paint.OuterRadius);
            brush->SetOpacity((float)paint.Opacity);
            context->FillRectangle(&clip, (ID2D1Brush*)brush);
            context->PopAxisAlignedClip();
        }
        finally
        {
            context->PopAxisAlignedClip();
        }
    }

    public void Dispose()
    {
        foreach (var style in strokes.Values)
        {
            var pointer = (ID2D1StrokeStyle1*)style;
            Native.Release(ref pointer);
        }
        strokes.Clear();
        Native.Release(ref shapeGeometry);
        Native.Release(ref rippleGeometry);
        Native.Release(ref shadow);
        Native.Release(ref spotlight);
    }

    private void DrawGlow(ID2D1DeviceContext1* context, CursorHighlightFrame frame, ID2D1PathGeometry1* shape, in AffineTransform transform, double scale)
    {
        // Record the shape in device pixels on the renderer's resource context, which is never inside a draw.
        var recorder = renderer.ResourceContext;
        ID2D1CommandList* list;
        Native.Check(recorder->CreateCommandList(&list), "ID2D1DeviceContext::CreateCommandList");
        try
        {
            recorder->SetTarget((ID2D1Image*)list);
            recorder->BeginDraw();
            var matrix = D2D.Matrix(transform);
            recorder->SetTransform(&matrix);
            recorder->SetAntialiasMode(D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
            var brush = renderer.SolidBrush;
            var opaque = new D2D1_COLOR_F { r = 0, g = 0, b = 0, a = 1 };
            brush->SetColor(&opaque);
            brush->SetOpacity(1);
            if (frame.Filled)
            {
                recorder->FillGeometry((ID2D1Geometry*)shape, (ID2D1Brush*)brush, null);
            }
            else
            {
                recorder->DrawGeometry((ID2D1Geometry*)shape, (ID2D1Brush*)brush, (float)frame.Stroke.Width, Stroke(frame.Stroke));
            }
            var ended = recorder->EndDraw(null, null);
            recorder->SetTarget(null);
            Native.Check(ended, "ID2D1DeviceContext::EndDraw(command list)");
            Native.Check(list->Close(), "ID2D1CommandList::Close");
            GlowRecordings++;

            var effect = Shadow();
            effect->SetInput(0, (ID2D1Image*)list, true);
            var deviation = (float)(frame.GlowRadius * CursorHighlightFrame.GlowDeviationPerRadius * scale);
            Native.Check(effect->SetValue(ShadowStandardDeviation, D2D1_PROPERTY_TYPE.D2D1_PROPERTY_TYPE_FLOAT, (byte*)&deviation, sizeof(float)),
                         "ID2D1Effect::SetValue(shadow deviation)");
            var tint = stackalloc float[4] { (float)frame.Color.Red, (float)frame.Color.Green, (float)frame.Color.Blue, 0.75f };
            Native.Check(effect->SetValue(ShadowColor, D2D1_PROPERTY_TYPE.D2D1_PROPERTY_TYPE_VECTOR4, (byte*)tint, 4 * sizeof(float)),
                         "ID2D1Effect::SetValue(shadow color)");
            ID2D1Image* output;
            effect->GetOutput(&output);
            var identity = D2D.Identity;
            context->SetTransform(&identity);
            context->DrawImage(output, null, null, D2D1_INTERPOLATION_MODE.D2D1_INTERPOLATION_MODE_LINEAR, D2D1_COMPOSITE_MODE.D2D1_COMPOSITE_MODE_SOURCE_OVER);
            Native.Release(ref output);
            effect->SetInput(0, null, false);
        }
        finally
        {
            Native.Release(ref list);
        }
    }

    private ID2D1Effect* Shadow()
    {
        if (shadow == null)
        {
            Guid id = PInvoke.CLSID_D2D1Shadow;
            ID2D1Effect* effect;
            Native.Check(renderer.ResourceContext->CreateEffect(&id, &effect), "ID2D1DeviceContext::CreateEffect(shadow)");
            uint quality = 2; // D2D1_SHADOW_OPTIMIZATION_QUALITY: no downsampling, so the glow is exact.
            Native.Check(effect->SetValue(ShadowOptimization, D2D1_PROPERTY_TYPE.D2D1_PROPERTY_TYPE_ENUM, (byte*)&quality, sizeof(uint)),
                         "ID2D1Effect::SetValue(shadow optimization)");
            shadow = effect;
        }
        return shadow;
    }

    private ID2D1RadialGradientBrush* SpotlightBrush(double ratio)
    {
        ratio = Math.Clamp(ratio, 0, 1);
        if (spotlight != null && ratio == spotlightRatio)
        {
            return spotlight;
        }
        Native.Release(ref spotlight);
        var stops = stackalloc D2D1_GRADIENT_STOP[2];
        stops[0] = new D2D1_GRADIENT_STOP { position = (float)ratio, color = new D2D1_COLOR_F { r = 0, g = 0, b = 0, a = 0 } };
        stops[1] = new D2D1_GRADIENT_STOP { position = 1, color = new D2D1_COLOR_F { r = 0, g = 0, b = 0, a = (float)SpotlightPaint.EdgeAlpha } };
        ID2D1GradientStopCollection* collection;
        Native.Check(renderer.ResourceContext->CreateGradientStopCollection(stops, 2, D2D1_GAMMA.D2D1_GAMMA_2_2, D2D1_EXTEND_MODE.D2D1_EXTEND_MODE_CLAMP, &collection),
                     "ID2D1RenderTarget::CreateGradientStopCollection");
        try
        {
            var properties = new D2D1_RADIAL_GRADIENT_BRUSH_PROPERTIES { radiusX = 1, radiusY = 1 };
            ID2D1RadialGradientBrush* brush;
            Native.Check(renderer.ResourceContext->CreateRadialGradientBrush(&properties, null, collection, &brush), "ID2D1RenderTarget::CreateRadialGradientBrush");
            spotlight = brush;
            spotlightRatio = ratio;
            return brush;
        }
        finally
        {
            Native.Release(ref collection);
        }
    }

    private ID2D1PathGeometry1* Geometry(PathData path, ref PathData? cachedPath, ref ID2D1PathGeometry1* cached)
    {
        if (cached != null && cachedPath is not null && (ReferenceEquals(cachedPath, path) || cachedPath.ContentEquals(path)))
        {
            return cached;
        }
        Native.Release(ref cached);
        cached = builder.Build(renderer.Factory, path, FillRule.NonZero);
        cachedPath = path;
        return cached;
    }

    private ID2D1StrokeStyle* Stroke(StrokeStyle style)
    {
        if (!strokes.TryGetValue(style, out var known))
        {
            if (strokes.Count >= 32)
            {
                foreach (var old in strokes.Values)
                {
                    var pointer = (ID2D1StrokeStyle1*)old;
                    Native.Release(ref pointer);
                }
                strokes.Clear();
            }
            known = (nint)DisplayListRenderer.CreateStrokeStyle(renderer.Factory, style);
            strokes[style] = known;
        }
        return (ID2D1StrokeStyle*)known;
    }
}
