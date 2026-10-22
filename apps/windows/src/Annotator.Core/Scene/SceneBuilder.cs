// Port of the geometry and paint decisions of apps/macos/Sources/PresentationRendering/SceneRenderer.swift and GradientPainter.swift.
// Each Core Graphics call becomes a display-list item; Direct2D executes the lists (a later task).
using System.Runtime.InteropServices;
using Annotator.Core.Geometry;

namespace Annotator.Core.Scene;

/// <summary>
/// Turns elements into display lists exactly as the Mac's <c>SceneRenderer</c> draws them. Lists and paths are
/// cached per element and rebuilt only when the element or its drawing options change; an in-progress freehand
/// stroke extends its cached path by the new samples. Not thread-safe: use one builder per scene owner (like the
/// Mac's main-actor renderer) and hand the immutable <see cref="SceneSnapshot"/> to the rendering thread.
/// </summary>
public sealed class SceneBuilder
{
    private const double FadedOpacity = 0.22;
    private const double HighlighterOpacity = 0.35;

    private static readonly StrokeStyle HairlineStroke = new(1);

    private readonly ITextMeasurer measurer;
    private readonly Dictionary<Guid, Entry> entries = [];
    private readonly DisplayListWriter writer = new();
    private readonly List<DisplayList> sceneLists = [];
    private readonly List<int> order = [];
    private Point[] scratchPoints = new Point[16];
    private long generation;
    private SceneSnapshot lastScene = SceneSnapshot.Empty;
    private SelectionCache? selectionCache;

    public SceneBuilder(ITextMeasurer textMeasurer)
    {
        ArgumentNullException.ThrowIfNull(textMeasurer);
        measurer = textMeasurer;
    }

    /// <summary>Paths built from scratch (Swift <c>fullPathBuilds</c>).</summary>
    public long FullPathBuilds { get; private set; }

    /// <summary>Cached freehand paths extended by new samples (Swift <c>incrementalAppends</c>).</summary>
    public long IncrementalAppends { get; private set; }

    /// <summary>Swift <c>reset()</c>: forgets every cached path and list.</summary>
    public void Reset()
    {
        entries.Clear();
        selectionCache = null;
        lastScene = SceneSnapshot.Empty;
    }

    /// <summary>
    /// Swift <c>draw(_:selection:in:pixelsAvailableForBlur:selectionScale:fadedIDs:hiddenLabelIDs:)</c>. Cached data of
    /// elements that are no longer in <paramref name="elements"/> is dropped. Returns the previous snapshot when
    /// nothing changed.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="selectionScale"/> is not finite and positive.</exception>
    public SceneSnapshot Build(ElementList elements, IReadOnlySet<Guid>? selection = null, bool pixelsAvailableForBlur = false,
                               double selectionScale = 1, IReadOnlySet<Guid>? fadedIds = null,
                               IReadOnlySet<Guid>? hiddenLabelIds = null)
    {
        if (!double.IsFinite(selectionScale) || !(selectionScale > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(selectionScale), selectionScale, "The selection scale must be finite and positive.");
        }
        generation++;
        StackingOrder(elements);
        sceneLists.Clear();
        var issues = SceneIssues.None;
        foreach (var index in CollectionsMarshal.AsSpan(order))
        {
            ref readonly var element = ref elements[index];
            var entry = EntryFor(element.Id);
            var flags = ListFlags.None;
            if (fadedIds?.Contains(element.Id) == true && !element.Tool.IsProtection)
            {
                flags |= ListFlags.Faded;
            }
            if (hiddenLabelIds?.Contains(element.Id) == true)
            {
                flags |= ListFlags.HideLabel;
            }
            if (pixelsAvailableForBlur && element.Tool.IsPixelEffect)
            {
                flags |= ListFlags.PixelsAvailable;
            }
            if (entry.List is null || entry.ListFlags != flags || !entry.ListElement.Equals(in element))
            {
                entry.List = BuildElementList(element, flags);
                entry.ListElement = element;
                entry.ListFlags = flags;
            }
            issues |= entry.List.Issues;
            if (!entry.List.IsEmpty)
            {
                sceneLists.Add(entry.List);
            }
        }
        Evict();
        var selected = SelectionIds(selection, fadedIds);
        var selectionList = selected is null || selected.Count == 0 ? DisplayList.Empty : CachedSelection(elements, selected, selectionScale);
        var lists = CollectionsMarshal.AsSpan(sceneLists);
        if (lists.IsEmpty && selectionList.IsEmpty && issues == SceneIssues.None)
        {
            lastScene = SceneSnapshot.Empty;
        }
        else if (!lastScene.SameLists(lists, selectionList, issues))
        {
            lastScene = new SceneSnapshot(lists.ToArray(), selectionList, issues);
        }
        return lastScene;
    }

    /// <summary>Swift <c>selection(_:ids:in:)</c>: outlines and handles at scale 1, without the scene.</summary>
    public DisplayList Selection(ElementList elements, IReadOnlySet<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return BuildSelection(elements, ids, 1);
    }

    /// <summary>
    /// The list <see cref="Build"/> draws for one element, from the same cache: an unchanged element returns the same
    /// list, and a freehand stroke that only grew extends its cached path. For layers that draw single elements, such
    /// as a live gesture. Unlike <see cref="Build"/>, it drops no cached data; call <see cref="Reset"/> when the
    /// elements it drew are gone.
    /// </summary>
    public DisplayList List(in Element element, bool faded = false)
    {
        var entry = EntryFor(element.Id);
        var flags = faded && !element.Tool.IsProtection ? ListFlags.Faded : ListFlags.None;
        if (entry.List is null || entry.ListFlags != flags || !entry.ListElement.Equals(in element))
        {
            entry.List = BuildElementList(element, flags);
            entry.ListElement = element;
            entry.ListFlags = flags;
        }
        return entry.List;
    }

    /// <summary>
    /// The text editor's live text (spec 013 S-7): <paramref name="text"/>, a text element, drawn exactly as
    /// <see cref="Build"/> draws a text object, turned by its rotation about its frame's center, with a stroke gradient
    /// that spans <paramref name="paintBounds"/> (the owner's frame for a shape label that follows its owner's paint) and
    /// its first baseline at <paramref name="baseline"/>. A shape label's text element is centered on its owner, so with
    /// the label's baseline (<see cref="MacTextMetrics.CenteredFirstBaseline"/>) this is also where <see cref="Build"/>
    /// draws the label. Nothing is cached.
    /// </summary>
    public DisplayList EditedText(in Element text, Rect paintBounds, double baseline)
    {
        writer.Reset();
        var rotated = text.Rotation != 0;
        if (rotated)
        {
            writer.Add(DisplayItem.PushTransform(AffineTransform.RotationAbout(text.Frame.Center, text.Rotation)));
        }
        DrawText(text, paintBounds, baseline);
        if (rotated)
        {
            if (writer.DrawsSince(1))
            {
                writer.Add(DisplayItem.PopTransform());
            }
            else
            {
                writer.Truncate(0);
            }
        }
        return writer.ToList(text.Id);
    }

    /// <summary>
    /// Swift <c>path(for:)</c>: the stroked outline of a shape, freehand stroke or connector, cached by geometry token.
    /// A freehand stroke whose samples were only appended (same append epoch, tool and roughness) grows its cached
    /// path instead of rebuilding it.
    /// </summary>
    public PathData Path(in Element element)
    {
        var entry = EntryFor(element.Id);
        if (entry.Path is { } cached && entry.Token == element.GeometryToken)
        {
            return cached;
        }
        if (entry.Path is { } growing && element.Tool.IsFreehand && entry.PathTool == element.Tool
            && entry.Roughness == element.Style.Roughness && entry.AppendEpoch == element.AppendEpoch
            && entry.PointCount > 0 && entry.PointCount < element.Points.Count)
        {
            var added = element.Points.AsSpan()[entry.PointCount..];
            PathData extended;
            if (element.Style.Roughness > 0)
            {
                if (scratchPoints.Length < added.Length)
                {
                    scratchPoints = new Point[Math.Max(added.Length, scratchPoints.Length * 2)];
                }
                for (var offset = 0; offset < added.Length; offset++)
                {
                    scratchPoints[offset] = Jitter(added[offset], entry.PointCount + offset, element);
                }
                extended = growing.AppendLines(scratchPoints.AsSpan(0, added.Length));
            }
            else
            {
                extended = growing.AppendLines(added);
            }
            entry.SetPath(element, extended);
            IncrementalAppends++;
            return extended;
        }
        var path = BuildPath(element);
        entry.SetPath(element, path);
        FullPathBuilds++;
        return path;
    }

    /// <summary>Swift <c>drawingBounds(for:)</c>: the area the element can paint, padded and rotated, in scene points.</summary>
    public Rect DrawingBounds(in Element element)
    {
        var entry = EntryFor(element.Id);
        if (entry.BoundsElement is { } known && known.Equals(in element))
        {
            return entry.Bounds;
        }
        var bounds = element.Frame;
        if (UsesPath(element.Tool))
        {
            bounds = RectMath.Union(bounds, Path(element).Bounds);
        }
        if (ShapeLabelLayout.TextElement(element, measurer) is { } label)
        {
            bounds = bounds.Union(label.Frame);
        }
        if (element.Tool == Tool.Counter)
        {
            bounds = bounds.Union(element.Frame.Translated(new Point(0, element.Frame.Height * 0.12)));
        }
        if (element.Tool == Tool.Ruler && !element.Points.IsEmpty)
        {
            bounds = bounds.Union(RulerLabelFrame(element.Points[0], element.Points[^1]));
        }
        if (element.Tool == Tool.Arrow)
        {
            for (var pass = 0; pass < 2; pass++)
            {
                if (ArrowHeadGeometry.Make(element, atStart: pass == 0) is not { } head)
                {
                    continue;
                }
                bounds = bounds.Union(HeadPointBounds(head));
                if (head.Kind is ArrowHead.Circle or ArrowHead.CircleOutline)
                {
                    bounds = bounds.Union(HeadCircle(head));
                }
                if (head.RelationshipCircle is { } circle)
                {
                    bounds = bounds.Union(CircleBounds(circle.Center, circle.Radius));
                }
            }
        }
        var padding = SwiftMath.Max(3, element.Style.Width * 2.5 + 2);
        var rotation = AffineTransform.RotationAbout(element.Frame.Center, element.Rotation);
        var result = rotation.TransformBounds(RectMath.Outset(bounds, padding, padding));
        entry.BoundsElement = element;
        entry.Bounds = result;
        return result;
    }

    private static bool UsesPath(Tool tool) => tool.IsShape || tool.IsFreehand || tool.IsConnector;

    private static Rect RulerLabelFrame(Point start, Point end) =>
        new((start.X + end.X) / 2 - 60, (start.Y + end.Y) / 2 - 25, 120, 22);

    private static Rect HeadCircle(in ArrowHeadGeometry head) =>
        new(head.Tip.X - head.Length / 3, head.Tip.Y - head.Length / 3, head.Length * 2 / 3, head.Length * 2 / 3);

    private static Rect CircleBounds(Point center, double radius) =>
        new(center.X - radius, center.Y - radius, radius * 2, radius * 2);

    private static Rect HeadPointBounds(in ArrowHeadGeometry head)
    {
        var polygon = head.Polygon;
        var segments = head.RelationshipSegments;
        Span<Point> points = stackalloc Point[polygon.Count + 3 + segments.Count * 2];
        var count = 0;
        foreach (ref readonly var point in polygon)
        {
            points[count++] = point;
        }
        points[count++] = head.Tip;
        points[count++] = head.Left;
        points[count++] = head.Right;
        foreach (ref readonly var segment in segments)
        {
            points[count++] = segment.Start;
            points[count++] = segment.End;
        }
        return Rect.Enclosing(points);
    }

    private Entry EntryFor(Guid id)
    {
        ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(entries, id, out _);
        slot ??= new Entry();
        slot.Generation = generation;
        return slot;
    }

    private void Evict()
    {
        foreach (var (id, entry) in entries)
        {
            if (entry.Generation != generation)
            {
                entries.Remove(id);
            }
        }
    }

    /// <summary><c>ElementStacking.ordered</c>: drawings first, then pixel effects, then solid covers.</summary>
    private void StackingOrder(ElementList elements)
    {
        order.Clear();
        for (var index = 0; index < elements.Count; index++)
        {
            if (!elements[index].Tool.IsProtection)
            {
                order.Add(index);
            }
        }
        for (var index = 0; index < elements.Count; index++)
        {
            if (elements[index].Tool.IsPixelEffect)
            {
                order.Add(index);
            }
        }
        for (var index = 0; index < elements.Count; index++)
        {
            if (elements[index].Tool == Tool.Cover)
            {
                order.Add(index);
            }
        }
    }

    private PathData BuildPath(in Element element)
    {
        if (element.Tool.IsShape)
        {
            var canonical = CanonicalShape(element);
            return element.Style.Roughness > 0 ? RoughGeometry.Outline(element, canonical) : canonical;
        }
        var points = element.Points.AsSpan();
        if (points.IsEmpty)
        {
            return PathData.Empty;
        }
        var connector = element.Tool.IsConnector;
        var builder = new PathBuilder(points.Length + 1, points.Length * 3);
        builder.MoveTo(points[0]);
        if (element.Style.Curved && connector && points.Length > 2)
        {
            foreach (ref readonly var curve in ConnectorGeometry.Curves(element.Points))
            {
                builder.CubicTo(curve.FirstControl, curve.SecondControl, curve.End);
            }
        }
        else
        {
            for (var index = 1; index < points.Length; index++)
            {
                builder.LineTo(connector ? points[index] : Jitter(points[index], index, element));
            }
        }
        var path = builder.ToPathData(reserveForAppends: element.Tool.IsFreehand);
        return connector && element.Style.Roughness > 0 ? RoughGeometry.Outline(element, path) : path;
    }

    private static PathData CanonicalShape(in Element element)
    {
        var box = element.Frame;
        if (Tool.SystemDesignShapes.Contains(element.Tool))
        {
            return SystemDiagramGeometry.Path(element);
        }
        if (element.Tool.PolygonKind is { } kind)
        {
            var vertices = PolygonShape.Vertices(kind, box);
            return new PathBuilder(vertices.Count + 1, vertices.Count).AddLines(vertices).Close().ToPathData();
        }
        switch (element.Tool)
        {
            case Tool.Database or Tool.Queue or Tool.Person or Tool.Grid or Tool.Axes:
                return DiagramGeometry.Path(element);
            case Tool.Ellipse:
                return new PathBuilder(6, 13).AddEllipse(box).ToPathData();
            case Tool.Diamond:
                return DiamondGeometry.Path(box, element.Style.CornerRadius);
            default:
                var radius = SwiftMath.Min(SwiftMath.Min(element.Style.CornerRadius, box.Width / 2), box.Height / 2);
                return new PathBuilder(10, 21).AddRoundedRect(box, radius, radius).ToPathData();
        }
    }

    /// <summary>A deterministic per-sample offset for rough freehand ink (SplitMix64 of seed and index).</summary>
    private static Point Jitter(Point point, int index, in Element element)
    {
        if (!(element.Style.Roughness > 0))
        {
            return point;
        }
        var value = unchecked(element.Seed + (ulong)index * 0x9e3779b97f4a7c15UL);
        value = unchecked((value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL);
        value = unchecked((value ^ (value >> 27)) * 0x94d049bb133111ebUL);
        value ^= value >> 31;
        var x = (value & 0xffff) / 65535.0 - 0.5;
        var y = ((value >> 16) & 0xffff) / 65535.0 - 0.5;
        return point + new Point(x, y) * element.Style.Roughness;
    }

    private DisplayList BuildElementList(in Element element, ListFlags flags)
    {
        writer.Reset();
        if ((flags & ListFlags.Faded) != 0)
        {
            var transform = AffineTransform.RotationAbout(element.Frame.Center, element.Rotation);
            var footprint = element.Tool == Tool.Arrow ? 8 * (element.Style.HeadScale ?? 1) : 5;
            var padding = SwiftMath.Max(70, element.Style.Width * footprint + element.Style.Roughness * 3);
            var geometryBounds = UsesPath(element.Tool) ? RectMath.Union(element.Frame, Path(element).Bounds) : element.Frame;
            var bounds = RectMath.Outset(transform.TransformBounds(geometryBounds), padding, padding);
            writer.Add(DisplayItem.PushLayer(FadedOpacity, bounds));
            var start = writer.Count;
            DrawElement(element, flags);
            if (writer.DrawsSince(start))
            {
                writer.Add(DisplayItem.PopLayer());
            }
            else
            {
                writer.Truncate(start - 1);
            }
        }
        else
        {
            DrawElement(element, flags);
        }
        return writer.ToList(element.Id);
    }

    private void DrawElement(in Element element, ListFlags flags)
    {
        if ((flags & ListFlags.PixelsAvailable) != 0)
        {
            return;
        }
        var start = writer.Count;
        var rotated = element.Rotation != 0;
        if (rotated)
        {
            writer.Add(DisplayItem.PushTransform(AffineTransform.RotationAbout(element.Frame.Center, element.Rotation)));
        }
        Element? labelText = null;
        DrawBody(element, ref labelText);
        if ((flags & ListFlags.HideLabel) == 0 && element.Label is { HasVisibleText: true } label
            && (labelText ??= ShapeLabelLayout.TextElement(element, measurer)) is { } text)
        {
            DrawText(text, label.FollowsStroke == true ? element.Frame : null,
                     MacTextMetrics.CenteredFirstBaseline(text.Frame, text.Text, TextFont.Resolve(text.Style).Size));
        }
        if (rotated)
        {
            if (writer.DrawsSince(start))
            {
                writer.Add(DisplayItem.PopTransform());
            }
            else
            {
                writer.Truncate(start);
            }
        }
    }

    private void DrawBody(in Element element, ref Element? labelText)
    {
        var tool = element.Tool;
        if (tool == Tool.Image)
        {
            DrawImage(element);
            return;
        }
        if (tool.IsProtection)
        {
            writer.Add(DisplayItem.FillRect(RectMath.Integral(element.Frame), Paint.Black, RectSnap.OutwardToPixels));
            return;
        }
        if (tool == Tool.Text)
        {
            DrawText(element, null);
            return;
        }
        var style = element.Style;
        var frame = element.Frame;
        var measurementOpacity = (tool == Tool.Counter || tool == Tool.Ruler) && style.Opacity < 1;
        if (measurementOpacity)
        {
            if (style.Opacity == 0)
            {
                return;
            }
            var bounds = RectMath.Outset(frame, style.Width + 4, style.Width + 4);
            if (tool == Tool.Counter)
            {
                bounds = bounds.Union(frame.Translated(new Point(0, frame.Height * 0.12)));
            }
            else if (!element.Points.IsEmpty)
            {
                bounds = bounds.Union(RulerLabelFrame(element.Points[0], element.Points[^1]));
            }
            writer.Add(DisplayItem.PushLayer(style.Opacity, bounds));
        }
        if (tool == Tool.Counter)
        {
            DrawCounter(element);
        }
        else if (tool == Tool.Ruler && !element.Points.IsEmpty)
        {
            DrawRuler(element);
        }
        else
        {
            DrawStroke(element, ref labelText);
        }
        if (measurementOpacity)
        {
            writer.Add(DisplayItem.PopLayer());
        }
    }

    private void DrawImage(in Element element)
    {
        if (element.ImagePng is { } png)
        {
            writer.Add(DisplayItem.DrawImage(new ImageDraw(element.Id, png, element.Frame, element.Style.Opacity,
                                                           ImageInterpolation.HighQualityCubic, fallbackItemCount: 2)));
        }
        else
        {
            writer.Issues |= SceneIssues.MissingImageData;
        }
        var frame = element.Frame;
        writer.Add(DisplayItem.StrokePath(new PathBuilder(5, 4).AddRect(frame).ToPathData(), HairlineStroke, Paint.Red));
        writer.Add(DisplayItem.StrokePath(
            new PathBuilder(2, 2).MoveTo(frame.Origin).LineTo(new Point(frame.MaxX, frame.MaxY)).ToPathData(), HairlineStroke, Paint.Red));
    }

    private void DrawCounter(in Element element)
    {
        var frame = element.Frame;
        var paint = BeginPaint(element.Style.StrokeGradient, frame, 1, RectMath.Outset(frame, 2, 2), element.Style.Color, 1);
        if (paint is not null)
        {
            writer.Add(DisplayItem.FillPath(new PathBuilder(6, 13).AddEllipse(frame).ToPathData(), FillRule.NonZero, paint));
        }
        EndPaint(paint);
        var label = element;
        label.Style.Color = Palette.White;
        label.Style.StrokeGradient = null;
        label.Style.Opacity = 1;
        label.Style.Bold = true;
        label.Style.Alignment = TextAlignment.Center;
        label.Style.FontSize = frame.Height * 0.56;
        label.Frame = frame with { Y = frame.Y + frame.Height * 0.12 };
        DrawText(label, null);
    }

    private void DrawRuler(in Element element)
    {
        var style = element.Style;
        Point start = element.Points[0], end = element.Points[^1];
        var paint = BeginPaint(style.StrokeGradient, element.Frame, 1,
                               RectMath.Outset(element.Frame, style.Width + 4, style.Width + 4), style.Color, 1);
        if (paint is not null)
        {
            var stroke = new StrokeStyle(style.Width);
            writer.Add(DisplayItem.StrokePath(new PathBuilder(2, 2).MoveTo(start).LineTo(end).ToPathData(), stroke, paint));
            writer.Add(DisplayItem.StrokePath(new PathBuilder(5, 4).AddRect(new Rect(start.X - 3, start.Y - 3, 6, 6)).ToPathData(), stroke, paint));
            writer.Add(DisplayItem.StrokePath(new PathBuilder(5, 4).AddRect(new Rect(end.X - 3, end.Y - 3, 6, 6)).ToPathData(), stroke, paint));
        }
        EndPaint(paint);
        var label = element;
        label.Style.Opacity = 1;
        label.Style.FontSize = 13;
        label.Style.Alignment = TextAlignment.Center;
        label.Frame = RulerLabelFrame(start, end);
        writer.Add(DisplayItem.FillRect(label.Frame, Paint.WindowBackground));
        DrawText(label, null);
    }

    private void DrawStroke(in Element element, ref Element? labelText)
    {
        var style = element.Style;
        var tool = element.Tool;
        var frame = element.Frame;
        var highlighter = tool == Tool.Highlighter;
        var ambient = style.Opacity * (highlighter ? HighlighterOpacity : 1);
        var lineWidth = highlighter ? style.Width * 5 : style.Width;
        var cap = highlighter ? LineCap.Butt : LineCap.Round;
        var join = highlighter ? LineJoin.Bevel : LineJoin.Round;
        ValueList<double> dashes = style.Pattern switch
        {
            StrokePattern.Dashed => [style.Width * 5, style.Width * 3],
            StrokePattern.Dotted => [0.01, style.Width * 3],
            _ => default,
        };
        var stroke = new StrokeStyle(lineWidth, cap, join, 10, dashes);
        if (style.Fill is { } fill && tool.SupportsFill)
        {
            DrawFill(element, fill, cap, join);
        }
        var strokeMargin = SwiftMath.Max(
            SwiftMath.Max(4, style.Width * (highlighter ? 3 : 1) + style.Roughness * 3),
            tool == Tool.Arrow ? SwiftMath.Max(24, style.Width * 8) * (style.HeadScale ?? 1) : 0);
        var usesPressureSegments = tool == Tool.Pen && style.Pressure && element.Pressures.Count == element.Points.Count;
        var strokeBounds = style.StrokeGradient is not null && !usesPressureSegments
            ? RectMath.Union(frame, Path(element).Bounds) : frame;
        var paint = BeginPaint(style.StrokeGradient, frame, ambient, RectMath.Outset(strokeBounds, strokeMargin, strokeMargin),
                               style.Color, ambient);
        if (paint is null)
        {
            return;
        }
        ArrowHeadGeometry? startHead = null, endHead = null;
        if (tool == Tool.Arrow)
        {
            startHead = ArrowHeadGeometry.Make(element, atStart: true);
            endHead = ArrowHeadGeometry.Make(element, atStart: false);
        }
        if (tool.IsFreehand && element.Points.Count == 1)
        {
            var point = element.Points[0];
            if (highlighter)
            {
                writer.Add(DisplayItem.FillRect(new Rect(point.X - style.Width * 0.8, point.Y - style.Width * 2.5,
                                                         style.Width * 1.6, style.Width * 5), paint));
            }
            else
            {
                var radius = style.Width / 2;
                var dot = new PathBuilder(6, 13).AddEllipse(new Rect(point.X - radius, point.Y - radius, radius * 2, radius * 2));
                writer.Add(DisplayItem.FillPath(dot.ToPathData(), FillRule.NonZero, paint));
            }
        }
        else if (usesPressureSegments)
        {
            writer.Add(DisplayItem.StrokeSegmentRun(
                new StrokeSegments(element.Points, element.Pressures, style.Width, stroke, paint, frame)));
        }
        else
        {
            var path = Path(element);
            var outer = RectMath.Outset(RectMath.Union(frame, path.Bounds), lineWidth + 2, lineWidth + 2);
            var masks = 0;
            if (element.Label is { HasVisibleText: true }
                && (labelText ??= ShapeLabelLayout.TextElement(element, measurer)) is { } text
                && RectMath.Inset(element.SymbolFrame, style.Width + 1, style.Width + 1) is { } interior
                && RectMath.Intersection(RectMath.Outset(text.Frame, 3, 2), interior) is { } gap && !RectMath.IsEmpty(gap))
            {
                var mask = new PathBuilder(10, 8).AddRect(outer).AddRect(gap).ToPathData();
                writer.Add(DisplayItem.PushLayer(1, mask: mask, maskRule: FillRule.EvenOdd));
                masks++;
            }
            masks += PushHollowHeadMask(startHead, outer) + PushHollowHeadMask(endHead, outer);
            writer.Add(DisplayItem.StrokePath(path, stroke, paint));
            for (var pop = 0; pop < masks; pop++)
            {
                writer.Add(DisplayItem.PopLayer());
            }
        }
        if (tool == Tool.Arrow && element.Points.Count >= 2)
        {
            var solid = stroke.Solid();
            if (startHead is { } first)
            {
                DrawHead(first, solid, paint);
            }
            if (endHead is { } last)
            {
                DrawHead(last, solid, paint);
            }
        }
        EndPaint(paint);
    }

    /// <summary>
    /// Keeps the shaft out of a hollow head. Swift clips with the context's clip bounding box plus the head, even-odd;
    /// <paramref name="outer"/> stands in for that box and contains everything the shaft can paint.
    /// </summary>
    private int PushHollowHeadMask(ArrowHeadGeometry? candidate, Rect outer)
    {
        if (candidate is not { } head || !(head.Hollow || head.RelationshipCircle is not null))
        {
            return 0;
        }
        var mask = new PathBuilder(12, 20).AddRect(outer);
        if (head.Kind == ArrowHead.CircleOutline)
        {
            mask.AddEllipse(HeadCircle(head));
        }
        else if (head.Hollow)
        {
            mask.AddLines(head.Polygon).Close();
        }
        else if (head.RelationshipCircle is { } circle)
        {
            mask.AddEllipse(CircleBounds(circle.Center, circle.Radius));
        }
        writer.Add(DisplayItem.PushLayer(1, mask: mask.ToPathData(), maskRule: FillRule.EvenOdd));
        return 1;
    }

    private void DrawHead(in ArrowHeadGeometry head, StrokeStyle stroke, Paint paint)
    {
        if (head.Kind is ArrowHead.Circle or ArrowHead.CircleOutline)
        {
            var circle = new PathBuilder(6, 13).AddEllipse(HeadCircle(head)).ToPathData();
            writer.Add(head.Hollow ? DisplayItem.StrokePath(circle, stroke, paint) : DisplayItem.FillPath(circle, FillRule.NonZero, paint));
        }
        else if (head.Kind is ArrowHead.Square or ArrowHead.Diamond or ArrowHead.DiamondOutline or ArrowHead.TriangleOutline)
        {
            var polygon = new PathBuilder(6, 4).AddLines(head.Polygon).Close().ToPathData();
            writer.Add(head.Hollow ? DisplayItem.StrokePath(polygon, stroke, paint) : DisplayItem.FillPath(polygon, FillRule.NonZero, paint));
        }
        else if (ArrowHead.RelationshipChoices.Contains(head.Kind))
        {
            foreach (var (start, end) in head.RelationshipSegments)
            {
                writer.Add(DisplayItem.StrokePath(new PathBuilder(2, 2).MoveTo(start).LineTo(end).ToPathData(), stroke, paint));
            }
            if (head.RelationshipCircle is { } circle)
            {
                writer.Add(DisplayItem.StrokePath(new PathBuilder(6, 13).AddEllipse(CircleBounds(circle.Center, circle.Radius)).ToPathData(),
                                                  stroke, paint));
            }
        }
        else
        {
            var path = new PathBuilder(4, 3).MoveTo(head.Left);
            if (head.Kind != ArrowHead.Bar)
            {
                path.LineTo(head.Tip);
            }
            path.LineTo(head.Right);
            if (head.Kind == ArrowHead.Filled)
            {
                writer.Add(DisplayItem.FillPath(path.Close().ToPathData(), FillRule.NonZero, paint));
            }
            else
            {
                writer.Add(DisplayItem.StrokePath(path.ToPathData(), stroke, paint));
            }
        }
    }

    private void DrawFill(in Element element, RGBA fill, LineCap cap, LineJoin join)
    {
        var style = element.Style;
        var frame = element.Frame;
        var canonical = style.Roughness > 0 ? CanonicalShape(element) : Path(element);
        var paint = BeginPaint(style.FillGradient, frame, style.Opacity, RectMath.Outset(frame, 2, 2), fill,
                               style.Opacity * (element.Tool == Tool.Highlighter ? HighlighterOpacity : 1));
        if (paint is not null)
        {
            if (style.FillPattern == FillPattern.Solid)
            {
                // Clipping a frame fill to the shape equals filling the shape while the shape stays inside the frame
                // (a millionth of a point of rounding is allowed).
                if (canonical.TightBounds is { } outline && RectMath.Contains(RectMath.Outset(frame, 1e-6, 1e-6), outline))
                {
                    writer.Add(DisplayItem.FillPath(canonical, FillRule.NonZero, paint));
                }
                else
                {
                    writer.Add(DisplayItem.PushLayer(1, mask: canonical));
                    writer.Add(DisplayItem.FillRect(frame, paint));
                    writer.Add(DisplayItem.PopLayer());
                }
            }
            else
            {
                writer.Add(DisplayItem.PushLayer(1, mask: canonical));
                writer.Add(DisplayItem.StrokePath(Hatching(frame, style), new StrokeStyle(SwiftMath.Max(1, style.Width / 2), cap, join), paint));
                writer.Add(DisplayItem.PopLayer());
            }
        }
        EndPaint(paint);
    }

    /// <summary>Diagonal hatch lines across the frame, at the positions of <see cref="HatchStride"/>.</summary>
    private static PathData Hatching(Rect frame, in Style style)
    {
        var crosshatch = style.FillPattern == FillPattern.Crosshatch;
        var stride = new HatchStride(frame, style.Width);
        var estimate = (int)Math.Min(8_200, Math.Max(0, (stride.End - stride.Start) / stride.Spacing) + 2);
        var path = new PathBuilder(estimate * (crosshatch ? 4 : 2), estimate * (crosshatch ? 4 : 2));
        while (stride.MoveNext())
        {
            var x = stride.Current;
            path.MoveTo(new Point(x, frame.Y));
            path.LineTo(new Point(x + frame.Height, frame.MaxY));
            if (crosshatch)
            {
                path.MoveTo(new Point(x, frame.Y));
                path.LineTo(new Point(x - frame.Height, frame.MaxY));
            }
        }
        return path.ToPathData();
    }

    /// <summary>
    /// Swift <c>drawText</c>: the color's alpha times the text's opacity; a stroke gradient fills the glyphs across
    /// <paramref name="paintBounds"/> (the text frame unless a label follows its owner's paint). The first baseline sits
    /// at <paramref name="baseline"/>, or where the Mac draws it from the top of the frame.
    /// </summary>
    private void DrawText(in Element text, Rect? paintBounds, double? baseline = null)
    {
        var style = text.Style;
        var color = style.Color with { Alpha = style.Color.Alpha * style.Opacity };
        var paint = BeginPaint(style.StrokeGradient, paintBounds ?? text.Frame, 1, RectMath.Outset(text.Frame, 2, 2), color, 1);
        if (paint is not null)
        {
            var font = TextFont.Resolve(style);
            writer.Add(DisplayItem.DrawText(new TextRun(text.Text, text.Frame, font, style.Alignment, paint,
                                                        baseline ?? MacTextMetrics.FirstBaseline(text.Frame, font.Size))));
        }
        EndPaint(paint);
    }

    /// <summary>
    /// Swift <c>GradientPainter.paint</c>. Without a gradient the content uses <paramref name="color"/> at the context
    /// alpha <paramref name="ambient"/>. With one, Core Graphics draws the content as an alpha mask at
    /// <paramref name="opacity"/> and fills it with the gradient (source-in), clipped to <paramref name="extent"/>; for
    /// content in one color that equals drawing it with a gradient brush at <c>color.alpha * opacity</c> inside the clip.
    /// Returns null when an invalid gradient means nothing is drawn.
    /// </summary>
    private Paint? BeginPaint(ColorGradient? gradient, Rect bounds, double opacity, Rect extent, RGBA color, double ambient)
    {
        if (gradient is not { } value)
        {
            return Paint.Solid(color, ambient);
        }
        try
        {
            value.Validate();
        }
        catch (CanvasError)
        {
            writer.Issues |= SceneIssues.InvalidGradient;
            return null;
        }
        writer.Add(DisplayItem.PushClip(extent));
        return Paint.Linear(LinearGradientPaint.Create(value, bounds), color.Alpha * opacity);
    }

    /// <summary>Closes the clip that <see cref="BeginPaint"/> opened for a gradient paint.</summary>
    private void EndPaint(Paint? paint)
    {
        if (paint is { Kind: PaintKind.LinearGradient })
        {
            writer.Add(DisplayItem.PopClip());
        }
    }

    private static IReadOnlySet<Guid>? SelectionIds(IReadOnlySet<Guid>? selection, IReadOnlySet<Guid>? faded)
    {
        if (selection is null || selection.Count == 0 || faded is null || faded.Count == 0 || !faded.Overlaps(selection))
        {
            return selection;
        }
        var result = new HashSet<Guid>(selection);
        result.ExceptWith(faded);
        return result;
    }

    private DisplayList CachedSelection(ElementList elements, IReadOnlySet<Guid> ids, double scale)
    {
        var cache = selectionCache;
        if (cache is not null && cache.Scale == scale && cache.Ids.SetEquals(ids) && cache.Matches(elements))
        {
            return cache.List;
        }
        var list = BuildSelection(elements, ids, scale);
        selectionCache = new SelectionCache(scale, [.. ids], elements, list);
        return list;
    }

    private DisplayList BuildSelection(ElementList elements, IReadOnlySet<Guid> ids, double scale)
    {
        writer.Reset();
        var group = SelectionGeometry.Create(elements, ids);
        foreach (ref readonly var element in elements)
        {
            if (ids.Contains(element.Id))
            {
                DrawSelection(element, scale, handles: group is null);
            }
        }
        if (group is { } shared)
        {
            DrawSelection(new Element(shared.CanRotate ? Tool.Rectangle : Tool.Cover, shared.Bounds), scale, handles: true);
        }
        return writer.ToList(Guid.Empty);
    }

    private void DrawSelection(in Element element, double scale, bool handles)
    {
        var rotated = element.Rotation != 0;
        if (rotated)
        {
            writer.Add(DisplayItem.PushTransform(AffineTransform.RotationAbout(element.Frame.Center, element.Rotation)));
        }
        var limited = SwiftMath.Max(scale, 0.1);
        ValueList<double> dashes = element.Locked ? [3 / scale, 3 / scale] : default;
        var stroke = new StrokeStyle(1 / limited, dashes: dashes);
        // A lone editable arrow or line shows its point handles; a bounding box around them only adds noise.
        var pointHandlesOnly = element.Tool.IsConnector && !element.Locked && handles;
        if (!pointHandlesOnly)
        {
            var outline = RectMath.Outset(element.Frame, 3 / scale, 3 / scale);
            writer.Add(DisplayItem.StrokePath(new PathBuilder(5, 4).AddRect(outline).ToPathData(), stroke, Paint.Accent));
        }
        if (!element.Locked && handles)
        {
            var radius = 4 / limited;
            var box = element.Frame;
            if (element.Tool.IsConnector)
            {
                foreach (ref readonly var point in element.Points)
                {
                    AddHandle(point, radius, stroke);
                }
            }
            else
            {
                AddHandle(box.Origin, radius, stroke);
                AddHandle(new Point(box.MaxX, box.Y), radius, stroke);
                AddHandle(new Point(box.MaxX, box.MaxY), radius, stroke);
                AddHandle(new Point(box.X, box.MaxY), radius, stroke);
            }
            if (!element.Tool.IsProtection && !element.Tool.IsConnector)
            {
                var knob = new Point(box.Center.X, box.Y - 24 / limited);
                var knobRadius = 6 / limited;
                writer.Add(DisplayItem.StrokePath(new PathBuilder(2, 2).MoveTo(new Point(box.Center.X, box.Y)).LineTo(knob).ToPathData(),
                                                  stroke, Paint.Accent));
                writer.Add(DisplayItem.FillPath(
                    new PathBuilder(6, 13).AddEllipse(new Rect(knob.X - knobRadius, knob.Y - knobRadius, knobRadius * 2, knobRadius * 2)).ToPathData(),
                    FillRule.NonZero, Paint.White));
            }
        }
        if (rotated)
        {
            writer.Add(DisplayItem.PopTransform());
        }
    }

    private void AddHandle(Point point, double radius, StrokeStyle stroke)
    {
        var handle = new PathBuilder(6, 13).AddEllipse(new Rect(point.X - radius, point.Y - radius, radius * 2, radius * 2)).ToPathData();
        writer.Add(DisplayItem.FillPath(handle, FillRule.NonZero, Paint.White));
        writer.Add(DisplayItem.StrokePath(handle, stroke, Paint.Accent));
    }

    [Flags]
    private enum ListFlags : byte
    {
        None = 0,
        Faded = 1,
        HideLabel = 2,
        PixelsAvailable = 4,
    }

    /// <summary>The cache of one element: Swift's <c>CachedPath</c>, the display list and the drawing bounds.</summary>
    private sealed class Entry
    {
        public long Generation;
        public PathData? Path;
        public Guid Token;
        public Guid AppendEpoch;
        public int PointCount;
        public Tool PathTool;
        public double Roughness;
        public DisplayList? List;
        public Element ListElement;
        public ListFlags ListFlags;
        public Element? BoundsElement;
        public Rect Bounds;

        public void SetPath(in Element element, PathData path)
        {
            Path = path;
            Token = element.GeometryToken;
            AppendEpoch = element.AppendEpoch;
            PointCount = element.Points.Count;
            PathTool = element.Tool;
            Roughness = element.Style.Roughness;
        }
    }

    private sealed class SelectionCache
    {
        private readonly Element[] selected;

        public SelectionCache(double scale, HashSet<Guid> ids, ElementList elements, DisplayList list)
        {
            Scale = scale;
            Ids = ids;
            List = list;
            var kept = new List<Element>(ids.Count);
            foreach (ref readonly var element in elements)
            {
                if (ids.Contains(element.Id))
                {
                    kept.Add(element);
                }
            }
            selected = [.. kept];
        }

        public double Scale { get; }

        public HashSet<Guid> Ids { get; }

        public DisplayList List { get; }

        /// <summary>Whether the selected elements are unchanged and in the same order.</summary>
        public bool Matches(ElementList elements)
        {
            var position = 0;
            foreach (ref readonly var element in elements)
            {
                if (!Ids.Contains(element.Id))
                {
                    continue;
                }
                if (position >= selected.Length || !selected[position].Equals(in element))
                {
                    return false;
                }
                position++;
            }
            return position == selected.Length;
        }
    }
}
