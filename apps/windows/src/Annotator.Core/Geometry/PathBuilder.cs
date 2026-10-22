namespace Annotator.Core.Geometry;

/// <summary>
/// Builds a <see cref="PathData"/> element by element, like <c>CGMutablePath</c>. The shape helpers emit the elements
/// Core Graphics produces for the Swift calls they replace, as the Mac conformance corpus records them
/// (<c>platform/cgpath.jsonl</c>), so paths compare element by element with the Mac.
/// </summary>
/// <remarks>
/// Core Graphics rules kept here: a move right after a move replaces it; a line or curve without a current point is
/// ignored; a close with no open subpath is ignored, and a segment after a close continues from the closed subpath's
/// start; rectangles are standardized first. Core Graphics computes some arc and rounded-corner points through
/// rotations, so its results can differ from these in the last bit or two.
/// </remarks>
public sealed class PathBuilder
{
    /// <summary>
    /// The cubic control distance for a quarter ellipse of radius 1 that Core Graphics uses: 0.5522847498, the exact
    /// 4 (sqrt 2 - 1) / 3 rounded to ten digits (corpus <c>platform/cgpath.jsonl</c>).
    /// </summary>
    public const double QuarterCircleControl = 0.5522847498;

    private const double QuarterTurn = Math.PI / 2;

    /// <summary>The exact quarter-circle factor 4 (sqrt 2 - 1) / 3, which Core Graphics uses for the first corner of a transformed rounded rectangle.</summary>
    private const double ExactQuarterCircleControl = 0.5522847498307936;

    private PathVerb[] verbs;
    private Point[] points;
    private int verbCount;
    private int pointCount;
    private PathCursor cursor;
    private bool hasCurves;
    private Rect? shapeBounds;

    public PathBuilder(int verbCapacity = 8, int pointCapacity = 8)
    {
        verbs = verbCapacity > 0 ? new PathVerb[verbCapacity] : [];
        points = pointCapacity > 0 ? new Point[pointCapacity] : [];
    }

    public bool IsEmpty => verbCount == 0;

    public int VerbCount => verbCount;

    public int PointCount => pointCount;

    /// <summary>The current point, or null before the first move.</summary>
    public Point? CurrentPoint => cursor.HasCurrent ? cursor.Current : null;

    /// <summary><c>move(to:)</c>. A move right after a move replaces it.</summary>
    public PathBuilder MoveTo(Point point)
    {
        if (verbCount > 0 && verbs[verbCount - 1] == PathVerb.MoveTo)
        {
            points[pointCount - 1] = point;
            shapeBounds = null;
        }
        else
        {
            Append(PathVerb.MoveTo, 1);
            points[pointCount++] = point;
        }
        cursor = new PathCursor(true, point, point, true);
        return this;
    }

    /// <summary><c>addLine(to:)</c>; ignored without a current point.</summary>
    public PathBuilder LineTo(Point point)
    {
        if (!cursor.HasCurrent)
        {
            return this;
        }
        Append(PathVerb.LineTo, 1);
        points[pointCount++] = point;
        cursor = cursor with { Current = point, Open = true };
        return this;
    }

    /// <summary><c>addQuadCurve(to:control:)</c>; ignored without a current point.</summary>
    public PathBuilder QuadTo(Point control, Point end)
    {
        if (!cursor.HasCurrent)
        {
            return this;
        }
        Append(PathVerb.QuadTo, 2);
        points[pointCount++] = control;
        points[pointCount++] = end;
        hasCurves = true;
        cursor = cursor with { Current = end, Open = true };
        return this;
    }

    /// <summary><c>addCurve(to:control1:control2:)</c>; ignored without a current point.</summary>
    public PathBuilder CubicTo(Point control1, Point control2, Point end)
    {
        if (!cursor.HasCurrent)
        {
            return this;
        }
        Append(PathVerb.CubicTo, 3);
        points[pointCount++] = control1;
        points[pointCount++] = control2;
        points[pointCount++] = end;
        hasCurves = true;
        cursor = cursor with { Current = end, Open = true };
        return this;
    }

    /// <summary><c>closeSubpath()</c>; ignored when no subpath is open (an empty path or a second close in a row).</summary>
    public PathBuilder Close()
    {
        if (!cursor.HasCurrent || !cursor.Open)
        {
            return this;
        }
        Append(PathVerb.Close, 0);
        cursor = cursor with { Current = cursor.Start, Open = false };
        return this;
    }

    /// <summary><c>addLines(between:)</c>: a move to the first point and a line to each following point.</summary>
    public PathBuilder AddLines(ReadOnlySpan<Point> linePoints)
    {
        if (linePoints.IsEmpty)
        {
            return this;
        }
        MoveTo(linePoints[0]);
        for (var index = 1; index < linePoints.Length; index++)
        {
            LineTo(linePoints[index]);
        }
        return this;
    }

    /// <summary>
    /// <c>addRect(_:)</c> and <c>CGPath(rect:)</c>: after standardizing, a move to (minX, minY), lines to (maxX, minY),
    /// (maxX, maxY) and (minX, maxY), then a close.
    /// </summary>
    public PathBuilder AddRect(Rect rect)
    {
        rect = RectMath.Standardized(rect);
        MoveTo(new Point(rect.X, rect.Y));
        LineTo(new Point(rect.MaxX, rect.Y));
        LineTo(new Point(rect.MaxX, rect.MaxY));
        LineTo(new Point(rect.X, rect.MaxY));
        return Close();
    }

    /// <summary>
    /// <c>addEllipse(in:)</c> and <c>CGPath(ellipseIn:)</c>: after standardizing, a move to (maxX, midY), then four
    /// quarter-ellipse cubics through (midX, maxY), (minX, midY) and (midX, minY) back to the start (increasing angle,
    /// which is clockwise on a top-left-origin screen), control distance <see cref="QuarterCircleControl"/> times each
    /// radius, then a close.
    /// </summary>
    public PathBuilder AddEllipse(Rect rect)
    {
        rect = RectMath.Standardized(rect);
        var center = rect.Center;
        var rx = rect.Width / 2;
        var ry = rect.Height / 2;
        var kx = rx * QuarterCircleControl;
        var ky = ry * QuarterCircleControl;
        MoveTo(new Point(center.X + rx, center.Y));
        CubicTo(new Point(center.X + rx, center.Y + ky), new Point(center.X + kx, center.Y + ry), new Point(center.X, center.Y + ry));
        CubicTo(new Point(center.X - kx, center.Y + ry), new Point(center.X - rx, center.Y + ky), new Point(center.X - rx, center.Y));
        CubicTo(new Point(center.X - rx, center.Y - ky), new Point(center.X - kx, center.Y - ry), new Point(center.X, center.Y - ry));
        CubicTo(new Point(center.X + kx, center.Y - ry), new Point(center.X + rx, center.Y - ky), new Point(center.X + rx, center.Y));
        return Close();
    }

    /// <summary>
    /// <c>addRoundedRect(in:cornerWidth:cornerHeight:)</c> and <c>CGPath(roundedRect:)</c>. A zero corner gives
    /// <see cref="AddRect"/>. Otherwise, after standardizing: a move to (maxX, midY), then for the corners (maxX, maxY),
    /// (minX, maxY), (minX, minY) and (maxX, minY) a line to the corner's first tangent point, added even when it has
    /// zero length (a full radius), and one quarter-ellipse cubic, then a close. Core Graphics asserts when a corner is
    /// larger than half the rectangle; this port clamps it instead.
    /// </summary>
    public PathBuilder AddRoundedRect(Rect rect, double cornerWidth, double cornerHeight) =>
        RoundedRect(rect, cornerWidth, cornerHeight, QuarterCircleControl, AffineTransform.Identity);

    /// <summary>
    /// <c>CGPath(roundedRect:cornerWidth:cornerHeight:transform:)</c> with a transform: the same elements as
    /// <see cref="AddRoundedRect(Rect, double, double)"/> with every point transformed. Core Graphics then draws the first
    /// corner with the exact quarter-circle factor 4 (sqrt 2 - 1) / 3 and the other three with
    /// <see cref="QuarterCircleControl"/>, and a path that is only this shape reports the transformed rectangle's bounding
    /// box (the Mac's diamond pointer halo; corpus <c>platform/cgpath.jsonl</c> and <c>geometry/pointer-shapes.jsonl</c>).
    /// </summary>
    public PathBuilder AddRoundedRect(Rect rect, double cornerWidth, double cornerHeight, in AffineTransform transform)
    {
        if (transform.IsIdentity)
        {
            return AddRoundedRect(rect, cornerWidth, cornerHeight);
        }
        var alone = verbCount == 0;
        RoundedRect(rect, cornerWidth, cornerHeight, ExactQuarterCircleControl, transform);
        shapeBounds = alone ? transform.TransformBounds(RectMath.Standardized(rect)) : null;
        return this;
    }

    private PathBuilder RoundedRect(Rect rect, double cornerWidth, double cornerHeight, double firstFactor, in AffineTransform transform)
    {
        rect = RectMath.Standardized(rect);
        var cw = Math.Clamp(cornerWidth, 0, rect.Width / 2);
        var ch = Math.Clamp(cornerHeight, 0, rect.Height / 2);
        var identity = transform.IsIdentity;
        var t = transform;
        Point Map(Point point) => identity ? point : t.Transform(point);
        if (!(cw > 0) || !(ch > 0))
        {
            MoveTo(Map(new Point(rect.X, rect.Y)));
            LineTo(Map(new Point(rect.MaxX, rect.Y)));
            LineTo(Map(new Point(rect.MaxX, rect.MaxY)));
            LineTo(Map(new Point(rect.X, rect.MaxY)));
            return Close();
        }
        double fx = cw * firstFactor, fy = ch * firstFactor;
        var kx = cw * QuarterCircleControl;
        var ky = ch * QuarterCircleControl;
        double minX = rect.X, minY = rect.Y, maxX = rect.MaxX, maxY = rect.MaxY;
        MoveTo(Map(new Point(maxX, rect.Center.Y)));
        LineTo(Map(new Point(maxX, maxY - ch)));
        CubicTo(Map(new Point(maxX, maxY - ch + fy)), Map(new Point(maxX - cw + fx, maxY)), Map(new Point(maxX - cw, maxY)));
        LineTo(Map(new Point(minX + cw, maxY)));
        CubicTo(Map(new Point(minX + cw - kx, maxY)), Map(new Point(minX, maxY - ch + ky)), Map(new Point(minX, maxY - ch)));
        LineTo(Map(new Point(minX, minY + ch)));
        CubicTo(Map(new Point(minX, minY + ch - ky)), Map(new Point(minX + cw - kx, minY)), Map(new Point(minX + cw, minY)));
        LineTo(Map(new Point(maxX - cw, minY)));
        CubicTo(Map(new Point(maxX - cw + kx, minY)), Map(new Point(maxX, minY + ch - ky)), Map(new Point(maxX, minY + ch)));
        return Close();
    }

    /// <summary>
    /// <c>addArc(center:radius:startAngle:endAngle:clockwise:)</c>. Angles are in radians. <paramref name="clockwise"/> is
    /// meant for y-up coordinates: false runs toward increasing angles, which is clockwise on a top-left-origin screen.
    /// A sweep against the chosen direction wraps by one turn; a longer sweep in the direction is kept (three half
    /// turns draw six quarters). A line goes from the current point to the arc start (a move on an empty path), then
    /// one cubic per quarter turn with <see cref="QuarterCircleControl"/> and a last cubic for the rest with control
    /// distance 4/3 tan(rest/4). Equal angles only move; a zero radius adds a line to the center and an empty cubic.
    /// </summary>
    public PathBuilder AddArc(Point center, double radius, double startAngle, double endAngle, bool clockwise)
    {
        const double Turn = 2 * Math.PI;
        var sweep = endAngle - startAngle;
        if (!clockwise && sweep < 0)
        {
            sweep = Turn - (-sweep % Turn);
        }
        else if (clockwise && sweep > 0)
        {
            sweep = -(Turn - (sweep % Turn));
        }
        return Arc(center, radius, startAngle, sweep);
    }

    /// <summary><c>addRelativeArc(center:radius:startAngle:delta:)</c>: an arc of exactly <paramref name="delta"/> radians.</summary>
    public PathBuilder AddRelativeArc(Point center, double radius, double startAngle, double delta) =>
        Arc(center, radius, startAngle, delta);

    /// <summary>
    /// <c>addArc(tangent1End:tangent2End:radius:)</c>: a line from the current point to where a circle of
    /// <paramref name="radius"/> touches the line toward <paramref name="tangent1End"/>, then the shorter arc to where it
    /// touches the line toward <paramref name="tangent2End"/>. Collinear points or a zero radius give a line to
    /// <paramref name="tangent1End"/>. Ignored without a current point.
    /// </summary>
    public PathBuilder AddArcToPoint(Point tangent1End, Point tangent2End, double radius)
    {
        if (!cursor.HasCurrent)
        {
            return this;
        }
        var first = cursor.Current - tangent1End;
        var second = tangent2End - tangent1End;
        double firstLength = Math.Sqrt(first.X * first.X + first.Y * first.Y);
        double secondLength = Math.Sqrt(second.X * second.X + second.Y * second.Y);
        if (!(radius > 0) || firstLength == 0 || secondLength == 0)
        {
            return LineTo(tangent1End);
        }
        var u1 = new Point(first.X / firstLength, first.Y / firstLength);
        var u2 = new Point(second.X / secondLength, second.Y / secondLength);
        var cross = u1.X * u2.Y - u1.Y * u2.X;
        if (cross == 0)
        {
            return LineTo(tangent1End);
        }
        var cosine = u1.X * u2.X + u1.Y * u2.Y;
        var distance = radius * (1 + cosine) / Math.Abs(cross);
        var start = new Point(tangent1End.X + u1.X * distance, tangent1End.Y + u1.Y * distance);
        var end = new Point(tangent1End.X + u2.X * distance, tangent1End.Y + u2.Y * distance);
        var normal = cross < 0 ? new Point(u1.Y, -u1.X) : new Point(-u1.Y, u1.X);
        var center = new Point(start.X + normal.X * radius, start.Y + normal.Y * radius);
        var startAngle = Math.Atan2(start.Y - center.Y, start.X - center.X);
        var sweep = Math.Atan2(end.Y - center.Y, end.X - center.X) - startAngle;
        if (sweep > Math.PI)
        {
            sweep -= 2 * Math.PI;
        }
        else if (sweep < -Math.PI)
        {
            sweep += 2 * Math.PI;
        }
        return Arc(center, radius, startAngle, sweep);
    }

    /// <summary><c>addPath(_:)</c>: appends every element unchanged.</summary>
    public PathBuilder AddPath(PathData path) => AddPath(path, AffineTransform.Identity);

    /// <summary><c>addPath(_:transform:)</c>: appends every element with its points transformed.</summary>
    public PathBuilder AddPath(PathData path, in AffineTransform transform)
    {
        ArgumentNullException.ThrowIfNull(path);
        var identity = transform.IsIdentity;
        foreach (var element in path)
        {
            var source = element.Points;
            switch (element.Verb)
            {
                case PathVerb.MoveTo:
                    MoveTo(identity ? source[0] : transform.Transform(source[0]));
                    break;
                case PathVerb.LineTo:
                    LineTo(identity ? source[0] : transform.Transform(source[0]));
                    break;
                case PathVerb.QuadTo:
                    QuadTo(identity ? source[0] : transform.Transform(source[0]), identity ? source[1] : transform.Transform(source[1]));
                    break;
                case PathVerb.CubicTo:
                    CubicTo(identity ? source[0] : transform.Transform(source[0]), identity ? source[1] : transform.Transform(source[1]),
                            identity ? source[2] : transform.Transform(source[2]));
                    break;
                default:
                    Close();
                    break;
            }
        }
        return this;
    }

    /// <summary>
    /// Hands the elements to an immutable path and empties the builder. With <paramref name="reserveForAppends"/>,
    /// spare capacity is kept so a growing stroke can append lines without copying.
    /// </summary>
    public PathData ToPathData(bool reserveForAppends = false)
    {
        if (verbCount == 0)
        {
            Clear();
            return PathData.Empty;
        }
        var bounds = new BoundsAccumulator();
        foreach (var point in points.AsSpan(0, pointCount))
        {
            bounds.Include(point);
        }
        var boundingBox = shapeBounds ?? bounds.Rect;
        var verbArray = verbs;
        var pointArray = points;
        if (!reserveForAppends && (verbArray.Length != verbCount || pointArray.Length != pointCount))
        {
            verbArray = verbs.AsSpan(0, verbCount).ToArray();
            pointArray = points.AsSpan(0, pointCount).ToArray();
        }
        var path = new PathData(new PathStorage(verbArray, pointArray, verbCount, pointCount), verbCount, pointCount,
                                boundingBox, hasCurves, cursor);
        verbs = [];
        points = [];
        Clear();
        return path;
    }

    public void Clear()
    {
        verbCount = 0;
        pointCount = 0;
        cursor = default;
        hasCurves = false;
        shapeBounds = null;
    }

    /// <summary>
    /// A line (or a move on an empty path) to the arc start, then one cubic per quarter turn and one for the rest.
    /// A cubic from angle a to b with control distance h runs through center + r(cos a, sin a), its controls step h
    /// along the tangents.
    /// </summary>
    private PathBuilder Arc(Point center, double radius, double startAngle, double sweep)
    {
        var start = new Point(center.X + radius * Math.Cos(startAngle), center.Y + radius * Math.Sin(startAngle));
        if (cursor.HasCurrent)
        {
            LineTo(start);
        }
        else
        {
            MoveTo(start);
        }
        var direction = sweep < 0 ? -1.0 : 1.0;
        var remaining = Math.Abs(sweep);
        var angle = startAngle;
        while (remaining > 0)
        {
            var piece = Math.Min(remaining, QuarterTurn);
            var next = angle + direction * piece;
            var factor = piece == QuarterTurn ? QuarterCircleControl : 4.0 / 3 * Math.Tan(piece / 4);
            var control = direction * factor * radius;
            double cos0 = Math.Cos(angle), sin0 = Math.Sin(angle), cos1 = Math.Cos(next), sin1 = Math.Sin(next);
            CubicTo(
                new Point(center.X + radius * cos0 - control * sin0, center.Y + radius * sin0 + control * cos0),
                new Point(center.X + radius * cos1 + control * sin1, center.Y + radius * sin1 - control * cos1),
                new Point(center.X + radius * cos1, center.Y + radius * sin1));
            remaining -= piece;
            angle = next;
        }
        return this;
    }

    private void Append(PathVerb verb, int pointsNeeded)
    {
        shapeBounds = null;
        if (verbCount == verbs.Length)
        {
            Array.Resize(ref verbs, Math.Max(8, verbs.Length * 2));
        }
        if (pointCount + pointsNeeded > points.Length)
        {
            Array.Resize(ref points, Math.Max(Math.Max(8, points.Length * 2), pointCount + pointsNeeded));
        }
        verbs[verbCount++] = verb;
    }
}
