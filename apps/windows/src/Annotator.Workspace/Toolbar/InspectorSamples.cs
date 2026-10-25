using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;

namespace Annotator.Workspace.Toolbar;

/// <summary>What a property sample shows (Mac InspectorSample).</summary>
public enum InspectorSampleKind
{
    Thickness,
    Pattern,
    Roughness,
    Edges,
    Fill,
    Route,
    Head,
    HeadSize,
    Pressure,
}

/// <summary>
/// One property sample: the picture inside a 36-point properties button, drawn with the scene renderer from a small
/// element in ink, exactly as the Mac's InspectorSampleView draws it (NativeInspectorComponents.swift).
/// </summary>
public readonly record struct InspectorSample(InspectorSampleKind Kind, double Value = 0, StrokePattern Pattern = StrokePattern.Solid,
                                              FillPattern FillPattern = FillPattern.Solid, ArrowHead Head = ArrowHead.None, bool Flag = false)
{
    /// <summary>The seed the Mac gives every element sample, so they always look the same.</summary>
    public const ulong Seed = 93;

    public static InspectorSample Thickness(double width) => new(InspectorSampleKind.Thickness, width);

    public static InspectorSample ForPattern(StrokePattern pattern) => new(InspectorSampleKind.Pattern, Pattern: pattern);

    public static InspectorSample Roughness(double roughness) => new(InspectorSampleKind.Roughness, roughness);

    public static InspectorSample Edges(bool rounded) => new(InspectorSampleKind.Edges, Flag: rounded);

    public static InspectorSample Fill(FillPattern pattern) => new(InspectorSampleKind.Fill, FillPattern: pattern);

    public static InspectorSample Route(bool curved) => new(InspectorSampleKind.Route, Flag: curved);

    public static InspectorSample ForHead(ArrowHead head, bool start) => new(InspectorSampleKind.Head, Head: head, Flag: start);

    public static InspectorSample HeadSize(double size) => new(InspectorSampleKind.HeadSize, size);

    public static InspectorSample Pressure(bool variable) => new(InspectorSampleKind.Pressure, Flag: variable);

    /// <summary>
    /// The sample's drawing in sample points (0 to 36 on both axes), in <paramref name="ink"/>. Draw it clipped to the
    /// sample's box. <paramref name="builder"/> builds the element's display list as the canvas would.
    /// </summary>
    public DisplayItem[] Draw(RGBA ink, SceneBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        switch (Kind)
        {
            case InspectorSampleKind.Route:
                return RouteItems(ink with { Alpha = 1 });
            case InspectorSampleKind.Roughness:
                return InspectorGlyph.Draw(InspectorGlyph.Roughness(Value), ink with { Alpha = 1 });
            case InspectorSampleKind.Edges:
                return InspectorGlyph.Draw(InspectorGlyph.Edges(Flag), ink with { Alpha = 1 });
        }
        Element element = Element(ink);
        return builder.List(element).Items.ToArray();
    }

    /// <summary>The element the Mac draws for every sample but Route, Roughness and Edges, which are drawn directly.</summary>
    public Element Element(RGBA ink)
    {
        var style = new Style { Color = ink with { Alpha = 1 }, Width = 1.5 };
        var element = new Element(Tool.Line, points: [new Point(7, 18), new Point(29, 18)], style: style);
        switch (Kind)
        {
            case InspectorSampleKind.Thickness:
                element.Style.Width = Math.Min(5, Math.Max(1, Value / 1.5));
                break;
            case InspectorSampleKind.Pattern:
                element.Style.Pattern = Pattern;
                break;
            case InspectorSampleKind.Fill:
                element = new Element(Tool.Rectangle, new Rect(8, 8, 21, 21), style: style);
                element.Style.Fill = style.Color with { Alpha = FillPattern == FillPattern.Solid ? 0.5 : 1 };
                element.Style.FillPattern = FillPattern;
                break;
            case InspectorSampleKind.Head:
                element.Tool = Tool.Arrow;
                element.Style.HeadScale = 0.35;
                element.Style.StartHead = Flag ? Head : ArrowHead.None;
                element.Style.EndHead = Flag ? ArrowHead.None : Head;
                break;
            case InspectorSampleKind.HeadSize:
                element.Tool = Tool.Arrow;
                element.Style.HeadScale = Value * 0.35;
                break;
            case InspectorSampleKind.Pressure:
                element = new Element(Tool.Pen,
                    points: [new Point(6, 22), new Point(11, 17), new Point(16, 15), new Point(21, 18), new Point(29, 17)], style: style);
                element.Style.Width = Flag ? 5 : 3;
                element.Style.Pressure = Flag;
                element.Pressures = Flag ? [0.15, 0.4, 1, 0.6, 0.2] : default;
                break;
            case InspectorSampleKind.Route or InspectorSampleKind.Roughness or InspectorSampleKind.Edges:
                throw new InvalidOperationException($"The {Kind} sample is drawn directly; use Draw.");
            default:
                throw new InvalidOperationException($"Unknown sample {Kind}.");
        }
        element.Seed = Seed;
        return element;
    }

    /// <summary>The Mac's direct drawing of a straight or curved arrow: a 1.8-point shaft and head with round ends.</summary>
    private DisplayItem[] RouteItems(RGBA ink)
    {
        var stroke = new StrokeStyle(1.8, LineCap.Round, LineJoin.Round);
        Paint paint = Paint.Solid(ink);
        PathData shaft, head;
        if (Flag)
        {
            shaft = new PathBuilder(2, 4).MoveTo(new Point(7, 28)).CubicTo(new Point(7, 8), new Point(15, 8), new Point(28, 8)).ToPathData();
            head = new PathBuilder(3, 3).MoveTo(new Point(23, 3)).LineTo(new Point(28, 8)).LineTo(new Point(23, 13)).ToPathData();
        }
        else
        {
            shaft = new PathBuilder(2, 2).MoveTo(new Point(7, 28)).LineTo(new Point(28, 8)).ToPathData();
            head = new PathBuilder(3, 3).MoveTo(new Point(21, 8)).LineTo(new Point(28, 8)).LineTo(new Point(28, 15)).ToPathData();
        }
        return [DisplayItem.StrokePath(shaft, stroke, paint), DisplayItem.StrokePath(head, stroke, paint)];
    }
}
