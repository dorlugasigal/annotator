using Annotator.Core;
using Annotator.Workspace.Motion;
using Annotator.Core.Motion;
using Style = Annotator.Core.VisibilityAnimationStyle;

namespace Annotator.Workspace.Tests.Motion;

/// <summary>
/// Ports of the timeline assertions in apps/macos/Tests/NativeAppTests/ToolbarSlideMotionTests.swift, MagicFlightTests.swift,
/// CornerRoutesTests.swift and the particle grid checks: every value the compositor animates comes from these timelines.
/// </summary>
public sealed class LayerMotionTests
{
    private static LayerMotion Motion(Style style, bool appearing, double duration, LayerState? initial = null,
                                      MotionTransform? hidden = null, bool annotation = false, ulong? magicSeed = null,
                                      double curveSign = 1, ulong? poseSeed = null, double width = 600, double height = 140) =>
        new(new LayerMotionOptions
        {
            Style = style,
            Appearing = appearing,
            Duration = duration,
            Initial = initial,
            HiddenTransform = hidden,
            Annotation = annotation,
            MagicSeed = magicSeed,
            CurveSign = curveSign,
            PoseSeed = poseSeed,
            Width = width,
            Height = height,
        });

    [Fact]
    public void GatherToolbarShrinksUniformlyAtItsCenterWithoutSquashing()
    {
        // ToolbarSlideMotionTests.testGatherToolbarShrinksUniformlyAtItsCenterWithoutSquashing.
        var source = new MotionRect(100, 200, 600, 120);
        MotionTransform hidden = ToolbarSlideMotion.AnnotationTransform(source, source.Center);
        foreach (var appearing in new[] { false, true })
        {
            LayerMotion motion = Motion(Style.Slide, appearing, 0.3, hidden: hidden);
            MotionTransform[] values = motion.Transform.Values;
            Assert.Equal(25, values.Length);
            Assert.All(values, value => Assert.Equal(0, value.B));
            Assert.All(values, value => Assert.True(value.Tx == 0 && value.Ty == 0));
            if (appearing)
            {
                Assert.True(values[^1].IsIdentity);
            }
            else
            {
                Assert.True(values[^1].A < 0.1);
            }
        }
        LayerMotion reverse = Motion(Style.Slide, true, 0.3, new LayerState(MotionTransform.MakeScale(0.4), 1), hidden);
        Assert.Equal(0.4, reverse.Transform.Values[0].A);
        Assert.All(reverse.Transform.Values, value => Assert.True(value.A >= 0.4));
    }

    [Fact]
    public void GatherKeepsSizeEarlyInAHideAndGrowsEarlyInAShow()
    {
        // ToolbarSlideMotionTests.testGatherKeepsSizeEarlyInAHideAndGrowsEarlyInAShow.
        var dock = new MotionTransform(0.1, 0, 400, 0);
        foreach (var appearing in new[] { false, true })
        {
            LayerMotion motion = Motion(Style.Slide, appearing, 0.3, hidden: dock, width: 200, height: 100);
            double[] scales = [.. motion.Transform.Values.Select(value => value.A)];
            if (appearing)
            {
                Assert.Equal(1, scales[20], 4);
                Assert.Equal(MotionCurves.Enter, motion.Transform.Curve);
            }
            else
            {
                Assert.Equal(1, scales[4], 4);
                Assert.Equal(MotionCurves.Exit, motion.Transform.Curve);
            }
            for (var index = 1; index < scales.Length; index++)
            {
                Assert.True(appearing ? scales[index - 1] <= scales[index] + 0.0001 : scales[index - 1] + 0.0001 >= scales[index]);
            }
        }
    }

    [Fact]
    public void ReversalsFinishSoonerWhenLittleDistanceIsLeft()
    {
        // ToolbarSlideMotionTests.testReversalsFinishSoonerWhenLittleDistanceIsLeft.
        var dock = new MotionTransform(0.1, 0, 400, 0);
        LayerMotion near = Motion(Style.Slide, true, 0.36, new LayerState(new MotionTransform(0.95, 0, 20, 0), 1), dock, width: 200, height: 100);
        Assert.Equal(0.36 * MotionTiming.MinimumReversalShare, near.EffectiveDuration, 3);
        LayerMotion middle = Motion(Style.Slide, true, 0.36, new LayerState(new MotionTransform(0.5, 0, 200, 0), 1), dock, width: 200, height: 100);
        Assert.Equal(0.36 * 0.5 / 0.9, middle.EffectiveDuration, 3);
        LayerMotion pose = Motion(Style.Pop, true, 0.34, new LayerState(MotionTransform.Identity, 1, 0.2), poseSeed: 1, width: 200, height: 100);
        Assert.Equal(0.34 * MotionTiming.MinimumReversalShare, pose.EffectiveDuration, 3);
        LayerMotion fresh = Motion(Style.Slide, true, 0.36, hidden: dock, width: 200, height: 100);
        Assert.Equal(0.36, fresh.EffectiveDuration);
    }

    [Fact]
    public void ReversalStartsAtTheVisibleTransformAndOpacityForEveryStyle()
    {
        // ToolbarSlideMotionTests.testReversalStartsAtTheVisibleTransformAndOpacityForEveryStyle.
        foreach (Style style in VisibilityAnimationStyle.AllCases)
        {
            if (style == Style.None)
            {
                continue;
            }
            var state = new LayerState(MotionTransform.MakeTranslation(0, 4), 0.4);
            LayerMotion motion = Motion(style, true, 0.26, state);
            Assert.Equal(state.Transform, motion.Transform.Values[0]);
            Assert.Equal(0.4, motion.Opacity.Values[0]);
            Assert.Equal(1, motion.Opacity.Values[^1]);
            Assert.Equal(MotionCurves.Standard, motion.Transform.Curve);
        }
    }

    [Fact]
    public void LayerTimelinesReverseTheirEndpoints()
    {
        // ToolbarSlideMotionTests.testLayerTimelinesReverseTheirEndpointsAndRestoreOriginalMask (endpoints).
        foreach (Style style in VisibilityAnimationStyle.AllCases)
        {
            if (style is Style.None or Style.Fold or Style.Reveal or Style.Curtain)
            {
                continue;
            }
            LayerMotion show = new(new LayerMotionOptions { Style = style, Appearing = true, Flipped = true, Duration = LayerMotion.Duration(style, true), Width = 600, Height = 140 });
            LayerMotion hide = new(new LayerMotionOptions { Style = style, Appearing = false, Flipped = true, Duration = LayerMotion.Duration(style, false), Width = 600, Height = 140 });
            Assert.Equal(show.Transform.Values[0], hide.Transform.Values[^1]);
            Assert.Equal(show.Transform.Values[^1], hide.Transform.Values[0]);
            Assert.Equal(show.Opacity.Values[0], hide.Opacity.Values[^1]);
            Assert.Equal(show.Opacity.Values[^1], hide.Opacity.Values[0]);
            Assert.True(show.EffectiveDuration <= (style == Style.Magic ? 1 : style == Style.Dissolve ? 0.7 : 0.4));
        }
    }

    [Fact]
    public void EveryStyleHasBoundedGeometryAndFullSizeEndpoints()
    {
        // ToolbarSlideMotionTests.testEveryStyleHasBoundedGeometryAndFullSizeEndpoints, for the styles that still play.
        foreach ((double width, double height) in new[] { (880.0, 220.0), (160.0, 720.0) })
        {
            foreach (Style style in VisibilityAnimationStyle.AllCases)
            {
                if (style is Style.None or Style.Fold)
                {
                    continue;
                }
                foreach (var flipped in new[] { true, false })
                {
                    for (var step = 0; step <= 100; step++)
                    {
                        MotionTransform transform = LayerMotion.ContentTransform(style, width, height, step / 100.0, flipped);
                        Assert.True(transform.A >= 0.98);
                        Assert.True(Math.Abs(transform.Tx) <= 12 && Math.Abs(transform.Ty) <= 12);
                        if (step == 100)
                        {
                            Assert.True(transform.IsIdentity);
                        }
                    }
                }
                foreach (var appearing in new[] { true, false })
                {
                    var duration = LayerMotion.Duration(style, appearing);
                    Assert.True(duration > 0 && duration <= (style == Style.Magic ? 1 : style == Style.Dissolve ? 0.7 : 0.4));
                }
            }
        }
        Assert.Equal(-40, LayerMotion.ContentTransform(Style.Rise, 400, 200, 0, flipped: false, annotation: true).Ty);
    }

    [Fact]
    public void PoseStylesEndExactlyAtRestOrHiddenAndStayBounded()
    {
        // ToolbarSlideMotionTests.testPoseStylesEndExactlyAtRestOrHiddenAndStayBounded.
        foreach (Style style in new[] { Style.Pop, Style.Drop, Style.Focus })
        {
            foreach (var annotation in new[] { true, false })
            {
                foreach (var appearing in new[] { true, false })
                {
                    LayerMotion motion = Motion(style, appearing, LayerMotion.Duration(style, appearing), annotation: annotation, poseSeed: 7,
                        width: 300, height: 120);
                    Assert.True(motion.EffectiveDuration <= 0.4);
                    MotionTransform[] transforms = motion.Transform.Values;
                    double[] opacities = motion.Opacity.Values;
                    Assert.Equal(VisibilityPose.StepCount + 1, transforms.Length);
                    if (appearing)
                    {
                        Assert.True(transforms[^1].IsIdentity, $"{style}");
                        Assert.Equal(1, opacities[^1]);
                        Assert.Equal(0, opacities[0], 3);
                    }
                    else
                    {
                        Assert.True(transforms[0].IsIdentity, $"{style}");
                        Assert.Equal(1, opacities[0]);
                        Assert.Equal(0, opacities[^1], 3);
                    }
                    Assert.All(transforms, transform => Assert.True(transform.Scale <= 1.07 && Math.Abs(transform.Ty) <= 111));
                }
            }
        }
    }

    [Fact]
    public void DropLandsWithOneSmallBounceAndFallsWithGravity()
    {
        // ToolbarSlideMotionTests.testDropLandsWithOneSmallBounceAndFallsWithGravity.
        double[] lifts = [.. Motion(Style.Drop, true, 0.4, annotation: true, poseSeed: 3, width: 200, height: 100).Transform.Values.Select(value => value.Ty)];
        Assert.True(lifts[0] > 40, "Drop arrives from above.");
        Assert.True(lifts.Min() < -2, "It dips slightly below its place once.");
        Assert.True(lifts.Min() > -10);
        double[] falls = [.. Motion(Style.Drop, false, 0.32, annotation: true, poseSeed: 3, width: 200, height: 100).Transform.Values.Select(value => -value.Ty)];
        Assert.True(falls[VisibilityPose.StepCount / 2] < falls[^1] * 0.3, "Falling accelerates instead of moving evenly.");
    }

    [Fact]
    public void SoftFocusBlursUpTo12PointsAndTheToolbar8()
    {
        // ToolbarSlideMotionTests.testSoftFocusAnimatesABlurOnTheSnapshotContainerAndCleansIt (the blur values).
        LayerMotion motion = Motion(Style.Focus, false, 0.28, annotation: true, poseSeed: 1);
        Assert.NotNull(motion.Blur);
        Assert.Equal(0, motion.Blur!.Values[0]);
        Assert.Equal(12, motion.Blur.Values[^1], 3);
        Assert.Equal(8, Motion(Style.Focus, false, 0.28, poseSeed: 0).Blur!.Values[^1], 3);
        Assert.Null(Motion(Style.Pop, false, 0.26, poseSeed: 0).Blur);
    }

    [Fact]
    public void FadeUsesTheFadeCurveInAndLinearOut()
    {
        LayerMotion show = Motion(Style.Fade, true, 0.2);
        Assert.Equal(MotionCurves.Fade, show.Opacity.Curve);
        Assert.Equal([0.0, 1.0], show.Opacity.Values);
        LayerMotion hide = Motion(Style.Fade, false, 0.16);
        Assert.True(hide.Opacity.Curve.IsLinear);
        Assert.Equal(0.5, hide.StateAt(0.08).Opacity, 12);
        LayerMotion gather = Motion(Style.Slide, true, 0.36, hidden: new MotionTransform(0.1, 0, 300, 0));
        Assert.Equal([0, 0.2, 1], gather.Opacity.KeyTimes);
        LayerMotion gone = Motion(Style.Slide, false, 0.28, hidden: new MotionTransform(0.1, 0, 300, 0));
        Assert.Equal([0, 0.72, 1], gone.Opacity.KeyTimes);
        Assert.Equal(1, gone.StateAt(0.28 * 0.72).Opacity, 9);
    }

    [Fact]
    public void ReversalFromAnInterruptedPoseBlendsWithoutJumping()
    {
        LayerMotion hide = Motion(Style.Pop, false, 0.26, annotation: true, poseSeed: 5);
        LayerState current = hide.StateAt(0.13);
        Assert.NotNull(current.Progress);
        LayerMotion show = Motion(Style.Pop, true, 0.34, current, annotation: true, poseSeed: 5);
        Assert.True(show.Transform.Values[0].IsClose(current.Transform, 1e-12));
        Assert.Equal(current.Opacity, show.Opacity.Values[0], 12);
        Assert.True(show.Transform.Values[^1].IsIdentity);
        Assert.Equal(0.34 * Math.Max(MotionTiming.MinimumReversalShare, current.Progress!.Value), show.EffectiveDuration, 12);
    }

    [Fact]
    public void FlightsSwoopCalmlyWithExactEndpointsInBothDirections()
    {
        // MagicFlightTests.testFlightsSwoopCalmlyWithExactEndpointsInBothDirections.
        var dock = new MotionTransform(0.04, 0, 700, 0);
        foreach (var seed in new ulong[] { 11, 22, 33, ulong.MaxValue })
        {
            foreach ((MotionTransform from, MotionTransform to) in new[] { (MotionTransform.Identity, dock), (dock, MotionTransform.Identity) })
            {
                MotionTransform[] frames = MagicFlight.Transforms(from, to, seed);
                Assert.Equal(MagicFlight.Steps + 1, frames.Length);
                Assert.Equal(from, frames[0]);
                Assert.Equal(to, frames[^1]);
                int[] signs = [.. frames.Where(frame => Math.Abs(frame.Ty) >= 0.01).Select(frame => frame.Ty > 0 ? 1 : -1)];
                var bends = signs.Zip(signs.Skip(1)).Count(pair => pair.First != pair.Second);
                Assert.InRange(bends, 1, 3);
                var early = frames.Take(MagicFlight.Steps / 2).Max(frame => Math.Abs(frame.Ty));
                Assert.True(frames.Skip(MagicFlight.Steps * 3 / 4).Max(frame => Math.Abs(frame.Ty)) < early,
                    "The swoop is strongest early and settles before landing.");
                foreach (MotionTransform frame in frames)
                {
                    Assert.True(double.IsFinite(frame.Tx) && double.IsFinite(frame.Ty));
                    Assert.True(Math.Abs(frame.Ty) <= MagicFlight.MaximumBend + 0.001);
                    Assert.True(Math.Abs(frame.Angle) <= MagicFlight.MaximumBank + 0.001);
                }
            }
        }
    }

    [Fact]
    public void MagicIsFasterThanTheOriginalFlight()
    {
        // MagicFlightTests.testMagicIsFasterThanTheOriginalFlight.
        foreach (var appearing in new[] { true, false })
        {
            Assert.True(LayerMotion.Duration(Style.Magic, appearing) <= 0.62);
            Assert.True(new VisibilityMotionTiming(Style.Magic, appearing, hasDrawings: true).ToolbarDuration <= 0.4);
        }
    }

    [Fact]
    public void RoutesAreSeededAndInterruptedFlightsStartAtTheirCurrentTransform()
    {
        // MagicFlightTests.testRoutesAreSeededAndInterruptedFlightsStartAtTheirCurrentTransform.
        var dock = new MotionTransform(0.05, 0, -600, 300);
        var seed = MagicFlight.Seed("shape-a");
        MotionTransform[] first = MagicFlight.Transforms(MotionTransform.Identity, dock, seed);
        Assert.Equal(first, MagicFlight.Transforms(MotionTransform.Identity, dock, seed));
        Assert.NotEqual(first, MagicFlight.Transforms(MotionTransform.Identity, dock, MagicFlight.Seed("shape-b")));
        MotionTransform current = first[37];
        MotionTransform[] reversed = MagicFlight.Transforms(current, MotionTransform.Identity, seed);
        Assert.Equal(current, reversed[0]);
        Assert.True(reversed[MagicFlight.Steps].IsIdentity);
        Assert.Equal(0xcbf29ce484222325UL, MagicFlight.Seed(""));
        Assert.Equal(0xaf63dc4c8601ec8cUL, MagicFlight.Seed("a"));
    }

    [Fact]
    public void TrailsShareTheFlightTimeline()
    {
        // MagicFlightTests.testTrailsShareTheFlightTimelineAndLeaveNoLayersBehind: three strokes and six sparks.
        var dock = new MotionTransform(0.05, 0, 500, 100);
        LayerMotion motion = Motion(Style.Magic, false, 0.75, hidden: dock, magicSeed: 42, width: 100, height: 80);
        MagicTrailGeometry trail = MagicTrailGeometry.Create(new MotionPoint(250, 240), motion.FlightTransforms, 42);
        Assert.Equal(3, MagicTrailGeometry.Strokes.Length);
        Assert.Equal(6, trail.Sparks.Length);
        Assert.Equal(motion.FlightTransforms.Length, trail.Points.Length);
        Assert.Equal(0, trail.Ends[0]);
        Assert.Equal(1, trail.Ends[^1], 12);
        Assert.All(trail.Starts.Zip(trail.Ends), pair => Assert.True(pair.First <= pair.Second));
        Assert.Equal(0, MagicTrailGeometry.Fade(0.85)[^1]);
        Assert.Equal(new MotionPoint(250, 240), trail.Points[0]);
        Assert.Equal(new MotionPoint(750, 340), trail.Points[^1]);
        Assert.Equal(MagicFlight.TimingFunction, motion.Transform.Curve);
        Assert.Equal([0, 0.82, 1], motion.Opacity.KeyTimes);
        Assert.Equal(MagicFlight.TimingFunction, motion.Opacity.Curve);
        Assert.Throws<CanvasError>(() => MagicTrailGeometry.Create(new MotionPoint(0, 0), [MotionTransform.Identity], 1));
    }

    [Fact]
    public void ToolbarPoofShrinksToAPointThenBursts()
    {
        MagicToolbarPoof.Pose rest = MagicToolbarPoof.PoseAt(0);
        Assert.Equal(1, rest.Scale);
        Assert.Equal(1, rest.ImageOpacity);
        Assert.Equal(0, rest.GlowOpacity);
        MagicToolbarPoof.Pose point = MagicToolbarPoof.PoseAt(0.62);
        Assert.Equal(0.015, point.Scale, 9);
        Assert.Equal(0, point.Radius);
        Assert.Equal(0, MagicToolbarPoof.PoseAt(0.64).ImageOpacity);
        MagicToolbarPoof.Pose gone = MagicToolbarPoof.PoseAt(1);
        Assert.Equal(46, gone.Radius);
        Assert.Equal(0, gone.SparkOpacity);
        Assert.Equal(0, gone.GlowOpacity);
        Assert.Equal(0.36 * 0.5, MagicToolbarPoof.Duration(0.36, 0.5, 1, reversing: true), 12);
        Assert.Equal(12, MagicToolbarPoof.SparkCount);
        Assert.Equal(0.7 * 10, MagicToolbarPoof.SparkOffset(0, 10).X, 12);
    }

    [Fact]
    public void NearestAssignmentsAreInsetAndRemainOnEachDisplay()
    {
        // CornerRoutesTests.testNearestAssignmentsAreInsetAndRemainOnEachDisplay.
        foreach (var count in new[] { 0, 1, 2, 3, 4, 5, 9, 63, 64 })
        {
            var routes = new CornerRoutes();
            MotionRect[] screens = [new(-1400, 80, 1400, 900), new(0, -200, 1200, 1000)];
            var items = screens.SelectMany((frame, display) => Enumerable.Range(0, count).Select(index =>
                new CornerRoutes.Item($"{display}-{index}", display.ToString(System.Globalization.CultureInfo.InvariantCulture), frame,
                                      CornerRoutes.Point(index % 4, frame)))).ToList();
            routes.Update(items);
            Assert.Equal(count * screens.Length, routes.Destinations.Count);
            for (var display = 0; display < screens.Length; display++)
            {
                MotionRect frame = screens[display];
                var index = 0;
                foreach (CornerRoutes.Item item in items.Where(item => item.Display == display.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                {
                    CornerRoutes.Destination destination = routes.Destinations[item.Id];
                    Assert.True(destination.Point.X > frame.MinX && destination.Point.X < frame.MaxX);
                    Assert.True(destination.Point.Y > frame.MinY && destination.Point.Y < frame.MaxY);
                    Assert.Equal(index % 4, destination.Corner);
                    index++;
                }
            }
        }
    }

    [Fact]
    public void PairReusesAssignmentsButNewMembershipOrLayoutRebuilds()
    {
        // CornerRoutesTests.testPairReusesAssignmentsButNewMembershipOrLayoutRebuilds.
        var routes = new CornerRoutes();
        var frame = new MotionRect(0, 0, 1200, 900);
        CornerRoutes.Item[] items = [.. Enumerable.Range(0, 6).Select(index =>
            new CornerRoutes.Item($"{index}", "1", frame, new MotionPoint(50 + index * 5, 850)))];
        routes.Update(items);
        var original = routes.Destinations.ToDictionary(pair => pair.Key, pair => pair.Value.Point);
        Assert.All(routes.Destinations.Values, destination => Assert.Equal(0, destination.Corner));
        routes.Update(Enumerable.Reverse(items));
        Assert.Equal(original, routes.Destinations.ToDictionary(pair => pair.Key, pair => pair.Value.Point));
        routes.Update(items[..^1]);
        Assert.False(routes.Destinations.ContainsKey("5"));
        routes.Update(items.Select(item => item with { VisibleFrame = frame.Offset(-1300, 0), Center = new MotionPoint(item.Center.X - 1300, item.Center.Y) }));
        Assert.All(routes.Destinations.Values, destination => Assert.True(destination.Point.X < 0));
        routes.Update([new CornerRoutes.Item("moved", "1", frame, new MotionPoint(1150, 50))]);
        Assert.Equal(3, routes.Destinations["moved"].Corner);
        routes.Update([new CornerRoutes.Item("tie", "1", frame, frame.Center)]);
        Assert.Equal(0, routes.Destinations["tie"].Corner);
    }

    [Fact]
    public void SemiDirectArcRetracesTheSameRouteWithoutBanking()
    {
        // CornerRoutesTests.testSemiDirectArcRetracesTheSameRouteWithoutBanking.
        var hidden = new MotionTransform(0.1, 0, -600, 400);
        MotionTransform[] Frames(bool appearing) =>
            Motion(Style.Corners, appearing, 0.4, hidden: hidden, curveSign: appearing ? -1 : 1, width: 120, height: 80).Transform.Values;
        MotionTransform[] hiding = Frames(false), showing = Frames(true);
        foreach ((MotionTransform outward, MotionTransform back) in hiding.Zip(Enumerable.Reverse(showing)))
        {
            Assert.Equal(outward.Tx, back.Tx, 6);
            Assert.Equal(outward.Ty, back.Ty, 6);
            Assert.Equal(0, outward.B);
        }
        Assert.True(hiding[0].IsIdentity);
        Assert.Equal(hidden.Tx, hiding[^1].Tx);
    }

    [Fact]
    public void ParticleBurstUsesADenseBoundedGrid()
    {
        // ToolbarSlideMotionTests.testParticleBurstUsesDenseBoundedGPUGridAndStopsDrawing (the grid and progress).
        foreach (var limit in new[] { 1, 12_000, 32_000 })
        {
            ParticleField.GridSize grid = ParticleField.Grid(101.5, 50.5, limit);
            Assert.InRange(grid.Count, 1, limit);
        }
        ParticleField.GridSize toolbar = ParticleField.Grid(600, 200, 32_000);
        Assert.InRange(toolbar.Count, 20_001, 32_000);
        Assert.Equal(0.4, ParticleField.Progress(0.4, 0, 0), 12);
        Assert.Equal(0.65 * 0.4, ParticleField.Duration(0.65, 0.4, 0, reversing: true), 12);
        Assert.Throws<ArgumentOutOfRangeException>(() => ParticleField.Grid(0, 10, 10));
    }

    [Fact]
    public void ParticlesDisperseOutwardAndFinishFullyTransparent()
    {
        // ToolbarSlideMotionTests.testParticlePixelsDisperseOutwardAndFinishFullyTransparent, on the shared math.
        ParticleField.GridSize grid = ParticleField.Grid(60, 40, 32_000);
        var outside = 0;
        for (uint id = 0; id < grid.Count; id++)
        {
            ParticleField.Particle start = ParticleField.At(id, grid, 100, 100, 60, 40, 0, -Math.PI / 2, 2);
            Assert.Equal(0, start.Opacity);
            ParticleField.Particle middle = ParticleField.At(id, grid, 100, 100, 60, 40, 0.5, -Math.PI / 2, 2);
            if (middle.Opacity > 0.02 && (middle.X < 100 || middle.X > 160 || middle.Y < 100 || middle.Y > 140))
            {
                outside++;
            }
            Assert.Equal(middle, ParticleField.At(id, grid, 100, 100, 60, 40, 0.5, -Math.PI / 2, 2));
            Assert.Equal(0, ParticleField.At(id, grid, 100, 100, 60, 40, 1, -Math.PI / 2, 2).Opacity, 12);
        }
        Assert.True(outside > 100, "Fine particles must leave the original element, not shrink into it.");
        Assert.Equal(1, ParticleField.ImageOpacity(0));
        Assert.Equal(0, ParticleField.ImageOpacity(0.18));
        // The edge facing the toolbar (up) breaks first.
        Assert.Equal(0, ParticleField.SweepAlong(0.5, 0, Math.PI / 2), 12);
        Assert.Equal(1, ParticleField.SweepAlong(0.5, 1, Math.PI / 2), 12);
    }
}
