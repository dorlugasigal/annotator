using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Export;
using Annotator.Rendering.Imaging;
using Annotator.Rendering.Text;

// Tests share one WARP device and create windows; run them one at a time.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace Annotator.Rendering.Tests;

/// <summary>Process-wide WARP devices and text measurer: WARP renders the same pixels on every machine and driver.</summary>
internal static class Warp
{
    private static readonly Lazy<GraphicsDevices> SharedDevices = new(() => GraphicsDevices.Create(new GraphicsDeviceOptions { UseWarp = true }));
    private static readonly Lazy<DirectWriteTextMeasurer> SharedMeasurer = new(() => new DirectWriteTextMeasurer());

    public static GraphicsDevices Devices => SharedDevices.Value;

    public static DirectWriteTextMeasurer Measurer => SharedMeasurer.Value;

    public static PngExporter Exporter() => new(Devices, Measurer, FixedSystemColors.Light);

    public static SceneBuilder Builder() => new(Measurer);
}

/// <summary>Builds snapshots and renders them on WARP.</summary>
internal static class Draw
{
    public static RasterImage Elements(int width, int height, params Element[] elements)
    {
        using var exporter = Warp.Exporter();
        return exporter.Image(ElementList.Create(elements), new Size(width, height));
    }

    public static RasterImage Items(int width, int height, params DisplayItem[] items) => Items(width, height, 1, items);

    /// <summary>Renders hand-written display items as one list at <paramref name="scale"/> pixels per point.</summary>
    public static RasterImage Items(int width, int height, double scale, params DisplayItem[] items)
    {
        var list = TestLists.Create(items);
        using var exporter = Warp.Exporter();
        return exporter.Render(TestLists.Scene(list), new Size(width / scale, height / scale), scale, includeSelection: false);
    }
}

/// <summary>Display lists built directly from items, through the internal constructor Core exposes to the scene builder.</summary>
internal static class TestLists
{
    public static DisplayList Create(params DisplayItem[] items) => DisplayListFactory.Create(Guid.NewGuid(), items);

    public static SceneSnapshot Scene(params DisplayList[] lists) => DisplayListFactory.Scene(lists, DisplayList.Empty);
}

internal static class Pixels
{
    public static (byte Red, byte Green, byte Blue, byte Alpha) At(RasterImage image, int x, int y) => image[x, y];

    public static byte Alpha(RasterImage image, int x, int y) => image[x, y].Alpha;

    /// <summary>Whether every pixel outside <paramref name="area"/> is fully transparent.</summary>
    public static bool TransparentOutside(RasterImage image, int left, int top, int right, int bottom)
    {
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                if ((x < left || x >= right || y < top || y >= bottom) && image[x, y].Alpha != 0)
                {
                    return false;
                }
            }
        }
        return true;
    }

    public static int Painted(RasterImage image)
    {
        var count = 0;
        foreach (var alpha in image.AlphaChannel())
        {
            if (alpha > 0)
            {
                count++;
            }
        }
        return count;
    }

    public static long AlphaSum(RasterImage image)
    {
        long sum = 0;
        foreach (var alpha in image.AlphaChannel())
        {
            sum += alpha;
        }
        return sum;
    }

    public static bool SamePixels(RasterImage a, RasterImage b) =>
        a.Width == b.Width && a.Height == b.Height && a.Pixels.SequenceEqual(b.Pixels);
}
