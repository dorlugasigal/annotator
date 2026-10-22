// Port of apps/macos/Sources/AnnotationCore/ElementInteraction.swift.
namespace Annotator.Core;

public static class ElementStacking
{
    /// <summary>Drawings first, then pixel effects, then solid covers: the visible stacking order.</summary>
    public static ElementList Ordered(ElementList elements) => ElementList.CreateRange(OrderedIndices(elements).Select(index => elements[index]));

    public static Element? Frontmost(Point point, ElementList elements)
    {
        var stack = new List<int>();
        foreach (var index in OrderedIndices(elements))
        {
            if (HitTesting.Contains(point, elements[index]))
            {
                stack.Add(index);
            }
        }
        if (stack.Count == 0)
        {
            return null;
        }
        ref readonly var top = ref elements[stack[^1]];
        if (!top.Tool.IsShape || !IsSeeThrough(top))
        {
            return top;
        }
        // Text drawn inside an outline shape stays clickable even when the shape is newer.
        for (var position = stack.Count - 1; position >= 0; position--)
        {
            ref readonly var element = ref elements[stack[position]];
            if (element.Tool == Tool.Text)
            {
                return element;
            }
            if (!element.Tool.IsShape || !IsSeeThrough(element))
            {
                break;
            }
        }
        return top;
    }

    private static List<int> OrderedIndices(ElementList elements)
    {
        var order = new List<int>(elements.Count);
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
        return order;
    }

    private static bool IsSeeThrough(in Element shape)
    {
        if (shape.Style.Fill is not { } fill || !(fill.Alpha * shape.Style.Opacity >= 0.5))
        {
            return true;
        }
        return shape.Style.FillPattern != FillPattern.Solid && shape.Style.FillGradient is null;
    }
}

public readonly record struct ConnectorCurve(Point Start, Point FirstControl, Point SecondControl, Point End)
{
    public Rect Bounds => Rect.Enclosing([Start, FirstControl, SecondControl, End]);

    /// <summary>Swift `point(at:)`.</summary>
    public Point PointAt(double t)
    {
        var u = 1 - t;
        return Start * (u * u * u) + FirstControl * (3 * u * u * t) + SecondControl * (3 * u * t * t) + End * (t * t * t);
    }

    public bool Contains(Point point, double tolerance) => Hits(point, tolerance, 0);

    private bool Hits(Point point, double tolerance, int depth)
    {
        if (!Bounds.Contains(point, tolerance))
        {
            return false;
        }
        var flatness = SwiftMath.Max(HitTesting.Distance(FirstControl, Start, End), HitTesting.Distance(SecondControl, Start, End));
        if (flatness <= 0.5 || depth >= 10)
        {
            return HitTesting.Distance(point, Start, End) <= tolerance;
        }
        var a = (Start + FirstControl) * 0.5;
        var b = (FirstControl + SecondControl) * 0.5;
        var c = (SecondControl + End) * 0.5;
        var d = (a + b) * 0.5;
        var e = (b + c) * 0.5;
        var middle = (d + e) * 0.5;
        return new ConnectorCurve(Start, a, d, middle).Hits(point, tolerance, depth + 1)
            || new ConnectorCurve(middle, e, c, End).Hits(point, tolerance, depth + 1);
    }
}

public static class ConnectorGeometry
{
    public static ValueList<ConnectorCurve> Curves(ValueList<Point> points)
    {
        if (points.Count < 3)
        {
            return default;
        }
        var curves = new ConnectorCurve[points.Count - 1];
        for (var index = 0; index < curves.Length; index++)
        {
            curves[index] = Segment(points, index);
        }
        return ValueList.Create<ConnectorCurve>(curves);
    }

    public static ConnectorCurve? EndpointCurve(ValueList<Point> points, bool atStart) =>
        points.Count < 3 ? null : Segment(points, atStart ? 0 : points.Count - 2);

    private static ConnectorCurve Segment(ValueList<Point> points, int index)
    {
        var a = points[SwiftMath.Max(0, index - 1)];
        var b = points[index];
        var c = points[index + 1];
        var d = points[SwiftMath.Min(points.Count - 1, index + 2)];
        return new ConnectorCurve(b, b + (c - a) * (1 / 6.0), c - (d - b) * (1 / 6.0), c);
    }
}
