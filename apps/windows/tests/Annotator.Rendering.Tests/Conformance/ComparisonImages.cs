using Annotator.Rendering.Imaging;

namespace Annotator.Rendering.Tests.Conformance;

/// <summary>Review pictures for a comparison: difference heat maps, class maps and side-by-side sheets.</summary>
internal static class ComparisonImages
{
    /// <summary>Gap between panels, in sheet pixels.</summary>
    private const int Gap = 8;

    /// <summary>
    /// The largest channel difference at each pixel: black for none, blue for 1, cyan for 2, green for 3-4, yellow for
    /// 5-16, orange for 17-64 and red above. Ignored pixels are dark gray.
    /// </summary>
    public static RasterImage Heat(ComparisonResult result) => Map(result, index =>
    {
        if (result.Classes[index] == PixelClass.Ignored)
        {
            return (40, 40, 40);
        }
        return result.ChannelDifference[index] switch
        {
            0 => (0, 0, 0),
            1 => (0, 0, 200),
            2 => (0, 160, 220),
            <= 4 => (0, 190, 0),
            <= 16 => (240, 220, 0),
            <= 64 => (255, 130, 0),
            _ => (255, 0, 0),
        };
    });

    /// <summary>
    /// What each pixel was judged as: flat light gray, edge blue-gray, other pale green, ignored dark gray; failures
    /// red (flat), magenta (edge) and orange (other above the CIEDE2000 limit).
    /// </summary>
    public static RasterImage Classes(ComparisonResult result) => Map(result, index => (result.Classes[index], result.Failed[index]) switch
    {
        (PixelClass.Flat, false) => (228, 228, 228),
        (PixelClass.Flat, true) => (230, 0, 0),
        (PixelClass.Edge, false) => (150, 165, 190),
        (PixelClass.Edge, true) => (230, 0, 230),
        (PixelClass.Other, false) => (190, 230, 190),
        (PixelClass.Other, true) => (255, 140, 0),
        _ => (40, 40, 40),
    });

    /// <summary>
    /// The Mac reference, the Windows image, the heat map and the class map side by side, each magnified
    /// <paramref name="magnify"/> times without smoothing. Transparent pixels show a checkerboard.
    /// </summary>
    public static RasterImage Sheet(RasterImage reference, RasterImage candidate, ComparisonResult result, int magnify)
    {
        var panels = new[] { reference, candidate, Heat(result), Classes(result) };
        int panelWidth = reference.Width * magnify, panelHeight = reference.Height * magnify;
        var width = panels.Length * panelWidth + (panels.Length + 1) * Gap;
        var height = panelHeight + 2 * Gap;
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < width * height; index++)
        {
            pixels[index * 4] = pixels[index * 4 + 1] = pixels[index * 4 + 2] = 90;
            pixels[index * 4 + 3] = 255;
        }
        for (var panel = 0; panel < panels.Length; panel++)
        {
            var image = panels[panel];
            var left = Gap + panel * (panelWidth + Gap);
            for (var y = 0; y < panelHeight; y++)
            {
                for (var x = 0; x < panelWidth; x++)
                {
                    var (red, green, blue, alpha) = image[x / magnify, y / magnify];
                    // A checkerboard of 8 x 8 sheet pixels under transparent areas.
                    var backdrop = ((x / 8) + (y / 8)) % 2 == 0 ? 204 : 160;
                    var transparent = 255 - alpha;
                    var offset = ((Gap + y) * width + left + x) * 4;
                    pixels[offset] = (byte)(blue + (backdrop * transparent + 127) / 255);
                    pixels[offset + 1] = (byte)(green + (backdrop * transparent + 127) / 255);
                    pixels[offset + 2] = (byte)(red + (backdrop * transparent + 127) / 255);
                    pixels[offset + 3] = 255;
                }
            }
        }
        return new RasterImage(width, height, width * 4, pixels);
    }

    public static void Save(RasterImage image, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, RasterAsset.EncodePng(image));
    }

    private static RasterImage Map(ComparisonResult result, Func<int, (int Red, int Green, int Blue)> color)
    {
        var pixels = new byte[result.Width * result.Height * 4];
        for (var index = 0; index < result.Width * result.Height; index++)
        {
            var (red, green, blue) = color(index);
            pixels[index * 4] = (byte)blue;
            pixels[index * 4 + 1] = (byte)green;
            pixels[index * 4 + 2] = (byte)red;
            pixels[index * 4 + 3] = 255;
        }
        return new RasterImage(result.Width, result.Height, result.Width * 4, pixels);
    }
}
