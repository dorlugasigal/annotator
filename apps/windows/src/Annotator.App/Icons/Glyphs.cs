using System.Globalization;
using Annotator.Core;
using Point = Windows.Foundation.Point;
using Style = Microsoft.UI.Xaml.Style;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using CorePath = Annotator.Core.Geometry.PathData;
using CorePathElement = Annotator.Core.Geometry.PathElement;
using CorePathVerb = Annotator.Core.Geometry.PathVerb;
using SystemDiagramGeometry = Annotator.Core.Geometry.SystemDiagramGeometry;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace Annotator.App.Icons;

/// <summary>One drawable part of an icon: absolute path data (M, L, C, Q, A, Z) on the 24-unit grid.</summary>
internal readonly record struct IconPart(string Data, bool Filled);

/// <summary>
/// An icon converted by tools/windows/update-lucide-icons.mjs: a Lucide icon, or one of the original glyphs in
/// Assets/icon-map.json (named "annotator/...").
/// </summary>
internal sealed record LucideIcon(string Name, IconPart[] Parts);

/// <summary>
/// The one place that turns tools and Mac SF Symbol names into Windows glyphs (D-6, WIN-UI-06). Icons are
/// strokes: width 2 on a 24-unit grid with round caps and joins, scaled to the glyph size.
/// </summary>
internal static class Glyphs
{
    /// <summary>
    /// A tool's toolbar glyph (Mac ToolGlyph): system-design shapes draw their <see cref="SystemDiagramGeometry"/>
    /// outline, stroked at a twelfth of the glyph size with round caps and joins inside a twelfth of padding; other
    /// tools show the icon of their toolbar symbol.
    /// </summary>
    public static FrameworkElement ForTool(Tool tool, double size, string style = "AnnotatorGlyph")
    {
        if (tool.SystemShapeAspectRatio is null)
        {
            return ForSymbol(ToolbarSymbol(tool), size, style);
        }
        // On the icons' 24-unit grid the Mac's size / 12 is a stroke of 2 and padding of 2.
        CorePath outline = SystemDiagramGeometry.Path(new Element(tool, new Annotator.Core.Rect(2, 2, 20, 20)));
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(new XamlPath
        {
            Data = Geometry(outline),
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Style = AppResources.Style(style),
        });
        return new Viewbox { Width = size, Height = size, Child = canvas, IsHitTestVisible = false };
    }

    /// <summary>
    /// The Windows glyph for a Mac SF Symbol name. Unknown names are a programming error. The icon is one path, so
    /// crossing strokes do not darken where a translucent color overlaps itself, as one SF Symbol does not. An icon
    /// with filled parts uses the style's "Filled" variant, whose opaque color keeps the fill and the stroke over it
    /// one color; a style that is already filled fills every part. <paramref name="strokeWidth"/> is on the icons' 24-unit
    /// grid, where Lucide's own is 2; a heavier SF Symbol weight needs more.
    /// </summary>
    public static FrameworkElement ForSymbol(string symbol, double size, string style = "AnnotatorGlyph", double strokeWidth = 2)
    {
        LucideIcon icon = LucideIcons.ForSymbol(symbol)
            ?? throw new ArgumentException($"No Windows icon is mapped for the SF Symbol \"{symbol}\" (Assets/icon-map.json).", nameof(symbol));
        bool fillAll = style.EndsWith("Filled", StringComparison.Ordinal);
        var geometry = new PathGeometry { FillRule = FillRule.Nonzero };
        bool filled = fillAll;
        foreach (IconPart part in icon.Parts)
        {
            AddFigures(geometry, part.Data, fillAll || part.Filled);
            filled |= part.Filled;
        }
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(new XamlPath
        {
            Data = geometry,
            StrokeThickness = strokeWidth,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Style = AppResources.Style(filled && !fillAll ? style + "Filled" : style),
        });
        return new Viewbox { Width = size, Height = size, Child = canvas, IsHitTestVisible = false };
    }

    /// <summary>The Mac toolbarSymbol of each tool (ToolLibrary.swift).</summary>
    public static string ToolbarSymbol(Tool tool) => tool switch
    {
        Tool.Select => "cursorarrow",
        Tool.Rectangle => "rectangle",
        Tool.Diamond => "diamond",
        Tool.Ellipse => "circle",
        Tool.Triangle => "triangle",
        Tool.Hexagon => "hexagon",
        Tool.Star => "star",
        Tool.Arrow => "arrow.up.right",
        Tool.Line => "line.diagonal",
        Tool.Pen => "pencil",
        Tool.Text => "textformat",
        Tool.Highlighter => "highlighter",
        Tool.Eraser => "eraser",
        Tool.Cover => "camera.filters",
        Tool.Blur => "aqi.medium",
        Tool.Pixelate => "square.grid.3x3.fill",
        Tool.Distort => "tornado",
        Tool.Ruler => "ruler",
        Tool.Counter => "1.circle",
        Tool.Image => "photo.on.rectangle",
        Tool.Database => "cylinder",
        Tool.DatabaseStack => "cylinder.split.1x2",
        Tool.Server => "server.rack",
        Tool.Messaging => "bubble.left.and.bubble.right",
        Tool.Web => "globe",
        Tool.Mobile => "iphone",
        Tool.Desktop => "desktopcomputer",
        Tool.Laptop => "laptopcomputer",
        Tool.Document => "doc.text",
        Tool.Configuration => "gearshape",
        Tool.LoadBalancer => "arrow.triangle.branch",
        Tool.ApiGateway => "arrow.right.square",
        Tool.Envelope => "envelope",
        Tool.Cart => "cart",
        Tool.Bell => "bell",
        Tool.Queue => "rectangle.stack",
        Tool.Person => "person",
        Tool.Grid => "grid",
        Tool.Axes => "chart.xyaxis.line",
        _ => throw new ArgumentOutOfRangeException(nameof(tool)),
    };

    /// <summary>
    /// XAML geometry for a Core path, outline only. As in Core Graphics, a segment that follows a close without a move
    /// starts a new figure at the closed subpath's start.
    /// </summary>
    public static PathGeometry Geometry(CorePath path)
    {
        var geometry = new PathGeometry();
        PathFigure? figure = null;
        Point start = default;
        foreach (CorePathElement element in path)
        {
            ReadOnlySpan<Annotator.Core.Point> points = element.Points;
            switch (element.Verb)
            {
                case CorePathVerb.MoveTo:
                    start = Xaml(points[0]);
                    figure = Figure(geometry, start);
                    break;
                case CorePathVerb.LineTo:
                    (figure ??= Figure(geometry, start)).Segments.Add(new LineSegment { Point = Xaml(points[0]) });
                    break;
                case CorePathVerb.QuadTo:
                    (figure ??= Figure(geometry, start)).Segments.Add(new QuadraticBezierSegment { Point1 = Xaml(points[0]), Point2 = Xaml(points[1]) });
                    break;
                case CorePathVerb.CubicTo:
                    (figure ??= Figure(geometry, start)).Segments.Add(new BezierSegment
                    {
                        Point1 = Xaml(points[0]),
                        Point2 = Xaml(points[1]),
                        Point3 = Xaml(points[2]),
                    });
                    break;
                case CorePathVerb.Close when figure is not null:
                    figure.IsClosed = true;
                    figure = null;
                    break;
            }
        }
        return geometry;

        static PathFigure Figure(PathGeometry geometry, Point start)
        {
            var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
            geometry.Figures.Add(figure);
            return figure;
        }
        static Point Xaml(Annotator.Core.Point point) => new(point.X, point.Y);
    }

    /// <summary>Builds XAML geometry from the generated absolute path data.</summary>
    public static PathGeometry Geometry(string data)
    {
        var geometry = new PathGeometry();
        AddFigures(geometry, data, filled: true);
        return geometry;
    }

    /// <summary>Adds the figures of generated absolute path data; <paramref name="filled"/> says whether a fill covers them.</summary>
    private static void AddFigures(PathGeometry geometry, string data, bool filled)
    {
        PathFigure? figure = null;
        string[] tokens = data.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int index = 0;
        Point Next()
        {
            string[] pair = tokens[index++].Split(',');
            return new Point(double.Parse(pair[0], CultureInfo.InvariantCulture), double.Parse(pair[1], CultureInfo.InvariantCulture));
        }
        double Number() => double.Parse(tokens[index++], CultureInfo.InvariantCulture);

        while (index < tokens.Length)
        {
            string command = tokens[index++];
            switch (command)
            {
                case "M":
                    figure = new PathFigure { StartPoint = Next(), IsClosed = false, IsFilled = filled };
                    geometry.Figures.Add(figure);
                    break;
                case "L":
                    figure!.Segments.Add(new LineSegment { Point = Next() });
                    break;
                case "C":
                    figure!.Segments.Add(new BezierSegment { Point1 = Next(), Point2 = Next(), Point3 = Next() });
                    break;
                case "Q":
                    figure!.Segments.Add(new QuadraticBezierSegment { Point1 = Next(), Point2 = Next() });
                    break;
                case "A":
                    Point radius = Next();
                    double rotation = Number();
                    bool large = Number() != 0;
                    bool sweep = Number() != 0;
                    figure!.Segments.Add(new ArcSegment
                    {
                        Size = new Size(radius.X, radius.Y),
                        RotationAngle = rotation,
                        IsLargeArc = large,
                        SweepDirection = sweep ? SweepDirection.Clockwise : SweepDirection.Counterclockwise,
                        Point = Next(),
                    });
                    break;
                case "Z":
                    figure!.IsClosed = true;
                    break;
                default:
                    throw new FormatException($"Unexpected path command \"{command}\".");
            }
        }
    }
}
