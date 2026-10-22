using System.Text.Json.Nodes;

namespace Annotator.RenderHarness;

/// <summary>
/// A live take's scripted pointer, drawn onto the Windows recording where it was at each frame. The Mac recorder shows its
/// scripted pointer in a window of its own above everything (DesktopDemoPointer: the arrow cursor's image, and a 26-point
/// circle of the accent color at 22% while pressed), which its own capture of the desktop leaves out, so zoom and the lens
/// never magnify it; the Windows take moves no real pointer and reports the path instead.
/// </summary>
internal sealed class ScriptedPointer
{
    /// <summary>
    /// The arrow's black body in points, from its tip; with a 1.25-point white border. The Mac draws the cursor image's
    /// top-left corner at the pointer, which puts the tip <see cref="Tip"/> points right of and below it.
    /// </summary>
    private static readonly (double X, double Y)[] Body =
        [(0, 0), (0, 14.6), (3.3, 11.6), (5.4, 16.5), (7.4, 15.6), (5.3, 10.9), (9.8, 10.9)];

    private const double Border = 1.25;
    private const int Samples = 4;

    /// <summary>Where the arrow's tip is drawn from the pointer, in points (the cursor image's hot spot).</summary>
    internal static (double X, double Y) Tip { get; set; } = (4, 4);

    private readonly (double Time, double X, double Y, bool Pressed)[] _path;
    private readonly double _scale;

    private ScriptedPointer((double, double, double, bool)[] path, double scale)
    {
        _path = path;
        _scale = scale;
    }

    /// <summary>
    /// The path in the app's take report (ticks, x and y in points on the stage display, pressed), as times from the take's
    /// start and pixels in the recorded area, whose top-left corner is (<paramref name="displayLeft"/>,
    /// <paramref name="displayTop"/>) pixels from the stage display's.
    /// </summary>
    public static ScriptedPointer? FromReport(JsonArray? path, double frequency, double start, double scale, int displayLeft, int displayTop)
    {
        if (path is null || path.Count == 0)
        {
            return null;
        }
        var points = new (double, double, double, bool)[path.Count];
        for (var index = 0; index < path.Count; index++)
        {
            var entry = path[index]!;
            points[index] = (entry["ticks"]!.GetValue<long>() / frequency - start, displayLeft + entry["x"]!.GetValue<double>() * scale,
                             displayTop + entry["y"]!.GetValue<double>() * scale, entry["pressed"]!.GetValue<bool>());
        }
        return new ScriptedPointer(points, scale);
    }

    /// <summary>The path for the take's JSON file: seconds from the take's start, points in the recorded area.</summary>
    public JsonArray ToJson() => new([.. _path.Select(point => (JsonNode)new JsonObject
    {
        ["time"] = Math.Round(point.Time, 6),
        ["x"] = Math.Round(point.X / _scale, 2),
        ["y"] = Math.Round(point.Y / _scale, 2),
        ["pressed"] = point.Pressed,
    })]);

    /// <summary>Where the pointer is <paramref name="time"/> seconds into the take, in area pixels.</summary>
    public (double X, double Y, bool Pressed) At(double time)
    {
        int low = 0, high = _path.Length - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (_path[middle].Time <= time)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }
        var (_, x, y, pressed) = _path[low];
        return (x, y, pressed);
    }

    /// <summary>Draws the pointer as it was <paramref name="time"/> seconds into the take onto a BGRA frame.</summary>
    public void Draw(Span<byte> bgra, int width, int height, double time)
    {
        var (x, y, pressed) = At(time);
        if (pressed)
        {
            Circle(bgra, width, height, x, y);
        }
        double left = x + (Tip.X - Border) * _scale, top = y + (Tip.Y - Border) * _scale;
        double right = x + (Tip.X + 9.8 + Border) * _scale, bottom = y + (Tip.Y + 16.5 + Border) * _scale;
        for (var row = Math.Max(0, (int)Math.Floor(top)); row < Math.Min(height, (int)Math.Ceiling(bottom)); row++)
        {
            for (var column = Math.Max(0, (int)Math.Floor(left)); column < Math.Min(width, (int)Math.Ceiling(right)); column++)
            {
                int black = 0, white = 0;
                for (var sy = 0; sy < Samples; sy++)
                {
                    for (var sx = 0; sx < Samples; sx++)
                    {
                        var px = (column + (sx + 0.5) / Samples - x) / _scale - Tip.X;
                        var py = (row + (sy + 0.5) / Samples - y) / _scale - Tip.Y;
                        if (Inside(px, py))
                        {
                            black++;
                        }
                        else if (Distance(px, py) <= Border)
                        {
                            white++;
                        }
                    }
                }
                if (black + white == 0)
                {
                    continue;
                }
                var offset = (row * width + column) * 4;
                var keep = 1 - (black + white) / (double)(Samples * Samples);
                var add = 255.0 * white / (Samples * Samples);
                for (var channel = 0; channel < 3; channel++)
                {
                    bgra[offset + channel] = (byte)Math.Clamp(Math.Round(bgra[offset + channel] * keep + add), 0, 255);
                }
            }
        }
    }

    /// <summary>The pressed pointer's circle: 13 points around the pointer, the Mac's accent at 22%.</summary>
    private void Circle(Span<byte> bgra, int width, int height, double x, double y)
    {
        var radius = 13 * _scale;
        (double Blue, double Green, double Red) accent = (255, 122, 0);
        for (var row = Math.Max(0, (int)(y - radius - 1)); row < Math.Min(height, (int)(y + radius + 2)); row++)
        {
            for (var column = Math.Max(0, (int)(x - radius - 1)); column < Math.Min(width, (int)(x + radius + 2)); column++)
            {
                var distance = Math.Sqrt(Math.Pow(column + 0.5 - x, 2) + Math.Pow(row + 0.5 - y, 2));
                var alpha = 0.22 * Math.Clamp(radius - distance + 0.5, 0, 1);
                if (alpha <= 0)
                {
                    continue;
                }
                var offset = (row * width + column) * 4;
                bgra[offset] = (byte)Math.Round(bgra[offset] * (1 - alpha) + accent.Blue * alpha);
                bgra[offset + 1] = (byte)Math.Round(bgra[offset + 1] * (1 - alpha) + accent.Green * alpha);
                bgra[offset + 2] = (byte)Math.Round(bgra[offset + 2] * (1 - alpha) + accent.Red * alpha);
            }
        }
    }

    private static bool Inside(double x, double y)
    {
        var inside = false;
        for (int index = 0, previous = Body.Length - 1; index < Body.Length; previous = index++)
        {
            var (xi, yi) = Body[index];
            var (xj, yj) = Body[previous];
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    private static double Distance(double x, double y)
    {
        var best = double.MaxValue;
        for (int index = 0, previous = Body.Length - 1; index < Body.Length; previous = index++)
        {
            var (ax, ay) = Body[previous];
            var (bx, by) = Body[index];
            double dx = bx - ax, dy = by - ay;
            var t = Math.Clamp(((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy), 0, 1);
            best = Math.Min(best, Math.Sqrt(Math.Pow(ax + dx * t - x, 2) + Math.Pow(ay + dy * t - y, 2)));
        }
        return best;
    }
}
