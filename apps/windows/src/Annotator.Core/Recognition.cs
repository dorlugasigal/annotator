// Port of apps/macos/Sources/AnnotationCore/Recognition.swift.
namespace Annotator.Core;

/// <summary>Smart Draw: turns a pen stroke into a rectangle, diamond, ellipse or arrow.</summary>
public static class ShapeRecognition
{
    private static readonly Tool[] LoopTools = [Tool.Rectangle, Tool.Diamond, Tool.Ellipse];

    /// <summary>At most 256 evenly spaced points of the stroke, keeping both ends, the identity and the seed.</summary>
    public static Element PreviewInput(in Element element)
    {
        var count = SwiftMath.Min(256, element.Points.Count);
        ValueList<Point> points;
        if (count < 2)
        {
            points = element.Points;
        }
        else
        {
            var picked = new Point[count];
            for (var index = 0; index < count; index++)
            {
                picked[index] = element.Points[(int)((long)index * (element.Points.Count - 1) / (count - 1))];
            }
            points = ValueList.Create<Point>(picked);
        }
        var preview = new Element(element.Tool, points: points, style: element.Style, id: element.Id);
        preview.Seed = element.Seed;
        return preview;
    }

    public static Element? Recognize(in Element element)
    {
        if (element.Tool != Tool.Pen || element.Points.Count < 5)
        {
            return null;
        }
        var bounded = PreviewInput(element).Points.ToArray();
        foreach (var point in bounded)
        {
            if (!point.IsFinite)
            {
                return null;
            }
        }
        var samples = Resample(bounded, 96);
        if (samples.Length < 6)
        {
            return null;
        }
        return RecognizeArrow(samples, element) ?? RecognizeLoop(samples, element);
    }

    private readonly record struct Fit(Tool Tool, Rect Frame, double Angle, double Score);

    private static Element? RecognizeLoop(Point[] samples, in Element source)
    {
        var box = Rect.Enclosing(samples);
        if (!(box.Width >= 16) || !(box.Height >= 16) || samples.Length == 0)
        {
            return null;
        }
        var first = samples[0];
        var last = samples[^1];
        var diagonal = double.Hypot(box.Width, box.Height);
        if (!(first.Distance(last) <= diagonal * 0.28))
        {
            return null;
        }
        var sum = Point.Zero;
        foreach (var sample in samples)
        {
            sum += sample;
        }
        var center = sum * (1 / (double)samples.Length);
        Point[] loop = [.. samples, first];
        double winding = 0, turning = 0;
        for (var index = 1; index < loop.Length; index++)
        {
            var a = loop[index - 1] - center;
            var b = loop[index] - center;
            var angle = Math.Atan2(a.X * b.Y - a.Y * b.X, Dot(a, b));
            winding += angle;
            turning += Math.Abs(angle);
        }
        if (!(Math.Abs(winding) >= Math.PI * 1.5) || !(Math.Abs(winding) <= Math.PI * 2.6)
            || !(turning <= Math.Abs(winding) * 1.45 + 0.4))
        {
            return null;
        }

        double xx = 0, yy = 0, xy = 0;
        foreach (var p in samples)
        {
            var d = p - center;
            xx += d.X * d.X;
            yy += d.Y * d.Y;
            xy += d.X * d.Y;
        }
        var principal = Math.Atan2(2 * xy, xx - yy) / 2;
        while (principal > Math.PI / 4)
        {
            principal -= Math.PI / 2;
        }
        while (principal < -Math.PI / 4)
        {
            principal += Math.PI / 2;
        }
        double[] angles = [-Math.PI / 6, -Math.PI / 12, 0, Math.PI / 12, Math.PI / 6, principal];
        Fit? best = null;
        foreach (var angle in angles)
        {
            var rotated = new Point[samples.Length];
            for (var index = 0; index < samples.Length; index++)
            {
                rotated[index] = (samples[index] - center).Rotated(Point.Zero, -angle);
            }
            var bounds = Rect.Enclosing(rotated);
            if (!(bounds.Width >= 16) || !(bounds.Height >= 16))
            {
                continue;
            }
            Point[] closed = [.. rotated, rotated[0]];
            var size = double.Hypot(bounds.Width, bounds.Height);
            foreach (var tool in LoopTools)
            {
                var template = Outline(tool, bounds);
                var expectedLength = Length(template);
                var observedLength = Length(closed);
                if (!(observedLength >= expectedLength * 0.70) || !(observedLength <= expectedLength * 1.45))
                {
                    continue;
                }
                // Check both directions so a partial outline cannot pass just
                // because its few drawn edges happen to match the template.
                var resampled = Resample(template, 48);
                var errors = new double[rotated.Length + resampled.Length];
                for (var index = 0; index < rotated.Length; index++)
                {
                    errors[index] = Distance(rotated[index], template);
                }
                for (var index = 0; index < resampled.Length; index++)
                {
                    errors[rotated.Length + index] = Distance(resampled[index], closed);
                }
                double total = 0;
                foreach (var error in errors)
                {
                    total += error;
                }
                var mean = total / errors.Length / size;
                var sorted = (double[])errors.Clone();
                Array.Sort(sorted);
                if (!(mean <= 0.055) || !(sorted[sorted.Length * 9 / 10] / size <= 0.12))
                {
                    continue;
                }
                var score = mean + Math.Abs(angle) / (Math.PI / 4) * 0.003;
                if (score < (best?.Score ?? double.PositiveInfinity))
                {
                    var fittedCenter = bounds.Center.Rotated(Point.Zero, angle) + center;
                    best = new Fit(tool,
                        new Rect(fittedCenter.X - bounds.Width / 2, fittedCenter.Y - bounds.Height / 2, bounds.Width, bounds.Height),
                        angle, score);
                }
            }
        }
        if (best is not { } fit)
        {
            return null;
        }
        var result = new Element(fit.Tool, frame: fit.Frame, style: source.Style, id: source.Id);
        result.Rotation = fit.Angle;
        return PreservingIdentity(result, source);
    }

    private static Element? RecognizeArrow(Point[] samples, in Element source)
    {
        var reversed = (Point[])samples.Clone();
        Array.Reverse(reversed);
        Point[][] directions = [samples, reversed];
        foreach (var points in directions)
        {
            var tail = points[0];
            var distances = new double[points.Length];
            for (var index = 0; index < points.Length; index++)
            {
                distances[index] = tail.Distance(points[index]);
            }
            if (distances.Length == 0)
            {
                continue;
            }
            var farthest = distances[0];
            for (var index = 1; index < distances.Length; index++)
            {
                if (farthest < distances[index])
                {
                    farthest = distances[index];
                }
            }
            // Resampling can put a retraced tip slightly farther out than its
            // first visit. Keep the head's return trip out of the shaft.
            var tipIndex = -1;
            for (var index = 1; index < points.Length - 1; index++)
            {
                if (distances[index] >= farthest * 0.97 && distances[index] >= distances[index - 1]
                    && distances[index] >= distances[index + 1])
                {
                    tipIndex = index;
                    break;
                }
            }
            if (tipIndex < 0 || tipIndex < 4 || tipIndex >= points.Length - 6)
            {
                continue;
            }
            var tip = points[tipIndex];
            var span = tail.Distance(tip);
            if (!(span >= 32))
            {
                continue;
            }
            var axis = (tip - tail) * (1 / span);
            var normal = new Point(-axis.Y, axis.X);
            var shaft = points.AsSpan(0, tipIndex + 1);
            double shaftTotal = 0;
            foreach (var point in shaft)
            {
                shaftTotal += Math.Abs(Dot(point - tail, normal));
            }
            var shaftError = shaftTotal / shaft.Length;
            if (!(shaftError / span <= 0.035) || !(Length(shaft) <= span * 1.18))
            {
                continue;
            }
            var head = points.AsSpan(tipIndex);
            var headInside = true;
            foreach (var point in head)
            {
                var offset = point - tip;
                if (!(Dot(offset, axis) >= -span * 0.5 && Dot(offset, axis) <= span * 0.08
                      && Math.Abs(Dot(offset, normal)) <= span * 0.4))
                {
                    headInside = false;
                    break;
                }
            }
            if (!headInside || head.IsEmpty)
            {
                continue;
            }
            // Swift max(by:) and min(by:) keep the first of equal candidates.
            var left = head[0];
            var right = head[0];
            for (var index = 1; index < head.Length; index++)
            {
                if (Dot(left - tip, normal) < Dot(head[index] - tip, normal))
                {
                    left = head[index];
                }
                if (Dot(head[index] - tip, normal) < Dot(right - tip, normal))
                {
                    right = head[index];
                }
            }
            var leftWidth = Dot(left - tip, normal);
            var rightWidth = -Dot(right - tip, normal);
            var leftBack = -Dot(left - tip, axis);
            var rightBack = -Dot(right - tip, axis);
            if (!(SwiftMath.Min(leftWidth, rightWidth) >= SwiftMath.Max(5, span * 0.04))
                || !(SwiftMath.Min(leftBack, rightBack) >= span * 0.035)
                || !(SwiftMath.Max(leftWidth, rightWidth) <= SwiftMath.Min(leftWidth, rightWidth) * 3)
                || !(SwiftMath.Max(leftBack, rightBack) <= SwiftMath.Min(leftBack, rightBack) * 3))
            {
                continue;
            }
            double headTotal = 0;
            foreach (var point in head)
            {
                headTotal += SwiftMath.Min(SegmentDistance(point, tip, left), SegmentDistance(point, tip, right));
            }
            var headError = headTotal / head.Length;
            if (!(headError / span <= 0.04) || !(Length(head) <= (tip.Distance(left) + tip.Distance(right)) * 2.1))
            {
                continue;
            }
            var style = source.Style;
            style.StartHead = ArrowHead.None;
            style.EndHead = ArrowHead.Open;
            style.Curved = false;
            style.HeadScale = SwiftMath.Min(2, SwiftMath.Max(0.5, (leftBack + rightBack) / 2 / (2 * SwiftMath.Max(12, style.Width * 4))));
            var result = new Element(Tool.Arrow, points: [tail, tip], style: style, id: source.Id);
            return PreservingIdentity(result, source);
        }
        return null;
    }

    private static Element PreservingIdentity(Element element, in Element source)
    {
        var result = element;
        result.GroupId = source.GroupId;
        result.Locked = source.Locked;
        result.Seed = source.Seed;
        result.Revision = unchecked(source.Revision + 1);
        return result;
    }

    private static Point[] Outline(Tool tool, Rect box)
    {
        switch (tool)
        {
            case Tool.Rectangle:
                return [box.Origin, new Point(box.MaxX, box.Y), new Point(box.MaxX, box.MaxY), new Point(box.X, box.MaxY), box.Origin];
            case Tool.Diamond:
                return
                [
                    new Point(box.Center.X, box.Y), new Point(box.MaxX, box.Center.Y),
                    new Point(box.Center.X, box.MaxY), new Point(box.X, box.Center.Y), new Point(box.Center.X, box.Y),
                ];
            default:
                var points = new Point[49];
                for (var index = 0; index <= 48; index++)
                {
                    var angle = index / 48.0 * 2 * Math.PI;
                    points[index] = new Point(box.Center.X + box.Width / 2 * Math.Cos(angle), box.Center.Y + box.Height / 2 * Math.Sin(angle));
                }
                return points;
        }
    }

    private static Point[] Resample(Point[] points, int count)
    {
        if (points.Length == 0)
        {
            return [];
        }
        var first = points[0];
        var last = points[^1];
        var distances = new double[points.Length];
        for (var index = 1; index < points.Length; index++)
        {
            distances[index] = distances[index - 1] + points[index - 1].Distance(points[index]);
        }
        var total = distances[^1];
        if (!(total > 0))
        {
            return [first];
        }
        var cursor = 1;
        var output = new List<Point>(count) { first };
        for (var sample = 1; sample < count - 1; sample++)
        {
            var position = total * sample / (count - 1);
            while (cursor < points.Length - 1 && distances[cursor] < position)
            {
                cursor += 1;
            }
            var segment = distances[cursor] - distances[cursor - 1];
            var fraction = segment > 0 ? (position - distances[cursor - 1]) / segment : 0;
            output.Add(points[cursor - 1] + (points[cursor] - points[cursor - 1]) * fraction);
        }
        output.Add(last);
        return [.. output];
    }

    private static double Length(ReadOnlySpan<Point> points)
    {
        double total = 0;
        for (var index = 1; index < points.Length; index++)
        {
            total += points[index - 1].Distance(points[index]);
        }
        return total;
    }

    private static double Distance(Point point, Point[] path)
    {
        var nearest = double.PositiveInfinity;
        for (var index = 1; index < path.Length; index++)
        {
            nearest = SwiftMath.Min(nearest, SegmentDistance(point, path[index - 1], path[index]));
        }
        return nearest;
    }

    private static double SegmentDistance(Point point, Point start, Point end)
    {
        var vector = end - start;
        var squared = Dot(vector, vector);
        if (!(squared > 0))
        {
            return point.Distance(start);
        }
        var t = SwiftMath.Min(1, SwiftMath.Max(0, Dot(point - start, vector) / squared));
        return point.Distance(start + vector * t);
    }

    private static double Dot(Point a, Point b) => a.X * b.X + a.Y * b.Y;
}
