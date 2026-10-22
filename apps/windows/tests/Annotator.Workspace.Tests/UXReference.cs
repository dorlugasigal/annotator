using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Annotator.Workspace.Tests;

/// <summary>
/// The Mac UX reference in <c>reference/ux-reference/macos</c> (WIN-PAR-06): finds a surface's folder above the test output and
/// reads its PNG images, which are 8-bit RGBA without interlacing.
/// </summary>
internal static class UXReference
{
    /// <summary>The folder of one surface, such as <c>cursors</c>.</summary>
    public static string Folder(string surface)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "reference", "ux-reference", "macos", surface);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new DirectoryNotFoundException($"reference/ux-reference/macos/{surface} is not above {AppContext.BaseDirectory}.");
    }

    /// <summary>Decodes an 8-bit RGBA PNG without interlacing into straight-alpha RGBA rows.</summary>
    public static (int Width, int Height, byte[] Rgba) ReadPng(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        int width = 0, height = 0;
        using var compressed = new MemoryStream();
        for (int offset = 8; offset < file.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(offset));
            string type = Encoding.ASCII.GetString(file, offset + 4, 4);
            ReadOnlySpan<byte> data = file.AsSpan(offset + 8, length);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                if (data[8] != 8 || data[9] != 6 || data[12] != 0)
                {
                    throw new InvalidDataException($"{path} is not 8-bit RGBA without interlacing.");
                }
            }
            else if (type == "IDAT")
            {
                compressed.Write(data);
            }
            offset += 12 + length;
        }
        compressed.Position = 0;
        using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
        int stride = width * 4;
        byte[] raw = new byte[(stride + 1) * height];
        inflater.ReadExactly(raw);
        byte[] rgba = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            byte filter = raw[y * (stride + 1)];
            for (int x = 0; x < stride; x++)
            {
                int left = x >= 4 ? rgba[y * stride + x - 4] : 0;
                int up = y > 0 ? rgba[(y - 1) * stride + x] : 0;
                int upLeft = x >= 4 && y > 0 ? rgba[(y - 1) * stride + x - 4] : 0;
                int value = raw[y * (stride + 1) + 1 + x];
                rgba[y * stride + x] = (byte)(filter switch
                {
                    0 => value,
                    1 => value + left,
                    2 => value + up,
                    3 => value + (left + up) / 2,
                    4 => value + Paeth(left, up, upLeft),
                    _ => throw new InvalidDataException($"{path} uses the unknown PNG filter {filter}."),
                });
            }
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
