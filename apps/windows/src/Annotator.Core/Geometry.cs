// Port of apps/macos/Sources/AnnotationCore/Geometry.swift.
using System.Globalization;

namespace Annotator.Core;

/// <summary>A position in points (one point is one DIP on Windows). Equality uses `==` on each coordinate, like Swift.</summary>
public readonly record struct Point(double X, double Y)
{
    public static Point Zero => default;

    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && Math.Abs(X) <= 10_000_000 && Math.Abs(Y) <= 10_000_000;

    public double Distance(Point other) => double.Hypot(X - other.X, Y - other.Y);

    public static Point operator +(Point left, Point right) => new(left.X + right.X, left.Y + right.Y);

    public static Point operator -(Point left, Point right) => new(left.X - right.X, left.Y - right.Y);

    public static Point operator *(Point left, double right) => new(left.X * right, left.Y * right);

    public Point Rotated(Point center, double angle)
    {
        var offset = this - center;
        return center + new Point(offset.X * Math.Cos(angle) - offset.Y * Math.Sin(angle),
                                  offset.X * Math.Sin(angle) + offset.Y * Math.Cos(angle));
    }

    public bool Equals(Point other) => X == other.X && Y == other.Y;

    public override int GetHashCode() => HashCode.Combine(X, Y);
}

/// <summary>An axis-aligned rectangle in points with a top-left origin.</summary>
public readonly record struct Rect(double X, double Y, double Width, double Height)
{
    /// <summary>Swift `Rect(from:to:)`: the rectangle spanned by two corners.</summary>
    public Rect(Point from, Point to)
        : this(SwiftMath.Min(from.X, to.X), SwiftMath.Min(from.Y, to.Y), Math.Abs(from.X - to.X), Math.Abs(from.Y - to.Y))
    {
    }

    public static Rect Zero => default;

    public Point Origin => new(X, Y);

    public Point Center => new(X + Width / 2, Y + Height / 2);

    public double MaxX => X + Width;

    public double MaxY => Y + Height;

    public bool IsValid =>
        double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height)
        && double.IsFinite(MaxX) && double.IsFinite(MaxY) && Width >= 0 && Height >= 0
        && Math.Abs(X) <= 10_000_000 && Math.Abs(Y) <= 10_000_000 && Width <= 1_000_000 && Height <= 1_000_000;

    public bool Contains(Point point, double padding = 0) =>
        point.X >= X - padding && point.X <= MaxX + padding
        && point.Y >= Y - padding && point.Y <= MaxY + padding;

    public bool Contains(Rect other) => Contains(other.Origin) && Contains(new Point(other.MaxX, other.MaxY));

    public Rect Translated(Point offset) => new(X + offset.X, Y + offset.Y, Width, Height);

    public Rect Union(Rect other) => new(
        SwiftMath.Min(X, other.X), SwiftMath.Min(Y, other.Y),
        SwiftMath.Max(MaxX, other.MaxX) - SwiftMath.Min(X, other.X),
        SwiftMath.Max(MaxY, other.MaxY) - SwiftMath.Min(Y, other.Y));

    /// <summary>Swift `Rect.enclosing`: folds unions in order, so rounding matches the Mac.</summary>
    public static Rect Enclosing(ReadOnlySpan<Point> points)
    {
        if (points.IsEmpty)
        {
            return Zero;
        }
        var result = new Rect(points[0], points[0]);
        for (var index = 1; index < points.Length; index++)
        {
            result = result.Union(new Rect(points[index], points[index]));
        }
        return result;
    }

    public bool Equals(Rect other) => X == other.X && Y == other.Y && Width == other.Width && Height == other.Height;

    public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height);
}

/// <summary>The bounds and rotation ability of a multi-object selection.</summary>
public readonly record struct SelectionGeometry
{
    private SelectionGeometry(Rect bounds, bool canRotate)
    {
        Bounds = bounds;
        CanRotate = canRotate;
    }

    public Rect Bounds { get; }

    public bool CanRotate { get; }

    public ValueList<Point> Corners =>
        [Bounds.Origin, new Point(Bounds.MaxX, Bounds.Y), new Point(Bounds.MaxX, Bounds.MaxY), new Point(Bounds.X, Bounds.MaxY)];

    /// <summary>Swift's failable `init?(elements:selection:)`: null unless at least two objects are selected and one is unlocked.</summary>
    public static SelectionGeometry? Create(ElementList elements, IReadOnlySet<Guid> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var selected = elements.Filter(element => selection.Contains(element.Id));
        if (selected.Count <= 1)
        {
            return null;
        }
        var editable = selected.Filter(element => !element.Locked);
        if (editable.IsEmpty)
        {
            return null;
        }
        var corners = new List<Point>(editable.Count * 4);
        var canRotate = true;
        foreach (ref readonly var element in editable)
        {
            var box = element.Frame;
            corners.Add(box.Origin.Rotated(box.Center, element.Rotation));
            corners.Add(new Point(box.MaxX, box.Y).Rotated(box.Center, element.Rotation));
            corners.Add(new Point(box.MaxX, box.MaxY).Rotated(box.Center, element.Rotation));
            corners.Add(new Point(box.X, box.MaxY).Rotated(box.Center, element.Rotation));
            if (element.Tool.IsProtection)
            {
                canRotate = false;
            }
        }
        return new SelectionGeometry(Rect.Enclosing(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(corners)), canRotate);
    }
}

/// <summary>Maps output (screen) coordinates to source coordinates for zoom.</summary>
public readonly record struct Viewport
{
    /// <summary>Throws <see cref="CanvasError.InvalidGeometry"/> unless both rectangles are valid and non-empty.</summary>
    public Viewport(Rect output, Rect source)
    {
        if (!output.IsValid || !source.IsValid || !(output.Width > 0) || !(output.Height > 0)
            || !(source.Width > 0) || !(source.Height > 0))
        {
            throw CanvasError.InvalidGeometry;
        }
        Output = output;
        Source = source;
    }

    public Rect Output { get; }

    public Rect Source { get; }

    public static Viewport Zoom(Rect display, double factor, Point focus)
    {
        if (!double.IsFinite(factor) || !(factor >= 1) || !(factor <= 16) || !focus.IsFinite
            || !display.IsValid || !(display.Width > 0) || !(display.Height > 0))
        {
            throw CanvasError.InvalidGeometry;
        }
        var width = display.Width / factor;
        var height = display.Height / factor;
        var x = SwiftMath.Min(SwiftMath.Max(focus.X - width / 2, display.X), display.MaxX - width);
        var y = SwiftMath.Min(SwiftMath.Max(focus.Y - height / 2, display.Y), display.MaxY - height);
        return new Viewport(display, new Rect(x, y, width, height));
    }

    public Point SourcePoint(Point outputPoint) => new(
        Source.X + (outputPoint.X - Output.X) * Source.Width / Output.Width,
        Source.Y + (outputPoint.Y - Output.Y) * Source.Height / Output.Height);

    public static Viewport CursorAnchored(Rect display, double factor, Point cursor)
    {
        if (!double.IsFinite(factor) || !(factor >= 1 && factor <= 16) || !cursor.IsFinite
            || !display.IsValid || !(display.Width > 0) || !(display.Height > 0))
        {
            throw CanvasError.InvalidGeometry;
        }
        var x = SwiftMath.Min(display.MaxX, SwiftMath.Max(display.X, cursor.X));
        var y = SwiftMath.Min(display.MaxY, SwiftMath.Max(display.Y, cursor.Y));
        return new Viewport(display, new Rect(
            display.X + (x - display.X) * (1 - 1 / factor),
            display.Y + (y - display.Y) * (1 - 1 / factor),
            display.Width / factor, display.Height / factor));
    }

    public Point OutputPoint(Point sourcePoint) => new(
        Output.X + (sourcePoint.X - Source.X) * Output.Width / Source.Width,
        Output.Y + (sourcePoint.Y - Source.Y) * Output.Height / Source.Height);
}

/// <summary>The cases of Swift's `CanvasError`.</summary>
public enum CanvasErrorKind
{
    InvalidGeometry,
    InvalidStyle,
    InvalidDocument,
    UnsupportedSchema,
    MissingElement,
    NoTransaction,
}

/// <summary>
/// Swift `CanvasError`. <see cref="Exception.Message"/> is the Swift `errorDescription`, word for word.
/// Two errors are equal when their case and payload are equal, like the Swift enum.
/// </summary>
public sealed class CanvasError : Exception, IEquatable<CanvasError>
{
    private CanvasError(CanvasErrorKind kind, string? detail = null, long? schemaVersion = null)
        : base(Describe(kind, detail, schemaVersion))
    {
        Kind = kind;
        Detail = detail;
        SchemaVersion = schemaVersion;
    }

    public CanvasErrorKind Kind { get; }

    /// <summary>The message of `invalidDocument(String)`.</summary>
    public string? Detail { get; }

    /// <summary>The version of `unsupportedSchema(Int)`.</summary>
    public long? SchemaVersion { get; }

    public string ErrorDescription => Message;

    public static CanvasError InvalidGeometry => new(CanvasErrorKind.InvalidGeometry);

    public static CanvasError InvalidStyle => new(CanvasErrorKind.InvalidStyle);

    public static CanvasError MissingElement => new(CanvasErrorKind.MissingElement);

    public static CanvasError NoTransaction => new(CanvasErrorKind.NoTransaction);

    public static CanvasError InvalidDocument(string message) => new(CanvasErrorKind.InvalidDocument, message);

    public static CanvasError UnsupportedSchema(long version) => new(CanvasErrorKind.UnsupportedSchema, schemaVersion: version);

    public bool Equals(CanvasError? other) =>
        other is not null && Kind == other.Kind && Detail == other.Detail && SchemaVersion == other.SchemaVersion;

    public override bool Equals(object? obj) => Equals(obj as CanvasError);

    public override int GetHashCode() => HashCode.Combine(Kind, Detail, SchemaVersion);

    private static string Describe(CanvasErrorKind kind, string? detail, long? version) => kind switch
    {
        CanvasErrorKind.InvalidGeometry => "The drawing contains invalid coordinates or dimensions.",
        CanvasErrorKind.InvalidStyle => "The drawing contains an unsupported style value.",
        CanvasErrorKind.InvalidDocument => $"The drawing could not be opened: {detail}",
        CanvasErrorKind.UnsupportedSchema =>
            $"Drawing format {version?.ToString(CultureInfo.InvariantCulture)} is not supported by this version.",
        CanvasErrorKind.MissingElement => "The selected object no longer exists.",
        CanvasErrorKind.NoTransaction => "There is no active edit to complete.",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
