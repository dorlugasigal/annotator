using System.Buffers.Binary;
using System.IO.Compression;

namespace Annotator.Workspace.Tests.Artwork;

/// <summary>
/// Reads the PNG files the icon tests compare: 8-bit RGB or RGBA, not interlaced, every filter type. Returns straight-alpha
/// RGBA rows, top row first.
/// </summary>
internal static class PngDecoder
{
    public static (int Width, int Height, byte[] Rgba) Decode(byte[] file)
    {
        ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        if (!file.AsSpan(0, 8).SequenceEqual(signature))
        {
            throw new InvalidDataException("Not a PNG file.");
        }
        int width = 0, height = 0, channels = 0;
        using var data = new MemoryStream();
        int offset = 8;
        while (offset < file.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(offset));
            string type = System.Text.Encoding.ASCII.GetString(file, offset + 4, 4);
            ReadOnlySpan<byte> chunk = file.AsSpan(offset + 8, length);
            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(chunk);
                    height = BinaryPrimitives.ReadInt32BigEndian(chunk[4..]);
                    if (chunk[8] != 8 || chunk[12] != 0 || (chunk[9] != 6 && chunk[9] != 2))
                    {
                        throw new InvalidDataException("Only 8-bit RGB or RGBA PNG files without interlacing are read here.");
                    }
                    channels = chunk[9] == 6 ? 4 : 3;
                    break;
                case "IDAT":
                    data.Write(chunk);
                    break;
            }
            offset += 12 + length;
        }
        data.Position = 0;
        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(data, CompressionMode.Decompress))
        {
            zlib.CopyTo(inflated);
        }
        byte[] raw = inflated.ToArray();
        int stride = width * channels;
        var current = new byte[stride];
        var previous = new byte[stride];
        var rgba = new byte[width * height * 4];
        for (int row = 0; row < height; row++)
        {
            int start = row * (stride + 1);
            byte filter = raw[start];
            for (int index = 0; index < stride; index++)
            {
                int left = index >= channels ? current[index - channels] : 0;
                int up = previous[index];
                int upLeft = index >= channels ? previous[index - channels] : 0;
                int value = raw[start + 1 + index];
                current[index] = (byte)(filter switch
                {
                    0 => value,
                    1 => value + left,
                    2 => value + up,
                    3 => value + (left + up) / 2,
                    4 => value + Paeth(left, up, upLeft),
                    _ => throw new InvalidDataException($"Unknown PNG filter {filter}."),
                });
            }
            for (int column = 0; column < width; column++)
            {
                int to = (row * width + column) * 4;
                rgba[to] = current[column * channels];
                rgba[to + 1] = current[column * channels + 1];
                rgba[to + 2] = current[column * channels + 2];
                rgba[to + 3] = channels == 4 ? current[column * channels + 3] : (byte)255;
            }
            (previous, current) = (current, previous);
        }
        return (width, height, rgba);
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        int estimate = left + up - upLeft;
        int toLeft = Math.Abs(estimate - left), toUp = Math.Abs(estimate - up), toUpLeft = Math.Abs(estimate - upLeft);
        return toLeft <= toUp && toLeft <= toUpLeft ? left : toUp <= toUpLeft ? up : upLeft;
    }
}
