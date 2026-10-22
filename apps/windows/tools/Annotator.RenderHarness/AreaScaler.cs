namespace Annotator.RenderHarness;

/// <summary>
/// Resamples BGRA frames by area averaging (each output pixel is the mean of the source area it covers, with partial
/// pixels weighted by their share), as ffmpeg's <c>flags=area</c> scaling did for the earlier comparisons. Reuses its
/// buffers, so a scaler per frame size scales a whole video without allocating.
/// </summary>
internal sealed class AreaScaler
{
    private readonly (int Start, float[] Weights)[] _columns;
    private readonly (int Start, float[] Weights)[] _rows;
    private readonly float[] _temporary;

    public AreaScaler(int sourceWidth, int sourceHeight, int width, int height)
    {
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        Width = width;
        Height = height;
        _columns = Taps(sourceWidth, width);
        _rows = Taps(sourceHeight, height);
        _temporary = new float[width * sourceHeight * 3];
    }

    public int SourceWidth { get; }

    public int SourceHeight { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Scales <paramref name="source"/> (rows <paramref name="stride"/> bytes apart) into <paramref name="destination"/> (Width x Height x 4, alpha 255).</summary>
    public void Scale(ReadOnlySpan<byte> source, int stride, Span<byte> destination)
    {
        if (source.Length < (long)stride * (SourceHeight - 1) + SourceWidth * 4 || destination.Length < Width * Height * 4)
        {
            throw new ArgumentException("The frames do not have the scaler's sizes.");
        }
        if (SourceWidth == Width && SourceHeight == Height)
        {
            for (var y = 0; y < Height; y++)
            {
                source.Slice(y * stride, Width * 4).CopyTo(destination.Slice(y * Width * 4, Width * 4));
            }
            return;
        }
        for (var y = 0; y < SourceHeight; y++)
        {
            var row = source.Slice(y * stride, SourceWidth * 4);
            var target = y * Width * 3;
            for (var x = 0; x < Width; x++)
            {
                var (start, weights) = _columns[x];
                float blue = 0, green = 0, red = 0;
                for (var tap = 0; tap < weights.Length; tap++)
                {
                    var offset = (start + tap) * 4;
                    var weight = weights[tap];
                    blue += row[offset] * weight;
                    green += row[offset + 1] * weight;
                    red += row[offset + 2] * weight;
                }
                _temporary[target + x * 3] = blue;
                _temporary[target + x * 3 + 1] = green;
                _temporary[target + x * 3 + 2] = red;
            }
        }
        for (var y = 0; y < Height; y++)
        {
            var (start, weights) = _rows[y];
            var target = destination.Slice(y * Width * 4, Width * 4);
            for (var x = 0; x < Width; x++)
            {
                float blue = 0, green = 0, red = 0;
                for (var tap = 0; tap < weights.Length; tap++)
                {
                    var offset = ((start + tap) * Width + x) * 3;
                    var weight = weights[tap];
                    blue += _temporary[offset] * weight;
                    green += _temporary[offset + 1] * weight;
                    red += _temporary[offset + 2] * weight;
                }
                target[x * 4] = (byte)Math.Clamp(blue + 0.5f, 0, 255);
                target[x * 4 + 1] = (byte)Math.Clamp(green + 0.5f, 0, 255);
                target[x * 4 + 2] = (byte)Math.Clamp(red + 0.5f, 0, 255);
                target[x * 4 + 3] = 255;
            }
        }
    }

    /// <summary>For each output index, the first source index and the weights of the source pixels it covers (they sum to 1).</summary>
    private static (int Start, float[] Weights)[] Taps(int source, int output)
    {
        var taps = new (int, float[])[output];
        var step = source / (double)output;
        for (var index = 0; index < output; index++)
        {
            double from = index * step, to = (index + 1) * step;
            var first = Math.Min(source - 1, (int)Math.Floor(from));
            var last = Math.Max(first, Math.Min(source - 1, (int)Math.Ceiling(to) - 1));
            var weights = new float[last - first + 1];
            for (var pixel = first; pixel <= last; pixel++)
            {
                weights[pixel - first] = (float)((Math.Min(pixel + 1, to) - Math.Max(pixel, from)) / step);
            }
            taps[index] = (first, weights);
        }
        return taps;
    }
}
