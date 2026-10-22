using Annotator.Core;
using Annotator.Core.Motion;

namespace Annotator.Workspace.Motion;

/// <summary>Port of the Mac's <c>ToolbarSlideMotion</c>, in the Mac's y-up space.</summary>
public static class ToolbarSlideMotion
{
    public const double ShowDuration = MotionTiming.ToolbarShowDuration;
    public const double HideDuration = MotionTiming.ToolbarHideDuration;

    /// <summary>The edge a toolbar slides toward: an explicit dock edge of its orientation, else the nearer screen edge.</summary>
    public static ToolbarDockEdge ResolvedEdge(MotionRect frame, MotionRect screen, ToolbarOrientation orientation, ToolbarDockEdge edge)
    {
        if (orientation == ToolbarOrientation.Vertical)
        {
            if (edge is ToolbarDockEdge.Left or ToolbarDockEdge.Right)
            {
                return edge;
            }
            return frame.MidX < screen.MidX ? ToolbarDockEdge.Left : ToolbarDockEdge.Right;
        }
        if (edge is ToolbarDockEdge.Top or ToolbarDockEdge.Bottom)
        {
            return edge;
        }
        return frame.MidY < screen.MidY ? ToolbarDockEdge.Bottom : ToolbarDockEdge.Top;
    }

    /// <summary>The 12-point offset toward <paramref name="edge"/>.</summary>
    public static MotionPoint Offset(ToolbarDockEdge edge, bool flipped) => edge switch
    {
        ToolbarDockEdge.Left => new MotionPoint(-12, 0),
        ToolbarDockEdge.Right => new MotionPoint(12, 0),
        ToolbarDockEdge.Top => new MotionPoint(0, flipped ? -12 : 12),
        _ => new MotionPoint(0, flipped ? 12 : -12),
    };

    /// <summary>Where a gathered shape goes: the toolbar's center on the shape's screen, else that screen's top-left corner.</summary>
    public static MotionPoint AnnotationTarget(MotionRect source, MotionRect toolbar, MotionRect screen)
    {
        if (screen.Intersects(toolbar))
        {
            return toolbar.Center;
        }
        return new MotionPoint(screen.MinX + 24, screen.MaxY - 24);
    }

    /// <summary>The hidden transform of a gathered shape: at most 24 points (and 12%) of its size, centered on the target.</summary>
    public static MotionTransform AnnotationTransform(MotionRect source, MotionPoint target)
    {
        var scale = Math.Min(0.12, 24 / Math.Max(1, Math.Max(source.Width, source.Height)));
        return new MotionTransform(scale, 0, target.X - source.MidX, target.Y - source.MidY);
    }

    /// <summary>Nearest shapes leave first, so a hide rolls toward its destination. Showing replays the order in reverse.</summary>
    public static IReadOnlyList<string> DistanceOrder(IEnumerable<(string Id, MotionPoint Center, MotionPoint Target)> items) =>
        [.. items.Select(item => (item.Id, Distance: item.Center.Distance(item.Target)))
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => item.Id)];

    /// <summary>Arrivals closer than 40 ms share one pulse, and a burst of shapes gets at most four.</summary>
    public static IReadOnlyList<double> PulseTimes(IEnumerable<double> arrivals)
    {
        var result = new List<double>();
        foreach (var time in arrivals.Order())
        {
            if (result.Count == 0 || time - result[^1] > 0.04)
            {
                result.Add(time);
            }
        }
        return result.Count > 4 ? result.GetRange(0, 4) : result;
    }

    /// <summary>0...1 bump height at <paramref name="time"/>; overlapping bumps never stack above one.</summary>
    public static double Pulse(double time, IReadOnlyList<double> arrivals, double width)
    {
        double height = 0;
        foreach (var arrival in arrivals)
        {
            var phase = (time - arrival) / width;
            if (phase > 0 && phase < 1)
            {
                height = Math.Max(height, Math.Sin(Math.PI * phase));
            }
        }
        return height;
    }
}
