using System.IO.Compression;
using System.Buffers.Binary;
using System.Text.Json;
using Annotator.Core.Geometry;
using Annotator.Rendering.Illustrations;
using Annotator.Rendering.Imaging;
using Annotator.Rendering.Tests.Conformance;
using Annotator.Workspace.Artwork;
using Annotator.Workspace.FirstUse;

namespace Annotator.Rendering.Tests;

/// <summary>
/// The tour's illustrations drawn with Direct2D on WARP, compared with the Mac's tour pages in reference/ux-reference/macos/tour
/// (WIN-PAR-06, WIN-UI-04): the final still of each page at 2x, cut from the reference at the illustration's frame (24, 38,
/// 472 x 120 points) over the window background, and the twelve frames of each story's motion contact sheet at 1x. Text
/// uses the Windows fonts (D-4) and rounded corners are circular, so pixels near text and corners differ; everything else
/// matches closely. Set ANNOTATOR_TOUR_EVIDENCE to a folder to write each pair, the Windows contact sheets and the numbers.
/// </summary>
public sealed class IllustrationRenderingTests
{
    private static readonly string? s_evidence = Environment.GetEnvironmentVariable("ANNOTATOR_TOUR_EVIDENCE");

    [Theory]
    [InlineData(IntroductionPage.Draw, "1-draw", false)]
    [InlineData(IntroductionPage.Draw, "1-draw", true)]
    [InlineData(IntroductionPage.Color, "2-color", false)]
    [InlineData(IntroductionPage.Color, "2-color", true)]
    [InlineData(IntroductionPage.Focus, "3-focus", false)]
    [InlineData(IntroductionPage.Focus, "3-focus", true)]
    public void FinalStillsMatchTheMacTourPages(IntroductionPage page, string state, bool dark)
    {
        using var renderer = new IllustrationRenderer(Warp.Devices, TestSymbols.Find);
        RasterImage windows = renderer.Render(page, 1, dark, IntroductionScene.StandardSize, 2);
        Assert.Equal(944, windows.Width);
        Assert.Equal(240, windows.Height);
        (int width, int height, byte[] mac) = Reference($"tour/{state}-{(dark ? "dark" : "light")}.png", 48, 76, 944, 240);
        // The window background behind the illustration's rounded corners (design-values.json).
        byte background = dark ? (byte)0x1E : (byte)0xFF;
        byte[] ours = OverBackground(windows, background);
        (double mean, double p50, double p95, double within) = Compare(ours, mac, width, height);
        if (s_evidence is { } folder)
        {
            Directory.CreateDirectory(folder);
            string stem = Path.Combine(folder, $"{state}-{(dark ? "dark" : "light")}");
            WritePng(stem + ".windows.png", width, height, ours);
            WritePng(stem + ".mac.png", width, height, mac);
            WritePng(stem + ".diff.png", width, height, Difference(ours, mac));
            File.AppendAllText(Path.Combine(folder, "illustrations.txt"),
                FormattableString.Invariant($"{state} {(dark ? "dark" : "light")}: CIEDE2000 mean {mean:0.00}, p50 {p50:0.00}, p95 {p95:0.00}; {within:P1} within 2.0\n"));
        }
        Assert.True(p50 < 1.5, FormattableString.Invariant($"{state}: half the pixels should match the Mac (p50 {p50:0.00})."));
        Assert.True(within > 0.85, FormattableString.Invariant($"{state}: {within:P1} of pixels within CIEDE2000 2.0."));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheColorWheelIsAConicGradientLikeTheMacs(bool dark)
    {
        using var renderer = new IllustrationRenderer(Warp.Devices, TestSymbols.Find);
        RasterImage windows = renderer.Render(IntroductionPage.Color, 1, dark, IntroductionScene.StandardSize, 2);
        (int width, int height, byte[] mac) = Reference($"tour/2-color-{(dark ? "dark" : "light")}.png", 48, 76, 944, 240);
        byte[] ours = OverBackground(windows, dark ? (byte)0x1E : (byte)0xFF);
        // The wheel: 14 points square at (110, 47) in the illustration.
        double total = 0;
        for (int y = 94; y < 122; y++)
        {
            for (int x = 220; x < 248; x++)
            {
                int index = (y * width + x) * 4;
                total += ColorDifference.DeltaE2000(ColorDifference.FromSrgb(ours[index], ours[index + 1], ours[index + 2]),
                                                    ColorDifference.FromSrgb(mac[index], mac[index + 1], mac[index + 2]));
            }
        }
        Assert.Equal(240, height);
        double mean = total / (28 * 28);
        Assert.True(mean < 4, FormattableString.Invariant($"The wheel differs from the Mac's by {mean:0.00} on average."));
    }

    [Theory]
    [InlineData(IntroductionPage.Draw, "draw", false)]
    [InlineData(IntroductionPage.Draw, "draw", true)]
    [InlineData(IntroductionPage.Color, "color", false)]
    [InlineData(IntroductionPage.Color, "color", true)]
    [InlineData(IntroductionPage.Focus, "focus", false)]
    [InlineData(IntroductionPage.Focus, "focus", true)]
    public void StoryFramesMatchTheMacMotionReference(IntroductionPage page, string name, bool dark)
    {
        // The contact sheet: 12 frames at 1x, two columns of 472 x 120 cells, 12 points apart with a 38-point label row.
        string stem = $"illustration-{name}-{(dark ? "dark" : "light")}";
        string folder = Path.Combine(ReferenceFolder(), "tour", "motion");
        using JsonDocument motion = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, stem + ".json")));
        double[] times = [.. motion.RootElement.GetProperty("contactSheetFrames").EnumerateArray().Select(time => time.GetDouble())];
        (int sheetWidth, int sheetHeight, byte[] sheet) = PngImages.DecodeStraight(File.ReadAllBytes(Path.Combine(folder, stem + ".png")));
        byte background = sheet[0];
        byte[] ours = new byte[sheet.Length];
        for (int index = 0; index < ours.Length; index += 4)
        {
            ours[index] = ours[index + 1] = ours[index + 2] = background;
            ours[index + 3] = 255;
        }
        using var renderer = new IllustrationRenderer(Warp.Devices, TestSymbols.Find);
        var lines = new List<string>();
        double worstMedian = 0, worstWithin = 1;
        for (int frame = 0; frame < times.Length; frame++)
        {
            int left = 12 + frame % 2 * 484, top = 12 + frame / 2 * 158;
            double progress = Math.Clamp(times[frame] / page.StoryDuration, 0, 1);
            byte[] windows = OverBackground(renderer.Render(page, progress, dark, IntroductionScene.StandardSize, 1), background);
            var mac = new byte[472 * 120 * 4];
            for (int y = 0; y < 120; y++)
            {
                Array.Copy(sheet, ((top + y) * sheetWidth + left) * 4, mac, y * 472 * 4, 472 * 4);
                Array.Copy(windows, y * 472 * 4, ours, ((top + y) * sheetWidth + left) * 4, 472 * 4);
            }
            (double mean, double p50, double p95, double within) = Compare(windows, mac, 472, 120);
            worstMedian = Math.Max(worstMedian, p50);
            worstWithin = Math.Min(worstWithin, within);
            lines.Add(FormattableString.Invariant($"{stem} {times[frame] * 1000:0} ms: mean {mean:0.00}, p50 {p50:0.00}, p95 {p95:0.00}; {within:P1} within 2.0"));
        }
        if (s_evidence is { } evidence)
        {
            Directory.CreateDirectory(evidence);
            WritePng(Path.Combine(evidence, stem + ".windows-sheet.png"), sheetWidth, sheetHeight, ours);
            File.AppendAllLines(Path.Combine(evidence, "motion.txt"), lines);
        }
        Assert.True(worstMedian < 1.5, FormattableString.Invariant($"{stem}: a frame's median difference reached {worstMedian:0.00}.\n{string.Join('\n', lines)}"));
        Assert.True(worstWithin > 0.85, FormattableString.Invariant($"{stem}: a frame had only {worstWithin:P1} within 2.0.\n{string.Join('\n', lines)}"));
    }

    [Fact]
    public void EveryMomentOfEveryStoryDrawsWithoutFailing()
    {
        using var renderer = new IllustrationRenderer(Warp.Devices, TestSymbols.Find);
        foreach (IntroductionPage page in new[] { IntroductionPage.Draw, IntroductionPage.Color, IntroductionPage.Focus })
        {
            RasterImage? previous = null;
            for (int step = 0; step <= 20; step++)
            {
                RasterImage frame = renderer.Render(page, step / 20.0, step % 2 == 0, IntroductionScene.StandardSize, 1.25);
                Assert.Equal(590, frame.Width);
                Assert.Equal(150, frame.Height);
                Assert.True(Pixels.Painted(frame) > frame.Width * frame.Height / 2);
                if (previous is not null)
                {
                    Assert.False(Pixels.SamePixels(previous, frame), $"{page} at {step / 20.0} should differ from the step before.");
                }
                previous = frame;
            }
        }
    }

    [Fact]
    public void ShadowsAndBlursDrawThroughEffects()
    {
        using var renderer = new IllustrationRenderer(Warp.Devices, TestSymbols.Find);
        renderer.Render(IntroductionPage.Color, 0.4, false, IntroductionScene.StandardSize, 2);
        // The card's window shadow, two slider knobs and the spark.
        Assert.Equal(4, renderer.FilteredOperations);
        renderer.Render(IntroductionPage.Draw, 0.46, false, IntroductionScene.StandardSize, 2);
        // Two windows, the fading toolbar pill and the pointer's outline as it appears.
        Assert.Equal(4, renderer.FilteredOperations);
    }

    private static (int Width, int Height, byte[] Rgba) Reference(string name, int left, int top, int width, int height)
    {
        string path = Path.Combine(ReferenceFolder(), name.Replace('/', Path.DirectorySeparatorChar));
        (int fullWidth, _, byte[] rgba) = PngImages.DecodeStraight(File.ReadAllBytes(path));
        var cut = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            Array.Copy(rgba, ((top + y) * fullWidth + left) * 4, cut, y * width * 4, width * 4);
        }
        return (width, height, cut);
    }

    private static string ReferenceFolder()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "reference", "ux-reference", "macos");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new DirectoryNotFoundException("reference/ux-reference/macos was not found above the test output.");
    }

    /// <summary>Premultiplied BGRA over an opaque gray, as straight RGBA.</summary>
    private static byte[] OverBackground(RasterImage image, byte background)
    {
        var rgba = new byte[image.Width * image.Height * 4];
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                (byte blue, byte green, byte red, byte alpha) = Bgra(image, x, y);
                int index = (y * image.Width + x) * 4;
                rgba[index] = (byte)Math.Min(255, red + background * (255 - alpha) / 255);
                rgba[index + 1] = (byte)Math.Min(255, green + background * (255 - alpha) / 255);
                rgba[index + 2] = (byte)Math.Min(255, blue + background * (255 - alpha) / 255);
                rgba[index + 3] = 255;
            }
        }
        return rgba;
    }

    private static (byte Blue, byte Green, byte Red, byte Alpha) Bgra(RasterImage image, int x, int y)
    {
        int index = y * image.Stride + x * 4;
        return (image.Pixels[index], image.Pixels[index + 1], image.Pixels[index + 2], image.Pixels[index + 3]);
    }

    private static (double Mean, double P50, double P95, double Within) Compare(byte[] first, byte[] second, int width, int height)
    {
        var differences = new double[width * height];
        for (int index = 0; index < differences.Length; index++)
        {
            Lab a = ColorDifference.FromSrgb(first[index * 4], first[index * 4 + 1], first[index * 4 + 2]);
            Lab b = ColorDifference.FromSrgb(second[index * 4], second[index * 4 + 1], second[index * 4 + 2]);
            differences[index] = ColorDifference.DeltaE2000(a, b);
        }
        double mean = differences.Average();
        double within = differences.Count(value => value <= 2) / (double)differences.Length;
        Array.Sort(differences);
        return (mean, differences[differences.Length / 2], differences[(int)(differences.Length * 0.95)], within);
    }

    private static byte[] Difference(byte[] first, byte[] second)
    {
        var heat = new byte[first.Length];
        for (int index = 0; index < first.Length; index += 4)
        {
            int delta = Math.Abs(first[index] - second[index]) + Math.Abs(first[index + 1] - second[index + 1]) + Math.Abs(first[index + 2] - second[index + 2]);
            byte level = (byte)Math.Min(255, delta * 2);
            heat[index] = level;
            heat[index + 1] = (byte)(255 - level);
            heat[index + 2] = (byte)(255 - level);
            heat[index + 3] = 255;
        }
        return heat;
    }

    /// <summary>An 8-bit RGBA PNG, stored with no filter.</summary>
    private static void WritePng(string path, int width, int height, byte[] rgba)
    {
        using var raw = new MemoryStream();
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            raw.Write(rgba, y * width * 4, width * 4);
        }
        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            raw.Position = 0;
            raw.CopyTo(deflate);
        }
        using FileStream file = File.Create(path);
        file.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 6;
        Chunk(file, "IHDR", header);
        Chunk(file, "IDAT", compressed.ToArray());
        Chunk(file, "IEND", []);
    }

    private static void Chunk(Stream file, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        file.Write(length);
        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        file.Write(typeBytes);
        file.Write(data);
        uint crc = Crc(typeBytes, data);
        Span<byte> check = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(check, crc);
        file.Write(check);
    }

    private static uint Crc(byte[] type, byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in type.Concat(data))
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
        }
        return ~crc;
    }

    /// <summary>The five toolbar symbols of the first page, copied from the app's icons (Icons/LucideIcons.g.cs).</summary>
    private static class TestSymbols
    {
        private static readonly Dictionary<string, IconRaster.Part[]> s_parts = new(StringComparer.Ordinal)
        {
            ["pencil.tip"] = [new("M 6.7,21 L 8.9,11.5 L 15.1,11.5 L 17.3,21", false), new("M 12,3 L 8.9,11.5 L 15.1,11.5 Z", true)],
            ["highlighter"] = [new("M 13,21 L 21,21", false), new("M 21.174,6.812 A 1,1 0 0 0 17.188,2.825 L 3.842,16.174 A 2,2 0 0 0 3.342,17.004 L 2.021,21.356 A 0.5,0.5 0 0 0 2.644,21.978 L 6.997,20.658 A 2,2 0 0 0 7.827,20.161 Z", false)],
            ["rectangle"] = [new("M 4.5,4.5 L 19.5,4.5 A 2.5,2.5 0 0 1 22,7 L 22,17 A 2.5,2.5 0 0 1 19.5,19.5 L 4.5,19.5 A 2.5,2.5 0 0 1 2,17 L 2,7 A 2.5,2.5 0 0 1 4.5,4.5 Z", false)],
            ["arrow.up.right"] = [new("M 13,5 L 19,5 L 19,11", false), new("M 19,5 L 5,19", false)],
            ["textformat"] = [new("M 2,16 L 6.039,6.31 A 0.5,0.5 0 0 1 6.962,6.31 L 11,16", false), new("M 22,9 L 22,16", false), new("M 3.304,13 L 9.696,13", false), new("M 15,12.5 A 3.5,3.5 0 1 0 22,12.5 A 3.5,3.5 0 1 0 15,12.5 Z", false)],
        };

        public static IReadOnlyList<IconRaster.Part>? Find(string symbol) => s_parts.GetValueOrDefault(symbol);
    }
}
