namespace Annotator.Core.Geometry;

/// <summary>
/// <c>CGRect</c> operations that the Mac renderer uses, with their Core Graphics results (corpus
/// <c>platform/rect.jsonl</c>). A null <see cref="Rect"/>? stands for <c>CGRect.null</c>, for example the bounds of an
/// empty path. Like Core Graphics, every operation standardizes negative sizes first.
/// </summary>
public static class RectMath
{
    /// <summary><c>standardized</c>: the same rectangle with a non-negative width and height.</summary>
    public static Rect Standardized(Rect rect)
    {
        double x = rect.X, y = rect.Y, width = rect.Width, height = rect.Height;
        if (width < 0)
        {
            x += width;
            width = -width;
        }
        if (height < 0)
        {
            y += height;
            height = -height;
        }
        return new Rect(x, y, width, height);
    }

    /// <summary><c>insetBy(dx:dy:)</c>: null when the result would have a negative width or height.</summary>
    public static Rect? Inset(Rect rect, double dx, double dy)
    {
        rect = Standardized(rect);
        var width = rect.Width - dx * 2;
        var height = rect.Height - dy * 2;
        if (width < 0 || height < 0)
        {
            return null;
        }
        return new Rect(rect.X + dx, rect.Y + dy, width, height);
    }

    /// <summary><c>insetBy(dx: -dx, dy: -dy)</c> for non-negative amounts, which never becomes null.</summary>
    public static Rect Outset(Rect rect, double dx, double dy)
    {
        rect = Standardized(rect);
        return new Rect(rect.X + -dx, rect.Y + -dy, rect.Width - -dx * 2, rect.Height - -dy * 2);
    }

    /// <summary><c>CGRect.union</c>: a null side yields the other side.</summary>
    public static Rect? Union(Rect? first, Rect? second)
    {
        if (first is not { } a)
        {
            return second is { } only ? Standardized(only) : null;
        }
        return second is { } b ? Standardized(a).Union(Standardized(b)) : Standardized(a);
    }

    /// <summary><c>CGRect.union</c> with a rectangle that is never null.</summary>
    public static Rect Union(Rect first, Rect? second) =>
        second is { } b ? Standardized(first).Union(Standardized(b)) : Standardized(first);

    /// <summary><c>CGRect.intersection</c>: null when the rectangles do not meet.</summary>
    public static Rect? Intersection(Rect first, Rect second)
    {
        first = Standardized(first);
        second = Standardized(second);
        var x = Math.Max(first.X, second.X);
        var y = Math.Max(first.Y, second.Y);
        var maxX = Math.Min(first.MaxX, second.MaxX);
        var maxY = Math.Min(first.MaxY, second.MaxY);
        if (maxX < x || maxY < y)
        {
            return null;
        }
        return new Rect(x, y, maxX - x, maxY - y);
    }

    /// <summary><c>CGRect.isEmpty</c>: true for null and for a zero width or height.</summary>
    public static bool IsEmpty(Rect? rect) => rect is not { } value || value.Width == 0 || value.Height == 0;

    /// <summary>
    /// <c>CGRect.integral</c>: after standardizing, the origin rounded down and the far corner rounded up, so a zero-size
    /// rectangle off the pixel grid becomes 1 x 1.
    /// </summary>
    public static Rect Integral(Rect rect)
    {
        rect = Standardized(rect);
        var minX = Math.Floor(rect.X);
        var minY = Math.Floor(rect.Y);
        var maxX = Math.Ceiling(rect.MaxX);
        var maxY = Math.Ceiling(rect.MaxY);
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>Whether <paramref name="inner"/> lies inside <paramref name="outer"/>, edges included.</summary>
    public static bool Contains(Rect outer, Rect inner)
    {
        outer = Standardized(outer);
        inner = Standardized(inner);
        return inner.X >= outer.X && inner.Y >= outer.Y && inner.MaxX <= outer.MaxX && inner.MaxY <= outer.MaxY;
    }

    /// <summary>
    /// Whether the rectangles overlap, like <c>CGRect.intersects</c>: rectangles that only touch, or whose overlap has
    /// no area, do not intersect.
    /// </summary>
    public static bool Intersects(Rect first, Rect second) => !IsEmpty(Intersection(first, second));

    /// <summary>
    /// <c>CGRect.contains(_:)</c> with <c>CGRect.null</c> (null): every rectangle contains null, and null contains only null.
    /// </summary>
    public static bool Contains(Rect? outer, Rect? inner) => inner is not { } contained || (outer is { } container && Contains(container, contained));
}
