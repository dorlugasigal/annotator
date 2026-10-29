using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Workspace.Canvas;

namespace Annotator.Workspace.Artwork;

/// <summary>
/// The canvas cursors Windows does not provide, drawn as the Mac's ToolCursorFactory draws them: the tool footprint of
/// the pen, highlighter and eraser, the laser pointer's dot and the rotation cursor. Images are rendered for one monitor
/// scale, so each becomes a sharp system cursor there (WIN-OVR-11). The hotspot is the image center, where the ink lands.
/// </summary>
public static class CursorArtwork
{
    private static readonly RGBA White = new(1, 1, 1);
    private static readonly RGBA Black = new(0, 0, 0);
    private static readonly RGBA Shade = new(0, 0, 0, 0.12);

    /// <summary>
    /// A cursor image: <see cref="Bgra"/> holds straight-alpha BGRA rows, top row first, as Windows cursor bitmaps expect.
    /// </summary>
    public sealed record Image(int Width, int Height, int HotspotX, int HotspotY, byte[] Bgra)
    {
        /// <summary>The alpha of a pixel, for tests.</summary>
        public byte Alpha(int x, int y) => Bgra[(y * Width + x) * 4 + 3];
    }

    /// <summary>
    /// Mac ToolCursorFactory.cursor(tool:width:) for <see cref="CanvasCursorKind.Footprint"/>: the nib (a circle, or a
    /// vertical chisel for the highlighter) outlined white and black, shaded and marked with a center cross except for the
    /// eraser, in a square of <see cref="CanvasCursor.ImageSide"/> points at <paramref name="scale"/> pixels per point.
    /// </summary>
    public static Image Footprint(CanvasCursor cursor, double scale)
    {
        if (cursor.Kind != CanvasCursorKind.Footprint)
        {
            throw new ArgumentException("Only footprint cursors have footprint artwork.", nameof(cursor));
        }
        var canvas = new Raster(cursor.ImageSide, scale);
        Size nib = cursor.Nib;
        double halfWidth = nib.Width * scale / 2, halfHeight = nib.Height * scale / 2;
        double center = canvas.Center;
        bool chisel = cursor.Tool == Tool.Highlighter;
        Shape outline = chisel
            ? Shape.RectangleStroke(center - halfWidth, center - halfHeight, center + halfWidth, center + halfHeight)
            : Shape.Ring(center, center, halfHeight);
        canvas.Stroke(outline, 3 * scale, White);
        canvas.Stroke(outline, 1 * scale, Black);
        if (cursor.Tool != Tool.Eraser)
        {
            canvas.Fill(chisel ? Shape.Box(center - halfWidth, center - halfHeight, center + halfWidth, center + halfHeight)
                               : Shape.Disk(center, center, halfHeight), Shade);
            Shape cross = Shape.Cross(center, center, 3 * scale);
            canvas.Stroke(cross, 2 * scale, White);
            canvas.Stroke(cross, 0.7 * scale, Black);
        }
        return canvas.ToImage();
    }

    /// <summary>
    /// Mac ToolCursorFactory.laserCursor(width:color:): a dot as wide as the trail's head in the laser's color, inside a
    /// 1.5-point white ring and a 5-point glow of the color at alpha 0.28, in a square of <see cref="CanvasCursor.ImageSide"/>
    /// points at <paramref name="scale"/> pixels per point.
    /// </summary>
    public static Image Laser(CanvasCursor cursor, double scale)
    {
        if (cursor.Kind != CanvasCursorKind.Laser)
        {
            throw new ArgumentException("Only the laser pointer has laser artwork.", nameof(cursor));
        }
        var canvas = new Raster(cursor.ImageSide, scale);
        double center = canvas.Center;
        double radius = cursor.LaserRadius * scale;
        var solid = new RGBA(cursor.Color.Red, cursor.Color.Green, cursor.Color.Blue);
        canvas.Fill(Shape.Disk(center, center, radius + 5 * scale), solid with { Alpha = 0.28 });
        canvas.Fill(Shape.Disk(center, center, radius + 1.5 * scale), White);
        canvas.Fill(Shape.Disk(center, center, radius), solid);
        return canvas.ToImage();
    }

    /// <summary>
    /// Mac ToolCursorFactory.rotationCursor: an open circle with an arrowhead, white under black, round ends, in a
    /// 32-point square at <paramref name="scale"/> pixels per point.
    /// </summary>
    public static Image Rotation(double scale)
    {
        var canvas = new Raster(32, scale);
        double unit = scale;
        double origin = canvas.Center - 16 * unit;
        // The Mac draws in a y-up image: the arc runs from 45 to 315 degrees and the arrowhead sits at the lower right.
        Shape symbol = Shape.Rotation(origin, unit);
        canvas.Stroke(symbol, 4 * unit, White);
        canvas.Stroke(symbol, 2 * unit, Black);
        return canvas.ToImage();
    }

    /// <summary>Geometry in pixels with a coverage rule for strokes and fills.</summary>
    private abstract class Shape
    {
        /// <summary>The covered fraction of the pixel whose top-left corner is (<paramref name="x"/>, <paramref name="y"/>).</summary>
        public abstract double Coverage(double x, double y, double halfWidth);

        public static Shape Ring(double x, double y, double radius) => new RingShape(x, y, radius);

        public static Shape Disk(double x, double y, double radius) => new DiskShape(x, y, radius);

        public static Shape RectangleStroke(double left, double top, double right, double bottom) => new RectangleShape(left, top, right, bottom);

        public static Shape Box(double left, double top, double right, double bottom) => new BoxShape(left, top, right, bottom);

        public static Shape Cross(double x, double y, double arm) => new CrossShape(x, y, arm);

        public static Shape Rotation(double origin, double unit) => new RotationShape(origin, unit);

        /// <summary>The overlap of [<paramref name="low"/>, <paramref name="high"/>] with a pixel-wide interval around <paramref name="at"/>.</summary>
        protected static double Band(double at, double low, double high) =>
            Math.Clamp(Math.Min(at + 0.5, high) - Math.Max(at - 0.5, low), 0, 1);

        /// <summary>The area a rectangle covers in the pixel at (<paramref name="x"/>, <paramref name="y"/>).</summary>
        protected static double Area(double x, double y, double left, double top, double right, double bottom) =>
            Math.Clamp(Math.Min(x + 1, right) - Math.Max(x, left), 0, 1) * Math.Clamp(Math.Min(y + 1, bottom) - Math.Max(y, top), 0, 1);

        /// <summary>A round-ended line of half width <paramref name="halfWidth"/> at distance <paramref name="distance"/>.</summary>
        protected static double Round(double distance, double halfWidth) => Math.Clamp(halfWidth + 0.5 - distance, 0, 1);

        private sealed class RingShape(double cx, double cy, double radius) : Shape
        {
            public override double Coverage(double x, double y, double halfWidth)
            {
                double distance = Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy));
                return Band(distance, radius - halfWidth, radius + halfWidth);
            }
        }

        private sealed class DiskShape(double cx, double cy, double radius) : Shape
        {
            public override double Coverage(double x, double y, double halfWidth)
            {
                double distance = Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy));
                return Band(distance, double.NegativeInfinity, radius);
            }
        }

        /// <summary>A rectangle outline with mitered corners, like NSBezierPath(rect:) stroked.</summary>
        private sealed class RectangleShape(double left, double top, double right, double bottom) : Shape
        {
            public override double Coverage(double x, double y, double halfWidth)
            {
                double outer = Area(x, y, left - halfWidth, top - halfWidth, right + halfWidth, bottom + halfWidth);
                double inner = right - left > halfWidth * 2 && bottom - top > halfWidth * 2
                    ? Area(x, y, left + halfWidth, top + halfWidth, right - halfWidth, bottom - halfWidth)
                    : 0;
                return outer - inner;
            }
        }

        private sealed class BoxShape(double left, double top, double right, double bottom) : Shape
        {
            public override double Coverage(double x, double y, double halfWidth) => Area(x, y, left, top, right, bottom);
        }

        /// <summary>Two butt-ended lines through the center, <paramref name="arm"/> long on each side.</summary>
        private sealed class CrossShape(double cx, double cy, double arm) : Shape
        {
            public override double Coverage(double x, double y, double halfWidth) => Math.Max(
                Area(x, y, cx - arm, cy - halfWidth, cx + arm, cy + halfWidth),
                Area(x, y, cx - halfWidth, cy - arm, cx + halfWidth, cy + arm));
        }

        /// <summary>The rotation symbol: an arc from 45 to 315 degrees of radius 10 around (16, 16) and the arrowhead polyline.</summary>
        private sealed class RotationShape(double origin, double unit) : Shape
        {
            private static readonly Point[] Arrow = [new(23, 23), new(29, 22), new(26, 28)];

            public override double Coverage(double x, double y, double halfWidth)
            {
                // Measure in the Mac's 32-point space (y down), then convert the distance back to pixels.
                var point = new Point((x + 0.5 - origin) / unit, (y + 0.5 - origin) / unit);
                double distance = Math.Min(ArcDistance(point), Math.Min(Segment(point, Arrow[0], Arrow[1]), Segment(point, Arrow[1], Arrow[2])));
                return Round(distance * unit, halfWidth);
            }

            private static double ArcDistance(Point point)
            {
                var center = new Point(16, 16);
                // Angles of the y-up drawing: up is positive.
                double angle = Math.Atan2(center.Y - point.Y, point.X - center.X);
                if (angle < 0)
                {
                    angle += 2 * Math.PI;
                }
                if (angle >= Math.PI / 4 && angle <= Math.PI * 7 / 4)
                {
                    return Math.Abs(point.Distance(center) - 10);
                }
                Point start = Polar(center, Math.PI / 4), end = Polar(center, Math.PI * 7 / 4);
                return Math.Min(point.Distance(start), point.Distance(end));
            }

            private static Point Polar(Point center, double angle) => new(center.X + 10 * Math.Cos(angle), center.Y - 10 * Math.Sin(angle));

            private static double Segment(Point point, Point a, Point b)
            {
                Point direction = b - a;
                double length = direction.X * direction.X + direction.Y * direction.Y;
                double t = length > 0 ? Math.Clamp(((point.X - a.X) * direction.X + (point.Y - a.Y) * direction.Y) / length, 0, 1) : 0;
                return point.Distance(a + direction * t);
            }
        }
    }

    /// <summary>A square premultiplied image whose side is an even number of pixels, so the center is a pixel corner.</summary>
    private sealed class Raster
    {
        private readonly int _side;
        private readonly double[] _pixels;

        public Raster(double sidePoints, double scale)
        {
            if (!double.IsFinite(scale) || !(scale > 0))
            {
                throw new ArgumentOutOfRangeException(nameof(scale), scale, "The monitor scale must be finite and positive.");
            }
            _side = Math.Max(2, 2 * (int)Math.Ceiling(sidePoints * scale / 2));
            _pixels = new double[_side * _side * 4];
        }

        public double Center => _side / 2.0;

        public void Stroke(Shape shape, double width, RGBA color) => Paint(shape, width / 2, color);

        public void Fill(Shape shape, RGBA color) => Paint(shape, 0, color);

        public Image ToImage()
        {
            var bgra = new byte[_side * _side * 4];
            for (int index = 0; index < _side * _side; index++)
            {
                double alpha = _pixels[index * 4 + 3];
                if (alpha <= 0)
                {
                    continue;
                }
                bgra[index * 4] = Channel(_pixels[index * 4 + 2] / alpha);
                bgra[index * 4 + 1] = Channel(_pixels[index * 4 + 1] / alpha);
                bgra[index * 4 + 2] = Channel(_pixels[index * 4] / alpha);
                bgra[index * 4 + 3] = Channel(alpha);
            }
            return new Image(_side, _side, _side / 2, _side / 2, bgra);
        }

        private void Paint(Shape shape, double halfWidth, RGBA color)
        {
            for (int y = 0; y < _side; y++)
            {
                for (int x = 0; x < _side; x++)
                {
                    double coverage = shape.Coverage(x, y, halfWidth);
                    if (coverage <= 0)
                    {
                        continue;
                    }
                    double alpha = color.Alpha * coverage;
                    int offset = (y * _side + x) * 4;
                    double keep = 1 - alpha;
                    _pixels[offset] = color.Red * alpha + _pixels[offset] * keep;
                    _pixels[offset + 1] = color.Green * alpha + _pixels[offset + 1] * keep;
                    _pixels[offset + 2] = color.Blue * alpha + _pixels[offset + 2] * keep;
                    _pixels[offset + 3] = alpha + _pixels[offset + 3] * keep;
                }
            }
        }

        private static byte Channel(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
    }
}
