// Port of apps/macos/Sources/PresentationRendering/GradientPainter.swift (geometry) and the RenderFailure cases of PNGExporter.swift.
namespace Annotator.Core.Geometry;

/// <summary>The line a linear gradient runs along, in the painted object's local coordinates.</summary>
public readonly record struct GradientAxis(Point Start, Point End);

public static class GradientGeometry
{
    /// <summary>
    /// Swift <c>GradientPainter.paint</c>: the gradient is stretched across <paramref name="bounds"/> at the
    /// given angle, centered, with half-length <c>max(1, |cos|*width + |sin|*height) / 2</c>.
    /// </summary>
    public static GradientAxis Axis(double angleDegrees, Rect bounds)
    {
        var angle = angleDegrees * Math.PI / 180;
        double dx = Math.Cos(angle), dy = Math.Sin(angle);
        var length = SwiftMath.Max(1, Math.Abs(dx) * bounds.Width + Math.Abs(dy) * bounds.Height) / 2;
        var center = bounds.Center;
        return new GradientAxis(new Point(center.X - dx * length, center.Y - dy * length),
                                new Point(center.X + dx * length, center.Y + dy * length));
    }

    /// <summary><c>CGGradient</c> with nil locations: colors evenly spaced from 0 to 1.</summary>
    public static double StopLocation(int index, int count) => count < 2 ? 0 : (double)index / (count - 1);
}

/// <summary>The cases of Swift's <c>RenderFailure</c>.</summary>
public enum RenderFailureKind
{
    InvalidSize,
    ContextUnavailable,
    ImageUnavailable,
    EncodingFailed,
}

/// <summary>Swift <c>RenderFailure</c>. <see cref="Exception.Message"/> is the Swift <c>errorDescription</c>.</summary>
public sealed class RenderFailure : Exception
{
    public RenderFailure(RenderFailureKind kind)
        : base(Describe(kind))
    {
        Kind = kind;
    }

    public RenderFailureKind Kind { get; }

    private static string Describe(RenderFailureKind kind) => kind switch
    {
        RenderFailureKind.InvalidSize => "Export dimensions exceed the supported raster budget.",
        RenderFailureKind.ContextUnavailable => "A drawing context could not be created.",
        RenderFailureKind.ImageUnavailable => "The drawing could not be rendered.",
        RenderFailureKind.EncodingFailed => "The PNG could not be encoded.",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
