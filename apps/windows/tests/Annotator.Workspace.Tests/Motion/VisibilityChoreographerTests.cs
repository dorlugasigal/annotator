using Annotator.Core;
using Annotator.Workspace.Motion;
using Style = Annotator.Core.VisibilityAnimationStyle;

namespace Annotator.Workspace.Tests.Motion;

/// <summary>
/// The planning of WorkspaceVisibilityAnimator.transition: ports of WorkspaceVisibilityTests (each shape's delay, the
/// shared shuffle, reversal) and CornerRoutesTests (the corner lifecycle), plus the budgets of spec 009.
/// </summary>
public sealed class VisibilityChoreographerTests
{
    private static readonly MotionDisplay Display = new("display", new Rect(0, 0, 2560, 1440), new Rect(0, 0, 2048, 1113.6), 1.25);
    private static readonly MotionToolbar Toolbar = new(new Rect(780, 15, 1000, 90), Display, ToolbarOrientation.Horizontal, ToolbarDockEdge.Automatic);

    private static MotionSnapshot Shape(Guid id, Rect frame) =>
        new($"doc/{id}", id, Display, frame, frame.Center, (long)(frame.Width * 1.25 * frame.Height * 1.25));

    private static MotionRequest Request(bool showing, Style style, double now, IReadOnlyList<MotionSnapshot> snapshots, object? version = null) => new()
    {
        Showing = showing,
        Style = style,
        Now = now,
        Snapshots = snapshots,
        Toolbar = Toolbar,
        SceneVersion = version ?? "scene",
        ToolbarPixels = 1000 * 90,
    };

    [Fact]
    public void EachShapeHasItsOwnDelayAndReversalKeepsTheSameShuffle()
    {
        // WorkspaceVisibilityTests.testEachShapeHasItsOwnDelayAndReversalKeepsTheSameShuffle.
        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        MotionSnapshot[] shapes = [.. ids.Select((id, index) => Shape(id, new Rect(100 + index * 150, 200, 90, 60)))];
        var shuffles = 0;
        var choreographer = new VisibilityChoreographer(keys =>
        {
            shuffles++;
            return [.. Enumerable.Reverse(keys)];
        });
        VisibilityPlan hide = choreographer.Plan(Request(false, Style.Dissolve, 10, shapes));
        IReadOnlyDictionary<Guid, double> hiding = choreographer.ObjectDelays;
        Assert.Equal(3, hiding.Count);
        Assert.Equal(3, hiding.Values.Distinct().Count());
        Assert.True(hiding[ids[2]] < hiding[ids[0]]);
        Assert.Equal(3, hide.Snapshots.Count);
        Assert.Equal(ToolbarSnapshotKind.Particles, hide.Toolbar!.Kind);

        VisibilityPlan show = choreographer.Plan(Request(true, Style.Dissolve, 10.2, shapes));
        Assert.True(show.Reversing);
        Assert.All(choreographer.ObjectDelays.Values, delay => Assert.Equal(0, delay));
        Assert.Equal(1, shuffles);
        Assert.All(show.Snapshots, plan => Assert.True(plan.Particles!.From is > 0 and < 1, "Particles resume from their progress."));

        choreographer.Clear();
        choreographer.Plan(Request(false, Style.Dissolve, 20, shapes));
        choreographer.Clear();
        choreographer.Plan(Request(true, Style.Dissolve, 21, shapes));
        IReadOnlyDictionary<Guid, double> freshShow = choreographer.ObjectDelays;
        Assert.True(freshShow[ids[0]] < freshShow[ids[2]]);
        Assert.Equal(2, shuffles);
    }

    [Fact]
    public void HideOrdersByDistanceToTheToolbarAndShowReplaysInReverse()
    {
        // Spec 009: the shape nearest its destination leaves first; show uses the reverse order after the toolbar starts.
        Guid near = Guid.NewGuid(), far = Guid.NewGuid();
        MotionSnapshot[] shapes = [Shape(far, new Rect(1500, 900, 80, 80)), Shape(near, new Rect(900, 200, 80, 80))];
        var choreographer = new VisibilityChoreographer();
        VisibilityPlan hide = choreographer.Plan(Request(false, Style.Slide, 0, shapes));
        Assert.True(choreographer.ObjectDelays[near] < choreographer.ObjectDelays[far]);
        var arrivals = hide.Snapshots.Max(plan => plan.Motion.EndTime);
        Assert.True(hide.Toolbar!.Delay >= arrivals, "Gather closes the toolbar after the last shape arrives.");
        Assert.Equal(ToolbarSnapshotKind.Gather, hide.Toolbar.Kind);
        Assert.Equal(ToolbarSlideMotion.PulseTimes(hide.Snapshots.Select(plan => plan.Motion.EndTime)), hide.Toolbar.Pulses);
        Assert.Equal(hide.Toolbar.Delay + hide.Toolbar.Duration, hide.TotalDuration, 12);
        choreographer.Clear();
        VisibilityPlan show = choreographer.Plan(Request(true, Style.Slide, 5, shapes));
        Assert.True(choreographer.ObjectDelays[far] < choreographer.ObjectDelays[near]);
        Assert.Equal(0, show.Toolbar!.Delay);
        Assert.Empty(show.Toolbar.Pulses);
        Assert.True(show.Snapshots.Min(plan => plan.Delay) >= show.Toolbar.Duration);
    }

    [Fact]
    public void GatheredShapesShrinkTowardTheToolbarCenterInYUpSpace()
    {
        Guid id = Guid.NewGuid();
        var frame = new Rect(900, 600, 120, 60);
        var choreographer = new VisibilityChoreographer();
        VisibilityPlan hide = choreographer.Plan(Request(false, Style.Slide, 0, [Shape(id, frame)]));
        MotionTransform end = hide.Snapshots[0].Motion.Transform.Values[^1];
        Assert.Equal(Math.Min(0.12, 24.0 / 120), end.A, 12);
        MotionRect toolbar = Display.DesktopToMotion(Toolbar.DesktopFrame);
        MotionRect source = Display.ToMotion(frame);
        Assert.Equal(toolbar.MidX, source.MidX + end.Tx, 9);
        Assert.Equal(toolbar.MidY, source.MidY + end.Ty, 9);
        // On a Windows screen the shape moves up to the toolbar near the top.
        Assert.True(end.ToScreen().Ty < 0);
    }

    [Fact]
    public void CornerLifecycleReusesTargetsAndRebuildsChangedContent()
    {
        // CornerRoutesTests.testCornerLifecycleReusesTargetsAndRebuildsChangedContent.
        MotionSnapshot[] shapes = [.. Enumerable.Range(0, 5).Select(index => Shape(Guid.NewGuid(),
            new Rect(60 + index * 15, 100 + index * 15, 40, 40)))];
        var choreographer = new VisibilityChoreographer(keys => [.. Enumerable.Reverse(keys)]);
        choreographer.Plan(Request(false, Style.Corners, 0, shapes));
        IReadOnlyDictionary<Guid, MotionPoint> destinations = choreographer.CornerTargets;
        Assert.Equal(5, destinations.Count);
        Assert.Single(destinations.Values.Distinct());
        choreographer.Clear();
        VisibilityPlan show = choreographer.Plan(Request(true, Style.Corners, 2, shapes));
        Assert.Equal(destinations, choreographer.CornerTargets);
        Assert.All(show.Snapshots, plan => Assert.Null(plan.Trail));
        Assert.Equal(ToolbarSnapshotKind.Gather, show.Toolbar!.Kind);
        choreographer.Clear();
        choreographer.Plan(Request(false, Style.Corners, 4, shapes));
        choreographer.Clear();
        MotionSnapshot added = Shape(Guid.NewGuid(), new Rect(300, 400, 60, 60));
        choreographer.Plan(Request(true, Style.Corners, 6, [.. shapes, added], version: "changed"));
        Assert.Equal(6, choreographer.CornerTargets.Count);
        Assert.True(choreographer.CornerTargets.ContainsKey(added.ElementId!.Value), "A stale snapshot must not hide newly added work.");
        // Top-left shapes go to the top-left corner of the work area, 24 points in.
        Assert.Equal(Display.VisibleFrame.MinX + 24, destinations.Values.First().X, 12);
        Assert.Equal(Display.VisibleFrame.MaxY - 24, destinations.Values.First().Y, 12);
    }

    [Theory]
    [InlineData(Style.Dissolve, ToolbarSnapshotKind.Particles)]
    [InlineData(Style.Slide, ToolbarSnapshotKind.Gather)]
    [InlineData(Style.Magic, ToolbarSnapshotKind.Poof)]
    [InlineData(Style.Corners, ToolbarSnapshotKind.Gather)]
    [InlineData(Style.Pop, ToolbarSnapshotKind.Pose)]
    [InlineData(Style.Drop, ToolbarSnapshotKind.Pose)]
    [InlineData(Style.Focus, ToolbarSnapshotKind.Pose)]
    [InlineData(Style.Fade, ToolbarSnapshotKind.None)]
    public void EachStyleHasItsOwnTreatment(Style style, ToolbarSnapshotKind toolbar)
    {
        MotionSnapshot board = new("doc/board", null, Display, new Rect(0, 0, Display.Width, Display.Height),
                                   new Point(Display.Width / 2, Display.Height / 2), 2560L * 1440);
        MotionSnapshot shape = Shape(Guid.NewGuid(), new Rect(400, 300, 200, 100));
        var choreographer = new VisibilityChoreographer();
        VisibilityPlan plan = choreographer.Plan(Request(false, style, 0, [board, shape]));
        Assert.Equal(toolbar, plan.Toolbar!.Kind);
        SnapshotPlan boardPlan = plan.Snapshots[0], shapePlan = plan.Snapshots[1];
        var boardFades = style is Style.Magic or Style.Corners or Style.Pop or Style.Drop or Style.Focus;
        Assert.Equal(boardFades ? Style.Fade : style, boardPlan.ImageStyle);
        Assert.Equal(style, shapePlan.ImageStyle);
        Assert.Equal(style == Style.Magic, shapePlan.Trail is not null);
        Assert.Equal(style == Style.Dissolve, shapePlan.Particles is not null);
        Assert.Equal(style == Style.Focus, shapePlan.Blur);
        if (style == Style.Dissolve)
        {
            Assert.Equal(ParticleField.DrawingLimit / 2, shapePlan.Particles!.Limit);
            Assert.Equal(ParticleField.ToolbarLimit, plan.Toolbar.Particles!.Limit);
            Assert.Equal(-Math.PI / 2, plan.Toolbar.Particles.SweepAngle);
            // The shape breaks up from the edge facing the toolbar, which is above it.
            Assert.True(shapePlan.Particles.SweepAngle > 0);
        }
        // The board starts after every shape on a hide and with the toolbar on a show.
        var timing = new VisibilityMotionTiming(style, false, true, 1);
        Assert.Equal(timing.DrawingsEndDelay, boardPlan.Delay, 12);
        Assert.Equal(timing.TotalDuration, plan.TotalDuration, 12);
        Assert.Equal(plan.TotalDuration + 0.02, plan.Deadline, 12);
    }

    [Fact]
    public void ReversalsFinishWithTheirLongestTimeline()
    {
        MotionSnapshot shape = Shape(Guid.NewGuid(), new Rect(400, 300, 200, 100));
        var choreographer = new VisibilityChoreographer();
        VisibilityPlan hide = choreographer.Plan(Request(false, Style.Magic, 0, [shape]));
        VisibilityPlan show = choreographer.Plan(Request(true, Style.Magic, 0.2, [shape]));
        Assert.True(show.Reversing);
        Assert.Equal(0, show.Toolbar!.Delay);
        var ends = new List<double> { show.Snapshots[0].Motion.EndTime, show.Toolbar.RealMotion.EndTime, show.Toolbar.Poof!.EndTime };
        Assert.Equal(ends.Max(), show.TotalDuration, 12);
        Assert.True(show.TotalDuration < new VisibilityMotionTiming(Style.Magic, true, true).TotalDuration);
        LayerState current = hide.Snapshots[0].Motion.StateAt(0.2);
        Assert.True(show.Snapshots[0].Motion.FlightTransforms[0].IsClose(current.Transform, 1e-12), "Magic turns around from where the shape is.");
        Assert.Equal(hide.Toolbar!.Poof!.ProgressAt(0.2), show.Toolbar.Poof.From, 12);
        // A scene that changed starts over instead.
        VisibilityPlan fresh = choreographer.Plan(Request(false, Style.Magic, 0.3, [shape], version: "other"));
        Assert.False(fresh.Reversing);
    }

    [Fact]
    public void SnapshotsShareTheFortyMillionPixelBudget()
    {
        var other = new MotionDisplay("other", new Rect(2560, 0, 2560, 1440), new Rect(0, 0, 2048, 1152), 1.25);
        var huge = new MotionSnapshot("a/board", null, Display, new Rect(0, 0, Display.Width, Display.Height), new Point(1, 1), 30_000_000);
        var second = new MotionSnapshot("a/1", Guid.NewGuid(), Display, new Rect(10, 10, 10, 10), new Point(15, 15), 11_000_000);
        var third = new MotionSnapshot("a/2", Guid.NewGuid(), Display, new Rect(10, 10, 10, 10), new Point(15, 15), 100);
        var elsewhere = new MotionSnapshot("b/1", Guid.NewGuid(), other, new Rect(10, 10, 10, 10), new Point(15, 15), 100);
        VisibilityPlan plan = new VisibilityChoreographer().Plan(Request(false, Style.Fade, 0, [huge, second, third, elsewhere]));
        Assert.Equal(["a/board", "b/1"], plan.Snapshots.Select(snapshot => snapshot.Snapshot.Key));
        Assert.Contains(VisibilityChoreographer.BudgetMessage, plan.Messages);
    }

    [Fact]
    public void AnOversizedToolbarFallsBackToAFade()
    {
        MotionRequest request = Request(false, Style.Pop, 0, []) with { ToolbarPixels = 4_000_001 };
        VisibilityPlan plan = new VisibilityChoreographer().Plan(request);
        Assert.Equal(ToolbarSnapshotKind.None, plan.Toolbar!.Kind);
        Assert.Contains(VisibilityChoreographer.ToolbarFallbackMessage, plan.Messages);
        // Without drawings the toolbar starts at once and the plan ends with it.
        Assert.Equal(0, plan.Toolbar.Delay);
        Assert.Equal(LayerMotion.Duration(Style.Pop, false), plan.TotalDuration, 12);
    }

    [Fact]
    public void TheRealToolbarSlidesFromItsDockEdgeAndDissolveRises()
    {
        VisibilityPlan slide = new VisibilityChoreographer().Plan(Request(true, Style.Slide, 0, []));
        Assert.Equal(ToolbarDockEdge.Top, slide.Toolbar!.Edge);
        Assert.Equal(12, slide.Toolbar.RealMotion.Transform.Values[0].Ty);
        VisibilityPlan dissolve = new VisibilityChoreographer().Plan(Request(true, Style.Dissolve, 0, []));
        Assert.Equal(-10, dissolve.Toolbar!.RealMotion.Transform.Values[0].Ty);
        VisibilityPlan fade = new VisibilityChoreographer().Plan(Request(false, Style.Fade, 0, []));
        Assert.True(fade.Toolbar!.RealMotion.Transform.Values.All(value => value.IsIdentity));
        Assert.Equal([1.0, 0.0], fade.Toolbar.RealMotion.Opacity.Values);
    }
}
