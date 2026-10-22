// Port of apps/macos/Sources/AnnotationCore/ArrowHeadGeometry.swift.
namespace Annotator.Core;

public readonly record struct ArrowHeadGeometry(ArrowHead Kind, Point Tip, Point Direction, double Length)
{
    public Point Side => new(-Direction.Y, Direction.X);

    public Point Base => Kind == ArrowHead.Bar ? Tip : Tip - Direction * Length;

    public Point Left => Base + Side * (Length * 0.6);

    public Point Right => Base - Side * (Length * 0.6);

    public ValueList<Point> Square
    {
        get
        {
            var along = Direction * (Length / 3);
            var across = Side * (Length / 3);
            return [Tip + along + across, Tip + along - across, Tip - along - across, Tip - along + across];
        }
    }

    public ValueList<Point> Polygon
    {
        get
        {
            if (Kind == ArrowHead.Square)
            {
                return Square;
            }
            if (Kind is ArrowHead.Diamond or ArrowHead.DiamondOutline)
            {
                var center = Tip - Direction * (Length * 0.5);
                return [Tip, center + Side * (Length * 0.36), Tip - Direction * Length, center - Side * (Length * 0.36)];
            }
            return [Left, Tip, Right];
        }
    }

    public bool Hollow => Kind is ArrowHead.TriangleOutline or ArrowHead.CircleOutline or ArrowHead.DiamondOutline;

    public ValueList<(Point Start, Point End)> RelationshipSegments
    {
        get
        {
            var tip = Tip;
            var direction = Direction;
            var side = Side;
            var length = Length;
            (Point, Point) Bar(double offset)
            {
                var center = tip - direction * (length * offset);
                return (center + side * (length * 0.4), center - side * (length * 0.4));
            }
            var basePoint = Base;
            (Point, Point)[] fork =
            [
                (basePoint, Tip + Side * (Length * 0.5)), (basePoint, Tip), (basePoint, Tip - Side * (Length * 0.5)),
            ];
            return Kind switch
            {
                ArrowHead.CardinalityOne => [Bar(0.25)],
                ArrowHead.CardinalityMany => ValueList.Create<(Point, Point)>(fork),
                ArrowHead.CardinalityOneOrMany => ValueList.Create<(Point, Point)>([.. fork, Bar(0.85)]),
                ArrowHead.CardinalityExactlyOne => [Bar(0.2), Bar(0.7)],
                ArrowHead.CardinalityZeroOrOne => [Bar(0.2)],
                ArrowHead.CardinalityZeroOrMany => ValueList.Create<(Point, Point)>(fork),
                _ => default,
            };
        }
    }

    public (Point Center, double Radius)? RelationshipCircle =>
        Kind is ArrowHead.CardinalityZeroOrOne or ArrowHead.CardinalityZeroOrMany
            ? (Tip - Direction * (Length * 1.25), Length * 0.23)
            : null;

    public static ArrowHeadGeometry? Make(in Element element, bool atStart)
    {
        var kind = atStart ? element.Style.StartHead : element.Style.EndHead;
        if (element.Tool != Tool.Arrow || kind == ArrowHead.None || element.Points.Count < 2)
        {
            return null;
        }
        var points = element.Points.AsSpan();
        var tip = atStart ? points[0] : points[^1];
        Point? found = null;
        if (atStart)
        {
            for (var index = 1; index < points.Length; index++)
            {
                if (points[index].Distance(tip) > 0.001)
                {
                    found = points[index];
                    break;
                }
            }
        }
        else
        {
            for (var index = points.Length - 2; index >= 0; index--)
            {
                if (points[index].Distance(tip) > 0.001)
                {
                    found = points[index];
                    break;
                }
            }
        }
        if (found is not { } neighbor)
        {
            return null;
        }
        var length = SwiftMath.Min(2 * SwiftMath.Max(12, element.Style.Width * 4) * (element.Style.HeadScale ?? 1),
                                   tip.Distance(neighbor) * 0.7);
        var reference = neighbor;
        if (element.Style.Curved && points.Length > 2
            && ConnectorGeometry.EndpointCurve(element.Points, atStart) is { } curve)
        {
            // Aim along the visible approach across the head's span, not a distant chord.
            var reach = SwiftMath.Min(length, tip.Distance(neighbor) * 0.4);
            var tangent = atStart ? curve.Start - curve.FirstControl : curve.End - curve.SecondControl;
            var tangentLength = double.Hypot(tangent.X, tangent.Y);
            for (var step = 1; step <= 24; step++)
            {
                var t = step / 48.0;
                var sample = curve.PointAt(atStart ? t : 1 - t);
                var distance = sample.Distance(tip);
                if (distance > 0.001)
                {
                    var delta = tip - sample;
                    if (tangentLength > 0.001
                        && (delta.X * tangent.X + delta.Y * tangent.Y) / (distance * tangentLength) < 0.866)
                    {
                        break;
                    }
                    reference = sample;
                }
                if (distance >= reach)
                {
                    break;
                }
            }
        }
        var direction = tip - reference;
        var magnitude = tip.Distance(reference);
        if (!(magnitude > 0.001))
        {
            return null;
        }
        return new ArrowHeadGeometry(kind, tip, direction * (1 / magnitude), length);
    }

    public bool Contains(Point point, double tolerance)
    {
        switch (Kind)
        {
            case ArrowHead.None:
                return false;
            case ArrowHead.Circle:
                return point.Distance(Tip) <= Length / 3 + tolerance;
            case ArrowHead.Square:
            {
                var delta = point - Tip;
                return Math.Abs(delta.X * Direction.X + delta.Y * Direction.Y) <= Length / 3 + tolerance
                    && Math.Abs(delta.X * Side.X + delta.Y * Side.Y) <= Length / 3 + tolerance;
            }
            case ArrowHead.Open:
                return HitTesting.Distance(point, Left, Tip) <= tolerance || HitTesting.Distance(point, Tip, Right) <= tolerance;
            case ArrowHead.Bar:
                return HitTesting.Distance(point, Left, Right) <= tolerance;
            case ArrowHead.Filled:
            {
                var delta = Tip - point;
                var along = delta.X * Direction.X + delta.Y * Direction.Y;
                var across = Math.Abs(delta.X * Side.X + delta.Y * Side.Y);
                return along >= -tolerance && along <= Length + tolerance && across <= SwiftMath.Max(0, along) * 0.6 + tolerance;
            }
            case ArrowHead.CircleOutline:
                return Math.Abs(point.Distance(Tip) - Length / 3) <= tolerance;
            case ArrowHead.TriangleOutline or ArrowHead.DiamondOutline or ArrowHead.Diamond:
            {
                var polygon = Polygon;
                for (var index = 0; index < polygon.Count; index++)
                {
                    if (HitTesting.Distance(point, polygon[index], polygon[(index + 1) % polygon.Count]) <= tolerance)
                    {
                        return true;
                    }
                }
                if (Kind != ArrowHead.Diamond)
                {
                    return false;
                }
                var center = Tip - Direction * (Length * 0.5);
                var delta = point - center;
                return Math.Abs(delta.X * Direction.X + delta.Y * Direction.Y) / (Length * 0.5)
                    + Math.Abs(delta.X * Side.X + delta.Y * Side.Y) / (Length * 0.36) <= 1;
            }
            case ArrowHead.CardinalityOne or ArrowHead.CardinalityMany or ArrowHead.CardinalityOneOrMany
                or ArrowHead.CardinalityExactlyOne or ArrowHead.CardinalityZeroOrOne or ArrowHead.CardinalityZeroOrMany:
            {
                foreach (var segment in RelationshipSegments)
                {
                    if (HitTesting.Distance(point, segment.Start, segment.End) <= tolerance)
                    {
                        return true;
                    }
                }
                if (RelationshipCircle is { } circle)
                {
                    return Math.Abs(point.Distance(circle.Center) - circle.Radius) <= tolerance;
                }
                return false;
            }
            default:
                return false;
        }
    }

    public bool Equals(ArrowHeadGeometry other) =>
        Kind == other.Kind && Tip == other.Tip && Direction == other.Direction && Length == other.Length;

    public override int GetHashCode() => HashCode.Combine(Kind, Tip, Direction, Length);
}
