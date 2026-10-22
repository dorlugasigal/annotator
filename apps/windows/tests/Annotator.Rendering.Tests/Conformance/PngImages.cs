using System.Buffers.Binary;
using System.IO.Compression;
using Annotator.Rendering.Imaging;

namespace Annotator.Rendering.Tests.Conformance;

/// <summary>
/// A small managed PNG reader for the corpus: 8-bit RGBA or RGB, not interlaced. It reads the stored bytes exactly,
/// without color management, so the comparison sees what the Mac wrote.
/// </summary>
internal static class PngImages
{
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>Straight (not premultiplied) RGBA rows, top row first.</summary>
    public static (int Width, int Height, byte[] Rgba) DecodeStraight(byte[] png)
    {
        if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(Signature))
        {
            throw new InvalidDataException("Not a PNG file.");
        }
        int width = 0, height = 0, channels = 0;
        using var compressed = new MemoryStream();
        var offset = 8;
        while (offset + 8 <= png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            var data = png.AsSpan(offset + 8, length);
            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(data);
                    height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                    var depth = data[8];
                    var colorType = data[9];
                    var interlace = data[12];
                    if (depth != 8 || interlace != 0 || colorType is not (2 or 6))
                    {
                        throw new InvalidDataException($"Unsupported PNG: depth {depth}, color type {colorType}, interlace {interlace}.");
                    }
                    channels = colorType == 6 ? 4 : 3;
                    break;
                case "IDAT":
                    compressed.Write(data);
                    break;
            }
            offset += 12 + length;
            if (type == "IEND")
            {
                break;
            }
        }
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("The PNG has no image header.");
        }
        var rowBytes = width * channels;
        var filtered = new byte[(rowBytes + 1) * height];
        compressed.Position = 0;
        using (var inflater = new ZLibStream(compressed, CompressionMode.Decompress))
        {
            inflater.ReadExactly(filtered);
        }
        var raw = new byte[rowBytes * height];
        for (var y = 0; y < height; y++)
        {
            var filter = filtered[y * (rowBytes + 1)];
            var line = filtered.AsSpan(y * (rowBytes + 1) + 1, rowBytes);
            var row = raw.AsSpan(y * rowBytes, rowBytes);
            var previous = y == 0 ? Span<byte>.Empty : raw.AsSpan((y - 1) * rowBytes, rowBytes);
            for (var x = 0; x < rowBytes; x++)
            {
                int left = x >= channels ? row[x - channels] : 0;
                int up = y > 0 ? previous[x] : 0;
                int corner = x >= channels && y > 0 ? previous[x - channels] : 0;
                row[x] = (byte)(line[x] + filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, corner),
                    _ => throw new InvalidDataException($"Unknown PNG row filter {filter}."),
                });
            }
        }
        if (channels == 4)
        {
            return (width, height, raw);
        }
        var rgba = new byte[width * height * 4];
        for (var index = 0; index < width * height; index++)
        {
            rgba[index * 4] = raw[index * 3];
            rgba[index * 4 + 1] = raw[index * 3 + 1];
            rgba[index * 4 + 2] = raw[index * 3 + 2];
            rgba[index * 4 + 3] = 255;
        }
        return (width, height, rgba);
    }

    /// <summary>
    /// Premultiplied BGRA, as Direct2D and Core Graphics bitmaps hold it. Each color is the straight value times alpha
    /// / 255 rounded to nearest, which gives back a premultiplied value exactly after an encoder that divided and
    /// rounded to nearest.
    /// </summary>
    public static RasterImage DecodePremultiplied(byte[] png)
    {
        var (width, height, rgba) = DecodeStraight(png);
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < width * height; index++)
        {
            int alpha = rgba[index * 4 + 3];
            pixels[index * 4] = Premultiply(rgba[index * 4 + 2], alpha);
            pixels[index * 4 + 1] = Premultiply(rgba[index * 4 + 1], alpha);
            pixels[index * 4 + 2] = Premultiply(rgba[index * 4], alpha);
            pixels[index * 4 + 3] = (byte)alpha;
        }
        return new RasterImage(width, height, width * 4, pixels);
    }

    private static byte Premultiply(int value, int alpha) => (byte)((2 * value * alpha + 255) / 510);

    private static int Paeth(int left, int up, int corner)
    {
        var estimate = left + up - corner;
        int toLeft = Math.Abs(estimate - left), toUp = Math.Abs(estimate - up), toCorner = Math.Abs(estimate - corner);
        return toLeft <= toUp && toLeft <= toCorner ? left : toUp <= toCorner ? up : corner;
    }
}
