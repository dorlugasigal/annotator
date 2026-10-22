using System.Numerics;
using System.Text.Json.Nodes;

namespace Annotator.App.Tests;

/// <summary>
/// Reads the pixels a window drew in a contrast theme: each must be one of the theme's colors or, at an edge, an antialiased
/// blend of two or three of them (a value fill drawn over a track mixes three), except inside parts that keep their own colors
/// (annotation colors, pictures of drawings and the logo, WIN-UI-07).
/// </summary>
internal static class ContrastPixels
{
    /// <summary>Night sky (Settings > Accessibility > Contrast themes), as the app's --contrast nightsky simulates it.</summary>
    public static IReadOnlyList<(byte R, byte G, byte B)> NightSky { get; } =
    [
        (0x00, 0x00, 0x00), (0xFF, 0xFF, 0xFF), (0x80, 0x80, 0xFF), (0xA6, 0xA6, 0xA6), (0x2B, 0x2B, 0x2B), (0xD6, 0xB4, 0xFD), (0xFF, 0xEE, 0x32),
    ];

    /// <summary>The share of opaque pixels outside parts with their own colors that are neither theme colors nor blends at an edge.</summary>
    public static double ForeignShare(ReferenceApp app, IReadOnlyList<(byte R, byte G, byte B)> palette)
    {
        byte[] pixels = app.Pixels();
        int width = app.ImageWidth, height = app.ImageHeight;
        double scale = width / Math.Max(1, app.Ready["surface"]![0]!.GetValue<double>());
        var exempt = new List<(int Left, int Top, int Right, int Bottom)>();
        foreach (JsonNode? frame in app.Accessibility["ownColors"]!.AsArray())
        {
            double[] rect = [.. frame!.AsArray().Select(value => value!.GetValue<double>())];
            exempt.Add(((int)Math.Floor(rect[0] * scale) - 1, (int)Math.Floor(rect[1] * scale) - 1,
                        (int)Math.Ceiling((rect[0] + rect[2]) * scale) + 1, (int)Math.Ceiling((rect[1] + rect[3]) * scale) + 1));
        }
        int counted = 0, foreign = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = (y * width + x) * 4;
                int alpha = pixels[index + 3];
                if (alpha < 250 || exempt.Any(rect => x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom))
                {
                    continue;
                }
                counted++;
                byte red = pixels[index + 2], green = pixels[index + 1], blue = pixels[index];
                // A theme color, or an antialiased blend at an edge; a blend that fills a flat area is a translucent fill over
                // the window, which a contrast theme does not have.
                if (!Near(red, green, blue, palette) && (!Blend(red, green, blue, palette) || Flat(pixels, width, height, x, y)))
                {
                    foreign++;
                }
            }
        }
        return counted == 0 ? 0 : foreign / (double)counted;
    }

    /// <summary>True when the 5 by 5 pixels around (x, y) all have its color, as inside a fill rather than at an edge.</summary>
    private static bool Flat(byte[] pixels, int width, int height, int x, int y)
    {
        int center = (y * width + x) * 4;
        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                {
                    return false;
                }
                int index = (ny * width + nx) * 4;
                if (Math.Abs(pixels[index] - pixels[center]) > 2 || Math.Abs(pixels[index + 1] - pixels[center + 1]) > 2
                    || Math.Abs(pixels[index + 2] - pixels[center + 2]) > 2)
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static bool Near(byte red, byte green, byte blue, IReadOnlyList<(byte R, byte G, byte B)> palette) =>
        palette.Any(color => Distance(red - color.R, green - color.G, blue - color.B) <= 12);

    /// <summary>True when the color lies within 12 levels of a mix of two or three theme colors.</summary>
    private static bool Blend(byte red, byte green, byte blue, IReadOnlyList<(byte R, byte G, byte B)> palette)
    {
        const double Allowed = 12;
        var point = new Vector3(red, green, blue);
        for (int first = 0; first < palette.Count; first++)
        {
            (byte R, byte G, byte B) a = palette[first];
            for (int second = first + 1; second < palette.Count; second++)
            {
                (byte R, byte G, byte B) b = palette[second];
                double dx = b.R - a.R, dy = b.G - a.G, dz = b.B - a.B;
                double length = dx * dx + dy * dy + dz * dz;
                double t = Math.Clamp(((red - a.R) * dx + (green - a.G) * dy + (blue - a.B) * dz) / length, 0, 1);
                if (Distance(red - (a.R + t * dx), green - (a.G + t * dy), blue - (a.B + t * dz)) <= Allowed)
                {
                    return true;
                }
                for (int third = second + 1; third < palette.Count; third++)
                {
                    (byte R, byte G, byte B) c = palette[third];
                    if (Closest(point, new Vector3(a.R, a.G, a.B), new Vector3(b.R, b.G, b.B), new Vector3(c.R, c.G, c.B)) is { } closest
                        && Vector3.Distance(point, closest) <= Allowed)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    /// <summary>
    /// The point of the triangle a, b, c nearest to <paramref name="p"/> (Ericson, Real-Time Collision Detection 5.1.5), or
    /// null when the three colors lie on one line, which the two-color check covers.
    /// </summary>
    private static Vector3? Closest(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        if (Vector3.Cross(ab, ac).LengthSquared() < 1)
        {
            return null;
        }
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0)
        {
            return a;
        }
        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3)
        {
            return b;
        }
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
        {
            return a + d1 / (d1 - d3) * ab;
        }
        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6)
        {
            return c;
        }
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
        {
            return a + d2 / (d2 - d6) * ac;
        }
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
        {
            return b + (d4 - d3) / (d4 - d3 + (d5 - d6)) * (c - b);
        }
        float denominator = 1 / (va + vb + vc);
        return a + ab * (vb * denominator) + ac * (vc * denominator);
    }

    private static double Distance(double x, double y, double z) => Math.Sqrt(x * x + y * y + z * z);
}
