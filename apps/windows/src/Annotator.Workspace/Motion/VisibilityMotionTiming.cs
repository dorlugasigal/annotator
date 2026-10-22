using Annotator.Core;

namespace Annotator.Workspace.Motion;

/// <summary>
/// Port of the Mac's <c>WorkspaceMotionTiming</c>: durations, delays and the per-shape stagger of one show or hide.
/// Hide starts the toolbar last and show starts it first; Gather, Magic and Corners wait for the shapes to arrive.
/// </summary>
public sealed class VisibilityMotionTiming
{
    private readonly int _objectCount;
    private readonly bool _appearing;
    private readonly bool _hasDrawings;

    public VisibilityMotionTiming(VisibilityAnimationStyle style, bool appearing, bool hasDrawings, int objectCount = 1)
    {
        _objectCount = objectCount;
        _appearing = appearing;
        _hasDrawings = hasDrawings;
        Duration = LayerMotion.Duration(style, appearing);
        ToolbarDuration = style == VisibilityAnimationStyle.Magic ? (appearing ? 0.40 : 0.36)
            : style is VisibilityAnimationStyle.Slide or VisibilityAnimationStyle.Corners ? (appearing ? 0.30 : 0.24)
            : Duration;
        var lead = hasDrawings && style != VisibilityAnimationStyle.Fade && style != VisibilityAnimationStyle.None
            ? (style == VisibilityAnimationStyle.Dissolve ? 0.12 : 0.07)
            : 0;
        ObjectStep = lead > 0 && objectCount > 1 ? Math.Min(0.065, 0.26 / (objectCount - 1)) : 0;
        if ((style is VisibilityAnimationStyle.Slide or VisibilityAnimationStyle.Magic or VisibilityAnimationStyle.Corners) && hasDrawings)
        {
            var lastArrival = Duration + ObjectStep * Math.Max(0, objectCount - 1);
            ToolbarDelay = appearing ? 0
                : style == VisibilityAnimationStyle.Magic ? Math.Max(0, lastArrival - ToolbarDuration * 0.6)
                : lastArrival + 0.025;
            DrawingsDelay = appearing ? ToolbarDuration + 0.035 : 0;
        }
        else
        {
            ToolbarDelay = appearing ? 0 : lead + ObjectStep * Math.Max(0, objectCount - 1);
            DrawingsDelay = appearing ? lead : 0;
        }
    }

    /// <summary>One shape's duration.</summary>
    public double Duration { get; }

    public double ToolbarDuration { get; }

    public double ToolbarDelay { get; }

    public double DrawingsDelay { get; }

    /// <summary>The start spacing between shapes: at most 65 ms, spreading at most 260 ms.</summary>
    public double ObjectStep { get; }

    public double DrawingsEndDelay => DrawingsDelay + ObjectStep * Math.Max(0, _objectCount - 1);

    public double TotalDuration => Math.Max(ToolbarDelay + ToolbarDuration, _hasDrawings ? DrawingsEndDelay + Duration : 0);

    /// <summary>The start of the shape with <paramref name="rank"/> in the distance order; showing replays the order in reverse.</summary>
    public double DrawingDelay(int rank) =>
        DrawingsDelay + ObjectStep * (_appearing ? Math.Max(0, _objectCount - 1 - rank) : rank);
}
