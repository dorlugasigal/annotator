using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Platform.Displays;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Export;
using Annotator.Rendering.Imaging;

namespace Annotator.Rendering.Tests;

/// <summary>
/// Every display operation, rendered on WARP and checked against the geometry: inside a fill every pixel has the fill
/// color, outside an item's bounds nothing is painted. These are regression checks, not Mac parity evidence.
/// </summary>
public sealed unsafe class DisplayOperationTests
{
    private static readonly Paint Red = Paint.Solid(new RGBA(1, 0, 0));
    private static readonly Paint Blue = Paint.Solid(new RGBA(0, 0, 1));

    private static PathData Rectangle(double x, double y, double width, double height) =>
        new PathBuilder(5, 4).AddRect(new Rect(x, y, width, height)).ToPathData();

    private static PathData Line(params Point[] points)
    {
        var builder = new PathBuilder(points.Length, points.Length).MoveTo(points[0]);
        for (var index = 1; index < points.Length; index++)
        {
            builder.LineTo(points[index]);
        }
        return builder.ToPathData();
    }

    [Fact]
    public void FillPathPaintsExactlyTheShape()
    {
        var image = Draw.Items(40, 40, DisplayItem.FillPath(Rectangle(10, 10, 20, 20), FillRule.NonZero, Red));
        for (var y = 10; y < 30; y++)
        {
            for (var x = 10; x < 30; x++)
            {
                Assert.Equal((255, 0, 0, 255), Pixels.At(image, x, y));
            }
        }
        Assert.True(Pixels.TransparentOutside(image, 10, 10, 30, 30));
    }

    [Fact]
    public void FillRulesDecideTheInside()
    {
        var ring = new PathBuilder(10, 8).AddRect(new Rect(4, 4, 32, 32)).AddRect(new Rect(14, 14, 12, 12)).ToPathData();
        var evenOdd = Draw.Items(40, 40, DisplayItem.FillPath(ring, FillRule.EvenOdd, Red));
        var nonZero = Draw.Items(40, 40, DisplayItem.FillPath(ring, FillRule.NonZero, Red));
        Assert.Equal(0, Pixels.Alpha(evenOdd, 20, 20));
        Assert.Equal(255, Pixels.Alpha(evenOdd, 8, 20));
        Assert.Equal(255, Pixels.Alpha(nonZero, 20, 20));
    }

    [Fact]
    public void StrokePathFollowsWidthCapsJoinsAndUserSpaceDashes()
    {
        var line = Line(new Point(5, 20), new Point(35, 20));
        var butt = Draw.Items(40, 40, DisplayItem.StrokePath(line, new StrokeStyle(4), Red));
        Assert.Equal(255, Pixels.Alpha(butt, 5, 18));
        Assert.Equal(255, Pixels.Alpha(butt, 34, 21));
        Assert.True(Pixels.TransparentOutside(butt, 5, 18, 35, 22));

        var round = Draw.Items(40, 40, DisplayItem.StrokePath(line, new StrokeStyle(4, LineCap.Round), Red));
        Assert.True(Pixels.Alpha(round, 3, 20) > 0, "A round cap reaches half the width past the end.");
        Assert.True(Pixels.TransparentOutside(round, 3, 18, 37, 22));

        // Dashes are user-space lengths (4 on, 4 off) even though Direct2D measures them in widths.
        var dashed = Draw.Items(40, 40, DisplayItem.StrokePath(line, new StrokeStyle(2, dashes: [4, 4]), Red));
        foreach (var x in new[] { 5, 8, 13, 16, 21 })
        {
            Assert.Equal(255, Pixels.Alpha(dashed, x, 19));
        }
        foreach (var x in new[] { 9, 12, 17, 20 })
        {
            Assert.Equal(0, Pixels.Alpha(dashed, x, 19));
        }

        var corner = Line(new Point(5, 35), new Point(20, 8), new Point(35, 35));
        var miter = Draw.Items(40, 40, DisplayItem.StrokePath(corner, new StrokeStyle(4), Red));
        var bevel = Draw.Items(40, 40, DisplayItem.StrokePath(corner, new StrokeStyle(4, join: LineJoin.Bevel), Red));
        Assert.True(Pixels.Alpha(miter, 20, 5) > 0, "The miter tip reaches above the corner.");
        Assert.Equal(0, Pixels.Alpha(bevel, 20, 5));
    }

    [Fact]
    public void SnappedRectanglesCoverWholeDevicePixelsWithoutAntialiasing()
    {
        var rect = new Rect(10.3, 10.3, 9.4, 9.4);
        var soft = Draw.Items(32, 32, DisplayItem.FillRect(rect, Paint.Black));
        Assert.InRange(Pixels.Alpha(soft, 10, 15), 1, 254);
        var snapped = Draw.Items(32, 32, DisplayItem.FillRect(rect, Paint.Black, RectSnap.OutwardToPixels));
        for (var y = 10; y < 20; y++)
        {
            for (var x = 10; x < 20; x++)
            {
                Assert.Equal((0, 0, 0, 255), Pixels.At(snapped, x, y));
            }
        }
        Assert.True(Pixels.TransparentOutside(snapped, 10, 10, 20, 20));

        // At 125% the rectangle (10, 10, 8, 8) covers device pixels 12.5 to 22.5, so 12 to 22 are opaque.
        var scaled = Draw.Items(40, 40, 1.25, DisplayItem.FillRect(new Rect(10, 10, 8, 8), Paint.Black, RectSnap.OutwardToPixels));
        Assert.Equal(255, Pixels.Alpha(scaled, 12, 12));
        Assert.Equal(255, Pixels.Alpha(scaled, 22, 22));
        Assert.True(Pixels.TransparentOutside(scaled, 12, 12, 23, 23));
    }

    [Fact]
    public void RotatedSnappedCoversNeverLeaveAPartlyCoveredPixel()
    {
        var rect = new Rect(12, 14, 20, 10);
        var rotation = AffineTransform.RotationAbout(rect.Center, Math.PI / 7);
        var image = Draw.Items(48, 48,
            DisplayItem.PushTransform(rotation), DisplayItem.FillRect(rect, Paint.Black, RectSnap.OutwardToPixels), DisplayItem.PopTransform());
        var corners = new[] { rotation.Transform(rect.Origin), rotation.Transform(new Point(rect.MaxX, rect.Y)),
                              rotation.Transform(new Point(rect.MaxX, rect.MaxY)), rotation.Transform(new Point(rect.X, rect.MaxY)) };
        var polygon = new PathBuilder(5, 4).AddLines(corners).Close().ToPathData();
        for (var y = 0; y < 48; y++)
        {
            for (var x = 0; x < 48; x++)
            {
                if (Touches(polygon, x, y))
                {
                    Assert.True(Pixels.Alpha(image, x, y) == 255, $"Pixel ({x}, {y}) is touched by the cover but not opaque.");
                }
            }
        }
    }

    [Fact]
    public void PressureSegmentsUseTheirOwnWidthsAndRestartDashes()
    {
        var points = ValueList.Create<Point>([new Point(4, 20), new Point(20, 20), new Point(36, 20)]);
        var pressures = ValueList.Create<double>([1, 0.25, 1]);
        var style = new StrokeStyle(8, LineCap.Round, LineJoin.Round);
        var run = new StrokeSegments(points, pressures, 8, style, Red, new Rect(4, 20, 32, 0));
        var image = Draw.Items(40, 40, DisplayItem.StrokeSegmentRun(run));
        // Segment 0 is 2 wide (pressure 0.25), segment 1 is 8 wide.
        Assert.Equal(0, Pixels.Alpha(image, 12, 17));
        Assert.Equal(255, Pixels.Alpha(image, 28, 17));
        Assert.Equal(255, Pixels.Alpha(image, 12, 20));
        Assert.True(Pixels.TransparentOutside(image, 0, 15, 40, 25));

        var dashed = new StrokeSegments(points, ValueList.Create<double>([1, 1, 1]), 2, new StrokeStyle(2, dashes: [3, 5]), Red, new Rect(4, 20, 32, 0));
        var dashes = Draw.Items(40, 40, DisplayItem.StrokeSegmentRun(dashed));
        // Each segment starts its own pattern at its first point: 4 to 7 and 20 to 23 are on, 8 to 11 and 24 to 27 off.
        Assert.Equal(255, Pixels.Alpha(dashes, 5, 20));
        Assert.Equal(255, Pixels.Alpha(dashes, 21, 20));
        Assert.Equal(0, Pixels.Alpha(dashes, 9, 20));
        Assert.Equal(0, Pixels.Alpha(dashes, 25, 20));
    }

    [Theory]
    [InlineData(TextAlignment.Left)]
    [InlineData(TextAlignment.Center)]
    [InlineData(TextAlignment.Right)]
    public void TextStartsAtTheTopAndAlignsInsideItsFrame(TextAlignment alignment)
    {
        var frame = new Rect(20, 10, 200, 50);
        var font = new TextFont(FontDesign.System, FontWeight.Semibold, 28);
        var run = new TextRun("Hello", frame, font, alignment, Paint.Solid(new RGBA(0, 0, 0)));
        var image = Draw.Items(240, 70, DisplayItem.DrawText(run));
        var (left, top, right, bottom) = InkBox(image);
        var metrics = Warp.Measurer.LineMetrics(font);
        var width = Warp.Measurer.Measure("Hello", font).Width;
        Assert.True(top >= 10 && bottom <= 10 + metrics.Ascent + metrics.Descent + 1, $"Ink rows {top} to {bottom}.");
        // The cap height of "H" starts below the line top by roughly ascent minus cap height.
        Assert.True(top > 10 + metrics.Ascent * 0.15, $"Ink starts at row {top}, too close to the frame top.");
        // The baseline is where the Mac's is, one SF ascender below the frame top ("Hello" has no descenders).
        Assert.InRange(bottom, 10 + MacTextMetrics.Ascender(28) - 0.5, 10 + MacTextMetrics.Ascender(28) + 1.5);
        var expectedLeft = alignment switch
        {
            TextAlignment.Left => frame.X,
            TextAlignment.Center => frame.X + (frame.Width - width) / 2,
            _ => frame.MaxX - width,
        };
        Assert.InRange(left, expectedLeft - 1, expectedLeft + 5);
        Assert.InRange(right, expectedLeft + width - 6, expectedLeft + width + 1);
    }

    [Fact]
    public void RightToLeftTextKeepsAbsoluteLeftAlignment()
    {
        var frame = new Rect(20, 10, 200, 50);
        var font = new TextFont(FontDesign.System, FontWeight.Regular, 28);
        var run = new TextRun("שלום עולם", frame, font, TextAlignment.Left, Paint.Solid(new RGBA(0, 0, 0)));
        var (left, _, right, _) = InkBox(Draw.Items(240, 70, DisplayItem.DrawText(run)));
        Assert.InRange(left, 19, 26);
        Assert.True(right < 160, $"Hebrew text drawn left-aligned ends at {right}.");
    }

    [Fact]
    public void ImagesDrawOrFallBackToTheirReplacement()
    {
        using var exporter = Warp.Exporter();
        var png = exporter.Data(ElementList.Empty, new Size(16, 16), background: Palette.Blue);
        var id = Guid.NewGuid();
        var outline = DisplayItem.StrokePath(Rectangle(8, 8, 24, 24), new StrokeStyle(1), Paint.Red);
        var diagonal = DisplayItem.StrokePath(Line(new Point(8, 8), new Point(32, 32)), new StrokeStyle(1), Paint.Red);
        var good = new ImageDraw(id, ValueList.Create<byte>(png), new Rect(8, 8, 24, 24), 1, ImageInterpolation.HighQualityCubic, 2);
        var drawn = Draw.Items(40, 40, DisplayItem.DrawImage(good), outline, diagonal);
        var (r, g, b, a) = Pixels.At(drawn, 20, 12);
        Assert.Equal(255, a);
        Assert.True(b > 200 && r < 80, $"Expected the blue image, got ({r}, {g}, {b}).");
        Assert.True(Pixels.TransparentOutside(drawn, 8, 8, 32, 32), "The fallback outline is skipped when the image draws.");

        var broken = new ImageDraw(id, ValueList.Create<byte>([1, 2, 3, 4]), new Rect(8, 8, 24, 24), 1, ImageInterpolation.HighQualityCubic, 2);
        var list = TestLists.Create(DisplayItem.DrawImage(broken), outline, diagonal);
        var image = exporter.Render(TestLists.Scene(list), new Size(40, 40), includeSelection: false);
        Assert.Equal(RenderIssues.ImageUnavailable, exporter.Renderer.TakeIssues());
        Assert.True(Pixels.Alpha(image, 20, 20) > 0, "The diagonal replaces the image.");
        Assert.True(Pixels.Alpha(image, 8, 20) > 0, "The outline replaces the image.");
        var opacity = new ImageDraw(id, ValueList.Create<byte>(png), new Rect(8, 8, 24, 24), 0.5, ImageInterpolation.NearestNeighbor, 0);
        Assert.InRange(Pixels.Alpha(Draw.Items(40, 40, DisplayItem.DrawImage(opacity)), 20, 20), 126, 129);
    }

    [Fact]
    public void TransformsApplyToEverythingUntilTheirPop()
    {
        var bar = Rectangle(4, 18, 32, 4);
        var image = Draw.Items(40, 40,
            DisplayItem.PushTransform(AffineTransform.RotationAbout(new Point(20, 20), Math.PI / 2)),
            DisplayItem.FillPath(bar, FillRule.NonZero, Red),
            DisplayItem.PopTransform(),
            DisplayItem.FillRect(new Rect(0, 0, 4, 4), Blue));
        Assert.Equal(255, Pixels.Alpha(image, 20, 6));
        Assert.Equal(0, Pixels.Alpha(image, 6, 20));
        Assert.Equal((0, 0, 255, 255), Pixels.At(image, 1, 1));
    }

    [Fact]
    public void LayersCompositeOnceAndHonorMasksAndBounds()
    {
        var image = Draw.Items(40, 40,
            DisplayItem.PushLayer(0.5),
            DisplayItem.FillRect(new Rect(4, 4, 20, 20), Red),
            DisplayItem.FillRect(new Rect(14, 14, 20, 20), Red),
            DisplayItem.PopLayer());
        Assert.InRange(Pixels.Alpha(image, 18, 18), 127, 128);
        Assert.InRange(Pixels.Alpha(image, 6, 6), 127, 128);

        var mask = new PathBuilder(10, 8).AddRect(new Rect(0, 0, 40, 40)).AddRect(new Rect(10, 10, 20, 20)).ToPathData();
        var masked = Draw.Items(40, 40,
            DisplayItem.PushLayer(1, mask: mask, maskRule: FillRule.EvenOdd), DisplayItem.FillRect(new Rect(0, 0, 40, 40), Red), DisplayItem.PopLayer());
        Assert.Equal(0, Pixels.Alpha(masked, 20, 20));
        Assert.Equal(255, Pixels.Alpha(masked, 5, 5));

        var bounded = Draw.Items(40, 40,
            DisplayItem.PushLayer(1, new Rect(0, 0, 20, 40)), DisplayItem.FillRect(new Rect(0, 0, 40, 40), Red), DisplayItem.PopLayer());
        Assert.Equal(255, Pixels.Alpha(bounded, 10, 10));
        Assert.True(Pixels.TransparentOutside(bounded, 0, 0, 20, 40));
    }

    [Fact]
    public void ClipsLimitDrawingAlignedOrRotated()
    {
        var aligned = Draw.Items(40, 40, DisplayItem.PushClip(new Rect(10, 10, 10, 10)), DisplayItem.FillRect(new Rect(0, 0, 40, 40), Red), DisplayItem.PopClip());
        Assert.Equal(255, Pixels.Alpha(aligned, 15, 15));
        Assert.True(Pixels.TransparentOutside(aligned, 10, 10, 20, 20));

        var rotated = Draw.Items(40, 40,
            DisplayItem.PushTransform(AffineTransform.RotationAbout(new Point(20, 20), Math.PI / 4)),
            DisplayItem.PushClip(new Rect(10, 10, 20, 20)),
            DisplayItem.FillRect(new Rect(-20, -20, 80, 80), Red),
            DisplayItem.PopClip(),
            DisplayItem.PopTransform());
        Assert.Equal(255, Pixels.Alpha(rotated, 20, 20));
        Assert.Equal(255, Pixels.Alpha(rotated, 20, 7));
        Assert.Equal(0, Pixels.Alpha(rotated, 9, 9));
        Assert.Equal(0, Pixels.Alpha(rotated, 31, 31));
    }

    [Fact]
    public void GradientsInterpolateInSrgbAcrossUserSpaceAndClampTheirEnds()
    {
        var gradient = new LinearGradientPaint(ValueList.Create<GradientStop>([new GradientStop(0, new RGBA(1, 0, 0)), new GradientStop(1, new RGBA(0, 0, 1))]),
                                               new Point(10, 0), new Point(30, 0));
        var image = Draw.Items(40, 8, DisplayItem.FillRect(new Rect(0, 0, 40, 8), Paint.Linear(gradient, 1)));
        Assert.Equal((255, 0, 0, 255), Pixels.At(image, 2, 4));
        Assert.Equal((0, 0, 255, 255), Pixels.At(image, 37, 4));
        var (r, _, b, _) = Pixels.At(image, 20, 4);
        Assert.InRange(r, 110, 145);
        Assert.InRange(b, 110, 145);
        var faint = Draw.Items(40, 8, DisplayItem.FillRect(new Rect(0, 0, 40, 8), Paint.Linear(gradient, 0.5)));
        Assert.InRange(Pixels.Alpha(faint, 2, 4), 127, 128);
    }

    [Fact]
    public void SystemColorsComeFromTheResolver()
    {
        var image = Draw.Items(8, 8, DisplayItem.FillRect(new Rect(0, 0, 8, 8), Paint.Accent));
        var accent = FixedSystemColors.Light.Resolve(SystemColor.Accent);
        Assert.Equal(((byte)Math.Round(accent.Red * 255), (byte)Math.Round(accent.Green * 255), (byte)Math.Round(accent.Blue * 255), (byte)255),
                     Pixels.At(image, 4, 4));
    }

    [Fact]
    public void DrawsOnlyInsideTheClipAndSkipsListsThatMissIt()
    {
        using var exporter = Warp.Exporter();
        var renderer = exporter.Renderer;
        var left = TestLists.Create(DisplayItem.FillRect(new Rect(0, 0, 40, 40), Red));
        var right = TestLists.Create(DisplayItem.FillRect(new Rect(30, 0, 10, 10), Blue));
        renderer.Enter(left);
        renderer.Enter(right);
        var image = exporter.Canvas.Render(40, 40, context =>
        {
            unsafe
            {
                renderer.Draw((nint)context, [left, right], new DrawArea(1, 0, 0, new PixelRect(0, 0, 20, 40)));
            }
        });
        Assert.Equal(255, Pixels.Alpha(image, 10, 10));
        Assert.True(Pixels.TransparentOutside(image, 0, 0, 20, 40));
        Assert.Equal(1, renderer.Counters.ListsSkipped);
        renderer.Leave(left);
        renderer.Leave(right);
    }

    [Fact]
    public void CachesFollowIdentityAndReleaseWhatNoListUses()
    {
        using var exporter = Warp.Exporter();
        var renderer = exporter.Renderer;
        var shared = Rectangle(4, 4, 20, 20);
        var first = TestLists.Create(DisplayItem.FillPath(shared, FillRule.NonZero, Red));
        var second = TestLists.Create(DisplayItem.StrokePath(shared, new StrokeStyle(2), Blue));
        renderer.Enter(first);
        renderer.Enter(first);
        renderer.Enter(second);
        Assert.Equal(2, renderer.EnteredLists);
        Assert.Equal(1, renderer.CachedGeometries);
        renderer.Leave(first);
        Assert.Equal(2, renderer.EnteredLists);
        renderer.Leave(first);
        Assert.Equal(1, renderer.EnteredLists);
        Assert.Equal(1, renderer.CachedGeometries);
        renderer.Leave(second);
        Assert.Equal(0, renderer.EnteredLists);
        Assert.Equal(0, renderer.CachedGeometries);
        Assert.Throws<InvalidOperationException>(() => renderer.Leave(second));
    }

    [Fact]
    public void RealizedElementsUseOneMethodForEveryRedrawAndLiveOnesNever()
    {
        var renderer = new DisplayListRenderer(Warp.Devices, FixedSystemColors.Light, new RendererOptions { UseRealizations = true });
        using var exporter = new PngExporter(renderer, Warp.Measurer, ownsRenderer: true);
        var path = Line(new Point(2, 2), new Point(30, 8), new Point(4, 30), new Point(36, 36));
        var list = TestLists.Create(DisplayItem.StrokePath(path, new StrokeStyle(3, LineCap.Round, LineJoin.Round), Red));
        renderer.Enter(list);
        RasterImage Render(bool live, PixelRect clip) => exporter.Canvas.Render(40, 40, context =>
        {
            renderer.Draw((nint)context, list, new DrawArea(1, 0, 0, clip, live));
        });
        var whole = new PixelRect(0, 0, 40, 40);
        _ = Render(live: true, whole);
        Assert.Equal(0, renderer.Counters.RealizationsBuilt);
        var first = Render(live: false, whole);
        Assert.Equal(1, renderer.Counters.RealizationsBuilt);
        var part = new PixelRect(10, 4, 26, 30);
        var partial = Render(live: false, part);
        Assert.Equal(1, renderer.Counters.RealizationsBuilt);
        Assert.Equal(2, renderer.Counters.RealizedDraws);
        // A partial redraw paints exactly the pixels of the full draw, so damage-limited redraws leave no seams.
        for (var y = part.Top; y < part.Bottom; y++)
        {
            for (var x = part.Left; x < part.Right; x++)
            {
                Assert.Equal(first[x, y], partial[x, y]);
            }
        }
        renderer.Leave(list);
    }

    [Fact]
    public void CulledLongStrokesPaintTheSamePixelsInsideTheClip()
    {
        var points = new Point[2_000];
        for (var index = 0; index < points.Length; index++)
        {
            points[index] = new Point(4 + index * 0.1, 50 + Math.Sin(index * 0.05) * 40);
        }
        foreach (var style in new[] { new StrokeStyle(6, LineCap.Round, LineJoin.Round), new StrokeStyle(15, LineCap.Butt, LineJoin.Bevel),
                                      new StrokeStyle(4, LineCap.Square, LineJoin.Miter) })
        {
            var list = TestLists.Create(DisplayItem.StrokePath(Line(points), style, Paint.Solid(new RGBA(0.2, 0.4, 0.8, 0.5))));
            using var exporter = Warp.Exporter();
            var renderer = exporter.Renderer;
            renderer.Enter(list);
            var clip = new PixelRect(90, 20, 130, 60);
            RasterImage Render(PixelRect area) => exporter.Canvas.Render(210, 100, context =>
            {
                unsafe
                {
                    renderer.Draw((nint)context, list, new DrawArea(1, 0, 0, area, Live: true));
                }
            });
            var full = Render(new PixelRect(0, 0, 210, 100));
            var culled = Render(clip);
            Assert.True(renderer.Counters.CulledStrokes >= 1);
            for (var y = clip.Top; y < clip.Bottom; y++)
            {
                for (var x = clip.Left; x < clip.Right; x++)
                {
                    Assert.Equal(full[x, y], culled[x, y]);
                }
            }
            Assert.True(Pixels.TransparentOutside(culled, clip.Left, clip.Top, clip.Right, clip.Bottom));
            renderer.Leave(list);
        }
    }

    /// <summary>Whether the pixel square (x, y) to (x + 1, y + 1) overlaps the polygon, by 8 x 8 samples and its corners.</summary>
    private static bool Touches(PathData polygon, int x, int y)
    {
        for (var j = 0; j <= 8; j++)
        {
            for (var i = 0; i <= 8; i++)
            {
                if (polygon.Contains(new Point(x + i / 8.0, y + j / 8.0)))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static (int Left, int Top, int Right, int Bottom) InkBox(RasterImage image)
    {
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                if (image[x, y].Alpha > 0)
                {
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x + 1);
                    bottom = Math.Max(bottom, y + 1);
                }
            }
        }
        return (left, top, right, bottom);
    }
}
