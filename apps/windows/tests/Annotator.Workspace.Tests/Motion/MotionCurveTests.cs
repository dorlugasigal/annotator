using Annotator.Core;
using Annotator.Workspace.Motion;
using Annotator.Core.Motion;
using Style = Annotator.Core.VisibilityAnimationStyle;

namespace Annotator.Workspace.Tests.Motion;

/// <summary>
/// The shared curves and timing of Motion.swift and ToolbarSlideMotion.swift (WIN-MOT-01), with ports of the formula
/// assertions in apps/macos/Tests/NativeAppTests/ToolbarSlideMotionTests.swift.
/// </summary>
public sealed class MotionCurveTests
{
    public static TheoryData<string> Curves => ["enter", "exit", "standard", "fade", "magic", "easeOut", "easeIn", "linear"];

    private static CubicBezier Named(string name) => name switch
    {
        "enter" => MotionCurves.Enter,
        "exit" => MotionCurves.Exit,
        "standard" => MotionCurves.Standard,
        "fade" => MotionCurves.Fade,
        "magic" => MagicFlight.TimingFunction,
        "easeOut" => MotionCurves.EaseOut,
        "easeIn" => MotionCurves.EaseIn,
        _ => MotionCurves.Linear,
    };

    [Fact]
    public void CurvesUseTheMacControlPoints()
    {
        Assert.Equal(new CubicBezier(0.2, 0, 0, 1), MotionCurves.Enter);
        Assert.Equal(new CubicBezier(0.45, 0, 0.55, 0.4), MotionCurves.Exit);
        Assert.Equal(new CubicBezier(0.3, 0, 0.2, 1), MotionCurves.Standard);
        Assert.Equal(new CubicBezier(0.2, 0, 0.4, 1), MotionCurves.Fade);
        Assert.Equal(new CubicBezier(0.22, 0.06, 0.24, 1), MagicFlight.TimingFunction);
        Assert.Equal(new SpringMotion(0.28, 0.86), MotionTiming.Snappy);
        Assert.Equal(new SpringMotion(0.38, 0.9), MotionTiming.Smooth);
        Assert.Equal(new SpringMotion(0.3, 0.62), MotionTiming.Playful);
        Assert.Equal(0.16, MotionTiming.FadeIn.Duration);
        Assert.Equal(0.12, MotionTiming.FadeOut.Duration);
        Assert.Equal(0.12, MotionTiming.Reduced.Duration);
    }

    [Theory]
    [MemberData(nameof(Curves))]
    public void ProgressMatchesTheBezierWithinTheCorpusTolerance(string name)
    {
        // Motion.progress bisects 40 times; an independent Newton solve of the same Bezier agrees within 1e-4 (WIN-MOT-01).
        CubicBezier curve = Named(name);
        for (var step = 0; step <= 200; step++)
        {
            var x = step / 200.0;
            Assert.Equal(Reference(curve, x), curve.Progress(x), 1e-9);
            Assert.InRange(curve.Progress(x), -1e-12, 1 + 1e-12);
        }
        Assert.Equal(0, curve.Progress(-0.5));
        Assert.Equal(1, curve.Progress(1.5));
        Assert.Equal(0, curve.Progress(double.NaN));
    }

    [Fact]
    public void SmoothIsTheClampedSmoothstep()
    {
        Assert.Equal(0, MotionCurves.Smooth(-1));
        Assert.Equal(0.5, MotionCurves.Smooth(0.5));
        Assert.Equal(0.104, MotionCurves.Smooth(0.2), 1e-12);
        Assert.Equal(1, MotionCurves.Smooth(4));
    }

    public static TheoryData<string, double[]> Schedules => new()
    {
        { "enter", [0, 1] }, { "exit", [0, 1] }, { "standard", [0, 1] }, { "fade", [0, 0.2, 1] }, { "linear", [0, 0.72, 1] },
        { "magic", [0, 0.15, 1] }, { "magic", [0, 0.82, 1] }, { "enter", Even(25) }, { "exit", Even(25) },
        { "standard", Even(25) }, { "magic", Even(97) }, { "magic", [0, 0.12, 0.75, 1] },
    };

    [Theory]
    [MemberData(nameof(Schedules))]
    public void ExactSchedulesReproduceCoreAnimationOnTheCompositor(string name, double[] keyTimes)
    {
        CubicBezier curve = Named(name);
        List<KeyframePoint> points = KeyframeSchedule.Exact(keyTimes, curve);
        Assert.Equal(0, points[0].Time);
        Assert.Equal(1, points[^1].Time);
        for (var index = 1; index < points.Count; index++)
        {
            Assert.True(points[index].Time >= points[index - 1].Time, $"Keyframe {index} goes back in time.");
            Assert.InRange(points[index].Easing.X1, 0, 1);
            Assert.InRange(points[index].Easing.X2, 0, 1);
        }
        foreach (var key in keyTimes)
        {
            Assert.Contains(points, point => Math.Abs(point.Eased - key) < 1e-12);
        }
        for (var step = 0; step <= 400; step++)
        {
            var time = step / 400.0;
            Assert.Equal(curve.Progress(time), KeyframeSchedule.EasedAt(points, time), 1e-6);
        }
    }

    [Fact]
    public void GatherWaitsForArrivalBeforeToolbarClosesAndOpensBeforeReturningShapes()
    {
        // ToolbarSlideMotionTests.testGatherWaitsForArrivalBeforeToolbarClosesAndOpensBeforeReturningShapes.
        var hide = new VisibilityMotionTiming(Style.Slide, appearing: false, hasDrawings: true, objectCount: 4);
        var lastArrival = Enumerable.Range(0, 4).Max(rank => hide.DrawingDelay(rank) + hide.Duration);
        Assert.True(hide.ToolbarDelay >= lastArrival);
        var show = new VisibilityMotionTiming(Style.Slide, appearing: true, hasDrawings: true, objectCount: 4);
        Assert.True(show.DrawingsDelay >= show.ToolbarDuration);
        Assert.Equal(hide.ToolbarDelay + hide.ToolbarDuration, hide.TotalDuration);
        Assert.Equal(show.DrawingsEndDelay + show.Duration, show.TotalDuration);
    }

    [Fact]
    public void ChoreographyShowsToolbarFirstAndHidesItLast()
    {
        // ToolbarSlideMotionTests.testChoreographyShowsToolbarFirstAndHidesItLast.
        foreach (Style style in VisibilityAnimationStyle.Available)
        {
            var show = new VisibilityMotionTiming(style, appearing: true, hasDrawings: true);
            var hide = new VisibilityMotionTiming(style, appearing: false, hasDrawings: true);
            if (style is Style.Fade or Style.None)
            {
                Assert.Equal(0, show.DrawingsDelay);
                Assert.Equal(0, hide.ToolbarDelay);
            }
            else
            {
                Assert.True(show.DrawingsDelay > show.ToolbarDelay, $"{style}");
                Assert.True(hide.ToolbarDelay > hide.DrawingsDelay, $"{style}");
                if (style == Style.Dissolve)
                {
                    Assert.Equal(show.DrawingsDelay, hide.ToolbarDelay);
                }
            }
            Assert.Equal(show.Duration + show.DrawingsDelay, show.TotalDuration);
            Assert.Equal(hide.ToolbarDuration + hide.ToolbarDelay, hide.TotalDuration);
            var empty = new VisibilityMotionTiming(style, appearing: true, hasDrawings: false);
            Assert.Equal(empty.ToolbarDuration, empty.TotalDuration);
        }
    }

    [Fact]
    public void TimingUsesTheMacDurationsDelaysAndStagger()
    {
        // Spec 009: per-element starts spaced by up to 65 ms with a 260 ms maximum spread; Gather's toolbar 240/300 ms;
        // Magic 620/540 ms per shape and 400/360 ms for the toolbar point and burst.
        Assert.Equal(0.065, new VisibilityMotionTiming(Style.Slide, false, true, 3).ObjectStep);
        Assert.Equal(0.26 / 63, new VisibilityMotionTiming(Style.Slide, false, true, 64).ObjectStep, 12);
        Assert.Equal(0, new VisibilityMotionTiming(Style.Fade, false, true, 10).ObjectStep);
        Assert.Equal(0.24, new VisibilityMotionTiming(Style.Slide, false, true).ToolbarDuration);
        Assert.Equal(0.30, new VisibilityMotionTiming(Style.Corners, true, true).ToolbarDuration);
        Assert.Equal(0.36, new VisibilityMotionTiming(Style.Magic, false, true).ToolbarDuration);
        Assert.Equal(0.40, new VisibilityMotionTiming(Style.Magic, true, true).ToolbarDuration);
        var magicHide = new VisibilityMotionTiming(Style.Magic, false, true, 3);
        Assert.Equal(Math.Max(0, 0.54 + 0.13 - 0.36 * 0.6), magicHide.ToolbarDelay, 12);
        var gatherShow = new VisibilityMotionTiming(Style.Slide, true, true, 3);
        Assert.Equal(0.335, gatherShow.DrawingsDelay, 12);
        Assert.Equal(0.335 + 0.13, gatherShow.DrawingDelay(0), 12);
        Assert.Equal(0.335, gatherShow.DrawingDelay(2), 12);
        var dissolve = new VisibilityMotionTiming(Style.Dissolve, false, true, 2);
        Assert.Equal(0.12 + 0.065, dissolve.ToolbarDelay, 12);
        foreach ((Style style, double show, double hide) in new[]
        {
            (Style.Fade, 0.20, 0.16), (Style.Dissolve, 0.65, 0.55), (Style.Magic, 0.62, 0.54), (Style.Corners, 0.38, 0.30),
            (Style.Pop, 0.34, 0.26), (Style.Drop, 0.40, 0.32), (Style.Focus, 0.36, 0.28), (Style.Slide, 0.36, 0.28),
            (Style.None, 0, 0),
        })
        {
            Assert.Equal(show, LayerMotion.Duration(style, appearing: true));
            Assert.Equal(hide, LayerMotion.Duration(style, appearing: false));
        }
    }

    [Fact]
    public void DirectionsRespectDockEdgesWithoutScalingOrRotation()
    {
        // ToolbarSlideMotionTests.testDirectionsRespectDockEdgesWithoutScalingOrRotation.
        foreach (ToolbarDockEdge edge in new[] { ToolbarDockEdge.Top, ToolbarDockEdge.Bottom, ToolbarDockEdge.Left, ToolbarDockEdge.Right })
        {
            MotionPoint offset = ToolbarSlideMotion.Offset(edge, flipped: false);
            Assert.Equal(12, double.Hypot(offset.X, offset.Y));
            switch (edge)
            {
                case ToolbarDockEdge.Top: Assert.True(offset.Y > 0); break;
                case ToolbarDockEdge.Bottom: Assert.True(offset.Y < 0); break;
                case ToolbarDockEdge.Left: Assert.True(offset.X < 0); break;
                default: Assert.True(offset.X > 0); break;
            }
        }
    }

    [Fact]
    public void AnnotationTransportKeepsAspectRatioAndEndsAtLocalDestination()
    {
        // ToolbarSlideMotionTests.testAnnotationTransportKeepsAspectRatioAndEndsAtLocalDestination.
        var source = new MotionRect(-900, 200, 600, 180);
        var screen = new MotionRect(-1200, 0, 1200, 900);
        var toolbar = new MotionRect(-1000, 30, 600, 120);
        MotionPoint target = ToolbarSlideMotion.AnnotationTarget(source, toolbar, screen);
        MotionTransform transform = ToolbarSlideMotion.AnnotationTransform(source, target);
        Assert.Equal(0, transform.B);
        Assert.Equal(toolbar.MidX, source.MidX + transform.Tx);
        Assert.Equal(toolbar.MidY, source.MidY + transform.Ty);
        Assert.Equal(24, source.Width * transform.A, 3);
        Assert.Equal(new MotionPoint(-1176, 876),
                     ToolbarSlideMotion.AnnotationTarget(source, new MotionRect(100, 50, 600, 120), screen));
    }

    [Fact]
    public void DistanceOrderStartsNearestAndPulsesGroupCloseArrivals()
    {
        // ToolbarSlideMotionTests.testDistanceOrderStartsNearestAndPulsesGroupCloseArrivals.
        var target = new MotionPoint(0, 0);
        IReadOnlyList<string> order = ToolbarSlideMotion.DistanceOrder(
        [
            ("far", new MotionPoint(500, 0), target), ("near", new MotionPoint(50, 0), target), ("middle", new MotionPoint(0, 200), target),
        ]);
        Assert.Equal(["near", "middle", "far"], order);
        Assert.Equal([1.0, 1.2, 1.3, 1.4], ToolbarSlideMotion.PulseTimes([1.0, 1.02, 1.2, 1.3, 1.4, 1.5, 1.6]));
        Assert.Equal(1, ToolbarSlideMotion.Pulse(1.065, [1.0], 0.13), 3);
        Assert.Equal(0, ToolbarSlideMotion.Pulse(0.99, [1.0], 0.13));
        Assert.True(ToolbarSlideMotion.Pulse(1.08, [1.0, 1.03], 0.13) <= 1);
        Assert.Equal(["a", "b"], ToolbarSlideMotion.DistanceOrder([("b", new MotionPoint(1, 0), target), ("a", new MotionPoint(0, 1), target)]));
    }

    [Fact]
    public void ResolvedEdgeFollowsTheDockOrTheNearerScreenEdge()
    {
        var screen = new MotionRect(0, 0, 1000, 800);
        Assert.Equal(ToolbarDockEdge.Top, ToolbarSlideMotion.ResolvedEdge(new MotionRect(300, 700, 400, 60), screen,
            ToolbarOrientation.Horizontal, ToolbarDockEdge.Automatic));
        Assert.Equal(ToolbarDockEdge.Bottom, ToolbarSlideMotion.ResolvedEdge(new MotionRect(300, 20, 400, 60), screen,
            ToolbarOrientation.Horizontal, ToolbarDockEdge.Automatic));
        Assert.Equal(ToolbarDockEdge.Right, ToolbarSlideMotion.ResolvedEdge(new MotionRect(900, 300, 60, 400), screen,
            ToolbarOrientation.Vertical, ToolbarDockEdge.Top));
        Assert.Equal(ToolbarDockEdge.Left, ToolbarSlideMotion.ResolvedEdge(new MotionRect(900, 300, 60, 400), screen,
            ToolbarOrientation.Vertical, ToolbarDockEdge.Left));
    }

    [Fact]
    public void TransformsFollowCoreAnimationAndFlipForWindowsScreens()
    {
        MotionTransform transform = MotionTransform.MakeRotation(0.3).Scaled(0.5).WithTranslation(12, -40);
        Assert.Equal(0.5, transform.Scale, 12);
        Assert.Equal(0.3, transform.Angle, 12);
        MotionTransform screen = transform.ToScreen();
        Assert.Equal(-0.3, screen.Angle, 12);
        Assert.Equal(40, screen.Ty);
        Assert.Equal(12, screen.Tx);
        MotionTransform halfway = MotionTransform.Interpolate(MotionTransform.MakeScale(1), MotionTransform.MakeRotation(0.2).Scaled(0.5), 0.5);
        Assert.Equal(0.75, halfway.Scale, 12);
        Assert.Equal(0.1, halfway.Angle, 12);
    }

    private static double[] Even(int count) => [.. Enumerable.Range(0, count).Select(index => (double)index / (count - 1))];

    /// <summary>The curve's y where its x reaches <paramref name="x"/>, by Newton's method with a bisection fallback.</summary>
    private static double Reference(CubicBezier curve, double x)
    {
        if (x <= 0)
        {
            return 0;
        }
        if (x >= 1)
        {
            return 1;
        }
        double low = 0, high = 1, s = x;
        for (var step = 0; step < 100; step++)
        {
            var value = curve.Point(s).X - x;
            if (Math.Abs(value) < 1e-15)
            {
                break;
            }
            if (value < 0)
            {
                low = s;
            }
            else
            {
                high = s;
            }
            var derivative = 3 * (1 - s) * (1 - s) * curve.X1 + 6 * (1 - s) * s * (curve.X2 - curve.X1) + 3 * s * s * (1 - curve.X2);
            var next = derivative > 1e-12 ? s - value / derivative : (low + high) / 2;
            s = next <= low || next >= high ? (low + high) / 2 : next;
        }
        return curve.Point(s).Y;
    }
}
