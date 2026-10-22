using System.Buffers.Binary;
using System.IO.Compression;

namespace Annotator.Workspace.Artwork;

/// <summary>
/// Writes the app's icon files without a platform imaging library: PNG for MSIX logos (8-bit RGBA with an sRGB chunk, like
/// the Mac's AppIcon PNGs) and ICO for the executable, with 32-bit bitmaps for small sizes and PNG for the larger ones.
/// Output is deterministic for one runtime; tests compare decoded pixels.
/// </summary>
public static class IconFiles
{
    /// <summary>Sizes below this go into an ICO as 32-bit bitmaps; larger ones as PNG, which Windows reads since Vista.</summary>
    public const int IcoPngThreshold = 64;

    private static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Encodes straight-alpha RGBA rows (top row first) as a PNG.</summary>
    public static byte[] EncodePng(ReadOnlySpan<byte> rgba, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (rgba.Length != width * height * 4)
        {
            throw new ArgumentException("The pixels do not match the size.", nameof(rgba));
        }
        using var output = new MemoryStream();
        output.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;  // Bit depth.
        header[9] = 6;  // Color type: RGBA.
        header[10] = 0; // Deflate.
        header[11] = 0; // Adaptive filtering.
        header[12] = 0; // No interlace.
        WriteChunk(output, "IHDR", header);
        // sRGB with the perceptual rendering intent, as the Mac's AppIcon PNGs are tagged.
        WriteChunk(output, "sRGB", [0]);
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                int stride = width * 4;
                byte[] previous = new byte[stride];
                byte[] filtered = new byte[stride + 1];
                for (int row = 0; row < height; row++)
                {
                    ReadOnlySpan<byte> line = rgba.Slice(row * stride, stride);
                    // Filter type 2 (Up): small files for flat icons, simple to decode.
                    filtered[0] = 2;
                    for (int index = 0; index < stride; index++)
                    {
                        filtered[index + 1] = (byte)(line[index] - previous[index]);
                    }
                    zlib.Write(filtered);
                    line.CopyTo(previous);
                }
            }
            WriteChunk(output, "IDAT", compressed.ToArray());
        }
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>
    /// Writes an ICO with one image per size, in the given order: sizes below <see cref="IcoPngThreshold"/> as 32-bit
    /// bitmaps with an AND mask, the others as PNG.
    /// </summary>
    /// <param name="render">Straight-alpha RGBA rows, top row first, for a square of the given size.</param>
    public static byte[] EncodeIco(IReadOnlyList<int> sizes, Func<int, byte[]> render)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        ArgumentNullException.ThrowIfNull(render);
        if (sizes.Count == 0 || sizes.Any(size => size is < 1 or > 256) || sizes.Distinct().Count() != sizes.Count)
        {
            throw new ArgumentException("An icon needs distinct sizes from 1 to 256 pixels.", nameof(sizes));
        }
        var images = new List<byte[]>(sizes.Count);
        foreach (int size in sizes)
        {
            byte[] rgba = render(size);
            images.Add(size < IcoPngThreshold ? Bitmap(rgba, size) : EncodePng(rgba, size, size));
        }
        using var output = new MemoryStream();
        Span<byte> directory = stackalloc byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(directory, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(directory[2..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(directory[4..], (ushort)sizes.Count);
        output.Write(directory);
        int offset = 6 + 16 * sizes.Count;
        Span<byte> entry = stackalloc byte[16];
        for (int index = 0; index < sizes.Count; index++)
        {
            int size = sizes[index];
            entry.Clear();
            entry[0] = (byte)(size == 256 ? 0 : size);
            entry[1] = (byte)(size == 256 ? 0 : size);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], 32);
            BinaryPrimitives.WriteInt32LittleEndian(entry[8..], images[index].Length);
            BinaryPrimitives.WriteInt32LittleEndian(entry[12..], offset);
            output.Write(entry);
            offset += images[index].Length;
        }
        foreach (byte[] image in images)
        {
            output.Write(image);
        }
        return output.ToArray();
    }

    /// <summary>A 32-bit BGRA DIB, bottom row first, with the AND mask that ICO bitmaps carry after it.</summary>
    private static byte[] Bitmap(byte[] rgba, int size)
    {
        int maskStride = (size + 31) / 32 * 4;
        var bytes = new byte[40 + size * size * 4 + maskStride * size];
        Span<byte> span = bytes;
        BinaryPrimitives.WriteInt32LittleEndian(span, 40);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], size);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], size * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(span[20..], size * size * 4 + maskStride * size);
        int pixels = 40;
        int mask = 40 + size * size * 4;
        for (int row = 0; row < size; row++)
        {
            int source = (size - 1 - row) * size * 4;
            for (int column = 0; column < size; column++)
            {
                int from = source + column * 4;
                int to = pixels + (row * size + column) * 4;
                bytes[to] = rgba[from + 2];
                bytes[to + 1] = rgba[from + 1];
                bytes[to + 2] = rgba[from];
                bytes[to + 3] = rgba[from + 3];
                if (rgba[from + 3] == 0)
                {
                    bytes[mask + row * maskStride + column / 8] |= (byte)(0x80 >> (column % 8));
                }
            }
        }
        return bytes;
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        Span<byte> typeBytes = stackalloc byte[4];
        for (int index = 0; index < 4; index++)
        {
            typeBytes[index] = (byte)type[index];
        }
        output.Write(typeBytes);
        output.Write(data);
        uint crc = Crc32(Crc32(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        Span<byte> check = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(check, crc);
        output.Write(check);
    }

    private static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }
        return crc;
    }
}
