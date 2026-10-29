using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Export;
using Annotator.Rendering.Imaging;
using Annotator.Rendering.Interop;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;

namespace Annotator.RenderHarness;

/// <summary>
/// The pointer mode: draws the pointer references of UXReferenceCaptureTests.test08PointerCursorsAndIcons with the
/// Windows renderer (WARP, so every run draws the same pixels) and compares them with reference/ux-reference/macos/pointer:
/// the seven halo shapes with each outline, with and without glow, in both themes; Spotlight at its default radius over
/// the capture's backdrop; and the laser pointer's trail and the Pulse, Ripple and Turn click effects at 60 frames per
/// second, compared tile by tile with the Mac's contact sheets. Writes, per state, the Windows image (.windows.png), a sheet with the Mac image, the
/// Windows image and their difference four times stronger (.sheet.png), and summary.md and summary.json.
/// </summary>
internal static unsafe class PointerPictures
{
    private const double Scale = 2;
    private const int SheetMargin = 12;
    private const int SheetCaption = 26;

    public static void Run(HarnessOptions options)
    {
        string output = options.Evidence ?? throw new ArgumentException("pointer needs --evidence <folder>.");
        string reference = options.Reference is { } given && Directory.Exists(Path.Combine(given, "pointer")) ? given : FindReference();
        Directory.CreateDirectory(Path.Combine(output, "pointer", "motion"));
        using GraphicsDevices devices = GraphicsDevices.Create(new GraphicsDeviceOptions { UseWarp = true });
        using var renderer = new DisplayListRenderer(devices);
        using var effects = new PointerEffectsRenderer(renderer);
        using var canvas = new OffscreenCanvas(devices);
        var rows = new List<Row>();
        RasterImage spotlight = Spotlight(canvas, effects);
        rows.Add(Compare(reference, output, "spotlight", "light", spotlight));
        foreach (bool dark in new[] { false, true })
        {
            string theme = dark ? "dark" : "light";
            foreach (StrokePattern pattern in new[] { StrokePattern.Solid, StrokePattern.Dashed, StrokePattern.Dotted })
            {
                foreach (double glow in new[] { 0.0, 12 })
                {
                    string state = $"halo-{pattern.RawValue}{(glow > 0 ? "-glow" : string.Empty)}";
                    rows.Add(Compare(reference, output, state, theme, HaloRow(canvas, effects, pattern, glow, dark)));
                }
            }
            rows.Add(CompareSequence(reference, output, "laser-trail", theme, time => LaserFrame(canvas, renderer, time, dark)));
            foreach (CursorClickEffect effect in new[] { CursorClickEffect.Pulse, CursorClickEffect.Ripple, CursorClickEffect.Turn })
            {
                rows.Add(CompareSequence(reference, output, $"click-{effect.RawValue}", theme, time => ClickFrame(canvas, effects, effect, time, dark)));
            }
        }
        WriteSummary(output, reference, rows);
        foreach (Row row in rows)
        {
            Console.WriteLine($"  {row.State} {row.Theme}: {row.Note}");
        }
        Console.WriteLine($"Summary: {Path.Combine(output, "pointer", "summary.md")}");
    }

    /// <summary>Seven halos 90 points apart on the theme's gray, default settings with one outline and glow (Mac test08).</summary>
    private static RasterImage HaloRow(OffscreenCanvas canvas, PointerEffectsRenderer effects, StrokePattern pattern, double glow, bool dark)
    {
        int width = (int)(7 * 90 * Scale), height = (int)(90 * Scale);
        var area = new DrawArea(Scale, 0, 0, new PixelRect(0, 0, width, height));
        return canvas.Render(width, height, context =>
        {
            Fill(context, dark ? 0.16 : 0.92);
            int index = 0;
            foreach (CursorHighlightShape shape in CursorHighlightShape.AllCases)
            {
                var settings = new CursorHighlightSettings { Shape = shape, BorderStyle = pattern, GlowRadius = glow };
                CursorHighlightFrame frame = CursorHighlightFrame.Create(settings, new Point(45 + index++ * 90, 45), clickProgress: null);
                effects.DrawHalo((nint)context, frame, area);
            }
        });
    }

    /// <summary>
    /// One click frame: a 140-point square of the theme's gray with the default halo at its middle and the click there
    /// (Mac test08 at time / clickDuration).
    /// </summary>
    private static RasterImage ClickFrame(OffscreenCanvas canvas, PointerEffectsRenderer effects, CursorClickEffect effect, double time, bool dark)
    {
        int side = (int)(140 * Scale);
        var area = new DrawArea(Scale, 0, 0, new PixelRect(0, 0, side, side));
        return canvas.Render(side, side, context =>
        {
            Fill(context, dark ? 0.16 : 0.92);
            var settings = new CursorHighlightSettings { ClickEffect = effect };
            CursorHighlightFrame frame = CursorHighlightFrame.Create(settings, new Point(70, 70), time / CursorHighlightSettings.ClickDuration, new Point(70, 70));
            effects.DrawHalo((nint)context, frame, area);
        });
    }

    /// <summary>
    /// One laser frame: a 360 by 220-point picture of the theme's gray with the trail of <see cref="LaserSwoosh"/> as drawn at
    /// <paramref name="time"/> (Mac test08's laser-trail).
    /// </summary>
    private static RasterImage LaserFrame(OffscreenCanvas canvas, DisplayListRenderer renderer, double time, bool dark)
    {
        int width = (int)(360 * Scale), height = (int)(220 * Scale);
        var area = new DrawArea(Scale, 0, 0, new PixelRect(0, 0, width, height));
        if (LaserTrailRenderer.List(LaserSwoosh(time), time) is not { } list)
        {
            return canvas.Render(width, height, context => Fill(context, dark ? 0.16 : 0.92));
        }
        renderer.Enter(list);
        try
        {
            return canvas.Render(width, height, context =>
            {
                Fill(context, dark ? 0.16 : 0.92);
                renderer.Draw((nint)context, list, area);
            });
        }
        finally
        {
            renderer.Leave(list);
        }
    }

    /// <summary>
    /// Mac UXReferenceCaptureTests.laserSwoosh(until:): a swoosh across the picture with the button down for half a second,
    /// one point each 60th of a second, as far as it got at <paramref name="time"/>.
    /// </summary>
    private static LaserTrail LaserSwoosh(double time)
    {
        var trail = new LaserTrail();
        trail.Press(new Point(40, 150), 0, LaserTrail.DefaultColor, LaserTrail.DefaultWidth);
        for (int step = 1; step <= 30 && step / 60.0 <= time; step++)
        {
            double share = step / 30.0;
            trail.Move(new Point(40 + 280 * share, 150 - 90 * Math.Sin(share * Math.PI)), step / 60.0);
        }
        if (time >= 0.5)
        {
            trail.Release(null, 0.5);
        }
        return trail;
    }

    /// <summary>
    /// Spotlight at the default 130-point radius around (600, 200) over UXReferenceCaptureTests.backdrop, which the Mac's
    /// flipped capture context draws upside down: the text bars and color blocks sit in the lower half.
    /// </summary>
    private static RasterImage Spotlight(OffscreenCanvas canvas, PointerEffectsRenderer effects)
    {
        int width = (int)(900 * Scale), height = (int)(560 * Scale);
        var area = new DrawArea(Scale, 0, 0, new PixelRect(0, 0, width, height));
        SpotlightPaint paint = SpotlightPaint.Create(new Rect(0, 0, 900, 560), new Point(600, 200), 130)!.Value;
        return canvas.Render(width, height, context =>
        {
            DrawBackdrop(context);
            effects.DrawSpotlight((nint)context, paint, area);
        });
    }

    private static void DrawBackdrop(ID2D1DeviceContext1* context)
    {
        static Rect Flip(Rect rect) => new(rect.X, 560 - rect.Y - rect.Height, rect.Width, rect.Height);
        var scale = D2D.Matrix(AffineTransform.Scale(Scale, Scale));
        context->SetTransform(&scale);
        Solid(context, new RGBA(0.91, 0.93, 0.96), new Rect(0, 0, 900, 560), 0);
        var card = new Rect(40, 36, 820, 488);
        Solid(context, new RGBA(1, 1, 1), Flip(card), 12);
        double[] lengths = [0.42, 0.66, 0.55, 0.61, 0.38];
        for (int index = 0; index < lengths.Length; index++)
        {
            Solid(context, new RGBA(0.80, 0.83, 0.88), Flip(new Rect(card.X + 32, card.Y + 40 + index * 30, (card.Width * 0.5 - 64) * lengths[index] * 1.6, 10)), 0);
        }
        RGBA[] colors = [new(0.98, 0.45, 0.35), new(0.98, 0.78, 0.25), new(0.32, 0.72, 0.55), new(0.32, 0.52, 0.95)];
        for (int index = 0; index < colors.Length; index++)
        {
            Solid(context, colors[index], Flip(new Rect(card.X + card.Width - 300 + index * 64, card.Y + 40, 52, 150)), 0);
        }
        var identity = D2D.Identity;
        context->SetTransform(&identity);
    }

    private static void Solid(ID2D1DeviceContext1* context, RGBA color, Rect rect, double radius)
    {
        ID2D1SolidColorBrush* brush = null;
        try
        {
            var value = D2D.Color(color);
            Native.Check(context->CreateSolidColorBrush(&value, null, &brush), "ID2D1DeviceContext::CreateSolidColorBrush");
            var bounds = D2D.Rect(rect.X, rect.Y, rect.X + rect.Width, rect.Y + rect.Height);
            if (radius > 0)
            {
                var rounded = new D2D1_ROUNDED_RECT { rect = bounds, radiusX = (float)radius, radiusY = (float)radius };
                context->FillRoundedRectangle(&rounded, (ID2D1Brush*)brush);
            }
            else
            {
                context->FillRectangle(&bounds, (ID2D1Brush*)brush);
            }
        }
        finally
        {
            Native.Release(ref brush);
        }
    }

    private static void Fill(ID2D1DeviceContext1* context, double gray)
    {
        var color = new D2D1_COLOR_F { r = (float)gray, g = (float)gray, b = (float)gray, a = 1 };
        context->Clear(&color);
    }

    private static Row Compare(string reference, string output, string state, string theme, RasterImage windows)
    {
        string stem = Path.Combine(output, "pointer", $"{state}-{theme}");
        File.WriteAllBytes(stem + ".windows.png", RasterAsset.EncodePng(windows));
        RasterImage mac = RasterAsset.Decode(File.ReadAllBytes(Path.Combine(reference, "pointer", $"{state}-{theme}.png")));
        Difference difference = Measure(mac, windows);
        File.WriteAllBytes(stem + ".sheet.png", RasterAsset.EncodePng(Sheet([mac, windows, difference.Image])));
        return new Row(state, theme, difference.Mean, difference.Visible, mac.Width == windows.Width && mac.Height == windows.Height,
            string.Create(CultureInfo.InvariantCulture, $"mean difference {difference.Mean:0.00}/255, {difference.Visible:P2} of pixels differ by more than 16"));
    }

    /// <summary>
    /// The frames of the Mac contact sheet's tiles (<paramref name="frame"/> draws one at 2x for a time), compared with the
    /// tiles at 1x: the Windows sheet is the Mac sheet with the Windows frames in its tiles, so the captions stay the Mac's.
    /// Tiles are the frame's size in points, four to a row, or two when a frame is 400 points wide or more
    /// (tools/macos/ux_reference.py).
    /// </summary>
    private static Row CompareSequence(string reference, string output, string state, string theme, Func<double, RasterImage> frame)
    {
        string folder = Path.Combine(reference, "pointer", "motion");
        JsonNode metadata = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, $"{state}-{theme}.json")))!;
        double[] times = [.. metadata["contactSheetFrames"]!.AsArray().Select(time => time!.GetValue<double>())];
        JsonArray size = metadata["size"]!.AsArray();
        int tileWidth = (int)size[0]!.GetValue<double>(), tileHeight = (int)size[1]!.GetValue<double>();
        int columns = tileWidth < 400 ? 4 : 2;
        RasterImage mac = RasterAsset.Decode(File.ReadAllBytes(Path.Combine(folder, metadata["contactSheet"]!.GetValue<string>())));
        byte[] pixels = mac.Pixels.ToArray();
        double total = 0, visible = 0;
        for (int index = 0; index < times.Length; index++)
        {
            // The Mac's frames are k / 60 s; the sheet lists them rounded to four places.
            int number = (int)Math.Round(times[index] * 60);
            RasterImage tile = Half(frame(number / 60.0));
            int left = SheetMargin + index % columns * (tileWidth + SheetMargin);
            int top = SheetMargin + index / columns * (tileHeight + SheetCaption + SheetMargin);
            RasterImage macTile = Crop(mac, left, top, tileWidth, tileHeight);
            Difference difference = Measure(macTile, tile);
            total += difference.Mean;
            visible += difference.Visible;
            for (int row = 0; row < tileHeight; row++)
            {
                tile.Pixels.Slice(row * tile.Stride, tileWidth * 4).CopyTo(pixels.AsSpan((top + row) * mac.Stride + left * 4, tileWidth * 4));
            }
        }
        var windows = new RasterImage(mac.Width, mac.Height, mac.Stride, pixels);
        string stem = Path.Combine(output, "pointer", "motion", $"{state}-{theme}");
        File.WriteAllBytes(stem + ".windows.png", RasterAsset.EncodePng(windows));
        Difference sheet = Measure(mac, windows);
        File.WriteAllBytes(stem + ".sheet.png", RasterAsset.EncodePng(Sheet([mac, windows, sheet.Image])));
        double mean = total / times.Length, share = visible / times.Length;
        return new Row($"motion/{state}", theme, mean, share, true,
            string.Create(CultureInfo.InvariantCulture, $"{times.Length} contact-sheet frames: mean difference {mean:0.00}/255, {share:P2} of tile pixels differ by more than 16"));
    }

    /// <summary>A 2x frame at 1x, each pixel the mean of four.</summary>
    private static RasterImage Half(RasterImage image)
    {
        int width = image.Width / 2, height = image.Height / 2;
        var pixels = new byte[width * height * 4];
        ReadOnlySpan<byte> source = image.Pixels;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                for (int channel = 0; channel < 4; channel++)
                {
                    int sum = source[2 * y * image.Stride + 2 * x * 4 + channel] + source[2 * y * image.Stride + (2 * x + 1) * 4 + channel]
                              + source[(2 * y + 1) * image.Stride + 2 * x * 4 + channel] + source[(2 * y + 1) * image.Stride + (2 * x + 1) * 4 + channel];
                    pixels[(y * width + x) * 4 + channel] = (byte)((sum + 2) / 4);
                }
            }
        }
        return new RasterImage(width, height, width * 4, pixels);
    }

    private static RasterImage Crop(RasterImage image, int left, int top, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int row = 0; row < height; row++)
        {
            image.Pixels.Slice((top + row) * image.Stride + left * 4, width * 4).CopyTo(pixels.AsSpan(row * width * 4));
        }
        return new RasterImage(width, height, width * 4, pixels);
    }

    private readonly record struct Difference(double Mean, double Visible, RasterImage Image);

    /// <summary>
    /// The mean color difference per channel over the shared area, the share of pixels whose largest channel difference is
    /// above 16, and the difference image four times stronger (opaque). Size differences count as fully different.
    /// </summary>
    private static Difference Measure(RasterImage mac, RasterImage windows)
    {
        int width = Math.Max(mac.Width, windows.Width), height = Math.Max(mac.Height, windows.Height);
        var pixels = new byte[width * height * 4];
        double sum = 0;
        long differing = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                pixels[offset + 3] = 255;
                if (x >= mac.Width || y >= mac.Height || x >= windows.Width || y >= windows.Height)
                {
                    sum += 255;
                    differing++;
                    pixels[offset + 2] = 255;
                    continue;
                }
                int largest = 0;
                for (int channel = 0; channel < 3; channel++)
                {
                    int delta = Math.Abs(mac.Pixels[y * mac.Stride + x * 4 + channel] - windows.Pixels[y * windows.Stride + x * 4 + channel]);
                    sum += delta / 3.0;
                    largest = Math.Max(largest, delta);
                    pixels[offset + channel] = (byte)Math.Min(255, delta * 4);
                }
                differing += largest > 16 ? 1 : 0;
            }
        }
        double count = (double)width * height;
        return new Difference(sum / count, differing / count, new RasterImage(width, height, width * 4, pixels));
    }

    /// <summary>The images one above the other on mid gray, 8 pixels apart.</summary>
    private static RasterImage Sheet(IReadOnlyList<RasterImage> images)
    {
        const int gap = 8;
        int width = images.Max(image => image.Width), height = images.Sum(image => image.Height) + gap * (images.Count - 1);
        var pixels = new byte[width * height * 4];
        for (int index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = pixels[index + 1] = pixels[index + 2] = 0x60;
            pixels[index + 3] = 255;
        }
        int top = 0;
        foreach (RasterImage image in images)
        {
            for (int row = 0; row < image.Height; row++)
            {
                image.Pixels.Slice(row * image.Stride, image.Width * 4).CopyTo(pixels.AsSpan((top + row) * width * 4));
            }
            top += image.Height + gap;
        }
        return new RasterImage(width, height, width * 4, pixels);
    }

    private sealed record Row(string State, string Theme, double Mean, double Visible, bool SameSize, string Note);

    private static void WriteSummary(string output, string reference, List<Row> rows)
    {
        var text = new StringBuilder();
        text.AppendLine("# Pointer references: Windows renderer against the Mac");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"- Run: {DateTime.Now:yyyy-MM-dd HH:mm}, WARP, 2x");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Reference: `{reference}`");
        text.AppendLine("- Mean: average color difference per channel (0 to 255); differing: pixels whose largest channel difference is above 16.");
        text.AppendLine();
        text.AppendLine("| State | Theme | Mean | Differing | Same size |");
        text.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (Row row in rows)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"| pointer/{row.State} | {row.Theme} | {row.Mean:0.00} | {row.Visible:P2} | {(row.SameSize ? "yes" : "no")} |");
        }
        File.WriteAllText(Path.Combine(output, "pointer", "summary.md"), text.ToString());
        var json = new JsonArray([.. rows.Select(row => (JsonNode)new JsonObject
        {
            ["state"] = $"pointer/{row.State}",
            ["theme"] = row.Theme,
            ["meanDifference"] = Math.Round(row.Mean, 3),
            ["differingShare"] = Math.Round(row.Visible, 5),
            ["sameSize"] = row.SameSize,
        })]);
        File.WriteAllText(Path.Combine(output, "pointer", "summary.json"), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string FindReference()
    {
        for (string? folder = Environment.CurrentDirectory; folder is not null; folder = Path.GetDirectoryName(folder))
        {
            string candidate = Path.Combine(folder, "reference", "ux-reference", "macos");
            if (Directory.Exists(Path.Combine(candidate, "pointer")))
            {
                return candidate;
            }
        }
        throw new ArgumentException("reference/ux-reference/macos was not found from the current folder. Pass --reference.");
    }
}
