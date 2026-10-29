using Annotator.App.Shell;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Workspace.Toolbar;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using CorePoint = Annotator.Core.Point;
using CoreRect = Annotator.Core.Rect;
using FillRule = Annotator.Core.Geometry.FillRule;
using Glyphs = Annotator.App.Icons.Glyphs;
using WinPoint = Windows.Foundation.Point;
using WinRect = Windows.Foundation.Rect;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace Annotator.App.Toolbar;

/// <summary>
/// Sizes of the full properties panel's words from the macOS UX reference (reference/ux-reference/macos/properties), so the
/// controls after them keep the Mac's positions with Segoe UI (D-4).
/// </summary>
internal static class PropertyMetrics
{
    /// <summary>The width SF Pro gives the panel's fixed words at their sizes; zero for other text.</summary>
    public static double TextWidth(string text, double size) => (text, size) switch
    {
        ("Stroke color", 11) => 65.5,
        ("Fill", 11) => 15.5,
        // PropertyBlock names, 11-point medium like the color pickers' names.
        ("Width", 11) => 32,
        ("Eraser size", 11) => 59,
        ("Pattern", 11) => 39.5,
        ("Roughness", 11) => 59.5,
        ("Fill style", 11) => 44.5,
        ("Edges", 11) => 33.5,
        ("Pressure", 11) => 47.5,
        ("Smart Draw", 11) => 63,
        ("Start", 11) => 27,
        ("End", 11) => 20.5,
        ("Arrow type", 11) => 59,
        ("Curved", 11) => 39,
        ("Head size", 11) => 53,
        ("Font", 11) => 24.5,
        ("Size", 11) => 23,
        ("Weight", 11) => 38,
        ("Alignment", 11) => 55,
        ("Image opacity", 11) => 76,
        ("Straight", 10) => 38.5,
        ("Curved", 10) => 35,
        ("Constant", 10) => 44,
        ("Ink", 10) => 14.5,
        ("75%", 10) => 22.5,
        ("Opacity", 10) => 37.5,
        ("Width", 10) => 28.5,
        ("Size", 10) => 20.5,
        ("Text size", 10) => 42.5,
        ("Blur", 10) => 19.5,
        ("Strength", 10) => 42,
        ("Pixel size", 10) => 45,
        ("pt", 10) => 10,
        ("%", 10) => 9.5,
        ("Relationships", 10) => 64.5,
        ("Start arrowhead", 13) => 105.5,
        ("End arrowhead", 13) => 97,
        ("None", 10) => 25.5,
        ("Arrow", 10) => 28.5,
        (InspectorPlan.CounterCaption, 10) => 206.5,
        (InspectorPlan.RulerCaption, 10) => 223.5,
        (InspectorPlan.CoverCaption, 10) => 483,
        (InspectorPlan.LaserCaption, 10) => 194,
        ("Editing Rectangle", 11) => 99.5,
        ("Editing Diamond", 11) => 93.5,
        ("Editing 2 selected objects", 11) => 145,
        _ => 0,
    };

    /// <summary>A one-line static text of the panel, as wide as the Mac's text when the reference records it (D-4).</summary>
    public static TextBlock Text(string text, double size, string style, Windows.UI.Text.FontWeight? weight = null)
    {
        TextBlock block = ToolbarParts.Text(text, size, style, weight);
        block.MinWidth = TextWidth(text, size);
        return block;
    }
}

/// <summary>The ink of property samples (Mac InspectorSampleView): the theme's label color at full alpha.</summary>
internal static class PropertiesInk
{
    public static Windows.UI.Color Color() =>
        AppResources.Brush("AnnotatorLabelBrush") is SolidColorBrush label ? label.Color with { A = 255 } : Microsoft.UI.Colors.Black;
}

/// <summary>
/// Text that UI Automation reads as one static text with the Mac's frame, such as the "Editing …" line whose square and
/// words SwiftUI reports together.
/// </summary>
internal sealed partial class AccessibleText : Grid
{
    public AccessibleText(string text, string? identifier = null)
    {
        AutomationProperties.SetName(this, text);
        if (identifier is not null)
        {
            AutomationProperties.SetAutomationId(this, identifier);
        }
    }

    /// <summary>The part of the element UI Automation reports, in its own coordinates; null for all of it.</summary>
    public WinRect? Bounds { get; set; }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed partial class Peer(AccessibleText owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => "TextBlock";

        protected override bool IsControlElementCore() => true;

        protected override IList<AutomationPeer> GetChildrenCore() => new AutomationPeerList();

        protected override WinRect GetBoundingRectangleCore() => owner.Bounds is { } bounds
            ? AutomationFrames.Of(owner, bounds, base.GetBoundingRectangleCore())
            : AutomationFrames.Of(owner, base.GetBoundingRectangleCore());
    }
}

/// <summary>
/// The root of a properties panel. UI Automation reads its controls in the order SwiftUI gives them
/// (<see cref="ReadingOrderGrid"/>): by rows from top to bottom, and from left to right within a row. Groups such as
/// "Arrow properties" keep their own order inside.
/// </summary>
internal sealed partial class PropertiesRoot : ReadingOrderGrid
{
    /// <summary>The one row below a horizontal toolbar; null for the column beside a vertical one.</summary>
    public PropertyRow? Row { get; set; }
}

/// <summary>
/// Draws a property sample's display list (<see cref="InspectorSample"/>) with XAML shapes in the theme's ink: the
/// display list is the one the canvas renderer would draw, and every paint becomes <paramref name="ink"/> at the
/// paint's alpha. Masks clip straight strokes geometrically (WinUI clips only to rectangles).
/// </summary>
internal static class SampleArt
{
    private const double Size = 36;

    public static Canvas Draw(ReadOnlySpan<DisplayItem> items, Brush ink)
    {
        var root = new Canvas
        {
            Width = Size,
            Height = Size,
            IsHitTestVisible = false,
            Clip = new RectangleGeometry { Rect = new WinRect(0, 0, Size, Size) },
        };
        var stack = new Stack<(Canvas Canvas, Mask? Mask)>();
        Canvas current = root;
        Mask? mask = null;
        foreach (DisplayItem item in items)
        {
            switch (item.Op)
            {
                case DisplayOp.StrokePath:
                    if (mask is { } clip && !clip.Rectangular)
                    {
                        foreach (UIElement piece in ClippedStroke(item.Path!, item.Stroke!, Alpha(item.Paint), ink, clip))
                        {
                            current.Children.Add(piece);
                        }
                    }
                    else
                    {
                        current.Children.Add(Stroke(PathGeometryOf(item.Path!, FillRule.NonZero), item.Stroke!, Alpha(item.Paint), ink));
                    }
                    break;
                case DisplayOp.FillPath:
                    current.Children.Add(new XamlPath { Data = PathGeometryOf(item.Path!, item.FillRule), Fill = ink, Opacity = Alpha(item.Paint) });
                    break;
                case DisplayOp.FillRect:
                    CoreRect rect = item.Rect;
                    var box = new Rectangle { Width = rect.Width, Height = rect.Height, Fill = ink, Opacity = Alpha(item.Paint) };
                    Canvas.SetLeft(box, rect.X);
                    Canvas.SetTop(box, rect.Y);
                    current.Children.Add(box);
                    break;
                case DisplayOp.StrokeSegments:
                    StrokeSegments segments = item.Segments!;
                    for (int index = 0; index < segments.Count; index++)
                    {
                        CorePoint start = segments.Start(index), end = segments.End(index);
                        var line = new Line { X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y, Stroke = ink, Opacity = Alpha(segments.Paint) };
                        Apply(line, segments.Style.WithWidth(segments.Width(index)));
                        current.Children.Add(line);
                    }
                    break;
                case DisplayOp.PushTransform:
                    AffineTransform t = item.Transform;
                    stack.Push((current, mask));
                    current = Nest(current, new Canvas
                    {
                        RenderTransform = new MatrixTransform { Matrix = new Matrix(t.A, t.B, t.C, t.D, t.Tx, t.Ty) },
                    });
                    break;
                case DisplayOp.PushLayer:
                    stack.Push((current, mask));
                    var layer = new Canvas { Opacity = item.Opacity };
                    if (item.LayerBounds is { } bounds)
                    {
                        layer.Clip = new RectangleGeometry { Rect = new WinRect(bounds.X, bounds.Y, bounds.Width, bounds.Height) };
                    }
                    if (item.Path is { } path)
                    {
                        mask = new Mask(path, item.FillRule);
                        if (mask.Rectangular && path.Bounds is { } area)
                        {
                            layer.Clip = new RectangleGeometry { Rect = new WinRect(area.X, area.Y, area.Width, area.Height) };
                        }
                    }
                    current = Nest(current, layer);
                    break;
                case DisplayOp.PushClip:
                    stack.Push((current, mask));
                    CoreRect area2 = item.Rect;
                    current = Nest(current, new Canvas { Clip = new RectangleGeometry { Rect = new WinRect(area2.X, area2.Y, area2.Width, area2.Height) } });
                    break;
                case DisplayOp.PopTransform or DisplayOp.PopLayer or DisplayOp.PopClip:
                    (current, mask) = stack.Pop();
                    break;
            }
        }
        return root;
    }

    private static Canvas Nest(Canvas parent, Canvas child)
    {
        parent.Children.Add(child);
        return child;
    }

    /// <summary>A paint's alpha; a contrast theme draws every visible paint at full alpha, in the theme's colors only.</summary>
    private static double Alpha(Paint? paint)
    {
        double alpha = paint is { Kind: PaintKind.Solid } solid ? solid.EffectiveAlpha : paint?.Opacity ?? 1;
        return ContrastTheme.IsOn && alpha > 0 ? 1 : alpha;
    }

    private static XamlPath Stroke(Geometry data, StrokeStyle style, double opacity, Brush ink)
    {
        var path = new XamlPath { Data = data, Stroke = ink, Opacity = opacity };
        Apply(path, style);
        return path;
    }

    private static void Apply(Shape shape, StrokeStyle style)
    {
        PenLineCap cap = style.Cap switch
        {
            LineCap.Round => PenLineCap.Round,
            LineCap.Square => PenLineCap.Square,
            _ => PenLineCap.Flat,
        };
        shape.StrokeThickness = style.Width;
        shape.StrokeStartLineCap = cap;
        shape.StrokeEndLineCap = cap;
        shape.StrokeDashCap = cap;
        shape.StrokeLineJoin = style.Join switch
        {
            LineJoin.Round => PenLineJoin.Round,
            LineJoin.Bevel => PenLineJoin.Bevel,
            _ => PenLineJoin.Miter,
        };
        shape.StrokeMiterLimit = style.MiterLimit;
        if (style.IsDashed && style.Width > 0)
        {
            // XAML measures dashes in stroke widths; the display list in points.
            var dashes = new DoubleCollection();
            foreach (double dash in style.Dashes)
            {
                dashes.Add(dash / style.Width);
            }
            shape.StrokeDashArray = dashes;
            shape.StrokeDashOffset = style.DashPhase / style.Width;
        }
    }

    /// <summary>XAML geometry for a display-list path.</summary>
    public static PathGeometry PathGeometryOf(PathData path, FillRule rule)
    {
        var geometry = new PathGeometry { FillRule = rule == FillRule.EvenOdd ? Microsoft.UI.Xaml.Media.FillRule.EvenOdd : Microsoft.UI.Xaml.Media.FillRule.Nonzero };
        PathFigure? figure = null;
        foreach (PathElement element in path)
        {
            ReadOnlySpan<CorePoint> points = element.Points;
            switch (element.Verb)
            {
                case PathVerb.MoveTo:
                    figure = new PathFigure { StartPoint = Win(points[0]), IsClosed = false, IsFilled = true };
                    geometry.Figures.Add(figure);
                    break;
                case PathVerb.LineTo:
                    figure?.Segments.Add(new LineSegment { Point = Win(points[0]) });
                    break;
                case PathVerb.QuadTo:
                    figure?.Segments.Add(new QuadraticBezierSegment { Point1 = Win(points[0]), Point2 = Win(points[1]) });
                    break;
                case PathVerb.CubicTo:
                    figure?.Segments.Add(new BezierSegment { Point1 = Win(points[0]), Point2 = Win(points[1]), Point3 = Win(points[2]) });
                    break;
                case PathVerb.Close:
                    if (figure is not null)
                    {
                        figure.IsClosed = true;
                    }
                    break;
            }
        }
        return geometry;
    }

    private static WinPoint Win(CorePoint point) => new(point.X, point.Y);

    /// <summary>
    /// The parts of a stroked path that a mask keeps, for paths of straight lines (curves are flattened): each piece
    /// keeps the stroke's cap where the path ends and is cut flat where the mask cuts it.
    /// </summary>
    private static IEnumerable<UIElement> ClippedStroke(PathData path, StrokeStyle style, double opacity, Brush ink, Mask mask)
    {
        foreach (CorePoint[] polyline in Polylines(path))
        {
            for (int index = 1; index < polyline.Length; index++)
            {
                CorePoint from = polyline[index - 1], to = polyline[index];
                foreach ((double start, double end) in mask.Inside(from, to))
                {
                    CorePoint a = from + (to - from) * start, b = from + (to - from) * end;
                    var line = new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = ink, Opacity = opacity };
                    Apply(line, style);
                    if (start > 0)
                    {
                        line.StrokeStartLineCap = PenLineCap.Flat;
                    }
                    if (end < 1)
                    {
                        line.StrokeEndLineCap = PenLineCap.Flat;
                    }
                    yield return line;
                }
            }
        }
    }

    private static List<CorePoint[]> Polylines(PathData path)
    {
        var lines = new List<CorePoint[]>();
        List<CorePoint>? current = null;
        CorePoint last = default, first = default;
        foreach (PathElement element in path)
        {
            ReadOnlySpan<CorePoint> points = element.Points;
            switch (element.Verb)
            {
                case PathVerb.MoveTo:
                    if (current is { Count: > 1 })
                    {
                        lines.Add([.. current]);
                    }
                    current = [points[0]];
                    first = last = points[0];
                    break;
                case PathVerb.LineTo:
                    current?.Add(points[0]);
                    last = points[0];
                    break;
                case PathVerb.QuadTo:
                    for (int step = 1; step <= 16; step++)
                    {
                        double t = step / 16.0, u = 1 - t;
                        current?.Add(last * (u * u) + points[0] * (2 * u * t) + points[1] * (t * t));
                    }
                    last = points[1];
                    break;
                case PathVerb.CubicTo:
                    for (int step = 1; step <= 16; step++)
                    {
                        double t = step / 16.0, u = 1 - t;
                        current?.Add(last * (u * u * u) + points[0] * (3 * u * u * t) + points[1] * (3 * u * t * t) + points[2] * (t * t * t));
                    }
                    last = points[2];
                    break;
                case PathVerb.Close:
                    current?.Add(first);
                    last = first;
                    break;
            }
        }
        if (current is { Count: > 1 })
        {
            lines.Add([.. current]);
        }
        return lines;
    }

    /// <summary>A layer's mask path: the area it keeps.</summary>
    private sealed record Mask(PathData Path, FillRule Rule)
    {
        /// <summary>An axis-aligned rectangle, which a XAML clip draws exactly.</summary>
        public bool Rectangular
        {
            get
            {
                if (Rule != FillRule.NonZero || Path.HasCurves || Path.Bounds is not { } bounds || Path.PointCount > 5)
                {
                    return false;
                }
                foreach (CorePoint point in Path.Points)
                {
                    bool onX = point.X == bounds.X || point.X == bounds.X + bounds.Width;
                    bool onY = point.Y == bounds.Y || point.Y == bounds.Y + bounds.Height;
                    if (!onX || !onY)
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        /// <summary>The parts of the segment from <paramref name="from"/> to <paramref name="to"/> inside the mask, as fractions.</summary>
        public IEnumerable<(double Start, double End)> Inside(CorePoint from, CorePoint to)
        {
            const int Steps = 64;
            bool Keeps(double t) => Path.Contains(from + (to - from) * t, Rule);
            double? open = Keeps(0) ? 0 : null;
            double previous = 0;
            bool inside = open is not null;
            for (int step = 1; step <= Steps; step++)
            {
                double t = step / (double)Steps;
                bool now = Keeps(t);
                if (now != inside)
                {
                    double edge = Edge(previous, t, inside);
                    if (now)
                    {
                        open = edge;
                    }
                    else if (open is { } begin)
                    {
                        yield return (begin, edge);
                        open = null;
                    }
                    inside = now;
                }
                previous = t;
            }
            if (open is { } last)
            {
                yield return (last, 1);
            }

            double Edge(double low, double high, bool lowInside)
            {
                for (int iteration = 0; iteration < 24; iteration++)
                {
                    double middle = (low + high) / 2;
                    if (Keeps(middle) == lowInside)
                    {
                        low = middle;
                    }
                    else
                    {
                        high = middle;
                    }
                }
                return (low + high) / 2;
            }
        }
    }
}
