using CorePoint = Annotator.Core.Point;

namespace Annotator.App.Reference;

/// <summary>
/// The Mac recorders' <c>DemoHandMotion</c> (apps/macos/Tests/NativeAppTests/Support): a seeded generator of human-like pointer paths
/// and durations. The live takes replay the Mac's scripted pointer with the same seed and the same calls in the same order,
/// so the pointer follows the Mac recording's path. Points are in the Mac's screen coordinates, where y grows upward; the
/// caller flips y for the stage's top-down points, so each path bends to the same side as on the Mac.
/// </summary>
internal struct DemoHandMotion(ulong seed)
{
    private ulong _seed = seed;

    /// <summary>The Mac's next(): a 64-bit linear congruential step, as a number from 0 to 1.</summary>
    public double Next()
    {
        _seed = unchecked(_seed * 6364136223846793005UL + 1442695040888963407UL);
        return (_seed >> 11) / (double)(ulong.MaxValue >> 11);
    }

    /// <summary>The Mac's duration(_:): <paramref name="seconds"/> scaled by 0.82 to 1.18.</summary>
    public double Duration(double seconds) => seconds * (0.82 + Next() * 0.36);

    /// <summary>
    /// The Mac's path(from:to:samples:bend:): <paramref name="samples"/> + 1 points from <paramref name="start"/> to
    /// <paramref name="end"/> on a smootherstep with a sideways drift of up to <paramref name="bend"/> points.
    /// </summary>
    public CorePoint[] Path(CorePoint start, CorePoint end, int samples, double bend)
    {
        if (samples < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(samples), "A path needs at least two samples.");
        }
        var phase = Next() * Math.PI * 2;
        var offset = (Next() * 2 - 1) * bend;
        double dx = end.X - start.X, dy = end.Y - start.Y;
        var length = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
        double normalX = -dy / length, normalY = dx / length;
        var points = new CorePoint[samples + 1];
        for (var index = 0; index <= samples; index++)
        {
            if (index == 0)
            {
                points[index] = start;
                continue;
            }
            if (index == samples)
            {
                points[index] = end;
                continue;
            }
            var t = index / (double)samples;
            var eased = t * t * t * (10 + t * (-15 + 6 * t));
            var progress = eased * 0.85 + t * 0.15;
            var drift = Math.Sin(Math.PI * t) * (offset + Math.Sin(t * Math.PI * 3 + phase) * bend * 0.15);
            points[index] = new CorePoint(start.X + dx * progress + normalX * drift, start.Y + dy * progress + normalY * drift);
        }
        return points;
    }
}
