using System.Globalization;
using System.Text;
using Annotator.Rendering.Imaging;

namespace Annotator.Rendering.Tests.Conformance;

/// <summary>
/// The WIN-REND-05 tolerance. Channel values are premultiplied 8-bit sRGB; color differences are CIEDE2000.
/// </summary>
internal sealed record ComparisonThresholds
{
    /// <summary>Flat color areas: each channel within this many 8-bit steps.</summary>
    public int FlatTolerance { get; init; } = 1;

    /// <summary>Edges: how far, in device pixels, an edge may move.</summary>
    public int EdgeDistance { get; init; } = 1;

    /// <summary>
    /// Edges: how many 8-bit steps a channel may lie outside the range the other image shows within
    /// <see cref="EdgeDistance"/>, for antialiasing that covers a pixel slightly differently.
    /// </summary>
    public int EdgeAllowance { get; init; } = 1;

    /// <summary>Other pixels: the CIEDE2000 difference allowed.</summary>
    public double DeltaE { get; init; } = 2.0;

    /// <summary>Other pixels: the share that must be within <see cref="DeltaE"/>.</summary>
    public double OtherShare { get; init; } = 0.995;

    /// <summary>
    /// A pixel is on an edge when, in either image, it differs from a pixel within <see cref="EdgeDistance"/> by more
    /// than this CIEDE2000 difference: there a shift of one pixel changes the color more than <see cref="DeltaE"/>.
    /// </summary>
    public double EdgeContrast { get; init; } = 2.0;

    /// <summary>
    /// The share of flat pixels, and separately of edge pixels, that may break their rule (isolated antialiasing
    /// outliers). Zero means every one must pass.
    /// </summary>
    public double OutlierShare { get; init; }

    /// <summary>The largest channel difference allowed anywhere outside ignored pixels, or null for no such limit.</summary>
    public int? MaxChannelDifference { get; init; }

    /// <summary>WIN-REND-05 as first written: 1/255 flat, one pixel for edges, CIEDE2000 2.0 for 99.5% of other pixels.</summary>
    public static ComparisonThresholds Initial { get; } = new();

    /// <summary>
    /// WIN-REND-05 as frozen after S-13 (owner decision, 2026-10-19): the first thresholds plus an antialiasing allowance
    /// of 32/255 (one step of WARP's 8 x 8 coverage grid) for edge pixels, and at most one flat or edge pixel in 10,000
    /// that breaks its rule. Only an ADR can loosen them.
    /// </summary>
    public static ComparisonThresholds Frozen { get; } = new() { EdgeAllowance = 32, OutlierShare = 0.0001 };

    /// <summary>
    /// WIN-REND-05 for pixel effects, frozen with <see cref="Frozen"/>: the first thresholds with no allowance or outliers,
    /// and no channel more than 2/255 apart, because the effects reproduce Core Image's filters rather than approximate them.
    /// </summary>
    public static ComparisonThresholds Effects { get; } = new() { MaxChannelDifference = 2 };

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"flat {FlatTolerance}/255, edges {EdgeDistance} px + {EdgeAllowance}/255, other dE00 {DeltaE} for {OtherShare:P1}, outliers {OutlierShare:P2}{(MaxChannelDifference is { } limit ? $", channels within {limit}/255" : "")}");
}

/// <summary>How the comparison judged a pixel.</summary>
internal enum PixelClass : byte
{
    /// <summary>The reference's neighborhood is one color: each channel must match within the flat tolerance.</summary>
    Flat,

    /// <summary>Next to a color step in either image: the value must be explained by the other image within the edge distance.</summary>
    Edge,

    /// <summary>Smooth color change (gradients, blurs, soft shading): judged by CIEDE2000, with a share allowed to differ.</summary>
    Other,

    /// <summary>Left out by the caller, for example glyphs in a font-dependent scene.</summary>
    Ignored,
}

/// <summary>The outcome of one WIN-REND-05 comparison, with per-pixel detail for difference images.</summary>
internal sealed class ComparisonResult
{
    internal ComparisonResult(int width, int height, ComparisonThresholds thresholds)
    {
        Width = width;
        Height = height;
        Thresholds = thresholds;
        Classes = new PixelClass[width * height];
        Failed = new bool[width * height];
        DeltaE = new float[width * height];
        ChannelDifference = new byte[width * height];
        EdgeExcess = new byte[width * height];
    }

    public int Width { get; }

    public int Height { get; }

    public ComparisonThresholds Thresholds { get; }

    public PixelClass[] Classes { get; }

    /// <summary>Pixels that broke their class's rule (for Other: a difference above the CIEDE2000 limit).</summary>
    public bool[] Failed { get; }

    /// <summary>CIEDE2000 between the two pixels: the larger of the differences over white and over black.</summary>
    public float[] DeltaE { get; }

    /// <summary>The largest premultiplied channel difference at each pixel.</summary>
    public byte[] ChannelDifference { get; }

    /// <summary>For edge pixels: how far a channel lies outside the other image's range within the edge distance.</summary>
    public byte[] EdgeExcess { get; }

    public int FlatCount { get; internal set; }

    public int EdgeCount { get; internal set; }

    public int OtherCount { get; internal set; }

    public int IgnoredCount { get; internal set; }

    public int FlatFailures { get; internal set; }

    public int EdgeFailures { get; internal set; }

    public int OtherFailures { get; internal set; }

    /// <summary>The largest channel difference in flat areas.</summary>
    public int FlatMaxDifference { get; internal set; }

    /// <summary>The largest edge excess.</summary>
    public int EdgeMaxExcess { get; internal set; }

    /// <summary>The largest channel difference anywhere outside ignored pixels.</summary>
    public int MaxDifference { get; internal set; }

    /// <summary>Pixels with any channel difference, outside ignored pixels.</summary>
    public int DifferentPixels { get; internal set; }

    /// <summary>The share of Other pixels within the CIEDE2000 limit (1 when there are none).</summary>
    public double OtherShareWithin => OtherCount == 0 ? 1 : 1 - OtherFailures / (double)OtherCount;

    public bool FlatPassed => FlatFailures <= Math.Floor(FlatCount * Thresholds.OutlierShare);

    public bool EdgesPassed => EdgeFailures <= Math.Floor(EdgeCount * Thresholds.OutlierShare);

    public bool OtherPassed => OtherShareWithin >= Thresholds.OtherShare;

    public bool ChannelsPassed => Thresholds.MaxChannelDifference is not { } limit || MaxDifference <= limit;

    public bool Passed => FlatPassed && EdgesPassed && OtherPassed && ChannelsPassed;

    /// <summary>A CIEDE2000 percentile over the pixels of <paramref name="kind"/> (null: every compared pixel).</summary>
    public double Percentile(double fraction, PixelClass? kind = null)
    {
        var values = new List<float>();
        for (var index = 0; index < DeltaE.Length; index++)
        {
            if (Classes[index] != PixelClass.Ignored && (kind is null || Classes[index] == kind))
            {
                values.Add(DeltaE[index]);
            }
        }
        return Quantile(values, fraction);
    }

    /// <summary>A percentile of the edge excess over edge pixels.</summary>
    public double EdgeExcessPercentile(double fraction)
    {
        var values = new List<float>();
        for (var index = 0; index < EdgeExcess.Length; index++)
        {
            if (Classes[index] == PixelClass.Edge)
            {
                values.Add(EdgeExcess[index]);
            }
        }
        return Quantile(values, fraction);
    }

    /// <summary>Edge pixels whose excess is above <paramref name="allowance"/>.</summary>
    public int EdgesBeyond(int allowance)
    {
        var count = 0;
        for (var index = 0; index < EdgeExcess.Length; index++)
        {
            if (Classes[index] == PixelClass.Edge && EdgeExcess[index] > allowance)
            {
                count++;
            }
        }
        return count;
    }

    public string Summary()
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"{(Passed ? "PASS" : "FAIL")}: flat {FlatCount - FlatFailures}/{FlatCount} within {Thresholds.FlatTolerance} (largest {FlatMaxDifference}); ");
        text.Append(CultureInfo.InvariantCulture,
            $"edges {EdgeCount - EdgeFailures}/{EdgeCount} within {Thresholds.EdgeDistance} px + {Thresholds.EdgeAllowance} (largest excess {EdgeMaxExcess}); ");
        text.Append(CultureInfo.InvariantCulture,
            $"other {OtherShareWithin:P2} of {OtherCount} within dE00 {Thresholds.DeltaE} (p99.5 {Percentile(0.995, PixelClass.Other):F2}, max {Percentile(1, PixelClass.Other):F2}); ");
        text.Append(CultureInfo.InvariantCulture,
            $"all: {DifferentPixels} differ, largest channel difference {MaxDifference}{(Thresholds.MaxChannelDifference is { } limit ? $" (limit {limit})" : "")}, dE00 p99 {Percentile(0.99):F2} max {Percentile(1):F2}");
        if (IgnoredCount > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"; {IgnoredCount} ignored");
        }
        return text.ToString();
    }

    private static double Quantile(List<float> values, double fraction)
    {
        if (values.Count == 0)
        {
            return 0;
        }
        values.Sort();
        return values[Math.Clamp((int)Math.Ceiling(fraction * values.Count) - 1, 0, values.Count - 1)];
    }
}

/// <summary>
/// The WIN-REND-05 comparison of a Windows image with the Mac reference, both premultiplied 8-bit sRGB of the same
/// size. Each pixel gets one class from its neighborhood within the edge distance (3 x 3 pixels for one pixel):
/// <list type="bullet">
/// <item><b>Flat</b>: every channel of the reference varies by at most the flat tolerance in the neighborhood. Each
/// Windows channel must match within the flat tolerance.</item>
/// <item><b>Edge</b>: not flat, and in either image the pixel differs from a neighbor by more than the edge contrast
/// (CIEDE2000). Each Windows channel must lie within the range of the reference's neighborhood, and each reference
/// channel within the range of the Windows neighborhood, widened by the edge allowance: an edge may move by up to one
/// pixel, but a color may not appear or disappear.</item>
/// <item><b>Other</b>: everything else, where colors change smoothly. The CIEDE2000 difference must be within the limit
/// for the required share of these pixels.</item>
/// </list>
/// The CIEDE2000 difference of premultiplied colors is the larger of the differences composited over white and over
/// black, so alpha counts. The comparison passes when the flat and the edge pixels that break their rule stay within the
/// outlier share, the Other share is met and, where the thresholds set a limit, no channel differs by more than it.
/// </summary>
internal static class ImageComparison
{
    public static ComparisonResult Compare(RasterImage reference, RasterImage candidate, ComparisonThresholds? thresholds = null,
                                           bool[]? ignore = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(candidate);
        thresholds ??= ComparisonThresholds.Initial;
        if (reference.Width != candidate.Width || reference.Height != candidate.Height)
        {
            throw new ArgumentException(
                $"The images differ in size: {reference.Width} x {reference.Height} and {candidate.Width} x {candidate.Height}.");
        }
        int width = reference.Width, height = reference.Height, count = width * height;
        if (ignore is not null && ignore.Length != count)
        {
            throw new ArgumentException("The ignore mask does not match the image size.", nameof(ignore));
        }
        var result = new ComparisonResult(width, height, thresholds);
        var r = Rgba(reference);
        var c = Rgba(candidate);
        var labs = new LabCache();
        var referenceLab = labs.Of(r);
        var candidateLab = labs.Of(c);
        var radius = thresholds.EdgeDistance;
        var referenceContrast = NeighborContrast(referenceLab, width, height, radius);
        var candidateContrast = NeighborContrast(candidateLab, width, height, radius);
        Span<int> referenceMin = stackalloc int[4], referenceMax = stackalloc int[4];
        Span<int> candidateMin = stackalloc int[4], candidateMax = stackalloc int[4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                var deltaE = (float)Math.Max(ColorDifference.DeltaE2000(referenceLab.White[index], candidateLab.White[index]),
                                             ColorDifference.DeltaE2000(referenceLab.Black[index], candidateLab.Black[index]));
                result.DeltaE[index] = deltaE;
                var difference = 0;
                for (var channel = 0; channel < 4; channel++)
                {
                    difference = Math.Max(difference, Math.Abs(r[index * 4 + channel] - c[index * 4 + channel]));
                }
                result.ChannelDifference[index] = (byte)difference;
                if (ignore is not null && ignore[index])
                {
                    result.Classes[index] = PixelClass.Ignored;
                    result.IgnoredCount++;
                    continue;
                }
                if (difference > 0)
                {
                    result.DifferentPixels++;
                    result.MaxDifference = Math.Max(result.MaxDifference, difference);
                }
                Range(r, width, height, x, y, radius, referenceMin, referenceMax);
                var flat = true;
                for (var channel = 0; channel < 4; channel++)
                {
                    flat &= referenceMax[channel] - referenceMin[channel] <= thresholds.FlatTolerance;
                }
                if (flat)
                {
                    result.Classes[index] = PixelClass.Flat;
                    result.FlatCount++;
                    result.FlatMaxDifference = Math.Max(result.FlatMaxDifference, difference);
                    if (difference > thresholds.FlatTolerance)
                    {
                        result.Failed[index] = true;
                        result.FlatFailures++;
                    }
                    continue;
                }
                if (referenceContrast[index] > thresholds.EdgeContrast || candidateContrast[index] > thresholds.EdgeContrast)
                {
                    result.Classes[index] = PixelClass.Edge;
                    result.EdgeCount++;
                    Range(c, width, height, x, y, radius, candidateMin, candidateMax);
                    var excess = Math.Max(Outside(c, index, referenceMin, referenceMax), Outside(r, index, candidateMin, candidateMax));
                    result.EdgeExcess[index] = (byte)excess;
                    result.EdgeMaxExcess = Math.Max(result.EdgeMaxExcess, excess);
                    if (excess > thresholds.EdgeAllowance)
                    {
                        result.Failed[index] = true;
                        result.EdgeFailures++;
                    }
                    continue;
                }
                result.Classes[index] = PixelClass.Other;
                result.OtherCount++;
                if (deltaE > thresholds.DeltaE)
                {
                    result.Failed[index] = true;
                    result.OtherFailures++;
                }
            }
        }
        return result;
    }

    /// <summary>RGBA bytes (premultiplied) without row padding.</summary>
    private static byte[] Rgba(RasterImage image)
    {
        var output = new byte[image.Width * image.Height * 4];
        var pixels = image.Pixels;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var source = y * image.Stride + x * 4;
                var target = (y * image.Width + x) * 4;
                output[target] = pixels[source + 2];
                output[target + 1] = pixels[source + 1];
                output[target + 2] = pixels[source];
                output[target + 3] = pixels[source + 3];
            }
        }
        return output;
    }

    /// <summary>How many 8-bit steps the pixel's furthest channel lies outside the range (0 when inside).</summary>
    private static int Outside(byte[] rgba, int index, ReadOnlySpan<int> min, ReadOnlySpan<int> max)
    {
        var excess = 0;
        for (var channel = 0; channel < 4; channel++)
        {
            int value = rgba[index * 4 + channel];
            excess = Math.Max(excess, Math.Max(min[channel] - value, value - max[channel]));
        }
        return excess;
    }

    private static void Range(byte[] rgba, int width, int height, int x, int y, int radius, Span<int> min, Span<int> max)
    {
        min.Fill(255);
        max.Fill(0);
        for (var row = Math.Max(0, y - radius); row <= Math.Min(height - 1, y + radius); row++)
        {
            for (var column = Math.Max(0, x - radius); column <= Math.Min(width - 1, x + radius); column++)
            {
                var offset = (row * width + column) * 4;
                for (var channel = 0; channel < 4; channel++)
                {
                    int value = rgba[offset + channel];
                    min[channel] = Math.Min(min[channel], value);
                    max[channel] = Math.Max(max[channel], value);
                }
            }
        }
    }

    /// <summary>For each pixel, the largest CIEDE2000 difference to a pixel within <paramref name="radius"/>.</summary>
    private static float[] NeighborContrast(LabImage lab, int width, int height, int radius)
    {
        var contrast = new float[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                // Each pair once: the neighbors after this pixel in reading order.
                for (var dy = 0; dy <= radius; dy++)
                {
                    for (var dx = -radius; dx <= radius; dx++)
                    {
                        if (dy == 0 && dx <= 0)
                        {
                            continue;
                        }
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || nx >= width || ny >= height)
                        {
                            continue;
                        }
                        var other = ny * width + nx;
                        if (lab.Key[index] == lab.Key[other])
                        {
                            continue;
                        }
                        var value = (float)Math.Max(ColorDifference.DeltaE2000(lab.White[index], lab.White[other]),
                                                    ColorDifference.DeltaE2000(lab.Black[index], lab.Black[other]));
                        contrast[index] = Math.Max(contrast[index], value);
                        contrast[other] = Math.Max(contrast[other], value);
                    }
                }
            }
        }
        return contrast;
    }

    private sealed class LabImage(int count)
    {
        public Lab[] White { get; } = new Lab[count];

        public Lab[] Black { get; } = new Lab[count];

        /// <summary>The packed premultiplied color, to skip equal neighbors.</summary>
        public uint[] Key { get; } = new uint[count];
    }

    /// <summary>CIELAB of premultiplied pixels over white and over black, memoized by color.</summary>
    private sealed class LabCache
    {
        private readonly Dictionary<uint, (Lab White, Lab Black)> known = [];

        public LabImage Of(byte[] rgba)
        {
            var count = rgba.Length / 4;
            var image = new LabImage(count);
            for (var index = 0; index < count; index++)
            {
                int red = rgba[index * 4], green = rgba[index * 4 + 1], blue = rgba[index * 4 + 2], alpha = rgba[index * 4 + 3];
                var key = (uint)(red | green << 8 | blue << 16 | alpha << 24);
                if (!known.TryGetValue(key, out var lab))
                {
                    // Source-over in sRGB-encoded values, as both platforms composite 8-bit sRGB.
                    var transparent = 255 - alpha;
                    lab = (ColorDifference.FromSrgb(Math.Min(255, red + transparent), Math.Min(255, green + transparent),
                                                    Math.Min(255, blue + transparent)),
                           ColorDifference.FromSrgb(red, green, blue));
                    known[key] = lab;
                }
                image.White[index] = lab.White;
                image.Black[index] = lab.Black;
                image.Key[index] = key;
            }
            return image;
        }
    }
}
