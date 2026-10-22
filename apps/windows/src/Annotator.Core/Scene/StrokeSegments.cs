using Annotator.Core.Geometry;

namespace Annotator.Core.Scene;

/// <summary>
/// Pressure ink: the Mac strokes every segment of the stroke separately with the width of its end sample,
/// <c>width * max(0.15, pressure)</c>, round caps and joins, and the dash pattern restarting at each segment.
/// The run references the element's points and pressures, so building it copies nothing.
/// </summary>
public sealed class StrokeSegments : IEquatable<StrokeSegments>
{
    public StrokeSegments(ValueList<Point> points, ValueList<double> pressures, double baseWidth, StrokeStyle style,
                          Paint paint, Rect frame)
    {
        if (pressures.Count != points.Count)
        {
            throw new ArgumentException("Pressure ink needs one pressure per point.", nameof(pressures));
        }
        Points = points;
        Pressures = pressures;
        BaseWidth = baseWidth;
        Style = style;
        Paint = paint;
        Frame = frame;
    }

    public ValueList<Point> Points { get; }

    public ValueList<double> Pressures { get; }

    /// <summary>The style width before pressure.</summary>
    public double BaseWidth { get; }

    /// <summary>Cap, join, miter limit and user-space dashes; the width comes from <see cref="Width"/> per segment.</summary>
    public StrokeStyle Style { get; }

    public Paint Paint { get; }

    /// <summary>The element frame, which contains every point.</summary>
    public Rect Frame { get; }

    /// <summary>The number of segments.</summary>
    public int Count => Math.Max(0, Points.Count - 1);

    public Point Start(int segment) => Points[segment];

    public Point End(int segment) => Points[segment + 1];

    public double Width(int segment) => BaseWidth * SwiftMath.Max(0.15, Pressures[segment + 1]);

    /// <summary>
    /// The Mac's culling margin: two device pixels in user space, <c>max(width, height)</c> of the device rectangle
    /// (0, 0, 2, 2) mapped back through the inverse of <paramref name="userToDevice"/>.
    /// </summary>
    public static double CullMargin(in AffineTransform userToDevice)
    {
        if (userToDevice.Inverted() is not { } inverse)
        {
            return double.PositiveInfinity;
        }
        var box = inverse.TransformBounds(new Rect(0, 0, 2, 2));
        return SwiftMath.Max(box.Width, box.Height);
    }

    /// <summary>
    /// Whether segments need culling: the clip's user-space bounding box does not contain the whole frame grown
    /// by half the width and the margin. Culling never changes pixels; it skips segments that cannot reach the clip.
    /// </summary>
    public bool NeedsCulling(Rect clipBounds, double margin) =>
        !RectMath.Contains(clipBounds, RectMath.Outset(Frame, BaseWidth / 2 + margin, BaseWidth / 2 + margin));

    /// <summary>Whether the segment's padded bounds reach <paramref name="clipBounds"/>; draw it only then.</summary>
    public bool Reaches(int segment, Rect clipBounds, double margin)
    {
        Point start = Points[segment], end = Points[segment + 1];
        var padding = Width(segment) / 2 + margin;
        var bounds = new Rect(SwiftMath.Min(start.X, end.X) - padding, SwiftMath.Min(start.Y, end.Y) - padding,
                              Math.Abs(end.X - start.X) + padding * 2, Math.Abs(end.Y - start.Y) + padding * 2);
        return RectMath.Intersects(bounds, clipBounds);
    }

    public bool Equals(StrokeSegments? other) =>
        other is not null && (ReferenceEquals(this, other)
            || (Points == other.Points && Pressures == other.Pressures && BaseWidth == other.BaseWidth
                && Style.Equals(other.Style) && Paint.Equals(other.Paint) && Frame == other.Frame));

    public override bool Equals(object? obj) => Equals(obj as StrokeSegments);

    public override int GetHashCode() => HashCode.Combine(Points.Count, BaseWidth, Frame);
}
