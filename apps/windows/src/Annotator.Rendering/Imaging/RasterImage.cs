namespace Annotator.Rendering.Imaging;

/// <summary>
/// A CPU image in 8-bit sRGB with premultiplied alpha, stored as BGRA rows (Direct2D's and WIC's native order),
/// like a <c>CGImage</c> with premultiplied alpha. Immutable after creation.
/// </summary>
public sealed class RasterImage
{
    private readonly byte[] pixels;

    public RasterImage(int width, int height, int stride, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(pixels);
        if (stride < width * 4 || pixels.Length < (long)stride * height)
        {
            throw new ArgumentException("The pixel buffer is smaller than the image.", nameof(pixels));
        }
        Width = width;
        Height = height;
        Stride = stride;
        this.pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Bytes per row (<c>bytesPerRow</c>).</summary>
    public int Stride { get; }

    /// <summary>BGRA rows with premultiplied alpha, top row first.</summary>
    public ReadOnlySpan<byte> Pixels => pixels;

    /// <summary>The premultiplied color at a pixel, in RGBA order.</summary>
    public (byte Red, byte Green, byte Blue, byte Alpha) this[int x, int y]
    {
        get
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            {
                throw new ArgumentOutOfRangeException(nameof(x), $"({x}, {y}) is outside the {Width} x {Height} image.");
            }
            var offset = y * Stride + x * 4;
            return (pixels[offset + 2], pixels[offset + 1], pixels[offset], pixels[offset + 3]);
        }
    }

    /// <summary>The alpha channel, row by row, without padding.</summary>
    public byte[] AlphaChannel()
    {
        var alpha = new byte[Width * Height];
        for (var y = 0; y < Height; y++)
        {
            var row = pixels.AsSpan(y * Stride, Width * 4);
            for (var x = 0; x < Width; x++)
            {
                alpha[y * Width + x] = row[x * 4 + 3];
            }
        }
        return alpha;
    }

    internal byte[] Buffer => pixels;
}
