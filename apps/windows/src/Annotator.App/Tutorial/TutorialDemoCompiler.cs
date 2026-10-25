using System.Collections.Immutable;
using System.Numerics;
using Annotator.Core.Tutorial;
using Annotator.Workspace.FirstUse;
using Windows.Graphics;
using CorePoint = Annotator.Core.Point;

namespace Annotator.App.Tutorial;

/// <summary>
/// Turns a demonstration into a timeline on the overlay (Mac TutorialDemoTimeline.compile): it finds every place, plans the
/// ghost's arcs with <see cref="TutorialMotion"/>, and times presses, ripples, ink and keycaps. Places it cannot find, such as
/// a control that is not on the toolbar, are left out; the keys still play.
/// </summary>
internal static class TutorialDemoCompiler
{
    private const int TravelSamples = 18;

    /// <param name="anchor">A toolbar control's frame in physical screen pixels, by accessible name.</param>
    /// <param name="monitor">The overlay's display: its bounds and work area in physical pixels and its scale.</param>
    /// <param name="onDisplay">Converts points on the overlay's display to physical screen pixels, when the workspace knows it.</param>
    /// <param name="origin">Where the ghost appears, in overlay pixels.</param>
    public static DemoTimeline Compile(ImmutableArray<TutorialDemoAction> actions, Func<string, RectInt32?> anchor,
                                       Shell.MonitorArea monitor, Func<CorePoint, CorePoint?> onDisplay, Vector2 origin)
    {
        var timeline = new DemoTimeline();
        double scale = monitor.Scale;
        var position = origin;
        double t = 0;
        int curveClicks = 0;
        timeline.Appear = 0;
        timeline.Path.Add((0, position));
        t += TutorialMotion.GhostAppearDuration;

        Vector2? Resolve(TutorialSpot spot)
        {
            switch (spot.Kind)
            {
                case TutorialSpotKind.Anchor when spot.Anchor is { } name && anchor(name) is { } rect:
                    return new Vector2(rect.X + rect.Width / 2f - monitor.Bounds.X, rect.Y + rect.Height / 2f - monitor.Bounds.Y);
                case TutorialSpotKind.WorkArea:
                    RectInt32 work = monitor.WorkArea;
                    return new Vector2((float)(work.X - monitor.Bounds.X + work.Width * spot.Value.X),
                                       (float)(work.Y - monitor.Bounds.Y + work.Height * spot.Value.Y));
                case TutorialSpotKind.Display when onDisplay(spot.Value) is { } desktop:
                    return new Vector2((float)(desktop.X - monitor.Bounds.X), (float)(desktop.Y - monitor.Bounds.Y));
                default:
                    return null;
            }
        }

        void Travel(Vector2 to)
        {
            double distance = Vector2.Distance(position, to) / scale;
            double duration = TutorialMotion.TravelDuration(distance);
            var from = new CorePoint(position.X, position.Y);
            var target = new CorePoint(to.X, to.Y);
            timeline.Path.Add((t, position));
            for (int step = 1; step <= TravelSamples; step++)
            {
                CorePoint point = TutorialMotion.TravelPoint(from, target, step / (double)TravelSamples);
                timeline.Path.Add((t + duration * step / TravelSamples, new Vector2((float)point.X, (float)point.Y)));
            }
            t += duration;
            position = to;
        }

        void Press(bool ripple)
        {
            timeline.Path.Add((t, position));
            timeline.Press.Add((t, 1));
            if (ripple)
            {
                timeline.Ripples.Add((t, position));
            }
            t += TutorialMotion.GhostPressDuration;
            timeline.Press.Add((t, (float)TutorialMotion.GhostPressScale));
        }

        // The curved arrow is known before the ghost starts: a smooth line through every point it clicks. The ghost draws each
        // span as it moves to the next point, so the arrow grows click by click, and the double-click on the last point (or
        // Enter) adds the head.
        const int CurveSegments = 12;
        Vector2[] curveTargets = [.. actions.OfType<TutorialDemoClick>().Where(click => click.Ink == TutorialInk.Curve)
            .Select(click => Resolve(click.Target)).OfType<Vector2>()];
        Vector2[] curve = curveTargets.Length >= 3
            ? [.. TutorialMotion.Smooth([.. curveTargets.Select(point => new CorePoint(point.X, point.Y))], CurveSegments)
                .Select(point => new Vector2((float)point.X, (float)point.Y))]
            : curveTargets;
        int curveStride = curveTargets.Length >= 3 ? CurveSegments : 1;

        // Draws or drags along a path at TutorialMotion.DragDuration, following it by length with the standard curve; with
        // Trail ink, the line appears behind the ghost.
        void Follow(IReadOnlyList<Vector2> path, bool trail)
        {
            var lengths = new double[path.Count];
            for (int index = 1; index < path.Count; index++)
            {
                lengths[index] = lengths[index - 1] + Vector2.Distance(path[index - 1], path[index]);
            }
            double total = lengths[^1];
            if (path.Count < 2 || total < 0.5)
            {
                position = path[^1];
                return;
            }
            double duration = TutorialMotion.DragDuration(total / scale);
            int count = Math.Max(2, (int)Math.Round(duration * 60));
            double begin = t;
            Vector2 previous = path[0];
            for (int step = 1; step <= count; step++)
            {
                double u = step / (double)count;
                Vector2 point = Along(path, lengths, Annotator.Core.Motion.MotionCurves.Standard.Progress(u) * total);
                if (trail)
                {
                    timeline.Ink.Add((begin + duration * (step - 1) / count, previous, point));
                }
                timeline.Path.Add((begin + duration * u, point));
                previous = point;
            }
            t = begin + duration;
            position = path[^1];
        }

        // The arrow is finished at <at>: its head appears at the last point.
        void Head(double at)
        {
            if (curveClicks < 2 || curve.Length < 2)
            {
                return;
            }
            Vector2 tip = curve[^1], from = curve[^2];
            (CorePoint left, CorePoint right) = TutorialMotion.Arrowhead(new CorePoint(from.X, from.Y), new CorePoint(tip.X, tip.Y),
                TutorialMotion.ArrowheadLength * scale);
            timeline.Ink.Add((at, tip, new Vector2((float)left.X, (float)left.Y)));
            timeline.Ink.Add((at, tip, new Vector2((float)right.X, (float)right.Y)));
            curveClicks = 0;
        }

        void Release()
        {
            timeline.Path.Add((t, position));
            timeline.Press.Add((t, (float)TutorialMotion.GhostPressScale));
            t += TutorialMotion.GhostReleaseDuration;
            timeline.Press.Add((t, 1));
            timeline.Path.Add((t, position));
        }

        foreach (TutorialDemoAction action in actions)
        {
            switch (action)
            {
                case TutorialDemoKeys keys:
                    timeline.Path.Add((t, position));
                    timeline.Keys.Add(new DemoKeyCue(t, keys.Keys));
                    Head(t + TutorialMotion.KeyDownDuration);
                    t += TutorialMotion.KeyStagger * Math.Max(0, keys.Keys.Length - 1) + TutorialMotion.KeyDownDuration +
                         TutorialMotion.KeyUpDuration + 0.35;
                    break;
                case TutorialDemoClick click when Resolve(click.Target) is { } target:
                    if (click.Ink == TutorialInk.Curve && curveClicks > 0 && curveClicks < curveTargets.Length)
                    {
                        int from = (curveClicks - 1) * curveStride;
                        Follow(curve[from..Math.Min(curve.Length, from + curveStride + 1)], trail: true);
                    }
                    else
                    {
                        Travel(target);
                    }
                    Press(ripple: true);
                    Release();
                    if (click.Ink == TutorialInk.Curve)
                    {
                        curveClicks++;
                    }
                    if (click.Double)
                    {
                        t += TutorialMotion.DoubleClickGap;
                        Press(ripple: true);
                        Head(t);
                        Release();
                    }
                    t += TutorialMotion.GhostPause;
                    break;
                case TutorialDemoDrag drag when Resolve(drag.From) is { } start && Resolve(drag.To) is { } end:
                {
                    Travel(start);
                    Press(ripple: drag.Ink == TutorialInk.None);
                    double begin = t;
                    Follow([start, end], trail: drag.Ink == TutorialInk.Trail);
                    if (drag.Ink == TutorialInk.Box)
                    {
                        timeline.Boxes.Add((begin, t, start, end));
                    }
                    Release();
                    t += TutorialMotion.GhostPause;
                    break;
                }
                case TutorialDemoLoop loop when Resolve(loop.Center) is { } center:
                {
                    Vector2[] points = [.. TutorialMotion.Loop(new CorePoint(center.X, center.Y), loop.Radius * scale)
                        .Select(point => new Vector2((float)point.X, (float)point.Y))];
                    Travel(points[0]);
                    Press(ripple: false);
                    Follow(points, trail: true);
                    Release();
                    t += TutorialMotion.GhostPause;
                    break;
                }
            }
        }
        timeline.Path.Add((t, position));
        timeline.Vanish = t;
        timeline.Duration = t + Math.Max(TutorialMotion.GhostVanishDuration, TutorialMotion.InkFadeDuration) + 0.05;
        return timeline;
    }

    /// <summary>The point <paramref name="distance"/> pixels along <paramref name="path"/>.</summary>
    private static Vector2 Along(IReadOnlyList<Vector2> path, double[] lengths, double distance)
    {
        for (int index = 1; index < path.Count; index++)
        {
            if (distance <= lengths[index] || index == path.Count - 1)
            {
                double span = lengths[index] - lengths[index - 1];
                float share = span <= 0 ? 1 : (float)Math.Clamp((distance - lengths[index - 1]) / span, 0, 1);
                return Vector2.Lerp(path[index - 1], path[index], share);
            }
        }
        return path[^1];
    }
}
