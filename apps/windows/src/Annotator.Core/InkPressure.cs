// Port of apps/macos/Sources/AnnotationCore/InkPressure.swift.
namespace Annotator.Core;

/// <summary>
/// Simulated pen pressure for a mouse, touchpad or touch, so Ink pen strokes look drawn with ink: faster movement
/// draws a thinner line, slower movement a thicker one, and both ends taper. Feed every sample of a stroke to
/// <see cref="Next"/> in order; when the stroke ends, <see cref="TaperEnd"/> thins its last part. Each value depends
/// only on earlier samples, so a stroke drawn so far never changes while it grows. Matches the Mac number for number.
/// </summary>
public struct InkPressure
{
    /// <summary>Pressure at full speed, before tapering.</summary>
    public const double Thinnest = 0.35;
    /// <summary>Speed in points per second at which the line is thinnest.</summary>
    public const double FastSpeed = 900.0;
    /// <summary>Shortest time in seconds over which speed is measured; samples closer together are added up first.</summary>
    public const double Window = 0.008;
    /// <summary>About how long in seconds the width takes to follow a change of speed.</summary>
    public const double Settle = 0.06;
    /// <summary>Longest pause in seconds counted between two samples.</summary>
    public const double LongestStep = 0.1;
    /// <summary>Pressure at the very tip of a tapered end.</summary>
    public const double Tip = 0.35;

    private Point _last;
    private bool _hasLast;
    private double _lastTime;
    private double _pendingDistance;
    private double _pendingTime;
    private double _level;
    private double _travelled;

    /// <summary><paramref name="width"/> is the stroke width; wider strokes taper over a longer distance.</summary>
    public InkPressure(double width)
    {
        TaperLength = SwiftMath.Max(12, (double.IsFinite(width) ? width : 0) * 4);
        _level = 1;
    }

    /// <summary>Path length in points over which each end tapers.</summary>
    public double TaperLength { get; }

    /// <summary>Pressure for the next sample of the stroke. <paramref name="time"/> is in seconds and only differences matter.</summary>
    public double Next(Point point, double time)
    {
        if (!_hasLast)
        {
            _hasLast = true;
            _last = point;
            _lastTime = time;
            return Clamped(_level * Taper(0));
        }
        var distance = _last.Distance(point);
        var step = time - _lastTime;
        _travelled += distance;
        _pendingDistance += distance;
        _pendingTime += double.IsFinite(step) ? SwiftMath.Min(SwiftMath.Max(step, 0), LongestStep) : 0;
        _last = point;
        _lastTime = time;
        if (_pendingTime >= Window)
        {
            var speed = _pendingDistance / _pendingTime;
            var target = 1 - (1 - Thinnest) * SwiftMath.Min(1, speed / FastSpeed);
            _level += (target - _level) * SwiftMath.Min(1, _pendingTime / Settle);
            _pendingDistance = 0;
            _pendingTime = 0;
        }
        return Clamped(_level * Taper(_travelled));
    }

    /// <summary>
    /// The pressures with the last <see cref="TaperLength"/> points of path thinned towards the end, as a pen lifts off.
    /// Returns <paramref name="pressures"/> unchanged when it does not have one value per point or the stroke has one point.
    /// </summary>
    public static ValueList<double> TaperEnd(ValueList<Point> points, ValueList<double> pressures, double width)
    {
        if (points.Count <= 1 || pressures.Count != points.Count)
        {
            return pressures;
        }
        var shape = new InkPressure(width);
        var result = pressures.AsSpan().ToArray();
        var remaining = 0.0;
        var index = points.Count - 1;
        while (index > 0 && remaining < shape.TaperLength)
        {
            result[index] = Clamped(result[index] * shape.Taper(remaining));
            remaining += points[index].Distance(points[index - 1]);
            index -= 1;
        }
        return ValueList.Create<double>(result);
    }

    private readonly double Taper(double distance)
    {
        var progress = SwiftMath.Min(1, distance / TaperLength);
        return Tip + (1 - Tip) * progress * (2 - progress);
    }

    private static double Clamped(double pressure) =>
        double.IsFinite(pressure) ? SwiftMath.Min(1, SwiftMath.Max(0, pressure)) : 1;
}
