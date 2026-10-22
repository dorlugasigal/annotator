using System.Globalization;
using System.Text.Json;
using Annotator.Core;
using Annotator.Workspace.Artwork;
using Annotator.Workspace.Canvas;

namespace Annotator.Workspace.Tests;

/// <summary>The footprint and rotation cursors (Mac ToolCursorFactory), drawn for a monitor scale (WIN-OVR-11).</summary>
public sealed class CursorArtworkTests
{
    [Fact]
    public void APenFootprintIsCenteredWithAnOpaqueCross()
    {
        CursorArtwork.Image image = CursorArtwork.Footprint(CanvasCursor.ForTool(Tool.Pen, 4), 1);
        Assert.Equal((24, 24, 12, 12), (image.Width, image.Height, image.HotspotX, image.HotspotY));
        Assert.Equal(24 * 24 * 4, image.Bgra.Length);
        Assert.Equal(0, image.Alpha(0, 0));
        Assert.Equal(255, image.Alpha(11, 11));
        Assert.Equal(255, image.Alpha(12, 12));
        Assert.Equal(0, image.Alpha(23, 23));
    }

    [Fact]
    public void TheEraserIsAnOutlineOnly()
    {
        CursorArtwork.Image image = CursorArtwork.Footprint(CanvasCursor.ForTool(Tool.Eraser, 10), 1);
        Assert.Equal(30, image.Width);
        Assert.Equal(0, image.Alpha(15, 15));
        Assert.Equal(255, image.Alpha(24, 15));
        Assert.Equal(0, image.Alpha(28, 15));
    }

    [Fact]
    public void TheHighlighterIsAVerticalChisel()
    {
        CursorArtwork.Image image = CursorArtwork.Footprint(CanvasCursor.ForTool(Tool.Highlighter, 10), 1);
        Assert.Equal(60, image.Width);
        Assert.Equal(255, image.Alpha(22, 30));
        Assert.Equal(255, image.Alpha(30, 5));
        Assert.Equal(255, image.Alpha(30, 30));
        Assert.Equal(0, image.Alpha(10, 30));
        Assert.Equal(0, image.Alpha(30, 1));
    }

    [Theory]
    [InlineData(1.25, 30)]
    [InlineData(1.5, 36)]
    [InlineData(2, 48)]
    public void ImagesFollowTheMonitorScaleWithACenteredHotspot(double scale, int side)
    {
        CursorArtwork.Image image = CursorArtwork.Footprint(CanvasCursor.ForTool(Tool.Pen, 4), scale);
        Assert.Equal((side, side, side / 2, side / 2), (image.Width, image.Height, image.HotspotX, image.HotspotY));
        Assert.Equal(255, image.Alpha(side / 2, side / 2));
    }

    [Fact]
    public void TheRotationCursorIsAnOpenCircleWithAnArrowhead()
    {
        CursorArtwork.Image image = CursorArtwork.Rotation(1);
        Assert.Equal((32, 32, 16, 16), (image.Width, image.Height, image.HotspotX, image.HotspotY));
        Assert.Equal(0, image.Alpha(16, 16));
        Assert.True(image.Alpha(5, 15) > 200);
        Assert.True(image.Alpha(15, 5) > 200);
        Assert.Equal(0, image.Alpha(27, 15));
        Assert.True(image.Alpha(28, 22) > 200);
        Assert.Equal(40, CursorArtwork.Rotation(1.25).Width);
    }

    [Fact]
    public void OnlyFootprintsHaveFootprintArtwork()
    {
        Assert.Throws<ArgumentException>(() => CursorArtwork.Footprint(CanvasCursor.ForTool(Tool.Rectangle, 4), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CursorArtwork.Footprint(CanvasCursor.ForTool(Tool.Pen, 4), 0));
    }

    /// <summary>
    /// WIN-PAR-06: every cursor of the Mac UX reference has the same size and hot spot in points. The drawn ones (footprints
    /// and rotation) have nearly the same pixels at 2x; the Mac's system crosshair and I-beam are Windows' own.
    /// </summary>
    [Fact]
    public void CursorsMatchTheMacUXReference()
    {
        string folder = UXReference.Folder("cursors");
        using JsonDocument index = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "cursors.json")));
        int drawn = 0;
        foreach (JsonElement entry in index.RootElement.EnumerateArray())
        {
            string name = entry.GetProperty("name").GetString()!;
            int[] size = [.. entry.GetProperty("size").EnumerateArray().Select(value => value.GetInt32())];
            int[] hotSpot = [.. entry.GetProperty("hotSpot").EnumerateArray().Select(value => value.GetInt32())];
            CursorArtwork.Image ours;
            if (name == "rotate")
            {
                ours = CursorArtwork.Rotation(2);
            }
            else
            {
                string[] parts = name.Split('-');
                Tool tool = Tool.FromRawValue(parts[0]) ?? throw new InvalidDataException($"The reference cursor {name} names no tool.");
                CanvasCursor cursor = CanvasCursor.ForTool(tool, int.Parse(parts[1], CultureInfo.InvariantCulture));
                if (cursor.Kind != CanvasCursorKind.Footprint)
                {
                    Assert.Equal(tool == Tool.Text ? CanvasCursorKind.IBeam : CanvasCursorKind.Crosshair, cursor.Kind);
                    continue;
                }
                ours = CursorArtwork.Footprint(cursor, 2);
            }
            Assert.Equal((size[0] * 2, size[1] * 2, hotSpot[0] * 2, hotSpot[1] * 2), (ours.Width, ours.Height, ours.HotspotX, ours.HotspotY));

            var (width, height, rgba) = UXReference.ReadPng(Path.Combine(folder, $"{name}-light.png"));
            Assert.Equal((ours.Width, ours.Height), (width, height));
            double alphaDifference = 0, colorDifference = 0;
            int differentEdges = 0, opaque = 0;
            for (int pixel = 0; pixel < width * height; pixel++)
            {
                int offset = pixel * 4;
                int alpha = Math.Abs(rgba[offset + 3] - ours.Bgra[offset + 3]);
                alphaDifference += alpha;
                differentEdges += alpha > 32 ? 1 : 0;
                if (rgba[offset + 3] > 128 && ours.Bgra[offset + 3] > 128)
                {
                    opaque++;
                    colorDifference += Math.Max(Math.Abs(rgba[offset] - ours.Bgra[offset + 2]),
                        Math.Max(Math.Abs(rgba[offset + 1] - ours.Bgra[offset + 1]), Math.Abs(rgba[offset + 2] - ours.Bgra[offset])));
                }
            }
            // Antialiasing may differ along edges; measured at most 1.05 mean alpha, 0.3% of pixels and 5.7 mean color.
            Assert.True(alphaDifference / (width * height) <= 2, $"{name}: mean alpha difference {alphaDifference / (width * height):F2}");
            Assert.True(differentEdges <= width * height / 100, $"{name}: {differentEdges} pixels differ by more than 32 in alpha");
            Assert.True(opaque > 0 && colorDifference / opaque <= 8, $"{name}: mean color difference {colorDifference / Math.Max(1, opaque):F1}");
            drawn++;
        }
        Assert.Equal(10, drawn);
    }
}
