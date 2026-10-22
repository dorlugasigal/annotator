using Annotator.Core.Motion;

namespace Annotator.Workspace.Motion;

/// <summary>
/// Port of the Mac's <c>ParticleBurstView</c> math: the particle grid, the burst's eased progress and each particle's
/// path, which the Windows shader (<c>particles.hlsl</c>) evaluates the same way. Positions are in points with y down,
/// like the shader's drawable.
/// </summary>
public static class ParticleField
{
    /// <summary>Particles across every drawing display.</summary>
    public const int DrawingLimit = 96_000;

    /// <summary>Particles of the toolbar.</summary>
    public const int ToolbarLimit = 32_000;

    /// <summary>How far the fastest particle travels, in points.</summary>
    public const double Distance = 140;

    /// <summary>Padding around a dissolving snapshot for its particles, in points.</summary>
    public const double Padding = 170;

    public readonly record struct GridSize(int Columns, int Rows)
    {
        public int Count => Columns * Rows;
    }

    public readonly record struct Particle(double X, double Y, double Size, double Opacity, double U, double V);

    /// <summary>Swift <c>ParticleBurstView.grid(size:limit:)</c>: about 1.5 to 3.5-point grains, at most <paramref name="limit"/>.</summary>
    public static GridSize Grid(double width, double height, int limit)
    {
        if (!(width > 0) || !(height > 0) || limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "A particle grid needs a positive size and limit.");
        }
        var spacing = Math.Max(1.5, Math.Sqrt(width * height / limit));
        var columns = Math.Max(1, Math.Min(limit, (int)(width / spacing)));
        var rows = Math.Max(1, Math.Min(limit / columns, (int)(height / spacing)));
        return new GridSize(columns, rows);
    }

    /// <summary>Swift <c>currentProgress</c>: smoothstep from <paramref name="from"/> to <paramref name="to"/> over linear time 0...1.</summary>
    public static double Progress(double from, double to, double time)
    {
        var t = Math.Min(1, Math.Max(0, time));
        return from + (to - from) * (t * t * (3 - 2 * t));
    }

    /// <summary>A reversal resumes the same field from its current progress and lasts proportionally (at least 35%).</summary>
    public static double Duration(double duration, double from, double to, bool reversing) =>
        reversing ? duration * Math.Max(MotionTiming.MinimumReversalShare, Math.Abs(to - from)) : duration;

    /// <summary>The shader's integer hash to 0...1.</summary>
    public static float RandomValue(uint value)
    {
        unchecked
        {
            value ^= value >> 16;
            value *= 0x7feb352d;
            value ^= value >> 15;
            value *= 0x846ca68b;
            value ^= value >> 16;
        }
        return (value & 0xffffff) / 16777216f;
    }

    /// <summary>
    /// 0 on the image edge that faces the sweep direction, 1 on the far edge. <paramref name="sweepAngle"/> is the
    /// direction toward the toolbar in the Mac's y-up space (<c>atan2(dy, dx)</c>); <paramref name="v"/> grows downward.
    /// </summary>
    public static double SweepAlong(double u, double v, double sweepAngle) =>
        Math.Clamp(0.5 - ((u - 0.5) * Math.Cos(sweepAngle) - (v - 0.5) * Math.Sin(sweepAngle)), 0, 1);

    public static double LocalProgress(double progress, double along) => Math.Clamp((progress - 0.25 * along) / 0.75, 0, 1);

    /// <summary>How visible the image itself still is at local progress <paramref name="local"/>.</summary>
    public static double ImageOpacity(double local) => 1 - SmoothStep(0, 0.18, local);

    /// <summary>
    /// Particle <paramref name="id"/> of a burst over <paramref name="width"/> by <paramref name="height"/> points at
    /// (<paramref name="originX"/>, <paramref name="originY"/>): where it is, its diameter in pixels and its opacity.
    /// </summary>
    public static Particle At(uint id, GridSize grid, double originX, double originY, double width, double height,
                              double progress, double sweepAngle, double pixelScale)
    {
        double u = (id % (uint)grid.Columns + 0.5) / grid.Columns;
        double v = (id / (uint)grid.Columns + 0.5) / grid.Rows;
        var p = LocalProgress(progress, SweepAlong(u, v, sweepAngle));
        var angle = RandomValue(id + 1) * 6.2831853;
        double dirX = Math.Cos(angle), dirY = Math.Sin(angle);
        var speed = 0.25 + 0.75 * RandomValue(id + 9209);
        // Drag: a quick burst that slows down, like a puff.
        var drag = (1 - Math.Exp(-3.5 * p)) / (1 - Math.Exp(-3.5));
        var x = dirX * Distance * speed * drag - dirY * Math.Sin(p * 3.1415927) * 10;
        var y = dirY * Distance * speed * drag + dirX * Math.Sin(p * 3.1415927) * 10 + p * p * 12;
        var size = (1.5 + 2.0 * RandomValue(id + 7919)) * (1 - 0.65 * p) * pixelScale;
        var opacity = SmoothStep(0, 0.18, p) * (1 - SmoothStep(0.35, 1, p));
        return new Particle(originX + u * width + x, originY + v * height + y, size, opacity, u, v);
    }

    public static double SmoothStep(double edge0, double edge1, double x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
