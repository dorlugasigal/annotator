using Annotator.Core;
using Annotator.Core.Motion;

namespace Annotator.Workspace.Motion;

/// <summary>
/// Port of the Mac's <c>VisibilityPose</c>: Pop, Drop and Soft focus describe a snapshot by how hidden it is (0 at rest,
/// 1 hidden). Transforms are in the Mac's y-up space.
/// </summary>
public static class VisibilityPose
{
    public const int StepCount = 30;
    public const double BlurPadding = 36;

    public readonly record struct Pose(MotionTransform Transform, double Opacity, double Blur);

    public static bool UsesPose(VisibilityAnimationStyle style) =>
        style is VisibilityAnimationStyle.Pop or VisibilityAnimationStyle.Drop or VisibilityAnimationStyle.Focus;

    /// <summary>Extra room around a snapshot so its pose never clips at the transient window edge.</summary>
    public static double Padding(VisibilityAnimationStyle style, double width, double height)
    {
        var longest = Math.Max(width, height);
        return style switch
        {
            VisibilityAnimationStyle.Pop => 48 + longest * 0.04,
            VisibilityAnimationStyle.Drop => 64 + longest * 0.1,
            VisibilityAnimationStyle.Focus => 48 + longest * 0.04 + BlurPadding,
            _ => 48,
        };
    }

    /// <summary>Drop falls below its frame when hiding and arrives from above when showing.</summary>
    public static MotionRect Travel(VisibilityAnimationStyle style, MotionRect frame)
    {
        if (style != VisibilityAnimationStyle.Drop)
        {
            return frame;
        }
        return frame.Union(frame.Offset(0, -122)).Union(frame.Offset(0, 60));
    }

    public static Pose PoseAt(VisibilityAnimationStyle style, double hidden, bool appearing, bool annotation, ulong seed)
    {
        switch (style)
        {
            case VisibilityAnimationStyle.Pop:
            {
                var p = Math.Max(0, hidden);
                double peak = annotation ? 0.06 : 0.03, low = annotation ? 0.18 : 0.6;
                var scale = p <= 0.22
                    ? 1 + peak * Math.Sin(p / 0.22 * Math.PI / 2)
                    : (1 + peak) * Math.Exp(Math.Log(low / (1 + peak)) * MotionCurves.Smooth((p - 0.22) / 0.78));
                return new Pose(MotionTransform.MakeScale(scale), 1 - MotionCurves.Smooth((p - 0.6) / 0.4), 0);
            }
            case VisibilityAnimationStyle.Drop:
            {
                double side = seed % 2 == 0 ? 1 : -1;
                MotionTransform transform;
                double opacity;
                if (appearing)
                {
                    transform = MotionTransform.MakeRotation(annotation ? side * 0.08 * Math.Max(0, hidden) : 0);
                    transform = transform with { Ty = (annotation ? 56 : 20) * hidden };
                    opacity = 1 - MotionCurves.Smooth((hidden - 0.55) / 0.45);
                }
                else
                {
                    var p = Math.Max(0, hidden);
                    transform = MotionTransform.MakeRotation(annotation ? side * 0.16 * p * p : 0);
                    transform = transform.Scaled(1 - 0.06 * p);
                    transform = transform with { Ty = -(annotation ? 110 : 28) * p * p };
                    opacity = 1 - MotionCurves.Smooth((p - 0.3) / 0.7);
                }
                return new Pose(transform, opacity, 0);
            }
            case VisibilityAnimationStyle.Focus:
            {
                var p = Math.Max(0, hidden);
                var scale = 1 + (annotation ? 0.06 : 0.03) * p;
                return new Pose(MotionTransform.MakeScale(scale), 1 - MotionCurves.Smooth((p - 0.15) / 0.85), (annotation ? 12 : 8) * p);
            }
            default:
                return new Pose(MotionTransform.Identity, hidden >= 1 ? 0 : 1, 0);
        }
    }

    /// <summary>
    /// Linear time to hidden-progress. Drop's exit is constant acceleration (the pose squares it) and its entrance lands
    /// with one small bounce.
    /// </summary>
    public static double Hidden(VisibilityAnimationStyle style, double from, double to, double time, bool appearing, bool reversing)
    {
        double eased;
        if (reversing)
        {
            eased = MotionCurves.Standard.Progress(time);
        }
        else if (style == VisibilityAnimationStyle.Drop)
        {
            eased = appearing ? MotionCurves.DropSettle(time) : Math.Min(1, Math.Max(0, time));
        }
        else
        {
            eased = (appearing ? MotionCurves.Enter : MotionCurves.Standard).Progress(time);
        }
        return from + (to - from) * eased;
    }

}
