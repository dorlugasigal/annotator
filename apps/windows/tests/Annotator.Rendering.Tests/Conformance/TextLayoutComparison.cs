using System.Globalization;
using System.Text;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Platform.Displays;
using Annotator.Rendering.Export;
using Annotator.Rendering.Imaging;

namespace Annotator.Rendering.Tests.Conformance;

/// <summary>Where one text run's glyphs lie in the Mac and the Windows image.</summary>
/// <param name="BaselineOffset">Windows minus Mac: the first line's baseline, in device pixels (positive is lower).</param>
/// <param name="TopOffset">Windows minus Mac: the top of the first line's ink, in device pixels; glyph designs differ, so it is reported only.</param>
/// <param name="Tolerance">The baseline offset allowed, in device pixels.</param>
internal sealed record TextRunPlacement(string Text, string Rule, PixelRect Region, PixelRect? Mac, PixelRect? Windows, int MacLines,
                                        int WindowsLines, double HorizontalOffset, double BaselineOffset, double TopOffset, double FontSize,
                                        double Tolerance, bool Passed)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"\"{Text.Replace("\n", "\\n", StringComparison.Ordinal)}\" ({Rule}, {FontSize:F1} px): {(Passed ? "pass" : "FAIL")}, lines {MacLines}/{WindowsLines}, "
        + $"horizontal offset {HorizontalOffset:F1} px, baseline offset {BaselineOffset:F1} px ({BaselineOffset / FontSize:F3} em, at most {Tolerance:F1} px), "
        + $"ink top offset {TopOffset:F1} px ({TopOffset / FontSize:F3} em)");
}

/// <summary>The outcome of comparing a font-dependent scene: its layout rules and its other pixels.</summary>
internal sealed class TextLayoutResult(IReadOnlyList<TextRunPlacement> runs, ComparisonResult rest, RasterImage windows, bool[] ignored)
{
    public IReadOnlyList<TextRunPlacement> Runs { get; } = runs;

    /// <summary>The WIN-REND-05 comparison of everything except glyphs.</summary>
    public ComparisonResult Rest { get; } = rest;

    public RasterImage Windows { get; } = windows;

    public bool[] Ignored { get; } = ignored;

    public bool Passed => Rest.Passed && Runs.All(run => run.Passed);

    public string Summary()
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{(Passed ? "PASS" : "FAIL")}: layout of {Runs.Count(run => run.Passed)}/{Runs.Count} text runs; ");
        text.Append("other pixels ").Append(Rest.Summary());
        foreach (var run in Runs.Where(run => !run.Passed))
        {
            text.Append("; ").Append(run);
        }
        return text.ToString();
    }
}

/// <summary>
/// The comparison for <c>fontDependent</c> scenes, which the Mac draws with its system fonts: it checks the layout rules
/// (WIN-REND-03) and where the glyphs sit instead of glyph pixels. For every text run it finds the glyph ink in both
/// images, by difference from a Windows render without text, with the same rule for both images, and requires the same
/// number of lines and:
/// <list type="bullet">
/// <item>text: the same alignment anchor (left edge, right edge or center) within 2 points;</item>
/// <item>counters, shape labels and ruler labels: the same horizontal center within 2 points;</item>
/// <item>every run: the first line's baseline (the median bottom of the ink's columns that end in the line's lower
/// half) within 0.05 em or one device pixel of the Mac's, which is looked for within a quarter em of the Windows line.</item>
/// </list>
/// Windows draws its system fonts' glyphs and line spacing (D-4) with the first baseline where the Mac's is
/// (<see cref="MacTextMetrics"/>); glyph heights differ between the fonts, so the top of the ink is reported, not
/// judged. Everything else (shapes, rulers, ruler label boxes, counter discs) must pass WIN-REND-05 with the glyphs left out.
/// </summary>
internal static class TextLayoutComparison
{
    private const double AnchorTolerancePoints = 2;
    private const double BaselineToleranceEm = 0.05;

    /// <summary>How far from the Windows glyphs the Mac's first line is looked for.</summary>
    private const double LineReachEm = 0.25;

    /// <summary>A pixel is glyph ink when a channel lies this far outside what the image without text shows within one pixel.</summary>
    private const int InkThreshold = 64;

    private enum Kind
    {
        Text,
        Counter,
        ShapeLabel,
        RulerLabel,
    }

    public static TextLayoutResult Compare(RenderCase item, RasterImage mac, ComparisonThresholds thresholds)
    {
        var elements = RenderCorpus.Elements(item);
        var scene = Warp.Builder().Build(elements);
        var runs = new List<(TextRun Run, PixelRect Box, Kind Kind)>();
        var stripped = new List<DisplayList>();
        foreach (var list in scene.Elements)
        {
            var kept = new List<DisplayItem>();
            var transforms = new Stack<AffineTransform>();
            var current = AffineTransform.Scale(item.Scale, item.Scale);
            var tool = Owner(elements, list.ElementId);
            foreach (var entry in list.Items)
            {
                switch (entry.Op)
                {
                    case DisplayOp.PushTransform:
                        transforms.Push(current);
                        current = AffineTransform.Concat(entry.Transform, current);
                        break;
                    case DisplayOp.PopTransform:
                        current = transforms.Pop();
                        break;
                    case DisplayOp.DrawText:
                        var kind = tool switch
                        {
                            Tool.Text => Kind.Text,
                            Tool.Counter => Kind.Counter,
                            Tool.Ruler => Kind.RulerLabel,
                            _ => Kind.ShapeLabel,
                        };
                        runs.Add((entry.Text!, Pixels(current.TransformBounds(entry.Text!.Frame)), kind));
                        continue;
                }
                kept.Add(entry);
            }
            stripped.Add(DisplayListFactory.Create(list.ElementId, [.. kept]));
        }

        using var exporter = new PngExporter(ConformanceDevices.Devices, Warp.Measurer, MacAppearance.For(item.Appearance));
        var windows = exporter.Render(scene, item.Size, item.Scale, includeSelection: false);
        var bare = exporter.Render(DisplayListFactory.Scene([.. stripped], DisplayList.Empty), item.Size, item.Scale, includeSelection: false);
        int width = mac.Width, height = mac.Height;
        var margin = 8 * item.Scale;
        var search = new bool[width * height];
        foreach (var region in runs.Select(run => run.Box))
        {
            Mark(search, width, height, Grow(region, margin));
        }
        var windowsInk = Differs(windows, bare, 8, 0, null);
        var macInk = Differs(mac, bare, 48, 1, search);
        var ignored = Grow(Or(windowsInk, macInk), width, height, 2);
        var rest = ImageComparison.Compare(mac, windows, thresholds, ignored);
        // Where the glyphs are: the same rule for both images, so their edges count alike.
        var macGlyphs = Differs(mac, bare, InkThreshold, 1, search);
        var windowsGlyphs = Differs(windows, bare, InkThreshold, 1, search);

        var placements = new List<TextRunPlacement>();
        var anchorTolerance = AnchorTolerancePoints * item.Scale;
        foreach (var (run, box, kind) in runs)
        {
            var region = Grow(box, 4 * item.Scale);
            var macBox = Bounds(macGlyphs, width, height, region);
            var windowsBox = Bounds(windowsGlyphs, width, height, region);
            int macLines = Lines(macGlyphs, width, height, region), windowsLines = Lines(windowsGlyphs, width, height, region);
            var fontSize = run.Font.Size * item.Scale;
            var tolerance = Math.Max(1, BaselineToleranceEm * fontSize);
            var rule = kind switch
            {
                Kind.Counter => "counter",
                Kind.ShapeLabel => "shape label",
                Kind.RulerLabel => "ruler label",
                _ => run.Alignment switch
                {
                    TextAlignment.Right => "right-aligned text",
                    TextAlignment.Center => "centered text",
                    _ => "left-aligned text",
                },
            };
            double horizontal = double.NaN, baseline = double.NaN, top = double.NaN;
            var passed = false;
            if (macBox is { } m && windowsBox is { } w && FirstLine(windowsGlyphs, width, region) is { } line)
            {
                horizontal = kind != Kind.Text ? Center(w.Left, w.Right) - Center(m.Left, m.Right) : run.Alignment switch
                {
                    TextAlignment.Right => w.Right - m.Right,
                    TextAlignment.Center => Center(w.Left, w.Right) - Center(m.Left, m.Right),
                    _ => w.Left - m.Left,
                };
                // The Mac's first line is looked for within a quarter em of the Windows one, so differences of a shape's edge
                // between the renderers (a counter's disc around a narrow digit) are not taken for glyphs.
                var reach = (int)Math.Ceiling(LineReachEm * fontSize);
                var macLine = Intersect(new PixelRect(w.Left - reach, line.Top - reach, w.Right + reach, line.Bottom + reach), region);
                if (Baseline(macGlyphs, width, macLine) is { } macBaseline && Bounds(macGlyphs, width, height, macLine) is { } macLineInk
                    && Baseline(windowsGlyphs, width, new PixelRect(region.Left, line.Top, region.Right, line.Bottom)) is { } windowsBaseline)
                {
                    baseline = windowsBaseline - macBaseline;
                    top = line.Top - macLineInk.Top;
                    passed = macLines == windowsLines && Math.Abs(horizontal) <= anchorTolerance && Math.Abs(baseline) <= tolerance;
                }
            }
            placements.Add(new TextRunPlacement(run.Text, rule, region, macBox, windowsBox, macLines, windowsLines, horizontal, baseline, top,
                                                fontSize, tolerance, passed));
        }
        return new TextLayoutResult(placements, rest, windows, ignored);
    }

    /// <summary>
    /// The first line's baseline in <paramref name="region"/>: in the first band of rows with ink, the median bottom edge
    /// of the columns whose ink ends in the band's lower half. Flags, bars and arms (a narrow "1", a "T", an "r") end
    /// higher, and descenders are a minority. Null without ink.
    /// </summary>
    private static double? Baseline(bool[] mask, int width, PixelRect region)
    {
        if (FirstLine(mask, width, region) is not { } line)
        {
            return null;
        }
        var bottoms = new List<int>();
        for (var x = Math.Max(0, region.Left); x < Math.Min(width, region.Right); x++)
        {
            for (var y = line.Bottom - 1; y >= line.Top; y--)
            {
                if (mask[y * width + x])
                {
                    if (2 * (y + 1) > line.Top + line.Bottom)
                    {
                        bottoms.Add(y + 1);
                    }
                    break;
                }
            }
        }
        if (bottoms.Count == 0)
        {
            return null;
        }
        bottoms.Sort();
        return bottoms.Count % 2 == 1 ? bottoms[bottoms.Count / 2] : (bottoms[bottoms.Count / 2 - 1] + bottoms[bottoms.Count / 2]) / 2.0;
    }

    /// <summary>The rows of the first band of ink in <paramref name="region"/>, ended by a row without ink; null without ink.</summary>
    private static (int Top, int Bottom)? FirstLine(bool[] mask, int width, PixelRect region)
    {
        int height = mask.Length / width;
        int top = -1, bottom = -1;
        for (var y = Math.Max(0, region.Top); y < Math.Min(height, region.Bottom); y++)
        {
            var ink = false;
            for (var x = Math.Max(0, region.Left); x < Math.Min(width, region.Right) && !ink; x++)
            {
                ink = mask[y * width + x];
            }
            if (ink)
            {
                top = top < 0 ? y : top;
                bottom = y + 1;
            }
            else if (top >= 0)
            {
                break;
            }
        }
        return top < 0 ? null : (top, bottom);
    }

    private static PixelRect Intersect(PixelRect a, PixelRect b) =>
        new(Math.Max(a.Left, b.Left), Math.Max(a.Top, b.Top), Math.Min(a.Right, b.Right), Math.Min(a.Bottom, b.Bottom));

    /// <summary>The tool of the element a display list draws.</summary>
    private static Tool Owner(ElementList elements, Guid id)
    {
        foreach (ref readonly var element in elements)
        {
            if (element.Id == id)
            {
                return element.Tool;
            }
        }
        return Tool.Rectangle;
    }

    private static double Center(int low, int high) => (low + high) / 2.0;

    private static PixelRect Pixels(Rect rect) => new((int)Math.Floor(rect.X), (int)Math.Floor(rect.Y), (int)Math.Ceiling(rect.MaxX), (int)Math.Ceiling(rect.MaxY));

    private static PixelRect Grow(PixelRect rect, int by) => new(rect.Left - by, rect.Top - by, rect.Right + by, rect.Bottom + by);

    private static void Mark(bool[] mask, int width, int height, PixelRect rect)
    {
        for (var y = Math.Max(0, rect.Top); y < Math.Min(height, rect.Bottom); y++)
        {
            for (var x = Math.Max(0, rect.Left); x < Math.Min(width, rect.Right); x++)
            {
                mask[y * width + x] = true;
            }
        }
    }

    /// <summary>
    /// Pixels of <paramref name="a"/> whose furthest channel lies more than <paramref name="threshold"/> outside the range
    /// of <paramref name="b"/> within <paramref name="reach"/> pixels, inside <paramref name="within"/>. With a reach of
    /// one pixel, an edge drawn a little differently by the other platform does not count as ink.
    /// </summary>
    private static bool[] Differs(RasterImage a, RasterImage b, int threshold, int reach, bool[]? within)
    {
        var mask = new bool[a.Width * a.Height];
        Span<int> low = stackalloc int[4], high = stackalloc int[4];
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                if (within is not null && !within[y * a.Width + x])
                {
                    continue;
                }
                low.Fill(255);
                high.Fill(0);
                for (var row = Math.Max(0, y - reach); row <= Math.Min(a.Height - 1, y + reach); row++)
                {
                    for (var column = Math.Max(0, x - reach); column <= Math.Min(a.Width - 1, x + reach); column++)
                    {
                        var (r, g, bl, al) = b[column, row];
                        Span<int> values = [r, g, bl, al];
                        for (var channel = 0; channel < 4; channel++)
                        {
                            low[channel] = Math.Min(low[channel], values[channel]);
                            high[channel] = Math.Max(high[channel], values[channel]);
                        }
                    }
                }
                var (r1, g1, b1, a1) = a[x, y];
                Span<int> own = [r1, g1, b1, a1];
                var outside = 0;
                for (var channel = 0; channel < 4; channel++)
                {
                    outside = Math.Max(outside, Math.Max(low[channel] - own[channel], own[channel] - high[channel]));
                }
                mask[y * a.Width + x] = outside > threshold;
            }
        }
        return mask;
    }

    private static bool[] Or(bool[] a, bool[] b)
    {
        var result = new bool[a.Length];
        for (var index = 0; index < a.Length; index++)
        {
            result[index] = a[index] || b[index];
        }
        return result;
    }

    private static bool[] Grow(bool[] mask, int width, int height, int by)
    {
        var result = new bool[mask.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (!mask[y * width + x])
                {
                    continue;
                }
                for (var row = Math.Max(0, y - by); row <= Math.Min(height - 1, y + by); row++)
                {
                    for (var column = Math.Max(0, x - by); column <= Math.Min(width - 1, x + by); column++)
                    {
                        result[row * width + column] = true;
                    }
                }
            }
        }
        return result;
    }

    private static PixelRect? Bounds(bool[] mask, int width, int height, PixelRect region)
    {
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        for (var y = Math.Max(0, region.Top); y < Math.Min(height, region.Bottom); y++)
        {
            for (var x = Math.Max(0, region.Left); x < Math.Min(width, region.Right); x++)
            {
                if (mask[y * width + x])
                {
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x + 1);
                    bottom = Math.Max(bottom, y + 1);
                }
            }
        }
        return left == int.MaxValue ? null : new PixelRect(left, top, right, bottom);
    }

    /// <summary>Bands of rows with ink, separated by at least one empty row.</summary>
    private static int Lines(bool[] mask, int width, int height, PixelRect region)
    {
        var lines = 0;
        var inLine = false;
        for (var y = Math.Max(0, region.Top); y < Math.Min(height, region.Bottom); y++)
        {
            var ink = false;
            for (var x = Math.Max(0, region.Left); x < Math.Min(width, region.Right) && !ink; x++)
            {
                ink = mask[y * width + x];
            }
            if (ink && !inLine)
            {
                lines++;
            }
            inLine = ink;
        }
        return lines;
    }
}
