using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;

namespace Annotator.Rendering.Interop;

/// <summary>Conversions between Annotator.Core values and Direct2D structures.</summary>
internal static class D2D
{
    public static D2D_POINT_2F Point(Point point) => new() { x = (float)point.X, y = (float)point.Y };

    public static D2D_POINT_2F Point(double x, double y) => new() { x = (float)x, y = (float)y };

    public static D2D_RECT_F Rect(Rect rect) =>
        new() { left = (float)rect.X, top = (float)rect.Y, right = (float)rect.MaxX, bottom = (float)rect.MaxY };

    public static D2D_RECT_F Rect(double left, double top, double right, double bottom) =>
        new() { left = (float)left, top = (float)top, right = (float)right, bottom = (float)bottom };

    /// <summary>The layer bounds Direct2D treats as unbounded.</summary>
    public static D2D_RECT_F Infinite => new() { left = float.MinValue, top = float.MinValue, right = float.MaxValue, bottom = float.MaxValue };

    public static D2D_MATRIX_3X2_F Matrix(in AffineTransform transform)
    {
        var matrix = default(D2D_MATRIX_3X2_F);
        matrix.m11 = (float)transform.A;
        matrix.m12 = (float)transform.B;
        matrix.m21 = (float)transform.C;
        matrix.m22 = (float)transform.D;
        matrix.dx = (float)transform.Tx;
        matrix.dy = (float)transform.Ty;
        return matrix;
    }

    public static D2D_MATRIX_3X2_F Identity
    {
        get
        {
            var matrix = default(D2D_MATRIX_3X2_F);
            matrix.m11 = 1;
            matrix.m22 = 1;
            return matrix;
        }
    }

    /// <summary>A straight-alpha sRGB color with <paramref name="opacity"/> folded into alpha.</summary>
    public static D2D1_COLOR_F Color(in RGBA color, double opacity = 1) =>
        new() { r = (float)color.Red, g = (float)color.Green, b = (float)color.Blue, a = (float)(color.Alpha * opacity) };

    public static D2D1_CAP_STYLE Cap(LineCap cap) => cap switch
    {
        LineCap.Round => D2D1_CAP_STYLE.D2D1_CAP_STYLE_ROUND,
        LineCap.Square => D2D1_CAP_STYLE.D2D1_CAP_STYLE_SQUARE,
        _ => D2D1_CAP_STYLE.D2D1_CAP_STYLE_FLAT,
    };

    /// <summary>Core Graphics' miter join becomes a bevel past the miter limit: Direct2D's MITER_OR_BEVEL.</summary>
    public static D2D1_LINE_JOIN Join(LineJoin join) => join switch
    {
        LineJoin.Round => D2D1_LINE_JOIN.D2D1_LINE_JOIN_ROUND,
        LineJoin.Bevel => D2D1_LINE_JOIN.D2D1_LINE_JOIN_BEVEL,
        _ => D2D1_LINE_JOIN.D2D1_LINE_JOIN_MITER_OR_BEVEL,
    };

    public static D2D1_FILL_MODE FillMode(FillRule rule) =>
        rule == FillRule.EvenOdd ? D2D1_FILL_MODE.D2D1_FILL_MODE_ALTERNATE : D2D1_FILL_MODE.D2D1_FILL_MODE_WINDING;

    public static D2D1_INTERPOLATION_MODE Interpolation(ImageInterpolation interpolation) => interpolation switch
    {
        ImageInterpolation.NearestNeighbor => D2D1_INTERPOLATION_MODE.D2D1_INTERPOLATION_MODE_NEAREST_NEIGHBOR,
        ImageInterpolation.Linear => D2D1_INTERPOLATION_MODE.D2D1_INTERPOLATION_MODE_LINEAR,
        _ => D2D1_INTERPOLATION_MODE.D2D1_INTERPOLATION_MODE_HIGH_QUALITY_CUBIC,
    };
}
