// Port of the path and timing geometry in apps/macos/Sources/PresentationRendering/CursorHighlightRenderer.swift.
namespace Annotator.Core.Geometry;

public static class CursorHighlightGeometry
{
    /// <summary>Swift <c>CursorHighlightRenderer.path(for:in:)</c>.</summary>
    public static PathData Path(CursorHighlightShape shape, Rect frame)
    {
        var path = new PathBuilder(100, 100);
        var center = frame.Center;
        switch (shape)
        {
            case CursorHighlightShape.Ring:
            case CursorHighlightShape.Disc:
                path.AddEllipse(frame);
                break;
            case CursorHighlightShape.Square:
                path.AddRect(frame);
                break;
            case CursorHighlightShape.RoundedSquare:
                for (var index = 0; index <= 96; index++)
                {
                    var angle = index * Math.PI * 2 / 96;
                    double c = Math.Cos(angle), s = Math.Sin(angle);
                    var point = new Point(center.X + (c < 0 ? -1 : 1) * Math.Sqrt(Math.Abs(c)) * frame.Width / 2,
                                          center.Y + (s < 0 ? -1 : 1) * Math.Sqrt(Math.Abs(s)) * frame.Height / 2);
                    if (index == 0)
                    {
                        path.MoveTo(point);
                    }
                    else
                    {
                        path.LineTo(point);
                    }
                }
                path.Close();
                break;
            case CursorHighlightShape.Diamond:
            {
                var side = frame.Width / Math.Sqrt(2);
                var box = new Rect(center.X - side / 2, center.Y - side / 2, side, side);
                var transform = AffineTransform.Translation(center.X, center.Y).Rotated(Math.PI / 4).Translated(-center.X, -center.Y);
                path.AddRoundedRect(box, side * 0.12, side * 0.12, transform);
                break;
            }
            case CursorHighlightShape.Hexagon:
                for (var index = 0; index < 6; index++)
                {
                    var angle = index * Math.PI / 3;
                    var point = new Point(center.X + Math.Cos(angle) * frame.Width / 2, center.Y + Math.Sin(angle) * frame.Height / 2);
                    if (index == 0)
                    {
                        path.MoveTo(point);
                    }
                    else
                    {
                        path.LineTo(point);
                    }
                }
                path.Close();
                break;
            case CursorHighlightShape.Crosshair:
            {
                var gap = frame.Width * 0.13;
                path.MoveTo(new Point(frame.X, center.Y)).LineTo(new Point(center.X - gap, center.Y));
                path.MoveTo(new Point(center.X + gap, center.Y)).LineTo(new Point(frame.MaxX, center.Y));
                path.MoveTo(new Point(center.X, frame.Y)).LineTo(new Point(center.X, center.Y - gap));
                path.MoveTo(new Point(center.X, center.Y + gap)).LineTo(new Point(center.X, frame.MaxY));
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(shape));
        }
        return path.ToPathData();
    }

    /// <summary>Pulse scale: a fast attack to the peak at 25% of the click, then a slower eased release back to 1.</summary>
    public static double PulseScale(double progress)
    {
        var p = SwiftMath.Min(1, SwiftMath.Max(0, progress));
        if (p < 0.25)
        {
            return 1 + 0.18 * (1 - Math.Pow(1 - p / 0.25, 3));
        }
        return 1 + 0.18 * (1 - EaseInOut((p - 0.25) / 0.75));
    }

    /// <summary>cubic-bezier(0.4, 0, 0.2, 1), solved by 32 bisection steps like the Swift module.</summary>
    public static double EaseInOut(double x)
    {
        if (!(x > 0))
        {
            return 0;
        }
        if (!(x < 1))
        {
            return 1;
        }
        static double Sample(double t, double a, double b) => 3 * (1 - t) * (1 - t) * t * a + 3 * (1 - t) * t * t * b + t * t * t;
        double low = 0, high = 1;
        for (var step = 0; step < 32; step++)
        {
            var middle = (low + high) / 2;
            if (Sample(middle, 0.4, 0.2) < x)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }
        return Sample((low + high) / 2, 0, 1);
    }
}
