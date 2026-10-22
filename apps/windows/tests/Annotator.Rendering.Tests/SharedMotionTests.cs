using System.Collections.Immutable;
using Annotator.Core;
using Annotator.Core.Scene;
using Annotator.Platform.Displays;
using Annotator.Platform.Threading;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Sharing;
using Annotator.Workspace.Motion;
using Windows.UI.Composition;
using Style = Annotator.Core.VisibilityAnimationStyle;

namespace Annotator.Rendering.Tests;

/// <summary>
/// The show and hide motion in the shared region's output (Mac SharedWorkspaceTransitionTests and the shared parts of
/// WorkspaceVisibilityAnimator): which snapshots get a copy, the budgets, reversal, the toolbar's copy, Dissolve's paced
/// particles, the hand-off and the release of everything. Composition objects on WARP; nothing is shown.
/// </summary>
public sealed class SharedMotionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static readonly MotionDisplay Display = new("display", new Rect(0, 0, 1000, 600), new Rect(0, 0, 1000, 560), 1);
    private static readonly MotionDisplay Other = new("other", new Rect(1000, 0, 800, 600), new Rect(1000, 0, 800, 560), 1);
    private static readonly MotionToolbar Toolbar = new(new Rect(300, 500, 400, 50), Display, ToolbarOrientation.Horizontal, ToolbarDockEdge.Bottom);

    /// <summary>The shared region: the left half, with a cover over a part of it.</summary>
    private static readonly SharedMotionArea Area = new("display", new PixelRect(0, 0, 500, 600), [new PixelRect(400, 80, 500, 180)]);

    private sealed record Shape(string Key, MotionDisplay Display, Rect Frame, bool Protection = false, RGBA? Board = null);

    private static readonly Shape[] Shapes =
    [
        new("inside", Display, new Rect(100, 100, 80, 60)),
        new("outside", Display, new Rect(700, 100, 80, 60)),
        new("cover", Display, new Rect(120, 300, 80, 60), Protection: true),
        new("other display", Other, new Rect(100, 100, 80, 60)),
        new("under the cover", Display, new Rect(420, 100, 40, 40)),
        new("across the edge", Display, new Rect(460, 300, 80, 60)),
    ];

    private static (VisibilityPlan Plan, SharedMotionSnapshot[] Snapshots) Plan(VisibilityChoreographer choreographer, bool showing, Style style,
                                                                                 double now, Shape[] shapes)
    {
        MotionSnapshot[] snapshots = [.. shapes.Select(shape => new MotionSnapshot(shape.Key, shape.Board is null ? Guid.NewGuid() : null, shape.Display,
            shape.Frame, shape.Frame.Center, (long)(shape.Frame.Width * shape.Frame.Height)))];
        VisibilityPlan plan = choreographer.Plan(new MotionRequest
        {
            Showing = showing,
            Style = style,
            Now = now,
            Snapshots = snapshots,
            Toolbar = Toolbar,
            SceneVersion = "scene",
            ToolbarPixels = 400 * 50,
        });
        SharedMotionSnapshot[] items = [.. plan.Snapshots.Select(item =>
        {
            Shape shape = shapes.Single(candidate => candidate.Key == item.Snapshot.Key);
            Rect frame = shape.Frame;
            var pixels = new PixelRect((int)frame.X, (int)frame.Y, (int)frame.MaxX, (int)frame.MaxY);
            DisplayList[]? lists = shape.Board is null ? [TestLists.Create(DisplayItem.FillRect(frame, Paint.Solid(new RGBA(1, 0, 0))))] : null;
            return new SharedMotionSnapshot(item, pixels, lists, shape.Board, shape.Protection, Particles: item.Particles is not null, Blurred: item.Blur);
        })];
        return (plan, items);
    }

    private static byte[] ToolbarPixels()
    {
        var bgra = new byte[400 * 50 * 4];
        Array.Fill(bgra, (byte)200);
        return bgra;
    }

    private static async Task OnCompositor(Action<Compositor, ContainerVisual, SharedMotion> test)
    {
        var thread = DispatcherThread.Start("Annotator shared motion test");
        try
        {
            await thread.RunAsync(() =>
            {
                using var compositor = new Compositor();
                using ContainerVisual root = compositor.CreateContainerVisual();
                using var motion = new SharedMotion(compositor, root, Warp.Devices, FixedSystemColors.Light, 1);
                test(compositor, root, motion);
            }).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }
        finally
        {
            await thread.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public Task OnlyDrawingsThatRestInTheUnprotectedRegionGetACopy() => OnCompositor((_, root, motion) =>
    {
        var (plan, snapshots) = Plan(new VisibilityChoreographer(), false, Style.Slide, 10, Shapes);
        motion.Play(plan, Area, snapshots, 10, 10);
        Assert.True(motion.IsActive);
        Assert.False(motion.IsFinishing);
        // Mac snapshots(): covers and effects stay live, other displays and what misses the region or lies wholly under
        // protection have no copy; a drawing across the region's edge keeps its part inside.
        Assert.Equal(2, motion.ImageCount);
        Assert.NotNull(motion.Copy("inside"));
        Assert.NotNull(motion.Copy("across the edge"));
        Assert.Null(motion.Copy("outside"));
        Assert.Null(motion.Copy("cover"));
        Assert.Null(motion.Copy("other display"));
        Assert.Null(motion.Copy("under the cover"));
        // A copy keeps the snapshot's own rectangle, so it scales and turns about the same center as on the overlay.
        Assert.Equal(new PixelRect(460, 300, 540, 360), motion.Copy("across the edge")!.Pixels);
        Assert.Single(root.Children);

        motion.Finish();
        Assert.True(motion.IsFinishing);
        Assert.Equal(2, motion.ImageCount);
        motion.HandOff(keepToolbar: false);
        Assert.False(motion.IsActive);
        Assert.Equal(0, motion.ImageCount);
        Assert.Empty(root.Children);
        Assert.Equal((1, 2, 0), motion.Statistics);
    });

    [Fact]
    public Task AReversalTurnsTheSameCopiesAroundAndANewPlanStartsOver() => OnCompositor((_, _, motion) =>
    {
        var choreographer = new VisibilityChoreographer();
        var (hide, hiding) = Plan(choreographer, false, Style.Slide, 10, Shapes);
        motion.Play(hide, Area, hiding, 10, 10);
        var copy = motion.Copy("inside");
        var (show, showing) = Plan(choreographer, true, Style.Slide, 10.2, Shapes);
        Assert.True(show.Reversing);
        motion.Play(show, Area, showing, 10.2, 10.2);
        Assert.Same(copy, motion.Copy("inside"));
        Assert.Equal(1, motion.Statistics.Transitions);

        // The protection moved: the copies are cut again.
        var moved = Area with { Protection = [new PixelRect(100, 100, 140, 140)] };
        var (back, again) = Plan(choreographer, false, Style.Slide, 10.3, Shapes);
        motion.Play(back, moved, again, 10.3, 10.3);
        Assert.NotSame(copy, motion.Copy("inside"));
        Assert.Equal(2, motion.Statistics.Transitions);

        choreographer.Clear();
        var (fresh, freshSnapshots) = Plan(choreographer, true, Style.Slide, 20, Shapes);
        Assert.False(fresh.Reversing);
        var before = motion.Copy("inside");
        motion.Play(fresh, moved, freshSnapshots, 20, 20);
        Assert.NotSame(before, motion.Copy("inside"));
        Assert.Equal(3, motion.Statistics.Transitions);
    });

    [Fact]
    public Task CopiesOverTheBudgetStopTheMotionWithTheMacsMessage() => OnCompositor((_, root, motion) =>
    {
        var huge = new MotionDisplay("display", new Rect(0, 0, 8000, 6000), new Rect(0, 0, 8000, 6000), 1);
        Shape[] shapes = [new("board", huge, new Rect(0, 0, 4000, 4001), Board: new RGBA(1, 1, 1))];
        var (plan, snapshots) = Plan(new VisibilityChoreographer(), false, Style.Fade, 0, shapes);
        var area = new SharedMotionArea("display", new PixelRect(0, 0, 8000, 6000), []);
        SharedMotionFailure failure = Assert.Throws<SharedMotionFailure>(() => motion.Play(plan, area, snapshots, 0, 0));
        Assert.Equal("Export dimensions exceed the supported raster budget.", failure.Message);
        Assert.False(motion.IsActive);
        Assert.Empty(root.Children);
        Assert.Equal(16_000_000, SharedMotion.SnapshotBudget);
        Assert.Equal(20_000_000, SharedMotion.ImageBudget);
    });

    [Fact]
    public Task TheToolbarsCopyPlaysInTheRegionAndHoldsUntilTheLiveToolbarShows() => OnCompositor((_, root, motion) =>
    {
        var choreographer = new VisibilityChoreographer();
        var (plan, snapshots) = Plan(choreographer, true, Style.Slide, 0, Shapes);
        motion.Play(plan, Area, snapshots, 0, 0);
        byte[] toolbar = ToolbarPixels();
        // Half of the toolbar lies in the region.
        motion.PlayToolbar(plan.Toolbar!, toolbar, 400, 50, new PixelRect(300, 500, 700, 550), blurred: false, particles: false, 0, 0, 0);
        Assert.True(motion.HasToolbar);
        Assert.False(motion.RealToolbar);
        Assert.Equal(3, motion.ImageCount);
        motion.Finish();
        motion.HandOff(keepToolbar: true);
        Assert.True(motion.IsActive);
        Assert.True(motion.HasToolbar);
        Assert.Equal(1, motion.ImageCount);
        Assert.True(motion.Copy("toolbar")!.Image.IsVisible);
        motion.ReleaseToolbar();
        Assert.False(motion.IsActive);
        Assert.Empty(root.Children);

        // A toolbar outside the region has no copy; the real toolbar's own motion is shown live.
        choreographer.Clear();
        (plan, snapshots) = Plan(choreographer, false, Style.Fade, 5, Shapes);
        motion.Play(plan, Area, snapshots, 5, 5);
        motion.PlayToolbar(plan.Toolbar!, toolbar, 400, 50, new PixelRect(600, 500, 1000, 550), blurred: false, particles: false, 5, 0, 5);
        Assert.False(motion.HasToolbar);
        motion.UseRealToolbar();
        Assert.True(motion.RealToolbar);
        motion.Cancel();
        Assert.False(motion.IsActive);
        Assert.False(motion.RealToolbar);
    });

    [Fact]
    public Task DissolvesParticlesAreDrawnEvery33Milliseconds() => OnCompositor((_, _, motion) =>
    {
        var (plan, snapshots) = Plan(new VisibilityChoreographer(), false, Style.Dissolve, 0, Shapes);
        motion.Play(plan, Area, snapshots, 0, 0);
        // Particle bursts draw their copies themselves.
        Assert.Equal(0, motion.ImageCount);
        Assert.Equal(2, motion.BurstCount);
        Assert.True(motion.Frame(0.010, 0));
        Assert.Equal(0, motion.Statistics.ParticleFrames);
        Assert.True(motion.Frame(0.034, 0));
        Assert.Equal(1, motion.Statistics.ParticleFrames);
        Assert.True(motion.Frame(0.050, 0));
        Assert.Equal(1, motion.Statistics.ParticleFrames);
        Assert.True(motion.Frame(0.070, 0));
        Assert.Equal(2, motion.Statistics.ParticleFrames);
        motion.Finish();
        motion.HandOff(keepToolbar: false);
        Assert.Equal(0, motion.BurstCount);
        Assert.False(motion.Frame(plan.TotalDuration + 1, 0), "Nothing is drawn after the hand-off.");
    });

    [Fact]
    public Task SoftFocusCopiesBlurWithTheirPlan() => OnCompositor((_, _, motion) =>
    {
        var (plan, snapshots) = Plan(new VisibilityChoreographer(), false, Style.Focus, 0, Shapes);
        Assert.Contains(snapshots, snapshot => snapshot.Blurred);
        motion.Play(plan, Area, snapshots, 0, 0);
        Assert.Equal(2, motion.ImageCount);
        Assert.True(motion.Frame(0.1, 0), "The blur changes while the plan runs.");
        Assert.False(motion.Frame(plan.TotalDuration + 0.5, 0));
    });

    [Fact]
    public void ProtectionIsDrawnAgainWhereTheOutputShowsIt()
    {
        // A 500 by 600-pixel region shown at 250 by 300 pixels from (10, 20): a cover at (400, 80)-(500, 180).
        var insets = SharedMotion.LidInsets(new PixelRect(400, 80, 500, 180), new PixelRect(0, 0, 500, 600), PixelRect.FromSize(10, 20, 250, 300));
        Assert.Equal((200f, 40f, 0f, 210f), insets);
        // Rounded outward: never a pixel of a moving copy over protection.
        Assert.Equal((33f, 33f, 215f, 265f), SharedMotion.LidInsets(new PixelRect(67, 67, 69, 69), new PixelRect(0, 0, 500, 600), PixelRect.FromSize(0, 0, 250, 300)));
        Assert.Null(SharedMotion.LidInsets(new PixelRect(600, 0, 700, 100), new PixelRect(0, 0, 500, 600), PixelRect.FromSize(0, 0, 250, 300)));
    }
}
