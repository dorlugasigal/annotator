using Annotator.Core;
using Annotator.Workspace.Toolbar;
using static Annotator.Workspace.Tests.TestWorkspace;
using Size = Annotator.Core.Geometry.Size;

namespace Annotator.Workspace.Tests;

/// <summary>
/// Press and hold anywhere on the toolbar to move it (Mac ToolbarHoldTests, ToolbarHold): the decisions of
/// <see cref="ToolbarHoldGesture"/> and the drag <see cref="ToolbarDocking.Hold"/> starts. Points are toolbar points for the
/// gesture and desktop pixels for the docking, as in <see cref="ToolbarDockingTests"/>.
/// </summary>
public sealed class ToolbarHoldTests
{
    private static readonly DisplayDescriptor[] Displays = [PrimaryDisplay, LeftDisplay];

    private static Size Measure(ToolbarDockEdge edge, DisplayDescriptor display) =>
        ToolbarDocking.OrientationFor(edge) == ToolbarOrientation.Vertical
            ? new Size(68 * display.Scale, 200 * display.Scale)
            : new Size(300 * display.Scale, 59 * display.Scale);

    private static Point Center(Rect rect) => new(Math.Floor(rect.X + rect.Width / 2), Math.Floor(rect.Y + rect.Height / 2));

    private static Size SizeOf(Rect frame) => new(frame.Width, frame.Height);

    private static readonly ToolbarPressTarget[] OnAButton = [ToolbarPressTarget.Other, ToolbarPressTarget.Other];

    [Fact]
    public void HoldTimingIsTheMacs()
    {
        Assert.Equal(0.4, ToolbarHold.Delay);
        Assert.Equal(3.0, ToolbarHold.Slop);
        Assert.True(ToolbarHold.IsStill(3, 0));
        Assert.True(ToolbarHold.IsStill(-2, 2));
        Assert.False(ToolbarHold.IsStill(0, -3.01));
        Assert.False(ToolbarHold.IsStill(2.2, 2.2));
    }

    [Fact]
    public void HoldingStillPicksTheToolbarUpFromThePress()
    {
        // testHoldingAButtonMovesTheToolbarInsteadOfPressingIt: the press waits, a small wobble is still a hold, and the end
        // of the delay picks the toolbar up from where the press started.
        var hold = new ToolbarHoldGesture();
        var press = new Point(120, 30);
        Assert.True(hold.Press(7, press, allowed: true, OnAButton));
        Assert.True(hold.IsWaiting);
        Assert.Equal(ToolbarHoldMove.Still, hold.Move(7, new Point(121, 31)));
        Assert.Equal(ToolbarHoldMove.None, hold.Move(8, new Point(200, 200)));
        Assert.True(hold.IsWaiting);
        Assert.Equal(press, hold.Elapse(buttonDown: true, allowed: true));
        Assert.False(hold.IsWaiting);
        Assert.Null(hold.Elapse(buttonDown: true, allowed: true));
    }

    [Fact]
    public void QuickAndSlowClicksStayTheControlsAndAMoveEndsTheWait()
    {
        var hold = new ToolbarHoldGesture();
        // testQuickAndSlowClicksStillPressTheButton: letting go before the delay keeps the click.
        Assert.True(hold.Press(1, new Point(10, 10), allowed: true, OnAButton));
        Assert.True(hold.Release(1));
        Assert.False(hold.IsWaiting);
        Assert.Null(hold.Elapse(buttonDown: true, allowed: true));
        // The button was already up when the hold ended: a slow click.
        Assert.True(hold.Press(1, new Point(10, 10), allowed: true, OnAButton));
        Assert.Null(hold.Elapse(buttonDown: false, allowed: true));
        Assert.False(hold.IsWaiting);
        // testMovingBeforeTheHoldEndsMovesTheToolbar: moving further than the slop ends the wait, and the window picks the
        // toolbar up from the press.
        Assert.True(hold.Press(1, new Point(10, 10), allowed: true, OnAButton));
        Assert.Equal(ToolbarHoldMove.Still, hold.Move(1, new Point(11, 10)));
        Assert.Equal(ToolbarHoldMove.Moved, hold.Move(1, new Point(11, 16)));
        Assert.False(hold.IsWaiting);
        Assert.False(hold.Release(1));
        Assert.Null(hold.Elapse(buttonDown: true, allowed: true));
    }

    [Fact]
    public void APressOnASliderOrAFieldIsNeverHeld()
    {
        // testSlidersGetTheirPressAtOnce: holding a slider is how people use it, so its press goes on at once.
        var hold = new ToolbarHoldGesture();
        ToolbarPressTarget[] slider = [ToolbarPressTarget.Other, ToolbarPressTarget.Slider, ToolbarPressTarget.Other];
        Assert.True(ToolbarHoldGesture.KeepsPressAtOnce(slider));
        Assert.False(hold.Press(1, new Point(40, 50), allowed: true, slider));
        Assert.False(hold.IsWaiting);
        Assert.Equal(ToolbarHoldMove.None, hold.Move(1, new Point(40, 50)));
        Assert.Null(hold.Elapse(buttonDown: true, allowed: true));
        foreach (ToolbarPressTarget field in new[] { ToolbarPressTarget.TextField, ToolbarPressTarget.NumberField })
        {
            Assert.False(hold.Press(1, new Point(40, 50), allowed: true, [field, ToolbarPressTarget.Other]));
            Assert.False(hold.IsWaiting);
        }
        Assert.False(ToolbarHoldGesture.KeepsPressAtOnce(OnAButton));
        Assert.False(ToolbarHoldGesture.KeepsPressAtOnce([]));
    }

    [Fact]
    public void HoldingIsOffWhileTheToolbarHidesOrMoves()
    {
        var hold = new ToolbarHoldGesture();
        Assert.False(hold.Press(1, new Point(10, 10), allowed: false, OnAButton));
        Assert.False(hold.IsWaiting);
        // The toolbar started to hide during the hold: the press stays the control's.
        Assert.True(hold.Press(1, new Point(10, 10), allowed: true, OnAButton));
        Assert.Null(hold.Elapse(buttonDown: true, allowed: false));
        Assert.True(hold.Press(1, new Point(10, 10), allowed: true, OnAButton));
        hold.Cancel();
        Assert.False(hold.IsWaiting);
    }

    [Fact]
    public void ANewPressDropsAHoldWhoseReleaseWentElsewhere()
    {
        var hold = new ToolbarHoldGesture();
        Assert.True(hold.Press(1, new Point(10, 10), allowed: true, OnAButton));
        Assert.False(hold.Press(2, new Point(80, 10), allowed: false, OnAButton));
        Assert.False(hold.IsWaiting);
        Assert.True(hold.Press(1, new Point(10, 10), allowed: true, OnAButton));
        Assert.True(hold.Press(3, new Point(80, 12), allowed: true, OnAButton));
        Assert.False(hold.Release(1));
        Assert.Equal(new Point(80, 12), hold.Elapse(buttonDown: true, allowed: true));
    }

    [Fact]
    public void AHoldMovesTheToolbarFromThePressAtOnce()
    {
        // Mac finishHold: beginToolbarDrag(at: press), so even a move shorter than the background's 3 points moves it.
        var docking = new ToolbarDocking();
        var frame = new Rect(100, 100, 600, 118);
        var press = new Point(110, 115);
        docking.Hold(press, frame, PrimaryDisplay.Scale);
        Assert.True(docking.IsDragging);
        ToolbarDragStep step = docking.Move(press + new Point(2, 1), SizeOf(frame), Displays, true, Measure)!.Value;
        Assert.Equal(new Rect(102, 101, 600, 118), step.Frame);
        ToolbarDrop drop = docking.Release(press + new Point(60, 40), SizeOf(frame), Displays, true, Measure)!.Value;
        Assert.Equal(ToolbarDropKind.Free, drop.Kind);
        Assert.Equal(new Rect(160, 140, 600, 118), drop.Frame);
        Assert.False(docking.IsTracking);
    }

    [Fact]
    public void AHoldOnTheBackgroundStartsFromItsOwnPress()
    {
        // A press on the background that waits to become a drag becomes one from its own point when the hold ends.
        var docking = new ToolbarDocking();
        var frame = new Rect(100, 100, 600, 118);
        docking.Press(new Point(400, 150), frame, PrimaryDisplay.Scale);
        Assert.Null(docking.Move(new Point(401, 151), SizeOf(frame), Displays, true, Measure));
        docking.Hold(new Point(999, 999), new Rect(0, 0, 10, 10), PrimaryDisplay.Scale);
        Assert.True(docking.IsDragging);
        ToolbarDragStep step = docking.Move(new Point(401, 151), SizeOf(frame), Displays, true, Measure)!.Value;
        Assert.Equal(new Rect(101, 101, 600, 118), step.Frame);
    }

    [Fact]
    public void AHoldLetGoWithoutMovingKeepsTheToolbarDocked()
    {
        // testHoldWithoutMovingKeepsTheToolbarDocked: the hold picks the toolbar up, letting go where it started cancels the
        // drag and the toolbar keeps its dock, so a toolbar that grows for its properties stays centered on its edge.
        var docking = new ToolbarDocking();
        Rect work = PrimaryDisplay.DesktopWorkArea;
        Rect placed = docking.Place(new Size(600, 118), PrimaryDisplay, ToolbarOrientation.Horizontal, ToolbarDockEdge.Top);
        (Rect? Docked, DisplayId? Display, bool Edge) before = (docking.DockedFrame, docking.DockDisplay, docking.EdgeDocked);
        Assert.NotNull(before.Docked);
        Point press = Center(placed);
        docking.Hold(press, placed, PrimaryDisplay.Scale);
        Assert.True(docking.IsDragging);
        Assert.Null(docking.DockedFrame);
        ToolbarDrop drop = docking.Release(press, SizeOf(placed), Displays, true, Measure)!.Value;
        docking.Cancel();
        Assert.Equal(ToolbarDropKind.Unmoved, drop.Kind);
        Assert.Equal(placed, drop.Frame);
        Assert.Equal(before, (docking.DockedFrame, docking.DockDisplay, docking.EdgeDocked));
        Rect grown = docking.Resize(placed, new Size(1000, 238), Displays, ToolbarOrientation.Horizontal, ToolbarDockEdge.Top)!.Value;
        Assert.Equal(work.X + work.Width / 2, grown.X + grown.Width / 2, 1.0);
        Assert.Equal(work.Y + ToolbarDocking.StartupInset * PrimaryDisplay.Scale, grown.Y);
    }

    [Fact]
    public void AHoldCancelledWithoutMovingKeepsAnEdgeDock()
    {
        // Mac onUserDragCancelled restores the dock when the toolbar is where the drag started: an edge dock stays flush.
        var docking = new ToolbarDocking();
        var frame = new Rect(400, 400, 600, 118);
        docking.Press(Center(frame), frame, PrimaryDisplay.Scale);
        var top = new Point(Center(frame).X, 0);
        docking.Move(top, SizeOf(frame), Displays, true, Measure);
        ToolbarDrop docked = docking.Release(top, SizeOf(frame), Displays, true, Measure)!.Value;
        Assert.Equal(ToolbarDropKind.Docked, docked.Kind);
        Rect flush = docked.Dock!.Frame;
        Assert.True(docking.EdgeDocked);

        docking.Hold(Center(flush), flush, PrimaryDisplay.Scale);
        Assert.False(docking.EdgeDocked);
        docking.Cancel();
        Assert.True(docking.EdgeDocked);
        Assert.Equal(flush, docking.DockedFrame);
        Assert.Equal(PrimaryDisplay.Id, docking.DockDisplay);
        Rect resized = docking.Resize(flush, new Size(800, 238), Displays, ToolbarOrientation.Horizontal, ToolbarDockEdge.Top)!.Value;
        Assert.Equal(PrimaryDisplay.DesktopWorkArea.Y, resized.Y);
    }

    [Fact]
    public void AHoldThatMovesTheToolbarUndocksIt()
    {
        var docking = new ToolbarDocking();
        Rect placed = docking.Place(new Size(600, 118), PrimaryDisplay, ToolbarOrientation.Horizontal, ToolbarDockEdge.Top);
        Point press = Center(placed);
        docking.Hold(press, placed, PrimaryDisplay.Scale);
        docking.Move(press + new Point(0, 200), SizeOf(placed), Displays, true, Measure);
        // Back where it started, then cancelled: the frame moved and came back, so it never left; the dock stays.
        docking.Move(press, SizeOf(placed), Displays, true, Measure);
        docking.Cancel();
        Assert.NotNull(docking.DockedFrame);

        docking.Hold(press, placed, PrimaryDisplay.Scale);
        docking.Move(press + new Point(0, 200), SizeOf(placed), Displays, true, Measure);
        docking.Cancel();
        Assert.Null(docking.DockedFrame);
        Assert.Null(docking.DockDisplay);
        Assert.False(docking.EdgeDocked);
    }
}
