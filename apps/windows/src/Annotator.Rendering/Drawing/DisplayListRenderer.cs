using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Platform;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Imaging;
using Annotator.Rendering.Interop;
using Annotator.Rendering.Text;
using Windows.Win32;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Annotator.Rendering.Drawing;

/// <summary>Problems met while drawing. The Mac logs these; Windows reports them to the caller.</summary>
[Flags]
public enum RenderIssues : byte
{
    None = 0,

    /// <summary>An image could not be decoded or uploaded; its replacement outline was drawn instead.</summary>
    ImageUnavailable = 1,
}

/// <summary>
/// Where a draw goes: scene points map to target pixels as <c>x * Scale + OffsetX</c>, and nothing is drawn outside
/// <see cref="Clip"/> (target pixels). <see cref="Live"/> marks content that changes every frame, such as a stroke
/// being drawn, which never gets geometry realizations.
/// </summary>
public readonly record struct DrawArea(double Scale, double OffsetX, double OffsetY, PixelRect Clip, bool Live = false)
{
    public AffineTransform PointsToPixels => new(Scale, 0, 0, Scale, OffsetX, OffsetY);
}

/// <summary>Tuning for <see cref="DisplayListRenderer"/>; the defaults come from measurements (see the README).</summary>
public sealed record RendererOptions
{
    /// <summary>
    /// Whether committed paths (not live, fewer than <see cref="CullMinimumVerbs"/> elements) are drawn through
    /// geometry realizations, built on their first draw at the current scale. A realization antialiases edges a little
    /// differently from a direct draw, so an element always uses one of the two: mixing them across partial redraws
    /// would leave seams.
    /// </summary>
    public bool UseRealizations { get; init; }

    /// <summary>
    /// Strokes with at least this many elements are never realized; a redraw that covers only part of them draws
    /// just the segments that reach the clip.
    /// </summary>
    public int CullMinimumVerbs { get; init; } = 256;

    /// <summary>The decoded image budget (Swift <c>DecodedImageCache</c>'s 128 MiB).</summary>
    public long ImageByteLimit { get; init; } = DecodedImageCache.DefaultByteLimit;

    /// <summary>
    /// Whether committed strokes of paths with several figures (rough shapes, system-design shapes, axes) are drawn as
    /// their widened area, merged and filled once. Core Graphics strokes such a path as one area; Direct2D's stroke on
    /// a GPU draws the figures' antialiased edges over each other, which leaves lighter seams where figures overlap.
    /// The merged area is built once per path, width and scale: about 0.1 ms for a rough rectangle and 3.5 ms for a
    /// rough arrow through 24 points. A draw that needs three or more builds them on worker threads. Straight-line
    /// paths of many figures (hatch fills) are stroked directly.
    /// </summary>
    public bool FillOverlappingStrokes { get; init; } = true;
}

/// <summary>Counters for measurements and tests.</summary>
public sealed class RendererCounters
{
    public long ListsDrawn { get; internal set; }

    public long ListsSkipped { get; internal set; }

    public long GeometriesBuilt { get; internal set; }

    public long RealizationsBuilt { get; internal set; }

    public long RealizedDraws { get; internal set; }

    public long CulledStrokes { get; internal set; }

    /// <summary>Strokes widened into filled areas (<see cref="RendererOptions.FillOverlappingStrokes"/>).</summary>
    public long WidenedStrokes { get; internal set; }

    public long TextLayoutsBuilt { get; internal set; }
}

/// <summary>
/// Executes Annotator.Core display lists on a Direct2D device context, with device resources cached by the identity
/// of the immutable payloads (paths, text runs, images, gradients) and by value for stroke styles. Lists enter and
/// leave through <see cref="Enter"/> and <see cref="Leave"/>; resources that no entered list uses are released at
/// once. Draws are clipped and skip lists whose bounds miss the clip. Create one renderer per device bundle and use
/// it on one thread; after a device loss, dispose it and create a new one for the new devices.
/// </summary>
public sealed unsafe class DisplayListRenderer : IDisposable
{
    private const double AntialiasMargin = 2;

    /// <summary>
    /// Straight-line paths with more figures than this (hatch fills) are stroked directly: their parallel lines leave
    /// no seams, and merging them costs milliseconds.
    /// </summary>
    private const int MergedLineFigures = 16;

    /// <summary>
    /// A draw that needs at least this many merged strokes (<see cref="RendererOptions.FillOverlappingStrokes"/>) builds
    /// them on worker threads first, for example when a board with many rough shapes opens. Fewer, such as a shape
    /// that was just drawn, are built in place.
    /// </summary>
    private const int ParallelMergeMinimum = 3;

    private readonly RendererOptions options;
    private readonly ISystemColorResolver colors;
    private readonly PathGeometryBuilder builder = new();
    private readonly Dictionary<DisplayList, ListEntry> lists = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PathData, GeometryEntry> geometries = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<StrokeStyle, StrokeEntry> strokes = [];
    private readonly Dictionary<LinearGradientPaint, GradientEntry> gradients = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TextRun, TextEntry> texts = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ImageDraw, int> images = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Guid, ValueList<byte>> failedImages = [];
    private readonly HashSet<Guid> imageIds = [];
    private readonly List<MergeRequest> merges = [];
    private readonly DecodedImageCache imageCache;
    private readonly TextSystem textSystem;
    private readonly WicImaging wic;
    private AffineTransform[] transforms = new AffineTransform[16];
    private AffineTransform[] mergeTransforms = new AffineTransform[16];
    private bool[] clipLayers = new bool[16];
    private ID2D1Factory1* factory;
    private ID2D1DeviceContext1* resourceContext;
    private ID2D1SolidColorBrush* solid;
    private ID2D1DeviceContext* lastContext;
    private ID2D1DeviceContext1* lastContext1;
    private bool imagesChanged;
    private RenderIssues issues;

    public DisplayListRenderer(GraphicsDevices devices, ISystemColorResolver? colors = null, RendererOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        this.options = options ?? new RendererOptions();
        this.colors = colors ?? FixedSystemColors.Light;
        Devices = devices;
        textSystem = TextSystem.Create();
        try
        {
            wic = WicImaging.Create();
            factory = Native.AddRef((ID2D1Factory1*)devices.D2DFactory);
            var device = (ID2D1Device*)devices.D2DDevice;
            ID2D1DeviceContext* context;
            Native.Check(device->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS.D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &context), "ID2D1Device::CreateDeviceContext");
            resourceContext = Native.Query<ID2D1DeviceContext1>(context, ID2D1DeviceContext1.IID_Guid, "QueryInterface(ID2D1DeviceContext1)");
            Native.Release(ref context);
            var white = new D2D1_COLOR_F { r = 1, g = 1, b = 1, a = 1 };
            ID2D1SolidColorBrush* brush;
            Native.Check(resourceContext->CreateSolidColorBrush(&white, null, &brush), "ID2D1DeviceContext::CreateSolidColorBrush");
            solid = brush;
            imageCache = new DecodedImageCache(Decode, this.options.ImageByteLimit);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public GraphicsDevices Devices { get; }

    public RendererCounters Counters { get; } = new();

    /// <summary>The decoded images of entered lists (Swift <c>decodedImageBuilds</c> and <c>decodedImageBytes</c>).</summary>
    public DecodedImageCache Images => imageCache;

    /// <summary>The number of lists entered at least once and not yet left.</summary>
    public int EnteredLists => lists.Count;

    public int CachedGeometries => geometries.Count;

    public int CachedTextLayouts => texts.Count;

    /// <summary>Returns and clears the issues met since the last call.</summary>
    public RenderIssues TakeIssues()
    {
        var value = issues;
        issues = RenderIssues.None;
        return value;
    }

    /// <summary>Elements whose image could not be shown; their replacement is drawn until their image changes.</summary>
    public IReadOnlyCollection<Guid> FailedImages => failedImages.Keys;

    /// <summary>
    /// Adds a list to the retained lists, building its cache entry and bounds the first time. Each call needs a matching
    /// <see cref="Leave"/>. Returns the list's bounds in scene points (null when it paints nothing).
    /// </summary>
    public Rect? Enter(DisplayList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (!lists.TryGetValue(list, out var entry))
        {
            entry = Acquire(list);
            lists[list] = entry;
        }
        entry.RefCount++;
        return entry.Bounds;
    }

    /// <summary>Removes one <see cref="Enter"/> of a list; its resources are released when nothing else uses them.</summary>
    public void Leave(DisplayList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (!lists.TryGetValue(list, out var entry))
        {
            throw new InvalidOperationException("The display list was not entered.");
        }
        if (--entry.RefCount > 0)
        {
            return;
        }
        lists.Remove(list);
        ReleaseEntry(entry);
    }

    /// <summary>The bounds of an entered list in scene points; null when it paints nothing.</summary>
    public Rect? Bounds(DisplayList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        return lists.TryGetValue(list, out var entry) ? entry.Bounds : throw new InvalidOperationException("The display list was not entered.");
    }

    /// <summary>
    /// Whether an entered list paints with a system color (<see cref="PaintKind.System"/>), so its pixels change when the
    /// theme or accent color does; other lists draw the same pixels in every theme.
    /// </summary>
    public bool UsesSystemColors(DisplayList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        return lists.TryGetValue(list, out var entry) ? entry.UsesSystemColors : throw new InvalidOperationException("The display list was not entered.");
    }

    /// <summary>The target pixels a list can change at <paramref name="area"/>'s scale and offset, antialiasing included.</summary>
    public static PixelRect PixelBounds(Rect bounds, in DrawArea area)
    {
        var box = area.PointsToPixels.TransformBounds(bounds);
        return new PixelRect((int)Math.Floor(box.X - AntialiasMargin), (int)Math.Floor(box.Y - AntialiasMargin),
                             (int)Math.Ceiling(box.MaxX + AntialiasMargin), (int)Math.Ceiling(box.MaxY + AntialiasMargin));
    }

    /// <summary>
    /// Keeps the decoded images of entered lists and releases the others (Swift <c>beginFrame</c> and
    /// <c>endFrame</c>). Call after a batch of <see cref="Enter"/> and <see cref="Leave"/> calls; it does nothing when
    /// no list with an image entered or left.
    /// </summary>
    public void CollectImages()
    {
        if (!imagesChanged)
        {
            return;
        }
        imagesChanged = false;
        imageIds.Clear();
        foreach (var image in images.Keys)
        {
            imageIds.Add(image.ElementId);
        }
        imageCache.BeginFrame(imageIds);
        foreach (var image in images.Keys)
        {
            imageCache.Touch(image.Png, image.ElementId);
        }
        imageCache.EndFrame();
        foreach (var id in failedImages.Keys)
        {
            if (!imageIds.Contains(id))
            {
                failedImages.Remove(id);
            }
        }
    }

    /// <summary>
    /// Draws entered lists in order into <paramref name="deviceContext"/> (an ID2D1DeviceContext between BeginDraw and
    /// EndDraw), clipped to <see cref="DrawArea.Clip"/>. Lists whose bounds miss the clip are skipped. The context's
    /// transform, antialiasing and DPI are set here; the target is not cleared.
    /// </summary>
    public void Draw(nint deviceContext, ReadOnlySpan<DisplayList> drawn, in DrawArea area)
    {
        PrepareMergedStrokes(drawn, area);
        var context = Begin(deviceContext, area);
        try
        {
            foreach (var list in drawn)
            {
                DrawEntry(context, EntryOf(list), area);
            }
        }
        finally
        {
            context->PopAxisAlignedClip();
        }
    }

    /// <summary>Draws one entered list (see <see cref="Draw(nint, ReadOnlySpan{DisplayList}, in DrawArea)"/>).</summary>
    public void Draw(nint deviceContext, DisplayList list, in DrawArea area)
    {
        PrepareMergedStrokes(new ReadOnlySpan<DisplayList>(in list), area);
        var context = Begin(deviceContext, area);
        try
        {
            DrawEntry(context, EntryOf(list), area);
        }
        finally
        {
            context->PopAxisAlignedClip();
        }
    }

    public void Dispose()
    {
        foreach (var entry in lists.Values)
        {
            ReleaseClipGeometries(entry);
        }
        lists.Clear();
        foreach (var entry in geometries.Values)
        {
            entry.Release();
        }
        geometries.Clear();
        foreach (var entry in strokes.Values)
        {
            entry.Release();
        }
        strokes.Clear();
        foreach (var entry in gradients.Values)
        {
            entry.Release();
        }
        gradients.Clear();
        foreach (var entry in texts.Values)
        {
            entry.Release();
        }
        texts.Clear();
        images.Clear();
        imageCache?.Dispose();
        Native.Release(ref lastContext1);
        lastContext = null;
        Native.Release(ref solid);
        Native.Release(ref resourceContext);
        Native.Release(ref factory);
        wic?.Dispose();
        textSystem?.Dispose();
    }

    internal ID2D1Factory1* Factory => factory;

    internal ID2D1DeviceContext1* ResourceContext => resourceContext;

    internal ID2D1SolidColorBrush* SolidBrush => solid;

    internal TextSystem TextSystem => textSystem;

    /// <summary>Sets up a context for drawing in target pixels and pushes the clip. Returns the ID2D1DeviceContext1.</summary>
    internal ID2D1DeviceContext1* Begin(nint deviceContext, in DrawArea area)
    {
        if (deviceContext == 0)
        {
            throw new ArgumentException("A device context is required.", nameof(deviceContext));
        }
        if (!double.IsFinite(area.Scale) || !(area.Scale > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(area), area.Scale, "The scale must be finite and positive.");
        }
        var context = Context1((ID2D1DeviceContext*)deviceContext);
        context->SetDpi(96, 96);
        context->SetUnitMode(D2D1_UNIT_MODE.D2D1_UNIT_MODE_DIPS);
        context->SetAntialiasMode(D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
        context->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE.D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
        context->SetPrimitiveBlend(D2D1_PRIMITIVE_BLEND.D2D1_PRIMITIVE_BLEND_SOURCE_OVER);
        var identity = D2D.Identity;
        context->SetTransform(&identity);
        var clip = D2D.Rect(area.Clip.Left, area.Clip.Top, area.Clip.Right, area.Clip.Bottom);
        context->PushAxisAlignedClip(&clip, D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_ALIASED);
        return context;
    }

    internal void DrawEntry(ID2D1DeviceContext1* context, ListEntry entry, in DrawArea area)
    {
        if (entry.Bounds is not { } bounds || !Intersects(PixelBounds(bounds, area), area.Clip))
        {
            Counters.ListsSkipped++;
            return;
        }
        Counters.ListsDrawn++;
        var items = entry.List.Items;
        var resources = entry.Resources;
        var depth = 0;
        var clipDepth = 0;
        var current = area.PointsToPixels;
        SetTransform(context, current);
        for (var index = 0; index < items.Length; index++)
        {
            ref readonly var item = ref items[index];
            ref var slot = ref resources[index];
            switch (item.Op)
            {
                case DisplayOp.FillPath:
                    FillPath(context, item, slot.Geometry!, current, area, Brush(item.Paint!, slot.Gradient));
                    break;
                case DisplayOp.StrokePath:
                    StrokePath(context, item, slot.Geometry!, slot.Stroke!, current, area, Brush(item.Paint!, slot.Gradient));
                    break;
                case DisplayOp.FillRect:
                    FillRect(context, item, current, Brush(item.Paint!, slot.Gradient));
                    break;
                case DisplayOp.StrokeSegments:
                    StrokeSegmentRun(context, item.Segments!, slot.Stroke!, current, area, Brush(item.Paint!, slot.Gradient));
                    break;
                case DisplayOp.DrawText:
                {
                    var text = item.Text!;
                    var options = text.Paint.Kind == PaintKind.LinearGradient
                        ? D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE
                        : D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT;
                    context->DrawTextLayout(D2D.Point(slot.Text!.Origin.X, slot.Text.Origin.Y), slot.Text.Layout, (ID2D1Brush*)Brush(text.Paint, slot.Gradient), options);
                    break;
                }
                case DisplayOp.DrawImage:
                    if (DrawImage(context, item.Image!))
                    {
                        index += item.Image!.FallbackItemCount;
                    }
                    break;
                case DisplayOp.PushTransform:
                    Push(ref transforms, depth++, current);
                    current = AffineTransform.Concat(item.Transform, current);
                    SetTransform(context, current);
                    break;
                case DisplayOp.PopTransform:
                    current = transforms[--depth];
                    SetTransform(context, current);
                    break;
                case DisplayOp.PushLayer:
                    PushLayer(context, item, slot.Geometry);
                    break;
                case DisplayOp.PopLayer:
                    context->PopLayer();
                    break;
                case DisplayOp.PushClip:
                {
                    var aligned = IsAxisAligned(current);
                    Push(ref clipLayers, clipDepth++, !aligned);
                    if (aligned)
                    {
                        var rect = D2D.Rect(item.Rect);
                        context->PushAxisAlignedClip(&rect, D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
                    }
                    else
                    {
                        PushRotatedClip(context, item.Rect, ref slot);
                    }
                    break;
                }
                case DisplayOp.PopClip:
                    if (clipLayers[--clipDepth])
                    {
                        context->PopLayer();
                    }
                    else
                    {
                        context->PopAxisAlignedClip();
                    }
                    break;
            }
        }
    }

    private ListEntry EntryOf(DisplayList list) =>
        lists.TryGetValue(list, out var entry) ? entry : throw new InvalidOperationException("Enter a display list before drawing it.");

    private ID2D1DeviceContext1* Context1(ID2D1DeviceContext* context)
    {
        if (context != lastContext || lastContext1 == null)
        {
            Native.Release(ref lastContext1);
            lastContext1 = Native.Query<ID2D1DeviceContext1>(context, ID2D1DeviceContext1.IID_Guid, "QueryInterface(ID2D1DeviceContext1)");
            lastContext = context;
        }
        return lastContext1;
    }

    private static void SetTransform(ID2D1DeviceContext1* context, in AffineTransform transform)
    {
        var matrix = D2D.Matrix(transform);
        context->SetTransform(&matrix);
    }

    private static bool IsAxisAligned(in AffineTransform transform) =>
        (Math.Abs(transform.B) < 1e-9 && Math.Abs(transform.C) < 1e-9) || (Math.Abs(transform.A) < 1e-9 && Math.Abs(transform.D) < 1e-9);

    private static void Push<T>(ref T[] stack, int depth, T value)
    {
        if (depth == stack.Length)
        {
            Array.Resize(ref stack, stack.Length * 2);
        }
        stack[depth] = value;
    }

    internal static bool Intersects(in PixelRect a, in PixelRect b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    /// <summary>The clip in the current user space, for culling.</summary>
    private static Rect ClipInUserSpace(in AffineTransform current, in PixelRect clip)
    {
        if (current.Inverted() is not { } inverse)
        {
            return new Rect(double.NegativeInfinity, double.NegativeInfinity, double.PositiveInfinity, double.PositiveInfinity);
        }
        return inverse.TransformBounds(new Rect(clip.Left, clip.Top, clip.Width, clip.Height));
    }

    private ID2D1Brush* Brush(Paint paint, GradientEntry? gradient)
    {
        switch (paint.Kind)
        {
            case PaintKind.LinearGradient:
                gradient!.Brush->SetOpacity((float)paint.Opacity);
                return (ID2D1Brush*)gradient.Brush;
            case PaintKind.System:
            {
                var color = D2D.Color(colors.Resolve(paint.SystemColor));
                solid->SetColor(&color);
                solid->SetOpacity((float)paint.Opacity);
                return (ID2D1Brush*)solid;
            }
            default:
            {
                var color = D2D.Color(paint.Color);
                solid->SetColor(&color);
                solid->SetOpacity((float)paint.Opacity);
                return (ID2D1Brush*)solid;
            }
        }
    }

    private void FillPath(ID2D1DeviceContext1* context, in DisplayItem item, GeometryEntry geometry, in AffineTransform current, in DrawArea area, ID2D1Brush* brush)
    {
        var rule = item.FillRule;
        if (Realizes(geometry, area))
        {
            var scale = MaximumScale(current);
            if (geometry.Filled == null || geometry.FilledRule != rule || scale > geometry.FilledScale)
            {
                Native.Release(ref geometry.Filled);
                ID2D1GeometryRealization* realization;
                Native.Check(context->CreateFilledGeometryRealization((ID2D1Geometry*)Geometry(geometry, rule),
                                                                       PInvoke.D2D1_DEFAULT_FLATTENING_TOLERANCE / scale, &realization),
                             "ID2D1DeviceContext1::CreateFilledGeometryRealization");
                geometry.Filled = realization;
                geometry.FilledRule = rule;
                geometry.FilledScale = scale;
                Counters.RealizationsBuilt++;
            }
            context->DrawGeometryRealization(geometry.Filled, brush);
            Counters.RealizedDraws++;
            return;
        }
        context->FillGeometry((ID2D1Geometry*)Geometry(geometry, rule), brush, null);
    }

    private bool Realizes(GeometryEntry geometry, in DrawArea area) =>
        options.UseRealizations && !area.Live && geometry.Path.VerbCount < options.CullMinimumVerbs;

    private void StrokePath(ID2D1DeviceContext1* context, in DisplayItem item, GeometryEntry geometry, StrokeEntry stroke, in AffineTransform current,
                            in DrawArea area, ID2D1Brush* brush)
    {
        var style = item.Stroke!;
        var width = (float)style.Width;
        var path = geometry.Path;
        if (path.VerbCount >= options.CullMinimumVerbs && path.Bounds is { } box)
        {
            var padding = StrokeExtent(style) + CullMarginFor(current);
            var clip = ClipInUserSpace(current, area.Clip);
            if (!RectMath.Contains(clip, RectMath.Outset(box, padding, padding)))
            {
                var culled = builder.BuildCulled(factory, path, clip, padding);
                context->DrawGeometry((ID2D1Geometry*)culled, brush, width, (ID2D1StrokeStyle*)stroke.Style);
                Native.Release(ref culled);
                Counters.CulledStrokes++;
                return;
            }
        }
        if (Realizes(geometry, area))
        {
            var scale = MaximumScale(current);
            if (geometry.Stroked == null || geometry.StrokedStyle != stroke.Style || geometry.StrokedWidth != width || scale > geometry.StrokedScale)
            {
                Native.Release(ref geometry.Stroked);
                ID2D1GeometryRealization* realization;
                Native.Check(context->CreateStrokedGeometryRealization((ID2D1Geometry*)Geometry(geometry, FillRule.NonZero),
                                                                        PInvoke.D2D1_DEFAULT_FLATTENING_TOLERANCE / scale, width,
                                                                        (ID2D1StrokeStyle*)stroke.Style, &realization),
                             "ID2D1DeviceContext1::CreateStrokedGeometryRealization");
                geometry.Stroked = realization;
                geometry.StrokedStyle = stroke.Style;
                geometry.StrokedWidth = width;
                geometry.StrokedScale = scale;
                Counters.RealizationsBuilt++;
            }
            context->DrawGeometryRealization(geometry.Stroked, brush);
            Counters.RealizedDraws++;
            return;
        }
        if (options.FillOverlappingStrokes && !area.Live && Merges(geometry))
        {
            var scale = MaximumScale(current);
            if (geometry.Widened == null || geometry.WidenedStyle != stroke.Style || geometry.WidenedWidth != width || scale > geometry.WidenedScale)
            {
                Native.Release(ref geometry.Widened);
                geometry.Widened = Widen(factory, factory, Geometry(geometry, FillRule.NonZero), width, stroke.Style,
                                         PInvoke.D2D1_DEFAULT_FLATTENING_TOLERANCE / scale);
                geometry.WidenedStyle = stroke.Style;
                geometry.WidenedWidth = width;
                geometry.WidenedScale = scale;
                Counters.WidenedStrokes++;
            }
            context->FillGeometry((ID2D1Geometry*)geometry.Widened, brush, null);
            return;
        }
        context->DrawGeometry((ID2D1Geometry*)Geometry(geometry, FillRule.NonZero), brush, width, (ID2D1StrokeStyle*)stroke.Style);
    }

    /// <summary>
    /// The area a stroke covers as one geometry without overlaps: Direct2D's widened outline, then its outline, which
    /// merges the overlapping parts. <paramref name="work"/> creates the intermediate geometry and
    /// <paramref name="target"/> the result, which the caller owns.
    /// </summary>
    private static ID2D1PathGeometry1* Widen(ID2D1Factory1* work, ID2D1Factory1* target, ID2D1PathGeometry1* source, float width,
                                             ID2D1StrokeStyle1* style, float tolerance)
    {
        ID2D1PathGeometry1* widened = null;
        ID2D1PathGeometry1* merged = null;
        ID2D1GeometrySink* sink = null;
        try
        {
            Native.Check(work->CreatePathGeometry(&widened), "ID2D1Factory1::CreatePathGeometry(widened)");
            Native.Check(widened->Open(&sink), "ID2D1PathGeometry::Open(widened)");
            sink->SetFillMode(D2D1_FILL_MODE.D2D1_FILL_MODE_WINDING);
            Native.Check(source->Widen(width, (ID2D1StrokeStyle*)style, null, tolerance, (ID2D1SimplifiedGeometrySink*)sink), "ID2D1Geometry::Widen");
            Native.Check(sink->Close(), "ID2D1GeometrySink::Close(widened)");
            Native.Release(ref sink);
            Native.Check(target->CreatePathGeometry(&merged), "ID2D1Factory1::CreatePathGeometry(outline)");
            Native.Check(merged->Open(&sink), "ID2D1PathGeometry::Open(outline)");
            Native.Check(widened->Outline(null, tolerance, (ID2D1SimplifiedGeometrySink*)sink), "ID2D1Geometry::Outline");
            Native.Check(sink->Close(), "ID2D1GeometrySink::Close(outline)");
            var result = merged;
            merged = null;
            return result;
        }
        finally
        {
            Native.Release(ref sink);
            Native.Release(ref merged);
            Native.Release(ref widened);
        }
    }

    private static int Figures(GeometryEntry geometry)
    {
        if (geometry.Figures < 0)
        {
            var count = 0;
            foreach (var verb in geometry.Path.Verbs)
            {
                if (verb == PathVerb.MoveTo)
                {
                    count++;
                }
            }
            geometry.Figures = count;
        }
        return geometry.Figures;
    }

    /// <summary>Whether a committed stroke of this path is drawn as its merged area (<see cref="RendererOptions.FillOverlappingStrokes"/>).</summary>
    private bool Merges(GeometryEntry geometry) =>
        geometry.Path.VerbCount < options.CullMinimumVerbs && Figures(geometry) is > 1 and var figures
        && (geometry.Path.HasCurves || figures <= MergedLineFigures);

    /// <summary>
    /// Builds the merged strokes that a draw of <paramref name="drawn"/> needs on worker threads when there are at least
    /// <see cref="ParallelMergeMinimum"/>. Each worker has its own single-threaded Direct2D factory, so the merges run in
    /// parallel (calls on the renderer's multithreaded factory share one lock). The results are the same geometries
    /// that <see cref="StrokePath"/> builds in place, which then finds them cached.
    /// </summary>
    private void PrepareMergedStrokes(ReadOnlySpan<DisplayList> drawn, in DrawArea area)
    {
        if (!options.FillOverlappingStrokes || options.UseRealizations || area.Live)
        {
            return;
        }
        try
        {
            foreach (var list in drawn)
            {
                var entry = EntryOf(list);
                if (entry.Bounds is not { } bounds || !Intersects(PixelBounds(bounds, area), area.Clip))
                {
                    continue;
                }
                var items = entry.List.Items;
                var resources = entry.Resources;
                var depth = 0;
                var current = area.PointsToPixels;
                for (var index = 0; index < items.Length; index++)
                {
                    ref readonly var item = ref items[index];
                    switch (item.Op)
                    {
                        case DisplayOp.StrokePath:
                            QueueMerge(item, resources[index].Geometry!, resources[index].Stroke!, current);
                            break;
                        case DisplayOp.DrawImage:
                            // Fallback items draw only when the image cannot; they are merged in place then.
                            index += item.Image!.FallbackItemCount;
                            break;
                        case DisplayOp.PushTransform:
                            Push(ref mergeTransforms, depth++, current);
                            current = AffineTransform.Concat(item.Transform, current);
                            break;
                        case DisplayOp.PopTransform:
                            current = mergeTransforms[--depth];
                            break;
                    }
                }
            }
            if (merges.Count >= ParallelMergeMinimum)
            {
                BuildMergedStrokes();
            }
        }
        finally
        {
            foreach (var request in merges)
            {
                request.Geometry.MergeQueued = false;
            }
            merges.Clear();
        }
    }

    private void QueueMerge(in DisplayItem item, GeometryEntry geometry, StrokeEntry stroke, in AffineTransform current)
    {
        if (geometry.MergeQueued || !Merges(geometry))
        {
            return;
        }
        var width = (float)item.Stroke!.Width;
        var scale = MaximumScale(current);
        if (geometry.Widened != null && geometry.WidenedStyle == stroke.Style && geometry.WidenedWidth == width && scale <= geometry.WidenedScale)
        {
            return;
        }
        geometry.MergeQueued = true;
        merges.Add(new MergeRequest(geometry, item.Stroke!, stroke, width, scale));
    }

    private void BuildMergedStrokes()
    {
        var requests = merges;
        var results = new nint[requests.Count];
        var target = (nint)factory;
        try
        {
            Parallel.For(0, requests.Count, MergeWorker.Create, (index, _, worker) =>
            {
                if (worker is not null)
                {
                    results[index] = worker.Build(target, requests[index]);
                }
                return worker;
            }, worker => worker?.Dispose());
        }
        catch (AggregateException failure)
        {
            foreach (var built in results)
            {
                if (built != 0)
                {
                    Marshal.Release(built);
                }
            }
            ExceptionDispatchInfo.Throw(failure.InnerExceptions[0]);
        }
        for (var index = 0; index < results.Length; index++)
        {
            if (results[index] == 0)
            {
                continue;
            }
            var request = requests[index];
            var geometry = request.Geometry;
            Native.Release(ref geometry.Widened);
            geometry.Widened = (ID2D1PathGeometry1*)results[index];
            geometry.WidenedStyle = request.Stroke.Style;
            geometry.WidenedWidth = request.Width;
            geometry.WidenedScale = request.Scale;
            Counters.WidenedStrokes++;
        }
    }

    /// <summary>A merged stroke to build before a draw, at the scale it is drawn.</summary>
    private readonly record struct MergeRequest(GeometryEntry Geometry, StrokeStyle Style, StrokeEntry Stroke, float Width, float Scale);

    /// <summary>A worker thread's own single-threaded Direct2D factory and path builder.</summary>
    private sealed class MergeWorker : IDisposable
    {
        private readonly PathGeometryBuilder builder = new();
        private ID2D1Factory1* factory;

        private MergeWorker(ID2D1Factory1* factory) => this.factory = factory;

        /// <summary>A worker, or null when Direct2D cannot create a factory; the strokes are then merged in place.</summary>
        public static MergeWorker? Create()
        {
            var iid = ID2D1Factory1.IID_Guid;
            var factoryOptions = default(D2D1_FACTORY_OPTIONS);
            void* created;
            return PInvoke.D2D1CreateFactory(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_SINGLE_THREADED, &iid, &factoryOptions, &created).Failed
                ? null
                : new MergeWorker((ID2D1Factory1*)created);
        }

        /// <summary>
        /// The merged stroke as a geometry of <paramref name="target"/>, or 0 when a Direct2D call failed: the draw then
        /// merges it in place and reports the failure there.
        /// </summary>
        public nint Build(nint target, in MergeRequest request)
        {
            ID2D1PathGeometry1* source = null;
            ID2D1StrokeStyle1* style = null;
            try
            {
                source = builder.Build(factory, request.Geometry.Path, FillRule.NonZero);
                style = CreateStrokeStyle(factory, request.Style);
                return (nint)Widen(factory, (ID2D1Factory1*)target, source, request.Width, style, PInvoke.D2D1_DEFAULT_FLATTENING_TOLERANCE / request.Scale);
            }
            catch (NativeCallException)
            {
                return 0;
            }
            finally
            {
                Native.Release(ref style);
                Native.Release(ref source);
            }
        }

        public void Dispose() => Native.Release(ref factory);
    }

    /// <summary>
    /// FillRect, snapped outward to whole device pixels without antialiasing when asked, so a cover never leaves a
    /// partly covered edge pixel. Under a rotation the rectangle grows by half a pixel diagonal before an aliased fill.
    /// </summary>
    private void FillRect(ID2D1DeviceContext1* context, in DisplayItem item, in AffineTransform current, ID2D1Brush* brush)
    {
        var rect = item.Rect;
        if (item.Snap == RectSnap.None)
        {
            var area = D2D.Rect(rect);
            context->FillRectangle(&area, brush);
            return;
        }
        context->SetAntialiasMode(D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_ALIASED);
        if (IsAxisAligned(current))
        {
            var device = current.TransformBounds(rect);
            var snapped = D2D.Rect(Math.Floor(device.X), Math.Floor(device.Y), Math.Ceiling(device.MaxX), Math.Ceiling(device.MaxY));
            var identity = D2D.Identity;
            context->SetTransform(&identity);
            context->FillRectangle(&snapped, brush);
            SetTransform(context, current);
        }
        else
        {
            var grow = Math.Sqrt(0.5) / Math.Max(1e-9, MinimumScale(current));
            var area = D2D.Rect(RectMath.Outset(rect, grow, grow));
            context->FillRectangle(&area, brush);
        }
        context->SetAntialiasMode(D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
    }

    /// <summary>
    /// Pressure ink as the Mac draws it: every segment stroked on its own with its own width and the dash pattern
    /// restarting at the segment start, skipping segments that cannot reach the clip (Swift's culling rules).
    /// </summary>
    private void StrokeSegmentRun(ID2D1DeviceContext1* context, StrokeSegments segments, StrokeEntry stroke, in AffineTransform current,
                                  in DrawArea area, ID2D1Brush* brush)
    {
        var margin = StrokeSegments.CullMargin(current);
        var clip = ClipInUserSpace(current, area.Clip);
        var cull = segments.NeedsCulling(clip, margin);
        var style = (ID2D1StrokeStyle*)stroke.Style;
        var dashes = segments.Style.Dashes.AsSpan();
        for (var segment = 0; segment < segments.Count; segment++)
        {
            if (cull && !segments.Reaches(segment, clip, margin))
            {
                continue;
            }
            Point start = segments.Start(segment), end = segments.End(segment);
            var width = (float)segments.Width(segment);
            if (dashes.IsEmpty)
            {
                context->DrawLine(D2D.Point(start), D2D.Point(end), brush, width, style);
            }
            else
            {
                DrawDashedLine(context, start, end, width, dashes, segments.Style.DashPhase, brush, style);
            }
        }
    }

    /// <summary>Core Graphics dashing of one segment in user-space lengths: each dash is a capped line of its own.</summary>
    private static void DrawDashedLine(ID2D1DeviceContext1* context, Point start, Point end, float width, ReadOnlySpan<double> dashes,
                                       double phase, ID2D1Brush* brush, ID2D1StrokeStyle* style)
    {
        var length = start.Distance(end);
        // Core Graphics repeats an odd pattern with the roles of dashes and gaps swapped, as if it were listed twice.
        var count = dashes.Length % 2 == 1 ? dashes.Length * 2 : dashes.Length;
        double period = 0;
        for (var index = 0; index < count; index++)
        {
            period += Math.Max(0, dashes[index % dashes.Length]);
        }
        if (!(period > 0) || !double.IsFinite(period) || !double.IsFinite(length))
        {
            context->DrawLine(D2D.Point(start), D2D.Point(end), brush, width, style);
            return;
        }
        var direction = length > 0 ? (end - start) * (1 / length) : new Point(0, 0);
        var position = -(((phase % period) + period) % period);
        for (var index = 0; position <= length; index = (index + 1) % count)
        {
            var size = Math.Max(0, dashes[index % dashes.Length]);
            if (index % 2 == 0 && position + size >= 0)
            {
                var from = Math.Max(0, position);
                var to = Math.Min(length, position + size);
                context->DrawLine(D2D.Point(start + direction * from), D2D.Point(start + direction * to), brush, width, style);
            }
            position += size;
        }
    }

    /// <summary>Returns true when the image was drawn (its fallback items are skipped), false to draw the fallback.</summary>
    private bool DrawImage(ID2D1DeviceContext1* context, ImageDraw image)
    {
        if (failedImages.TryGetValue(image.ElementId, out var failed) && failed == image.Png)
        {
            return false;
        }
        DecodedImage decoded;
        try
        {
            decoded = imageCache.Image(image.Png, image.ElementId);
        }
        catch (RenderFailure)
        {
            MarkFailed(image);
            return false;
        }
        catch (NativeCallException exception) when (!DeviceLoss.IsDeviceLoss(exception.HResult))
        {
            MarkFailed(image);
            return false;
        }
        var destination = D2D.Rect(image.Destination);
        if (image.Source is { } part)
        {
            var source = D2D.Rect(part);
            context->DrawBitmap((ID2D1Bitmap*)decoded.Bitmap, &destination, (float)image.Opacity, D2D.Interpolation(image.Interpolation), &source, null);
        }
        else
        {
            context->DrawBitmap((ID2D1Bitmap*)decoded.Bitmap, &destination, (float)image.Opacity, D2D.Interpolation(image.Interpolation), null, null);
        }
        if (!DecodedImageCache.IsCached(decoded))
        {
            decoded.Dispose();
        }
        return true;
    }

    private void MarkFailed(ImageDraw image)
    {
        failedImages[image.ElementId] = image.Png;
        issues |= RenderIssues.ImageUnavailable;
    }

    /// <summary>Decodes image bytes and uploads them to this renderer's device (the decoder of <see cref="Images"/>).</summary>
    internal DecodedImage Decode(ValueList<byte> data)
    {
        var bitmap = wic.Decode(data.AsSpan());
        try
        {
            uint width, height;
            Native.Check(bitmap->GetSize(&width, &height), "IWICBitmap::GetSize");
            var properties = new D2D1_BITMAP_PROPERTIES1
            {
                pixelFormat = new D2D1_PIXEL_FORMAT { format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED },
                dpiX = 96,
                dpiY = 96,
                bitmapOptions = D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_NONE,
            };
            ID2D1Bitmap1* uploaded;
            Native.Check(resourceContext->CreateBitmapFromWicBitmap((Windows.Win32.Graphics.Imaging.IWICBitmapSource*)bitmap, &properties, &uploaded),
                         "ID2D1DeviceContext::CreateBitmapFromWicBitmap");
            return new DecodedImage(uploaded, (int)width, (int)height);
        }
        finally
        {
            Native.Release(ref bitmap);
        }
    }

    private void PushLayer(ID2D1DeviceContext1* context, in DisplayItem item, GeometryEntry? mask)
    {
        var parameters = new D2D1_LAYER_PARAMETERS1
        {
            contentBounds = item.LayerBounds is { } bounds ? D2D.Rect(bounds) : D2D.Infinite,
            geometricMask = mask is null ? null : (ID2D1Geometry*)Geometry(mask, item.FillRule),
            maskAntialiasMode = D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_PER_PRIMITIVE,
            maskTransform = D2D.Identity,
            opacity = (float)item.Opacity,
            opacityBrush = null,
            layerOptions = D2D1_LAYER_OPTIONS1.D2D1_LAYER_OPTIONS1_NONE,
        };
        context->PushLayer(&parameters, null);
    }

    private void PushRotatedClip(ID2D1DeviceContext1* context, Rect rect, ref ItemResources slot)
    {
        if (slot.ClipGeometry == 0)
        {
            var area = D2D.Rect(rect);
            ID2D1RectangleGeometry* geometry;
            Native.Check(factory->CreateRectangleGeometry(&area, &geometry), "ID2D1Factory::CreateRectangleGeometry");
            slot.ClipGeometry = (nint)geometry;
        }
        var parameters = new D2D1_LAYER_PARAMETERS1
        {
            contentBounds = D2D.Rect(rect),
            geometricMask = (ID2D1Geometry*)slot.ClipGeometry,
            maskAntialiasMode = D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_PER_PRIMITIVE,
            maskTransform = D2D.Identity,
            opacity = 1,
            opacityBrush = null,
            layerOptions = D2D1_LAYER_OPTIONS1.D2D1_LAYER_OPTIONS1_NONE,
        };
        context->PushLayer(&parameters, null);
    }

    private ID2D1PathGeometry1* Geometry(GeometryEntry entry, FillRule rule)
    {
        if (rule == FillRule.EvenOdd)
        {
            if (entry.Alternate == null)
            {
                entry.Alternate = builder.Build(factory, entry.Path, FillRule.EvenOdd);
                Counters.GeometriesBuilt++;
            }
            return entry.Alternate;
        }
        if (entry.Winding == null)
        {
            entry.Winding = builder.Build(factory, entry.Path, FillRule.NonZero);
            Counters.GeometriesBuilt++;
        }
        return entry.Winding;
    }

    private static float MaximumScale(in AffineTransform transform)
    {
        var matrix = D2D.Matrix(transform);
        return Math.Max(1e-3f, PInvoke.D2D1ComputeMaximumScaleFactor(&matrix));
    }

    private static double MinimumScale(in AffineTransform transform)
    {
        // The smaller singular value of the 2 x 2 part.
        double a = transform.A, b = transform.B, c = transform.C, d = transform.D;
        var s1 = a * a + b * b + c * c + d * d;
        var s2 = Math.Sqrt(Math.Max(0, (a * a + c * c - b * b - d * d) * (a * a + c * c - b * b - d * d) + 4 * (a * b + c * d) * (a * b + c * d)));
        return Math.Sqrt(Math.Max(0, (s1 - s2) / 2));
    }

    /// <summary>Two device pixels in user space, for culling (the Mac's margin).</summary>
    private static double CullMarginFor(in AffineTransform current) => 2 / Math.Max(1e-9, MinimumScale(current));

    /// <summary>How far a stroke can paint from its path: half the width, more for miter joins and square caps.</summary>
    internal static double StrokeExtent(StrokeStyle style)
    {
        var factor = 1.0;
        if (style.Join == LineJoin.Miter)
        {
            factor = Math.Max(factor, style.MiterLimit);
        }
        if (style.Cap == LineCap.Square)
        {
            factor = Math.Max(factor, Math.Sqrt(2));
        }
        return style.Width / 2 * factor;
    }

    private ListEntry Acquire(DisplayList list)
    {
        var items = list.Items;
        var resources = new ItemResources[items.Length];
        var bounds = new BoundsWalker();
        var systemColors = false;
        for (var index = 0; index < items.Length; index++)
        {
            ref readonly var item = ref items[index];
            ref var slot = ref resources[index];
            if (item.Paint is { Kind: PaintKind.LinearGradient } paint)
            {
                slot.Gradient = GradientFor(paint.Gradient!);
            }
            systemColors |= item.Paint?.Kind == PaintKind.System || item.Text?.Paint.Kind == PaintKind.System;
            switch (item.Op)
            {
                case DisplayOp.FillPath:
                    slot.Geometry = GeometryFor(item.Path!);
                    bounds.Include(item.Path!.Bounds);
                    break;
                case DisplayOp.StrokePath:
                    slot.Geometry = GeometryFor(item.Path!);
                    slot.Stroke = StrokeFor(item.Stroke!);
                    if (item.Path!.Bounds is { } pathBounds)
                    {
                        var extent = StrokeExtent(item.Stroke!);
                        bounds.Include(RectMath.Outset(pathBounds, extent, extent));
                    }
                    break;
                case DisplayOp.FillRect:
                    bounds.Include(item.Rect);
                    break;
                case DisplayOp.StrokeSegments:
                {
                    var segments = item.Segments!;
                    slot.Stroke = StrokeFor(new StrokeStyle(1, segments.Style.Cap, segments.Style.Join, segments.Style.MiterLimit));
                    double widest = 0.15;
                    foreach (var pressure in segments.Pressures.AsSpan())
                    {
                        widest = Math.Max(widest, pressure);
                    }
                    var extent = StrokeExtent(segments.Style.WithWidth(segments.BaseWidth * widest));
                    bounds.Include(RectMath.Outset(segments.Frame, extent, extent));
                    break;
                }
                case DisplayOp.DrawText:
                    slot.Text = TextFor(item.Text!);
                    bounds.Include(slot.Text.InkBounds);
                    break;
                case DisplayOp.DrawImage:
                    images[item.Image!] = images.GetValueOrDefault(item.Image!) + 1;
                    imagesChanged = true;
                    bounds.Include(item.Image!.Destination);
                    break;
                case DisplayOp.PushTransform:
                    bounds.PushTransform(item.Transform);
                    break;
                case DisplayOp.PopTransform:
                    bounds.PopTransform();
                    break;
                case DisplayOp.PushLayer:
                    if (item.Path is { } mask)
                    {
                        slot.Geometry = GeometryFor(mask);
                    }
                    bounds.PushClip(Narrow(item.LayerBounds, item.Path?.Bounds));
                    break;
                case DisplayOp.PushClip:
                    bounds.PushClip(item.Rect);
                    break;
                case DisplayOp.PopLayer:
                case DisplayOp.PopClip:
                    bounds.PopClip();
                    break;
            }
        }
        return new ListEntry(list, resources, bounds.Result, systemColors);
    }

    private static Rect? Narrow(Rect? first, Rect? second) => (first, second) switch
    {
        (null, null) => null,
        ({ } a, null) => a,
        (null, { } b) => b,
        ({ } a, { } b) => RectMath.Intersection(a, b) ?? new Rect(a.X, a.Y, 0, 0),
    };

    private void ReleaseEntry(ListEntry entry)
    {
        var items = entry.List.Items;
        for (var index = 0; index < items.Length; index++)
        {
            ref var slot = ref entry.Resources[index];
            if (slot.Geometry is { } geometry && --geometry.RefCount == 0)
            {
                geometries.Remove(geometry.Path);
                geometry.Release();
            }
            if (slot.Stroke is { } stroke && --stroke.RefCount == 0)
            {
                RemoveStroke(stroke);
            }
            if (slot.Gradient is { } gradient && --gradient.RefCount == 0)
            {
                RemoveGradient(gradient);
            }
            if (slot.Text is { } text && --text.RefCount == 0)
            {
                texts.Remove(items[index].Text!);
                text.Release();
            }
            if (items[index].Op == DisplayOp.DrawImage)
            {
                var image = items[index].Image!;
                if (images.TryGetValue(image, out var count) && count > 1)
                {
                    images[image] = count - 1;
                }
                else
                {
                    images.Remove(image);
                }
                imagesChanged = true;
            }
        }
        ReleaseClipGeometries(entry);
    }

    private static void ReleaseClipGeometries(ListEntry entry)
    {
        for (var index = 0; index < entry.Resources.Length; index++)
        {
            if (entry.Resources[index].ClipGeometry != 0)
            {
                var geometry = (ID2D1RectangleGeometry*)entry.Resources[index].ClipGeometry;
                Native.Release(ref geometry);
                entry.Resources[index].ClipGeometry = 0;
            }
        }
    }

    private void RemoveStroke(StrokeEntry stroke)
    {
        foreach (var (key, value) in strokes)
        {
            if (ReferenceEquals(value, stroke))
            {
                strokes.Remove(key);
                break;
            }
        }
        stroke.Release();
    }

    private void RemoveGradient(GradientEntry gradient)
    {
        foreach (var (key, value) in gradients)
        {
            if (ReferenceEquals(value, gradient))
            {
                gradients.Remove(key);
                break;
            }
        }
        gradient.Release();
    }

    private GeometryEntry GeometryFor(PathData path)
    {
        if (!geometries.TryGetValue(path, out var entry))
        {
            entry = new GeometryEntry(path);
            geometries[path] = entry;
        }
        entry.RefCount++;
        return entry;
    }

    private StrokeEntry StrokeFor(StrokeStyle style)
    {
        if (!strokes.TryGetValue(style, out var entry))
        {
            entry = new StrokeEntry { Style = CreateStrokeStyle(factory, style) };
            strokes[style] = entry;
        }
        entry.RefCount++;
        return entry;
    }

    /// <summary>
    /// A Direct2D stroke style for a Core Graphics style. Direct2D measures dashes in stroke widths and Core Graphics
    /// in user space, so dashes and phase are divided by the width; an odd pattern is listed twice, as Core Graphics
    /// repeats it. The caller owns the result.
    /// </summary>
    internal static ID2D1StrokeStyle1* CreateStrokeStyle(ID2D1Factory1* factory, StrokeStyle style)
    {
        var dashes = style.Dashes.AsSpan();
        var width = style.Width > 0 ? style.Width : 1;
        var properties = new D2D1_STROKE_STYLE_PROPERTIES1
        {
            startCap = D2D.Cap(style.Cap),
            endCap = D2D.Cap(style.Cap),
            dashCap = D2D.Cap(style.Cap),
            lineJoin = D2D.Join(style.Join),
            miterLimit = (float)Math.Max(1, style.MiterLimit),
            dashStyle = dashes.IsEmpty ? D2D1_DASH_STYLE.D2D1_DASH_STYLE_SOLID : D2D1_DASH_STYLE.D2D1_DASH_STYLE_CUSTOM,
            dashOffset = (float)(style.DashPhase / width),
            transformType = D2D1_STROKE_TRANSFORM_TYPE.D2D1_STROKE_TRANSFORM_TYPE_NORMAL,
        };
        var count = dashes.Length % 2 == 1 ? dashes.Length * 2 : dashes.Length;
        Span<float> scaled = count <= 32 ? stackalloc float[count] : new float[count];
        for (var index = 0; index < count; index++)
        {
            scaled[index] = (float)(Math.Max(0, dashes[index % dashes.Length]) / width);
        }
        ID2D1StrokeStyle1* result;
        fixed (float* pointer = scaled)
        {
            Native.Check(factory->CreateStrokeStyle(&properties, count == 0 ? null : pointer, (uint)count, &result), "ID2D1Factory1::CreateStrokeStyle");
        }
        return result;
    }

    private GradientEntry GradientFor(LinearGradientPaint paint)
    {
        if (!gradients.TryGetValue(paint, out var entry))
        {
            entry = new GradientEntry { Brush = CreateGradientBrush(paint) };
            gradients[paint] = entry;
        }
        entry.RefCount++;
        return entry;
    }

    private ID2D1LinearGradientBrush* CreateGradientBrush(LinearGradientPaint paint)
    {
        var stops = paint.Stops.AsSpan();
        Span<D2D1_GRADIENT_STOP> native = stops.Length <= 16 ? stackalloc D2D1_GRADIENT_STOP[stops.Length] : new D2D1_GRADIENT_STOP[stops.Length];
        for (var index = 0; index < stops.Length; index++)
        {
            native[index] = new D2D1_GRADIENT_STOP { position = (float)stops[index].Position, color = D2D.Color(stops[index].Color) };
        }
        ID2D1GradientStopCollection* collection;
        fixed (D2D1_GRADIENT_STOP* pointer = native)
        {
            // Gamma 2.2 interpolates in encoded sRGB, like a Core Graphics gradient in the sRGB space; clamping extends
            // the end colors (drawsBeforeStartLocation and drawsAfterEndLocation).
            Native.Check(resourceContext->CreateGradientStopCollection(pointer, (uint)native.Length, D2D1_GAMMA.D2D1_GAMMA_2_2,
                                                                       D2D1_EXTEND_MODE.D2D1_EXTEND_MODE_CLAMP, &collection),
                         "ID2D1RenderTarget::CreateGradientStopCollection");
        }
        try
        {
            var properties = new D2D1_LINEAR_GRADIENT_BRUSH_PROPERTIES { startPoint = D2D.Point(paint.Start), endPoint = D2D.Point(paint.End) };
            ID2D1LinearGradientBrush* brush;
            Native.Check(resourceContext->CreateLinearGradientBrush(&properties, null, collection, &brush), "ID2D1RenderTarget::CreateLinearGradientBrush");
            return brush;
        }
        finally
        {
            Native.Release(ref collection);
        }
    }

    private TextEntry TextFor(TextRun run)
    {
        if (!texts.TryGetValue(run, out var entry))
        {
            var frame = run.Frame;
            var layout = textSystem.CreateLayout(run.Text, run.Font, run.Alignment, (float)Math.Max(0, frame.Width), (float)Math.Max(0, frame.Height));
            Windows.Win32.Graphics.DirectWrite.DWRITE_OVERHANG_METRICS overhang;
            float baseline;
            try
            {
                Native.Check(layout->GetOverhangMetrics(&overhang), "IDWriteTextLayout::GetOverhangMetrics");
                baseline = TextSystem.FirstBaseline(layout);
            }
            catch
            {
                Native.Release(ref layout);
                throw;
            }
            // The layout keeps the Windows font's line spacing; it moves so its first baseline lands where the Mac's is.
            var origin = new Point(frame.X, baseline > 0 ? run.Baseline - baseline : frame.Y);
            var width = Math.Max(0, frame.Width);
            var height = Math.Max(0, frame.Height);
            var ink = new Rect(origin.X - Math.Max(0, overhang.left), origin.Y - Math.Max(0, overhang.top),
                               width + Math.Max(0, overhang.left) + Math.Max(0, overhang.right),
                               height + Math.Max(0, overhang.top) + Math.Max(0, overhang.bottom));
            entry = new TextEntry { Layout = layout, Origin = origin, InkBounds = ink };
            texts[run] = entry;
            Counters.TextLayoutsBuilt++;
        }
        entry.RefCount++;
        return entry;
    }

    /// <summary>Accumulates the scene-space bounds of a list's drawing items through its transforms and clips.</summary>
    private struct BoundsWalker
    {
        private AffineTransform[]? transforms;
        private Rect?[]? clips;
        private int transformDepth;
        private int clipDepth;
        private AffineTransform current;
        private bool hasTransform;
        private Rect? clip;
        private Rect? result;

        public readonly Rect? Result => result;

        public void Include(Rect? local)
        {
            if (local is not { } box)
            {
                return;
            }
            var scene = hasTransform ? current.TransformBounds(box) : box;
            if (clip is { } limit)
            {
                if (RectMath.Intersection(scene, limit) is not { } visible)
                {
                    return;
                }
                scene = visible;
            }
            result = RectMath.Union(result, scene);
        }

        public void PushTransform(in AffineTransform transform)
        {
            transforms ??= new AffineTransform[4];
            Push(ref transforms, transformDepth++, hasTransform ? current : AffineTransform.Identity);
            current = AffineTransform.Concat(transform, hasTransform ? current : AffineTransform.Identity);
            hasTransform = true;
        }

        public void PopTransform()
        {
            current = transforms![--transformDepth];
            hasTransform = transformDepth > 0 || !current.IsIdentity;
        }

        public void PushClip(Rect? local)
        {
            clips ??= new Rect?[4];
            Push(ref clips, clipDepth++, clip);
            if (local is { } box)
            {
                var scene = hasTransform ? current.TransformBounds(box) : box;
                clip = clip is { } outer ? RectMath.Intersection(outer, scene) ?? new Rect(scene.X, scene.Y, 0, 0) : scene;
            }
        }

        public void PopClip() => clip = clips![--clipDepth];
    }
}
