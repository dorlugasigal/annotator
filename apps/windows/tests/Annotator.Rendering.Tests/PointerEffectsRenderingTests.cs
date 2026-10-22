// Pixel ports of apps/macos/Tests/RenderingTests/CursorHighlightRenderingTests.swift and SpotlightTests.swift. The halo is drawn
// from Annotator.Core's CursorHighlightFrame at 256 x 256 around (128, 128), like the Mac's test context.
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Platform.Displays;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Export;
using Annotator.Rendering.Imaging;

namespace Annotator.Rendering.Tests;

public sealed unsafe class PointerEffectsRenderingTests
{
    private static RasterImage Halo(CursorHighlightSettings settings, double? progress = null, bool reduced = false)
    {
        using var renderer = new DisplayListRenderer(Warp.Devices);
        using var effects = new PointerEffectsRenderer(renderer);
        using var canvas = new OffscreenCanvas(Warp.Devices);
        var frame = CursorHighlightFrame.Create(settings, new Point(128, 128), progress, reduceMotion: reduced);
        return canvas.Render(256, 256, context => effects.DrawHalo((nint)context, frame, OffscreenCanvas.Area(256, 256, 1)));
    }

    private static byte[] Png(RasterImage image) => RasterAsset.EncodePng(image);

    [Fact]
    public void YellowHaloAndGlowContainNoBlackFringe()
    {
        foreach (var shape in CursorHighlightShape.AllCases)
        {
            foreach (var glow in new[] { 0.0, 12, 24 })
            {
                var settings = new CursorHighlightSettings
                {
                    Shape = shape,
                    Color = new RGBA(1, 0.82, 0, 0.65),
                    Diameter = 100,
                    BorderWidth = 7,
                    GlowRadius = glow,
                };
                var image = Halo(settings);
                var bytes = image.Pixels;
                var painted = 0;
                for (var offset = 0; offset < bytes.Length; offset += 4)
                {
                    int blue = bytes[offset], green = bytes[offset + 1], red = bytes[offset + 2], alpha = bytes[offset + 3];
                    if (alpha == 0)
                    {
                        continue;
                    }
                    painted++;
                    // Premultiplied yellow keeps red == alpha, even at its antialiased or glowing edge.
                    Assert.True(Math.Abs(red - alpha) <= 2, $"{shape} glow {glow}: red {red} alpha {alpha}.");
                    Assert.True(Math.Abs(green - alpha * 0.82) <= 2, $"{shape} glow {glow}: green {green} alpha {alpha}.");
                    Assert.True(blue <= 1, $"{shape} glow {glow}: blue {blue}.");
                }
                Assert.True(painted > 0);
            }
        }
    }

    [Fact]
    public void HaloShapesAreDistinctAndIdleHasNoClickRing()
    {
        var images = new HashSet<string>();
        foreach (var shape in CursorHighlightShape.AllCases)
        {
            var settings = new CursorHighlightSettings { Shape = shape, Diameter = 74, BorderWidth = 7 };
            var image = Halo(settings);
            images.Add(Convert.ToHexString(Png(image)));
            Assert.Equal(shape == CursorHighlightShape.Disc ? 255 : 0, image[128, 128].Alpha);
            Assert.Equal(0, image[0, 0].Alpha);
        }
        Assert.Equal(CursorHighlightShape.AllCases.Count, images.Count);
    }

    [Fact]
    public void GlowAddsSoftPixelsAndOpacityAppliesOnce()
    {
        var settings = new CursorHighlightSettings { Diameter = 74, Shape = CursorHighlightShape.RoundedSquare };
        var plain = Halo(settings);
        settings.GlowRadius = 12;
        var glow = Halo(settings);
        Assert.True(Pixels.Painted(glow) > Pixels.Painted(plain));
        settings.Color = settings.Color with { Alpha = 0.34 };
        var faint = Halo(settings);
        var glowAlpha = glow.AlphaChannel();
        var faintAlpha = faint.AlphaChannel();
        var largest = glowAlpha.Zip(faintAlpha, (a, b) => Math.Abs(b - a * 0.34)).Max();
        Assert.True(largest <= 2, $"Largest error {largest}.");
    }

    [Fact]
    public void ClickEffectsAnimateSelectedShapeAndRespectReducedMotion()
    {
        foreach (var effect in new[] { CursorClickEffect.Pulse, CursorClickEffect.Ripple, CursorClickEffect.Turn })
        {
            var settings = new CursorHighlightSettings { Shape = CursorHighlightShape.RoundedSquare, Diameter = 70, ClickEffect = effect };
            var idle = Png(Halo(settings));
            var early = Png(Halo(settings, progress: 0.2));
            var late = Png(Halo(settings, progress: 0.7));
            Assert.NotEqual(idle, early);
            Assert.NotEqual(early, late);
            var still = Png(Halo(settings, progress: 0.2, reduced: true));
            Assert.Equal(still, Png(Halo(settings, progress: 0.7, reduced: true)));
        }
        var off = new CursorHighlightSettings { ClickEffect = CursorClickEffect.Off };
        Assert.Equal(Png(Halo(off)), Png(Halo(off, progress: 0.5)));
    }

    [Fact]
    public void HaloScalesToDevicePixelsWithTheDisplay()
    {
        var settings = new CursorHighlightSettings { Shape = CursorHighlightShape.Disc, Diameter = 40, GlowRadius = 0 };
        var frame = CursorHighlightFrame.Create(settings, new Point(50, 50), null);
        using var renderer = new DisplayListRenderer(Warp.Devices);
        using var effects = new PointerEffectsRenderer(renderer);
        using var canvas = new OffscreenCanvas(Warp.Devices);
        var image = canvas.Render(160, 160, context => effects.DrawHalo((nint)context, frame, OffscreenCanvas.Area(160, 160, 1.5)));
        // The 40-point disc around (50, 50) covers 45 to 105 device pixels at 150%.
        Assert.Equal(255, image[75, 50].Alpha);
        Assert.Equal(255, image[48, 75].Alpha);
        Assert.Equal(0, image[42, 75].Alpha);
        Assert.Equal(0, image[75, 108].Alpha);
    }

    /// <summary>
    /// PointerEffectsLayer draws the resting halo once around a pixel corner and moves it by whole pixels: that must give the
    /// pixels a draw at the new place gives, for every shape, outline and glow at 125% and 150%. Without a glow they are
    /// equal; Direct2D's shadow blur rounds differently by position, by at most 2/255.
    /// </summary>
    [Fact]
    public void RestingHaloMovedByWholePixelsMatchesADrawThere()
    {
        using var renderer = new DisplayListRenderer(Warp.Devices);
        using var effects = new PointerEffectsRenderer(renderer);
        using var canvas = new OffscreenCanvas(Warp.Devices);
        foreach (double scale in new[] { 1.25, 1.5 })
        {
            foreach (var shape in CursorHighlightShape.AllCases)
            {
                foreach (var (pattern, glow) in new[] { (StrokePattern.Solid, 0.0), (StrokePattern.Dashed, 12.0), (StrokePattern.Dotted, 24.0) })
                {
                    var settings = new CursorHighlightSettings { Shape = shape, BorderStyle = pattern, GlowRadius = glow, Color = new RGBA(1, 0.82, 0, 0.8) };
                    int half = (int)Math.Ceiling((settings.Diameter / 2 + settings.BorderWidth / 2 + glow * CursorHighlightFrame.GlowDeviationPerRadius * 3) * scale + 2) + 1;
                    var sprite = canvas.Render(half * 2, half * 2, context => effects.DrawHalo((nint)context,
                        CursorHighlightFrame.Create(settings, new Point(half / scale, half / scale), null), OffscreenCanvas.Area(half * 2, half * 2, scale)));
                    const int x = 157, y = 131;
                    int size = half * 2 + 80;
                    var placed = canvas.Render(size, size, context => effects.DrawHalo((nint)context,
                        CursorHighlightFrame.Create(settings, new Point(x / scale, y / scale), null), OffscreenCanvas.Area(size, size, scale)));
                    int allowed = glow > 0 ? 2 : 0;
                    for (var row = 0; row < size; row++)
                    {
                        for (var column = 0; column < size; column++)
                        {
                            int u = column - (x - half), v = row - (y - half);
                            var expected = u >= 0 && v >= 0 && u < half * 2 && v < half * 2 ? sprite[u, v] : default;
                            var actual = placed[column, row];
                            int difference = Math.Max(Math.Max(Math.Abs(expected.Red - actual.Red), Math.Abs(expected.Green - actual.Green)),
                                Math.Max(Math.Abs(expected.Blue - actual.Blue), Math.Abs(expected.Alpha - actual.Alpha)));
                            Assert.True(difference <= allowed,
                                $"{shape} {pattern} glow {glow} at {scale}: pixel {column}, {row} is {actual}, the moved sprite gives {expected}.");
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void ClickFramesPaintOnlyInsideTheirBounds()
    {
        using var renderer = new DisplayListRenderer(Warp.Devices);
        using var effects = new PointerEffectsRenderer(renderer);
        using var canvas = new OffscreenCanvas(Warp.Devices);
        const double scale = 1.25;
        const int size = 300;
        foreach (var effect in new[] { CursorClickEffect.Pulse, CursorClickEffect.Ripple, CursorClickEffect.Turn })
        {
            foreach (var shape in CursorHighlightShape.AllCases)
            {
                foreach (var progress in new[] { 0.1, 0.25, 0.5, 0.9 })
                {
                    foreach (var reduced in new[] { false, true })
                    {
                        var settings = new CursorHighlightSettings { Shape = shape, ClickEffect = effect, GlowRadius = 10, Diameter = 60, BorderWidth = 5 };
                        // The ripple stays where the click was while the halo follows the pointer.
                        var frame = CursorHighlightFrame.Create(settings, new Point(120, 120), progress, new Point(100, 110), reduced);
                        Rect bounds = frame.PaintBounds();
                        var image = canvas.Render(size, size, context => effects.DrawHalo((nint)context, frame, OffscreenCanvas.Area(size, size, scale)));
                        int left = (int)Math.Floor(bounds.X * scale), top = (int)Math.Floor(bounds.Y * scale);
                        int right = (int)Math.Ceiling(bounds.MaxX * scale), bottom = (int)Math.Ceiling(bounds.MaxY * scale);
                        for (var y = 0; y < size; y++)
                        {
                            for (var x = 0; x < size; x++)
                            {
                                if (x < left || x >= right || y < top || y >= bottom)
                                {
                                    Assert.True(image[x, y].Alpha == 0, $"{effect} {shape} at {progress} (reduced {reduced}): pixel {x}, {y} is painted outside {bounds}.");
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void SpotlightFadesBetweenItsRadiiInsideItsClip()
    {
        var paint = SpotlightPaint.Create(new Rect(0, 0, 200, 120), new Point(100, 60), 40)!.Value;
        using var renderer = new DisplayListRenderer(Warp.Devices);
        using var effects = new PointerEffectsRenderer(renderer);
        using var canvas = new OffscreenCanvas(Warp.Devices);
        var image = canvas.Render(220, 140, context => effects.DrawSpotlight((nint)context, paint, OffscreenCanvas.Area(220, 140, 1)));
        foreach (var x in new[] { 100, 112, 125, 135, 145, 160, 190 })
        {
            var expected = paint.AlphaAt(x + 0.5 - 100) * 255;
            var actual = image[x, 60];
            Assert.True(Math.Abs(actual.Alpha - expected) <= 2, $"At x = {x}: alpha {actual.Alpha}, expected {expected:F1}.");
            Assert.Equal((0, 0, 0), (actual.Red, actual.Green, actual.Blue));
        }
        Assert.Equal(0, image[205, 60].Alpha);
        Assert.Equal(0, image[100, 130].Alpha);
        var faded = SpotlightPaint.Create(new Rect(0, 0, 200, 120), new Point(100, 60), 40, 0.5)!.Value;
        var half = canvas.Render(220, 140, context => effects.DrawSpotlight((nint)context, faded, OffscreenCanvas.Area(220, 140, 1)));
        Assert.InRange(half[195, 60].Alpha, (int)(0.62 * 0.5 * 255) - 1, (int)(0.62 * 0.5 * 255) + 1);
        Assert.Null(SpotlightPaint.Create(new Rect(0, 0, 10, 10), default, 10, 0));
        Assert.Throws<RenderFailure>(() => SpotlightPaint.Create(new Rect(0, 0, 10, 10), default, 0));
    }
}
