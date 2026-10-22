using System.Runtime.InteropServices;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Platform;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Export;
using Annotator.Rendering.Imaging;
using Annotator.Rendering.Interop;
using Annotator.Rendering.Text;
using Annotator.Workspace.Artwork;
using Annotator.Workspace.FirstUse;
using Windows.Win32;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.DirectWrite;

namespace Annotator.Rendering.Illustrations;

/// <summary>
/// Draws the tour's illustrations (<see cref="IntroductionScene"/>) with Direct2D, as the Mac's SwiftUI Canvas draws them:
/// opacity, shadows and blurs apply to each drawing operation on its own, clips are antialiased geometry masks, gradients
/// interpolate in gamma-encoded sRGB, and text sits on the Mac's baselines in the Windows fonts (D-4). Mac SF Symbols are
/// drawn as their Windows icons (D-6) from <paramref name="symbols"/>. Use it on one thread; after a device loss, dispose
/// it and create another for the new devices.
/// </summary>
public sealed unsafe class IllustrationRenderer : IDisposable
{
    private const uint ShadowDeviation = 0;
    private const uint ShadowColor = 1;
    private const uint ShadowOptimization = 2;
    private const uint BlurDeviation = 0;
    private const uint BlurOptimization = 1;
    private const uint BlurBorder = 2;
    private const int ConicWedges = 120;

    /// <summary>SwiftUI centers a text's line box on its anchor: the Mac's first baseline sits this many ems below the anchor.</summary>
    private const double CenterToBaseline = MacTextMetrics.AscenderPerEm - (MacTextMetrics.AscenderPerEm + MacTextMetrics.DescenderPerEm) / 2;

    /// <summary>The icons' 24-unit grid against an SF Symbol's point size, as the app's other glyphs use it (16 for 14).</summary>
    private const double SymbolBox = 16.0 / 14.0;

    /// <summary>A semibold SF Symbol's stroke on the icons' grid; Lucide's own is 2.</summary>
    private const double SymbolStroke = 2.25;

    private static readonly Guid GaussianBlur = new("1FEB6D69-2FE6-4AC9-8C58-1D7F93E7A6A5");

    private readonly GraphicsDevices devices;
    private readonly Func<string, IReadOnlyList<IconRaster.Part>?> symbols;
    private readonly PathGeometryBuilder builder = new();
    private readonly TextSystem textSystem;
    private readonly Dictionary<(string Text, TextFont Font), TextLayoutEntry> layouts = [];
    private readonly Dictionary<IReadOnlyList<IllustrationStop>, nint> stops = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(LineCap, LineJoin), nint> strokeStyles = [];
    private readonly Dictionary<string, (PathData Path, bool Filled)[]> symbolPaths = new(StringComparer.Ordinal);
    private readonly SceneCanvas canvas;
    private ID2D1Factory1* factory;
    private ID2D1DeviceContext1* recorder;
    private ID2D1SolidColorBrush* solid;
    private ID2D1Effect* shadow;
    private ID2D1Effect* blur;
    private OffscreenCanvas? offscreen;

    /// <param name="symbols">The parts of the Windows icon for a Mac SF Symbol name (Icons/LucideIcons.g.cs), or null.</param>
    public IllustrationRenderer(GraphicsDevices devices, Func<string, IReadOnlyList<IconRaster.Part>?> symbols)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(symbols);
        this.devices = devices;
        this.symbols = symbols;
        textSystem = TextSystem.Create();
        try
        {
            factory = Native.AddRef((ID2D1Factory1*)devices.D2DFactory);
            var device = (ID2D1Device*)devices.D2DDevice;
            ID2D1DeviceContext* context;
            Native.Check(device->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS.D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &context), "ID2D1Device::CreateDeviceContext");
            recorder = Native.Query<ID2D1DeviceContext1>(context, ID2D1DeviceContext1.IID_Guid, "QueryInterface(ID2D1DeviceContext1)");
            Native.Release(ref context);
            var white = new D2D1_COLOR_F { r = 1, g = 1, b = 1, a = 1 };
            ID2D1SolidColorBrush* brush;
            Native.Check(recorder->CreateSolidColorBrush(&white, null, &brush), "ID2D1DeviceContext::CreateSolidColorBrush");
            solid = brush;
        }
        catch
        {
            Dispose();
            throw;
        }
        canvas = new SceneCanvas(this);
    }

    /// <summary>Operations drawn with a shadow or a blur in the last draw (measurements).</summary>
    public int FilteredOperations { get; private set; }

    /// <summary>
    /// Draws <paramref name="page"/> at <paramref name="progress"/> into <paramref name="deviceContext"/> (an
    /// ID2D1DeviceContext between BeginDraw and EndDraw), in points mapped to pixels by <paramref name="area"/>. With
    /// <paramref name="clear"/>, the area's clip is cleared to transparent first, as a composition surface's update needs.
    /// </summary>
    public void Draw(nint deviceContext, IntroductionPage page, double progress, bool dark, Size size, in DrawArea area, bool clear = false)
    {
        if (deviceContext == 0)
        {
            throw new ArgumentException("A device context is required.", nameof(deviceContext));
        }
        ObjectDisposedException.ThrowIf(factory == null, this);
        ID2D1DeviceContext1* context = Native.Query<ID2D1DeviceContext1>((void*)deviceContext, ID2D1DeviceContext1.IID_Guid,
            "QueryInterface(ID2D1DeviceContext1)");
        try
        {
            context->SetDpi(96, 96);
            context->SetUnitMode(D2D1_UNIT_MODE.D2D1_UNIT_MODE_DIPS);
            context->SetAntialiasMode(D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
            context->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE.D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
            context->SetPrimitiveBlend(D2D1_PRIMITIVE_BLEND.D2D1_PRIMITIVE_BLEND_SOURCE_OVER);
            var identity = D2D.Identity;
            context->SetTransform(&identity);
            var clip = D2D.Rect(area.Clip.Left, area.Clip.Top, area.Clip.Right, area.Clip.Bottom);
            context->PushAxisAlignedClip(&clip, D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_ALIASED);
            if (clear)
            {
                var transparent = default(D2D1_COLOR_F);
                context->Clear(&transparent);
            }
            FilteredOperations = 0;
            canvas.Begin(context, area);
            try
            {
                new IntroductionScene(size, progress, dark).Draw(page, canvas);
            }
            finally
            {
                canvas.End();
                context->PopAxisAlignedClip();
            }
        }
        finally
        {
            Native.Release(ref context);
        }
    }

    /// <summary>The illustration drawn offscreen and read back, for tests and reference images: premultiplied BGRA.</summary>
    public RasterImage Render(IntroductionPage page, double progress, bool dark, Size size, double scale)
    {
        int width = (int)Math.Ceiling(size.Width * scale), height = (int)Math.Ceiling(size.Height * scale);
        offscreen ??= new OffscreenCanvas(devices);
        return offscreen.Render(width, height, context => Draw((nint)context, page, progress, dark, size, OffscreenCanvas.Area(width, height, scale)));
    }

    public void Dispose()
    {
        foreach (TextLayoutEntry entry in layouts.Values)
        {
            var layout = (IDWriteTextLayout*)entry.Layout;
            Native.Release(ref layout);
        }
        layouts.Clear();
        foreach (nint collection in stops.Values)
        {
            var pointer = (ID2D1GradientStopCollection*)collection;
            Native.Release(ref pointer);
        }
        stops.Clear();
        foreach (nint style in strokeStyles.Values)
        {
            var pointer = (ID2D1StrokeStyle1*)style;
            Native.Release(ref pointer);
        }
        strokeStyles.Clear();
        offscreen?.Dispose();
        offscreen = null;
        Native.Release(ref shadow);
        Native.Release(ref blur);
        Native.Release(ref solid);
        Native.Release(ref recorder);
        Native.Release(ref factory);
        textSystem?.Dispose();
    }

    private readonly record struct TextLayoutEntry(nint Layout, float Baseline, double Width);

    private TextLayoutEntry Layout(string text, TextFont font)
    {
        if (layouts.TryGetValue((text, font), out TextLayoutEntry known))
        {
            return known;
        }
        if (layouts.Count >= 64)
        {
            foreach (TextLayoutEntry old in layouts.Values)
            {
                var pointer = (IDWriteTextLayout*)old.Layout;
                Native.Release(ref pointer);
            }
            layouts.Clear();
        }
        IDWriteTextLayout* layout = textSystem.CreateLayout(text, font, TextAlignment.Left, 4096, 4096);
        try
        {
            DWRITE_TEXT_METRICS metrics;
            Native.Check(layout->GetMetrics(&metrics), "IDWriteTextLayout::GetMetrics");
            var entry = new TextLayoutEntry((nint)layout, TextSystem.FirstBaseline(layout), metrics.widthIncludingTrailingWhitespace);
            layouts[(text, font)] = entry;
            return entry;
        }
        catch
        {
            Native.Release(ref layout);
            throw;
        }
    }

    private ID2D1GradientStopCollection* Stops(IReadOnlyList<IllustrationStop> list)
    {
        if (stops.TryGetValue(list, out nint known))
        {
            return (ID2D1GradientStopCollection*)known;
        }
        if (stops.Count >= 32)
        {
            foreach (nint old in stops.Values)
            {
                var pointer = (ID2D1GradientStopCollection*)old;
                Native.Release(ref pointer);
            }
            stops.Clear();
        }
        Span<D2D1_GRADIENT_STOP> native = list.Count <= 16 ? stackalloc D2D1_GRADIENT_STOP[list.Count] : new D2D1_GRADIENT_STOP[list.Count];
        for (int index = 0; index < list.Count; index++)
        {
            native[index] = new D2D1_GRADIENT_STOP { position = (float)list[index].Location, color = D2D.Color(list[index].Color) };
        }
        ID2D1GradientStopCollection* collection;
        fixed (D2D1_GRADIENT_STOP* pointer = native)
        {
            // Gamma 2.2 interpolates in encoded sRGB, like a Core Graphics gradient; clamping extends the end colors.
            Native.Check(recorder->CreateGradientStopCollection(pointer, (uint)native.Length, D2D1_GAMMA.D2D1_GAMMA_2_2,
                                                                D2D1_EXTEND_MODE.D2D1_EXTEND_MODE_CLAMP, &collection),
                         "ID2D1RenderTarget::CreateGradientStopCollection");
        }
        stops[list] = (nint)collection;
        return collection;
    }

    private ID2D1StrokeStyle1* StrokeStyle(LineCap cap, LineJoin join)
    {
        if (!strokeStyles.TryGetValue((cap, join), out nint known))
        {
            known = (nint)DisplayListRenderer.CreateStrokeStyle(factory, new Annotator.Core.Scene.StrokeStyle(1, cap, join));
            strokeStyles[(cap, join)] = known;
        }
        return (ID2D1StrokeStyle1*)known;
    }

    private (PathData Path, bool Filled)[] Symbol(string name)
    {
        if (!symbolPaths.TryGetValue(name, out (PathData, bool)[]? parts))
        {
            IReadOnlyList<IconRaster.Part> source = symbols(name)
                ?? throw new ArgumentException($"No Windows icon is mapped for the SF Symbol \"{name}\".", nameof(name));
            parts = [.. source.Select(part => (IconPaths.Parse(part.Data), part.Filled))];
            symbolPaths[name] = parts;
        }
        return parts;
    }

    private ID2D1Effect* Effect(ref ID2D1Effect* cached, bool isShadow)
    {
        if (cached != null)
        {
            return cached;
        }
        Guid id = isShadow ? PInvoke.CLSID_D2D1Shadow : GaussianBlur;
        ID2D1Effect* created;
        Native.Check(recorder->CreateEffect(&id, &created), isShadow ? "ID2D1DeviceContext::CreateEffect(shadow)" : "ID2D1DeviceContext::CreateEffect(blur)");
        try
        {
            // Exact (no downsampling) for these small images; a blur fades at its edges, as a SwiftUI blur does.
            uint quality = 2;
            Native.Check(created->SetValue(isShadow ? ShadowOptimization : BlurOptimization, D2D1_PROPERTY_TYPE.D2D1_PROPERTY_TYPE_ENUM,
                                           (byte*)&quality, sizeof(uint)), "ID2D1Effect::SetValue(optimization)");
            if (!isShadow)
            {
                uint soft = 0;
                Native.Check(created->SetValue(BlurBorder, D2D1_PROPERTY_TYPE.D2D1_PROPERTY_TYPE_ENUM, (byte*)&soft, sizeof(uint)),
                             "ID2D1Effect::SetValue(blur border)");
            }
        }
        catch
        {
            Native.Release(ref created);
            throw;
        }
        cached = created;
        return created;
    }

    /// <summary>The scene's drawing surface for one draw: the Direct2D side of <see cref="IIllustrationCanvas"/>.</summary>
    private sealed class SceneCanvas(IllustrationRenderer owner) : IIllustrationCanvas
    {
        private enum Saved : byte
        {
            Opacity,
            Transform,
            Clip,
            Filter,
        }

        private readonly Stack<(Saved Kind, double Opacity, AffineTransform Transform, Filter Filter)> _stack = new();
        private ID2D1DeviceContext1* _context;
        private double _scale;
        private double _opacity;
        private AffineTransform _transform;
        private Filter _filter;

        private readonly record struct Filter(bool Active, bool IsShadow, RGBA Color, double Radius, double OffsetX, double OffsetY);

        public void Begin(ID2D1DeviceContext1* context, in DrawArea area)
        {
            _context = context;
            _scale = area.Scale;
            _opacity = 1;
            _transform = area.PointsToPixels;
            _filter = default;
            _stack.Clear();
        }

        public void End()
        {
            while (_stack.Count > 0)
            {
                Pop();
            }
            _context = null;
        }

        public void PushOpacity(double opacity)
        {
            _stack.Push((Saved.Opacity, _opacity, _transform, _filter));
            _opacity *= Math.Clamp(opacity, 0, 1);
        }

        public void PushTransform(in AffineTransform transform)
        {
            _stack.Push((Saved.Transform, _opacity, _transform, _filter));
            _transform = AffineTransform.Concat(transform, _transform);
        }

        public void PushClip(PathData path)
        {
            ID2D1PathGeometry1* geometry = owner.builder.Build(owner.factory, path, FillRule.NonZero);
            try
            {
                PushMask(geometry, 1);
            }
            finally
            {
                Native.Release(ref geometry);
            }
            _stack.Push((Saved.Clip, _opacity, _transform, _filter));
        }

        /// <summary>A layer masked by <paramref name="geometry"/> in the current transform: the world transform also moves a layer's mask, so it is reset first.</summary>
        private void PushMask(ID2D1PathGeometry1* geometry, double opacity)
        {
            var identity = D2D.Identity;
            _context->SetTransform(&identity);
            var parameters = new D2D1_LAYER_PARAMETERS1
            {
                contentBounds = D2D.Infinite,
                geometricMask = (ID2D1Geometry*)geometry,
                maskAntialiasMode = D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_PER_PRIMITIVE,
                maskTransform = D2D.Matrix(_transform),
                opacity = (float)opacity,
                layerOptions = D2D1_LAYER_OPTIONS1.D2D1_LAYER_OPTIONS1_NONE,
            };
            _context->PushLayer(&parameters, null);
        }

        public void PushShadow(RGBA color, double radius, double offsetX, double offsetY)
        {
            _stack.Push((Saved.Filter, _opacity, _transform, _filter));
            _filter = new Filter(true, true, color, radius, offsetX, offsetY);
        }

        public void PushBlur(double radius)
        {
            _stack.Push((Saved.Filter, _opacity, _transform, _filter));
            _filter = new Filter(true, false, default, radius, 0, 0);
        }

        public void Pop()
        {
            (Saved kind, double opacity, AffineTransform transform, Filter filter) = _stack.Pop();
            if (kind == Saved.Clip)
            {
                _context->PopLayer();
            }
            _opacity = opacity;
            _transform = transform;
            _filter = filter;
        }

        public void Fill(PathData path, IllustrationPaint paint, FillRule rule = FillRule.NonZero)
        {
            if (!(_opacity > 0) || path.IsEmpty)
            {
                return;
            }
            ID2D1PathGeometry1* geometry = owner.builder.Build(owner.factory, path, rule);
            try
            {
                if (paint.Kind == IllustrationPaintKind.Conic)
                {
                    FillConic(geometry, paint);
                    return;
                }
                Draw(target =>
                {
                    ID2D1Brush* brush = Brush(target, paint, out bool owned);
                    try
                    {
                        target->FillGeometry((ID2D1Geometry*)geometry, brush, null);
                    }
                    finally
                    {
                        if (owned)
                        {
                            Native.Release(ref brush);
                        }
                    }
                });
            }
            finally
            {
                Native.Release(ref geometry);
            }
        }

        public void Stroke(PathData path, IllustrationPaint paint, double width, LineCap cap = LineCap.Butt, LineJoin join = LineJoin.Miter)
        {
            if (!(_opacity > 0) || path.IsEmpty || !(width > 0))
            {
                return;
            }
            ID2D1PathGeometry1* geometry = owner.builder.Build(owner.factory, path, FillRule.NonZero);
            ID2D1StrokeStyle1* style = owner.StrokeStyle(cap, join);
            try
            {
                Draw(target =>
                {
                    ID2D1Brush* brush = Brush(target, paint, out bool owned);
                    try
                    {
                        target->DrawGeometry((ID2D1Geometry*)geometry, brush, (float)width, (ID2D1StrokeStyle*)style);
                    }
                    finally
                    {
                        if (owned)
                        {
                            Native.Release(ref brush);
                        }
                    }
                });
            }
            finally
            {
                Native.Release(ref geometry);
            }
        }

        public void DrawText(string text, TextFont font, IllustrationPaint paint, Point point, IllustrationAnchor anchor)
        {
            if (!(_opacity > 0) || text.Length == 0)
            {
                return;
            }
            TextLayoutEntry entry = owner.Layout(text, font);
            double x = anchor switch
            {
                IllustrationAnchor.Leading => point.X,
                IllustrationAnchor.Trailing => point.X - entry.Width,
                _ => point.X - entry.Width / 2,
            };
            double baseline = point.Y + CenterToBaseline * font.Size;
            var origin = D2D.Point(x, baseline - entry.Baseline);
            Draw(target =>
            {
                ID2D1Brush* brush = Brush(target, paint, out bool owned);
                try
                {
                    D2D1_DRAW_TEXT_OPTIONS options = paint.Kind == IllustrationPaintKind.Solid
                        ? D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT
                        : D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE;
                    target->DrawTextLayout(origin, (IDWriteTextLayout*)entry.Layout, brush, options);
                }
                finally
                {
                    if (owned)
                    {
                        Native.Release(ref brush);
                    }
                }
            });
        }

        public double MeasureText(string text, TextFont font) => text.Length == 0 ? 0 : owner.Layout(text, font).Width;

        public void DrawSymbol(string symbol, Point center, double size, RGBA color)
        {
            double box = size * SymbolBox;
            PushTransform(AffineTransform.Translation(center.X - box / 2, center.Y - box / 2).Scaled(box / 24, box / 24));
            try
            {
                IllustrationPaint paint = IllustrationPaint.Solid(color);
                foreach ((PathData path, bool filled) in owner.Symbol(symbol))
                {
                    if (filled)
                    {
                        Fill(path, paint);
                    }
                    Stroke(path, paint, SymbolStroke, LineCap.Round, LineJoin.Round);
                }
            }
            finally
            {
                Pop();
            }
        }

        /// <summary>
        /// Draws one operation with the current transform: directly with the current opacity, or, under a shadow or a
        /// blur, recorded and drawn through the effect, as SwiftUI filters each operation on its own.
        /// </summary>
        private void Draw(DrawOperation operation)
        {
            if (!_filter.Active)
            {
                var matrix = D2D.Matrix(_transform);
                _context->SetTransform(&matrix);
                operation(_context);
                return;
            }
            owner.FilteredOperations++;
            ID2D1DeviceContext1* recorder = owner.recorder;
            ID2D1CommandList* list;
            Native.Check(recorder->CreateCommandList(&list), "ID2D1DeviceContext::CreateCommandList");
            try
            {
                double opacity = _opacity;
                _opacity = 1;
                recorder->SetTarget((ID2D1Image*)list);
                recorder->BeginDraw();
                recorder->SetAntialiasMode(D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
                recorder->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE.D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
                var matrix = D2D.Matrix(_transform);
                recorder->SetTransform(&matrix);
                try
                {
                    operation(recorder);
                }
                finally
                {
                    _opacity = opacity;
                    var ended = recorder->EndDraw(null, null);
                    recorder->SetTarget(null);
                    Native.Check(ended, "ID2D1DeviceContext::EndDraw(command list)");
                }
                Native.Check(list->Close(), "ID2D1CommandList::Close");
                var identity = D2D.Identity;
                _context->SetTransform(&identity);
                bool layer = opacity < 1;
                if (layer)
                {
                    var parameters = new D2D1_LAYER_PARAMETERS1
                    {
                        contentBounds = D2D.Infinite,
                        maskAntialiasMode = D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_PER_PRIMITIVE,
                        maskTransform = D2D.Identity,
                        opacity = (float)opacity,
                        layerOptions = D2D1_LAYER_OPTIONS1.D2D1_LAYER_OPTIONS1_NONE,
                    };
                    _context->PushLayer(&parameters, null);
                }
                try
                {
                    if (_filter.IsShadow)
                    {
                        ID2D1Effect* effect = owner.Effect(ref owner.shadow, isShadow: true);
                        effect->SetInput(0, (ID2D1Image*)list, true);
                        float deviation = (float)(_filter.Radius / 2 * _scale);
                        Native.Check(effect->SetValue(ShadowDeviation, D2D1_PROPERTY_TYPE.D2D1_PROPERTY_TYPE_FLOAT, (byte*)&deviation, sizeof(float)),
                                     "ID2D1Effect::SetValue(shadow deviation)");
                        float* tint = stackalloc float[4] { (float)_filter.Color.Red, (float)_filter.Color.Green, (float)_filter.Color.Blue, (float)_filter.Color.Alpha };
                        Native.Check(effect->SetValue(ShadowColor, D2D1_PROPERTY_TYPE.D2D1_PROPERTY_TYPE_VECTOR4, (byte*)tint, 4 * sizeof(float)),
                                     "ID2D1Effect::SetValue(shadow color)");
                        DrawEffect(effect, _filter.OffsetX * _scale, _filter.OffsetY * _scale);
                        _context->DrawImage((ID2D1Image*)list, null, null, D2D1_INTERPOLATION_MODE.D2D1_INTERPOLATION_MODE_LINEAR,
                                            D2D1_COMPOSITE_MODE.D2D1_COMPOSITE_MODE_SOURCE_OVER);
                    }
                    else
                    {
                        ID2D1Effect* effect = owner.Effect(ref owner.blur, isShadow: false);
                        effect->SetInput(0, (ID2D1Image*)list, true);
                        float deviation = (float)(_filter.Radius * _scale);
                        Native.Check(effect->SetValue(BlurDeviation, D2D1_PROPERTY_TYPE.D2D1_PROPERTY_TYPE_FLOAT, (byte*)&deviation, sizeof(float)),
                                     "ID2D1Effect::SetValue(blur deviation)");
                        DrawEffect(effect, 0, 0);
                    }
                }
                finally
                {
                    if (layer)
                    {
                        _context->PopLayer();
                    }
                }
            }
            finally
            {
                Native.Release(ref list);
            }
        }

        private void DrawEffect(ID2D1Effect* effect, double offsetX, double offsetY)
        {
            ID2D1Image* output;
            effect->GetOutput(&output);
            try
            {
                var offset = D2D.Point(offsetX, offsetY);
                _context->DrawImage(output, &offset, null, D2D1_INTERPOLATION_MODE.D2D1_INTERPOLATION_MODE_LINEAR,
                                    D2D1_COMPOSITE_MODE.D2D1_COMPOSITE_MODE_SOURCE_OVER);
            }
            finally
            {
                Native.Release(ref output);
                effect->SetInput(0, null, false);
            }
        }

        /// <summary>
        /// A brush for <paramref name="paint"/> at the current opacity. The shared solid brush is not owned; gradient
        /// brushes are created for the call and owned by the caller.
        /// </summary>
        private ID2D1Brush* Brush(ID2D1DeviceContext1* target, IllustrationPaint paint, out bool owned)
        {
            switch (paint.Kind)
            {
                case IllustrationPaintKind.Linear:
                {
                    var properties = new D2D1_LINEAR_GRADIENT_BRUSH_PROPERTIES { startPoint = D2D.Point(paint.Start), endPoint = D2D.Point(paint.End) };
                    ID2D1LinearGradientBrush* brush;
                    Native.Check(owner.recorder->CreateLinearGradientBrush(&properties, null, owner.Stops(paint.Stops), &brush),
                                 "ID2D1RenderTarget::CreateLinearGradientBrush");
                    brush->SetOpacity((float)_opacity);
                    owned = true;
                    return (ID2D1Brush*)brush;
                }
                case IllustrationPaintKind.Radial:
                {
                    var properties = new D2D1_RADIAL_GRADIENT_BRUSH_PROPERTIES
                    {
                        center = D2D.Point(paint.Center),
                        radiusX = (float)paint.Radius,
                        radiusY = (float)paint.Radius,
                    };
                    ID2D1RadialGradientBrush* brush;
                    Native.Check(owner.recorder->CreateRadialGradientBrush(&properties, null, owner.Stops(paint.Stops), &brush),
                                 "ID2D1RenderTarget::CreateRadialGradientBrush");
                    brush->SetOpacity((float)_opacity);
                    owned = true;
                    return (ID2D1Brush*)brush;
                }
                default:
                {
                    var color = D2D.Color(paint.Color, _opacity);
                    owner.solid->SetColor(&color);
                    owner.solid->SetOpacity(1);
                    owned = false;
                    return (ID2D1Brush*)owner.solid;
                }
            }
        }

        /// <summary>
        /// SwiftUI's conic gradient: wedges of the gradient's colors from the positive x axis clockwise, without
        /// antialiasing so they tile exactly, inside the shape's antialiased mask.
        /// </summary>
        private void FillConic(ID2D1PathGeometry1* geometry, IllustrationPaint paint)
        {
            PushMask(geometry, _opacity);
            try
            {
                var matrix = D2D.Matrix(_transform);
                _context->SetTransform(&matrix);
                _context->SetAntialiasMode(D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_ALIASED);
                D2D_RECT_F bounds;
                Native.Check(geometry->GetBounds(null, &bounds), "ID2D1Geometry::GetBounds");
                double reach = Math.Max(bounds.right - bounds.left, bounds.bottom - bounds.top) * 1.5 + 1;
                Point center = paint.Center;
                for (int wedge = 0; wedge < ConicWedges; wedge++)
                {
                    double from = wedge * 2 * Math.PI / ConicWedges, to = (wedge + 1) * 2 * Math.PI / ConicWedges;
                    PathData triangle = new PathBuilder(4, 3)
                        .MoveTo(center)
                        .LineTo(new Point(center.X + reach * Math.Cos(from), center.Y + reach * Math.Sin(from)))
                        .LineTo(new Point(center.X + reach * Math.Cos(to), center.Y + reach * Math.Sin(to)))
                        .Close()
                        .ToPathData();
                    ID2D1PathGeometry1* piece = owner.builder.Build(owner.factory, triangle, FillRule.NonZero);
                    try
                    {
                        var color = D2D.Color(ConicColor(paint.Stops, (wedge + 0.5) / ConicWedges));
                        owner.solid->SetColor(&color);
                        owner.solid->SetOpacity(1);
                        _context->FillGeometry((ID2D1Geometry*)piece, (ID2D1Brush*)owner.solid, null);
                    }
                    finally
                    {
                        Native.Release(ref piece);
                    }
                }
            }
            finally
            {
                _context->SetAntialiasMode(D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
                _context->PopLayer();
            }
        }

        private static RGBA ConicColor(IReadOnlyList<IllustrationStop> stops, double location)
        {
            if (location <= stops[0].Location)
            {
                return stops[0].Color;
            }
            for (int index = 1; index < stops.Count; index++)
            {
                if (location <= stops[index].Location)
                {
                    IllustrationStop a = stops[index - 1], b = stops[index];
                    double t = b.Location > a.Location ? (location - a.Location) / (b.Location - a.Location) : 0;
                    return new RGBA(a.Color.Red + (b.Color.Red - a.Color.Red) * t, a.Color.Green + (b.Color.Green - a.Color.Green) * t,
                                    a.Color.Blue + (b.Color.Blue - a.Color.Blue) * t, a.Color.Alpha + (b.Color.Alpha - a.Color.Alpha) * t);
                }
            }
            return stops[^1].Color;
        }
    }

    private delegate void DrawOperation(ID2D1DeviceContext1* target);
}
