using Annotator.Core;
using Annotator.Workspace.Toolbar;
using static Annotator.Workspace.Tests.TestWorkspace;
using Size = Annotator.Core.Geometry.Size;

namespace Annotator.Workspace.Tests;

/// <summary>
/// Ports of the Mac's ToolbarWindowFitTests (dock previews, drops, drag tracking, startup placement, resizing and recovery)
/// against the placement, drag and docking logic (WIN-OVR-05, WIN-OVR-10). Coordinates are desktop pixels, y down: the
/// primary display is 1280 by 960 pixels at scale 2 with a 48-pixel bar at the top, the left display 800 by 600 at scale 1
/// with a 40-pixel bar at the bottom.
/// </summary>
public sealed class ToolbarDockingTests
{
    private static readonly DisplayDescriptor[] Displays = [PrimaryDisplay, LeftDisplay];

    /// <summary>The toolbar's measured size for an edge: 68 by 200 points vertical, 300 by 59 points horizontal.</summary>
    private static Size Measure(ToolbarDockEdge edge, DisplayDescriptor display) =>
        ToolbarDocking.OrientationFor(edge) == ToolbarOrientation.Vertical
            ? new Size(68 * display.Scale, 200 * display.Scale)
            : new Size(300 * display.Scale, 59 * display.Scale);

    /// <summary>The outermost pixel of each edge, halfway along it.</summary>
    private static Point EdgePoint(DisplayDescriptor display, ToolbarDockEdge edge)
    {
        Rect bounds = display.DesktopFrame;
        double midX = Math.Floor(bounds.X + bounds.Width / 2), midY = Math.Floor(bounds.Y + bounds.Height / 2);
        return edge switch
        {
            ToolbarDockEdge.Top => new Point(midX, bounds.Y),
            ToolbarDockEdge.Bottom => new Point(midX, bounds.MaxY - 1),
            ToolbarDockEdge.Left => new Point(bounds.X, midY),
            _ => new Point(bounds.MaxX - 1, midY),
        };
    }

    private static Point Center(Rect rect) => new(Math.Floor(rect.X + rect.Width / 2), Math.Floor(rect.Y + rect.Height / 2));

    private static Size SizeOf(Rect frame) => new(frame.Width, frame.Height);

    /// <summary>A docking with a free toolbar of 300 by 59 points in the middle of <paramref name="display"/>'s work area.</summary>
    private static (ToolbarDocking Docking, Rect Frame) FreeToolbar(DisplayDescriptor display)
    {
        var docking = new ToolbarDocking();
        Size size = Measure(ToolbarDockEdge.Top, display);
        Point center = Center(display.DesktopWorkArea);
        return (docking, new Rect(center.X - size.Width / 2, center.Y - size.Height / 2, size.Width, size.Height));
    }

    /// <summary>Presses at the frame's center, moves past the drag threshold and returns the drag's frame.</summary>
    private static Rect StartDrag(ToolbarDocking docking, Rect frame, DisplayDescriptor display)
    {
        Point press = Center(frame);
        docking.Press(press, frame, display.Scale);
        ToolbarDragStep step = Assert.IsType<ToolbarDragStep>(docking.Move(press + new Point(10 * display.Scale, 0), SizeOf(frame), Displays, true, Measure));
        Assert.True(docking.IsDragging);
        return step.Frame;
    }

    [Fact]
    public void EdgeDropsCenterAndReorientOnTheDestinationDisplay()
    {
        // ToolbarWindowFitTests.testEdgeDropsCenterAndReorientOnTheDestinationDisplay.
        WorkspaceModel model = Drawing();
        var shape = new Element(Tool.Rectangle) { Frame = new Rect(40, 60, 100, 80) };
        AssertAccepted(model.Add(Primary, shape));
        foreach (DisplayDescriptor display in Displays)
        {
            foreach (ToolbarDockEdge edge in new[] { ToolbarDockEdge.Top, ToolbarDockEdge.Left, ToolbarDockEdge.Bottom, ToolbarDockEdge.Right })
            {
                (ToolbarDocking docking, Rect frame) = FreeToolbar(display);
                Rect current = StartDrag(docking, frame, display);
                ToolbarOrientation? before = model.Snapshot.Preferences.ToolbarOrientation;
                Point point = EdgePoint(display, edge);
                ToolbarDragStep step = docking.Move(point, SizeOf(current), Displays, true, Measure)!.Value;
                ToolbarDockPreview preview = Assert.IsType<ToolbarDockPreview>(step.Preview);
                Assert.Equal(before, model.Snapshot.Preferences.ToolbarOrientation);
                Assert.Null(docking.Move(Center(display.DesktopFrame), SizeOf(current), Displays, true, Measure)!.Value.Preview);
                Assert.Null(docking.Preview);
                docking.Move(point, SizeOf(current), Displays, true, Measure);

                ToolbarDrop drop = Assert.IsType<ToolbarDrop>(docking.Release(point, SizeOf(current), Displays, true, Measure));
                Assert.Equal(ToolbarDropKind.Docked, drop.Kind);
                Assert.Null(docking.Preview);
                Assert.False(docking.IsTracking);
                Rect docked = drop.Dock!.Frame;
                Assert.Equal(preview.Frame, docked);
                Assert.Equal(edge, drop.Dock.Edge);
                Assert.Equal(display, drop.Display);
                Assert.True(docking.EdgeDocked);
                Assert.Equal(display.Id, docking.DockDisplay);
                Assert.Equal(docked, docking.DockedFrame);
                Rect work = display.DesktopWorkArea;
                Assert.True(work.Contains(docked), $"{edge}: {docked} outside {work}");
                model.DockToolbar(edge);
                Assert.Equal(edge, model.Snapshot.Preferences.ToolbarDockEdge);
                if (edge is ToolbarDockEdge.Left or ToolbarDockEdge.Right)
                {
                    Assert.Equal(ToolbarOrientation.Vertical, model.Snapshot.Preferences.ToolbarOrientation);
                    Assert.Equal(work.Y + work.Height / 2, docked.Y + docked.Height / 2, display.Scale);
                    Assert.Equal(edge == ToolbarDockEdge.Left ? work.X : work.MaxX, edge == ToolbarDockEdge.Left ? docked.X : docked.MaxX);
                }
                else
                {
                    Assert.Equal(ToolbarOrientation.Horizontal, model.Snapshot.Preferences.ToolbarOrientation);
                    Assert.Equal(work.X + work.Width / 2, docked.X + docked.Width / 2, display.Scale);
                    Assert.Equal(edge == ToolbarDockEdge.Top ? work.Y : work.MaxY, edge == ToolbarDockEdge.Top ? docked.Y : docked.MaxY);
                }
                Assert.Equal([shape], model.Elements(Primary));
                Assert.Equal(WorkspaceMode.Draw, model.Snapshot.Mode);
            }
        }
    }

    [Fact]
    public void DropsAnywhereAlongAnEdgeDockAtItsCenter()
    {
        // The drop at three quarters of the top edge in testEdgeDropsCenterAndReorientOnTheDestinationDisplay.
        (ToolbarDocking docking, Rect frame) = FreeToolbar(PrimaryDisplay);
        Rect current = StartDrag(docking, frame, PrimaryDisplay);
        Rect bounds = PrimaryDisplay.DesktopFrame;
        var drop = new Point(bounds.X + bounds.Width * 0.75, bounds.Y);
        ToolbarDockPreview preview = docking.Move(drop, SizeOf(current), Displays, true, Measure)!.Value.Preview!;
        ToolbarDrop result = docking.Release(drop, SizeOf(current), Displays, true, Measure)!.Value;
        Assert.Equal(preview.Frame, result.Dock!.Frame);
        Rect work = PrimaryDisplay.DesktopWorkArea;
        Assert.Equal(work.X + work.Width / 2, result.Dock.Frame.X + result.Dock.Frame.Width / 2, 1.0);
    }

    [Fact]
    public void MovingSlightlyInwardLeavesTheToolbarFree()
    {
        // The end of testEdgeDropsCenterAndReorientOnTheDestinationDisplay: no near-edge attraction.
        (ToolbarDocking docking, Rect frame) = FreeToolbar(LeftDisplay);
        Rect current = StartDrag(docking, frame, LeftDisplay);
        Point nearTop = EdgePoint(LeftDisplay, ToolbarDockEdge.Top) + new Point(50, 3);
        ToolbarDragStep step = docking.Move(nearTop, SizeOf(current), Displays, true, Measure)!.Value;
        Assert.Null(step.Preview);
        ToolbarDrop drop = docking.Release(nearTop, SizeOf(current), Displays, true, Measure)!.Value;
        Assert.Equal(ToolbarDropKind.Free, drop.Kind);
        Assert.Equal(step.Frame, drop.Frame);
        Assert.Equal(LeftDisplay, drop.Display);
        Assert.False(docking.EdgeDocked);
        Assert.Null(docking.DockedFrame);
        Assert.Null(docking.DockDisplay);
    }

    [Fact]
    public void DragTrackingStopsOnReleaseAndCancel()
    {
        // ToolbarWindowFitTests.testDragTrackingStopsOnReleaseAndHide.
        var docking = new ToolbarDocking();
        var frame = new Rect(100, 100, 100, 50);
        docking.Press(new Point(110, 115), frame, 1);
        ToolbarDragStep step = docking.Move(new Point(150, 155), SizeOf(frame), Displays, true, Measure)!.Value;
        Assert.True(docking.IsDragging);
        Assert.Equal(new Rect(140, 140, 100, 50), step.Frame);
        ToolbarDrop drop = docking.Release(new Point(150, 155), SizeOf(frame), Displays, true, Measure)!.Value;
        Assert.False(docking.IsTracking);
        Assert.Equal(ToolbarDropKind.Free, drop.Kind);
        Assert.Equal(new Rect(140, 140, 100, 50), drop.Frame);

        docking.Press(new Point(150, 155), drop.Frame, 1);
        docking.Move(new Point(180, 185), SizeOf(frame), Displays, true, Measure);
        Assert.True(docking.IsDragging);
        docking.Cancel();
        Assert.False(docking.IsTracking);
        Assert.Null(docking.Preview);
        Assert.Null(docking.Release(new Point(180, 185), SizeOf(frame), Displays, true, Measure));
    }

    [Fact]
    public void AClickOnTheBackgroundDoesNotDrag()
    {
        // ToolbarView's DragGesture(minimumDistance: 3): presses that move less than 3 points never move the toolbar.
        var docking = new ToolbarDocking();
        var frame = new Rect(100, 100, 200, 96);
        docking.Press(new Point(110, 110), frame, 2);
        Assert.Null(docking.Move(new Point(115, 110), SizeOf(frame), Displays, true, Measure));
        Assert.False(docking.IsDragging);
        Assert.Null(docking.Release(new Point(115, 110), SizeOf(frame), Displays, true, Measure));
        Assert.False(docking.IsTracking);
        docking.Press(new Point(110, 110), frame, 2);
        Assert.NotNull(docking.Move(new Point(116, 110), SizeOf(frame), Displays, true, Measure));
        Assert.True(docking.IsDragging);
    }

    [Fact]
    public void ADragBackToItsStartIsCancelled()
    {
        // InspectorPanel.endToolbarDrag: a drag that leaves the frame unchanged calls onUserDragCancelled.
        (ToolbarDocking docking, Rect frame) = FreeToolbar(PrimaryDisplay);
        Point press = Center(frame);
        docking.Press(press, frame, PrimaryDisplay.Scale);
        docking.Move(press + new Point(40, 0), SizeOf(frame), Displays, true, Measure);
        ToolbarDrop drop = docking.Release(press, SizeOf(frame), Displays, true, Measure)!.Value;
        Assert.Equal(ToolbarDropKind.Unmoved, drop.Kind);
        Assert.Equal(frame, drop.Frame);
        Assert.Null(docking.Preview);
    }

    [Fact]
    public void ADragThatEndsWhereItStartedNeverDocks()
    {
        // The Mac's endToolbarDrag compares frames, not pointers: a toolbar already at the top of the work area, pushed
        // straight up to the top edge, keeps its frame, so the ghost shows but the release cancels instead of docking.
        var docking = new ToolbarDocking();
        Rect work = PrimaryDisplay.DesktopWorkArea;
        var frame = new Rect(100, work.Y, 600, 118);
        var press = new Point(400, work.Y + 10);
        docking.Press(press, frame, PrimaryDisplay.Scale);
        var top = new Point(400, 0);
        ToolbarDragStep step = docking.Move(top, SizeOf(frame), Displays, true, Measure)!.Value;
        Assert.Equal(frame, step.Frame);
        Assert.Equal(ToolbarDockEdge.Top, step.Preview?.Edge);
        ToolbarDrop drop = docking.Release(top, SizeOf(frame), Displays, true, Measure)!.Value;
        Assert.Equal(ToolbarDropKind.Unmoved, drop.Kind);
        Assert.False(docking.EdgeDocked);
        Assert.Null(docking.Preview);
    }

    [Fact]
    public void DraggingCannotCoverTheBarsButThePointerStillTriggersTheGhost()
    {
        // ToolbarWindowFitTests.testDraggingCannotCoverMenuBarButPointerStillTriggersDockPreview, with the primary
        // display's bar at the top and the left display's taskbar at the bottom.
        foreach ((DisplayDescriptor display, ToolbarDockEdge edge) in new[] { (PrimaryDisplay, ToolbarDockEdge.Top), (LeftDisplay, ToolbarDockEdge.Bottom) })
        {
            (ToolbarDocking docking, Rect frame) = FreeToolbar(display);
            Point start = Center(frame);
            docking.Press(start, frame, display.Scale);
            Point point = EdgePoint(display, edge);
            ToolbarDragStep step = docking.Move(point, SizeOf(frame), Displays, true, Measure)!.Value;
            Rect work = display.DesktopWorkArea;
            Assert.True(step.Frame.Y >= work.Y && step.Frame.MaxY <= work.MaxY, $"{step.Frame} covers a bar of {work}");
            Assert.Equal(edge, step.Preview?.Edge);
            ToolbarDragStep back = docking.Move(start, SizeOf(frame), Displays, true, Measure)!.Value;
            Assert.Equal(frame, back.Frame);
            Assert.Null(back.Preview);
            docking.Cancel();
        }
        Rect negative = new(-1920, -600, 1920, 1050);
        Rect constrained = ToolbarPlacement.ConstrainedDragFrame(new Rect(-2000, 440, 400, 100), negative);
        Assert.True(negative.Contains(constrained));
    }

    [Fact]
    public void DockDetectionUsesTheNearestEdgeAndLeavesTheInteriorFree()
    {
        // ToolbarWindowFitTests.testDockDetectionUsesNearestEdgeAndLeavesInteriorFree, y down.
        var bounds = new Rect(-1400, -900, 1400, 900);
        Assert.Equal(ToolbarDockEdge.Left, ToolbarPlacement.DockEdge(new Point(-1399, -450), bounds));
        Assert.Equal(ToolbarDockEdge.Right, ToolbarPlacement.DockEdge(new Point(-1, -450), bounds));
        Assert.Equal(ToolbarDockEdge.Top, ToolbarPlacement.DockEdge(new Point(-700, -899), bounds));
        Assert.Equal(ToolbarDockEdge.Bottom, ToolbarPlacement.DockEdge(new Point(-700, -1), bounds));
        Assert.Null(ToolbarPlacement.DockEdge(new Point(-700, -10), bounds));
        Assert.Null(ToolbarPlacement.DockEdge(new Point(-700, -2), bounds));
        Assert.Null(ToolbarPlacement.DockEdge(new Point(-700, -450), bounds));
        Assert.Null(ToolbarPlacement.DockEdge(new Point(500, 500), bounds));
    }

    [Fact]
    public void TheOutermostPixelDocksAtEveryScale()
    {
        // Windows: a pointer pushed against an edge rests on the display's first or last pixel; one point covers it.
        foreach (double scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
        {
            var display = new DisplayDescriptor(new DisplayId("scaled"), new Rect(0, 0, 1000, 800), new Rect(0, 0, 1000, 752), scale);
            foreach (ToolbarDockEdge edge in new[] { ToolbarDockEdge.Top, ToolbarDockEdge.Left, ToolbarDockEdge.Bottom, ToolbarDockEdge.Right })
            {
                Assert.Equal(edge, ToolbarPlacement.DockEdge(EdgePoint(display, edge), display.DesktopFrame, display.Scale));
            }
            Assert.Null(ToolbarPlacement.DockEdge(new Point(500, 3), display.DesktopFrame, display.Scale));
            Assert.Null(ToolbarPlacement.DockEdge(new Point(997, 400), display.DesktopFrame, display.Scale));
        }
    }

    [Fact]
    public void ADragAcrossDisplaysUsesTheDestinationsWorkAreaScaleAndEdges()
    {
        // Spec 009: moving to another monitor uses its bounds and scale (WIN-OVR-05).
        (ToolbarDocking docking, Rect frame) = FreeToolbar(PrimaryDisplay);
        Rect current = StartDrag(docking, frame, PrimaryDisplay);
        var measured = new List<(ToolbarDockEdge, DisplayId)>();
        Size Measured(ToolbarDockEdge edge, DisplayDescriptor display)
        {
            measured.Add((edge, display.Id));
            return Measure(edge, display);
        }
        var onLeft = new Point(-400, 400);
        ToolbarDragStep step = docking.Move(onLeft, SizeOf(current), Displays, true, Measured)!.Value;
        Assert.True(LeftDisplay.DesktopWorkArea.Contains(step.Frame), $"{step.Frame} is not on the left display's work area");
        Assert.Null(step.Preview);

        // The pixels on either side of the shared edge dock to their own display's edge.
        ToolbarDockPreview leftEdge = docking.Move(new Point(-1, 400), SizeOf(current), Displays, true, Measured)!.Value.Preview!;
        Assert.Equal((ToolbarDockEdge.Right, LeftDisplay.Id), (leftEdge.Edge, leftEdge.Display.Id));
        Assert.Equal(LeftDisplay.DesktopWorkArea.MaxX, leftEdge.Frame.MaxX);
        Assert.Equal(new Size(68, 200), SizeOf(leftEdge.Frame));
        ToolbarDockPreview primaryEdge = docking.Move(new Point(0, 400), SizeOf(current), Displays, true, Measured)!.Value.Preview!;
        Assert.Equal((ToolbarDockEdge.Left, Primary), (primaryEdge.Edge, primaryEdge.Display.Id));
        Assert.Equal(PrimaryDisplay.DesktopWorkArea.X, primaryEdge.Frame.X);
        Assert.Equal(new Size(136, 400), SizeOf(primaryEdge.Frame));
        Assert.Equal(new[] { (ToolbarDockEdge.Right, Left), (ToolbarDockEdge.Left, Primary) }, measured);

        ToolbarDrop drop = docking.Release(new Point(0, 400), SizeOf(current), Displays, true, Measured)!.Value;
        Assert.Equal(primaryEdge.Frame, drop.Dock!.Frame);
        Assert.Equal(Primary, docking.DockDisplay);
    }

    [Fact]
    public void TheMeasuredSizeIsKeptWhileThePointerStaysOnOneEdge()
    {
        (ToolbarDocking docking, Rect frame) = FreeToolbar(PrimaryDisplay);
        Rect current = StartDrag(docking, frame, PrimaryDisplay);
        int measured = 0;
        Size Counted(ToolbarDockEdge edge, DisplayDescriptor display)
        {
            measured++;
            return Measure(edge, display);
        }
        foreach (double x in new[] { 100.0, 300, 700, 1100 })
        {
            Assert.Equal(ToolbarDockEdge.Top, docking.Move(new Point(x, 0), SizeOf(current), Displays, true, Counted)!.Value.Preview?.Edge);
        }
        Assert.Equal(1, measured);
        docking.Move(EdgePoint(PrimaryDisplay, ToolbarDockEdge.Left), SizeOf(current), Displays, true, Counted);
        Assert.Equal(2, measured);
        docking.Move(Center(PrimaryDisplay.DesktopFrame), SizeOf(current), Displays, true, Counted);
        docking.Move(new Point(640, 0), SizeOf(current), Displays, true, Counted);
        Assert.Equal(3, measured);
    }

    [Fact]
    public void NoGhostOrDockWhileTheToolsAreHiddenOrMoving()
    {
        // AppDelegate.updateDockPreview and finishToolbarDrag: nothing docks while the workspace hides or animates.
        (ToolbarDocking docking, Rect frame) = FreeToolbar(PrimaryDisplay);
        Rect current = StartDrag(docking, frame, PrimaryDisplay);
        Point top = EdgePoint(PrimaryDisplay, ToolbarDockEdge.Top);
        Assert.Null(docking.Move(top, SizeOf(current), Displays, false, Measure)!.Value.Preview);
        ToolbarDrop drop = docking.Release(top, SizeOf(current), Displays, false, Measure)!.Value;
        Assert.Equal(ToolbarDropKind.Ignored, drop.Kind);
        Assert.False(docking.EdgeDocked);
    }

    [Fact]
    public void StartupPlacementAndSizeChangesStayDockedAndOnScreen()
    {
        // ToolbarWindowFitTests.testStartupAndOrientationChangesStayDockedAndOnScreen: docked toolbars keep their edge and
        // stay centered along it when their content changes size.
        var docking = new ToolbarDocking();
        Rect work = PrimaryDisplay.DesktopWorkArea;
        double inset = 12 * PrimaryDisplay.Scale;
        Rect vertical = docking.Place(new Size(136, 600), PrimaryDisplay, ToolbarOrientation.Vertical, ToolbarDockEdge.Left);
        Assert.Equal(work.X + inset, vertical.X);
        Assert.Equal(work.Y + work.Height / 2, vertical.Y + vertical.Height / 2, 1.0);
        Rect taller = docking.Resize(vertical, new Size(136, 800), Displays, ToolbarOrientation.Vertical, ToolbarDockEdge.Left)!.Value;
        Assert.Equal(work.X + inset, taller.X);
        Assert.Equal(work.Y + work.Height / 2, taller.Y + taller.Height / 2, 1.0);

        Rect horizontal = docking.Resize(taller, new Size(1000, 238), Displays, ToolbarOrientation.Horizontal, ToolbarDockEdge.Automatic)!.Value;
        Assert.Equal(work.X + work.Width / 2, horizontal.X + horizontal.Width / 2, 1.0);
        Assert.Equal(work.Y + inset, horizontal.Y);
        Rect fewer = docking.Resize(horizontal, new Size(700, 238), Displays, ToolbarOrientation.Horizontal, ToolbarDockEdge.Automatic)!.Value;
        Assert.Equal(work.X + work.Width / 2, fewer.X + fewer.Width / 2, 1.0);
        Assert.Null(docking.Resize(fewer, new Size(700, 238), Displays, ToolbarOrientation.Horizontal, ToolbarDockEdge.Automatic));
    }

    [Fact]
    public void AnEdgeDockedToolbarStaysFlushWhenItsSizeChanges()
    {
        (ToolbarDocking docking, Rect frame) = FreeToolbar(PrimaryDisplay);
        Rect current = StartDrag(docking, frame, PrimaryDisplay);
        Point right = EdgePoint(PrimaryDisplay, ToolbarDockEdge.Right);
        docking.Move(right, SizeOf(current), Displays, true, Measure);
        ToolbarDrop drop = docking.Release(right, SizeOf(current), Displays, true, Measure)!.Value;
        Rect resized = docking.Resize(drop.Dock!.Frame, new Size(616, 500), Displays, ToolbarOrientation.Vertical, ToolbarDockEdge.Right)!.Value;
        Rect work = PrimaryDisplay.DesktopWorkArea;
        Assert.Equal(work.MaxX, resized.MaxX);
        Assert.Equal(work.Y + work.Height / 2, resized.Y + resized.Height / 2, 1.0);
    }

    [Fact]
    public void AFreeToolbarKeepsItsTopLeftCornerWhenItsSizeChanges()
    {
        (ToolbarDocking docking, Rect frame) = FreeToolbar(PrimaryDisplay);
        Rect current = StartDrag(docking, frame, PrimaryDisplay);
        ToolbarDrop drop = docking.Release(Center(current) + new Point(-20, 30), SizeOf(current), Displays, true, Measure)!.Value;
        Assert.Equal(ToolbarDropKind.Free, drop.Kind);
        Rect resized = docking.Resize(drop.Frame, new Size(700, 300), Displays, ToolbarOrientation.Horizontal, ToolbarDockEdge.Automatic)!.Value;
        Assert.Equal((drop.Frame.X, drop.Frame.Y), (resized.X, resized.Y));
        Rect wide = docking.Resize(resized, new Size(1200, 300), Displays, ToolbarOrientation.Horizontal, ToolbarDockEdge.Automatic)!.Value;
        Assert.True(PrimaryDisplay.DesktopWorkArea.Contains(wide), $"{wide} left the work area");
    }

    [Fact]
    public void SizesThatArriveDuringADragWaitForItsEnd()
    {
        // AppDelegate.resizeToolbar keeps pendingToolbarSize while the panel tracks a drag.
        (ToolbarDocking docking, Rect frame) = FreeToolbar(PrimaryDisplay);
        Rect current = StartDrag(docking, frame, PrimaryDisplay);
        Assert.Null(docking.Resize(current, new Size(640, 200), Displays, ToolbarOrientation.Horizontal, ToolbarDockEdge.Automatic));
        Assert.Equal(new Size(640, 200), docking.PendingSize);
        Point bottom = EdgePoint(PrimaryDisplay, ToolbarDockEdge.Bottom);
        docking.Move(bottom, SizeOf(current), Displays, true, Measure);
        docking.Release(bottom, SizeOf(current), Displays, true, Measure);
        // A drop at an edge measures the toolbar again, so the waiting size is dropped (Mac finishToolbarDrag).
        Assert.Null(docking.TakePendingSize());

        current = StartDrag(docking, current, PrimaryDisplay);
        docking.Resize(current, new Size(640, 200), Displays, ToolbarOrientation.Horizontal, ToolbarDockEdge.Bottom, deferred: true);
        docking.Release(Center(current), SizeOf(current), Displays, true, Measure);
        Assert.Equal(new Size(640, 200), docking.TakePendingSize());
        Assert.Null(docking.PendingSize);
    }

    [Fact]
    public void RecoveryReturnsAToolbarFromADisconnectedDisplay()
    {
        // ToolbarWindowFitTests.testHiddenToolbarReturnsFromDisconnectedDisplayAndKeepsValidPlacement.
        var docking = new ToolbarDocking();
        var visible = new DisplayDescriptor(new DisplayId("only"), new Rect(0, 0, 1000, 740), new Rect(0, 40, 1000, 700), 1);
        docking.Place(new Size(800, 130), visible, ToolbarOrientation.Horizontal, ToolbarDockEdge.Automatic);
        var missing = new Rect(-1800, 250, 800, 130);
        Rect recovered = docking.Recover(missing, [visible])!.Value;
        Assert.True(visible.DesktopWorkArea.Contains(recovered));
        Assert.Null(docking.DockedFrame);
        Assert.Null(docking.DockDisplay);
        Assert.Null(docking.Recover(recovered, [visible]));
    }

    [Fact]
    public void RecoveryFitsASmallerDisplayAndPartlyOffscreenFrames()
    {
        // ToolbarWindowFitTests.testToolbarRecoveryFitsSmallerDisplayAndPartialOffscreenFrame.
        var docking = new ToolbarDocking();
        var small = new DisplayDescriptor(new DisplayId("small"), new Rect(-500, 100, 500, 400), new Rect(-500, 100, 500, 400), 1);
        foreach (Rect frame in new[] { new Rect(-1600, 800, 1200, 600), new Rect(-50, 450, 300, 100) })
        {
            Rect recovered = docking.Recover(frame, [small])!.Value;
            Assert.True(small.DesktopWorkArea.Contains(recovered), $"{recovered} is outside {small.DesktopWorkArea}");
        }
        // A toolbar on any display stays where it is.
        Assert.Null(docking.Recover(new Rect(-700, 300, 200, 100), Displays));
    }

    [Fact]
    public void ATallFrameKeepsItsTopWhileDraggedAndItsBottomWhenResized()
    {
        // Mac constrainedDragFrame keeps the top below the menu bar; resizeToolbar's clamp keeps the bottom in the area.
        var work = new Rect(0, 40, 1000, 600);
        Rect dragged = ToolbarPlacement.ConstrainedDragFrame(new Rect(10, 300, 200, 800), work);
        Assert.Equal(40, dragged.Y);
        Rect resized = ToolbarPlacement.ConstrainedResizedFrame(new Rect(10, 300, 200, 800), work);
        Assert.Equal(work.MaxY, resized.MaxY);
    }

    [Fact]
    public void DockingSavesTheEdgeAndOrientationAndLeavesTheWorkspaceAlone()
    {
        // WorkspaceModel.dockToolbar(to:), setToolbarDockEdge and setToolbarOrientation.
        var store = new MemoryPreferencesStore();
        WorkspaceModel model = Create(store: store);
        model.BeginDrawing();
        var shape = new Element(Tool.Ellipse) { Frame = new Rect(10, 20, 30, 40) };
        AssertAccepted(model.Add(Primary, shape));
        int saves = store.SaveCount;
        model.DockToolbar(ToolbarDockEdge.Automatic);
        Assert.Equal(saves, store.SaveCount);
        Assert.Null(model.Snapshot.Preferences.ToolbarDockEdge);

        model.DockToolbar(ToolbarDockEdge.Left);
        Assert.Equal(ToolbarOrientation.Vertical, model.Snapshot.Preferences.ToolbarOrientation);
        Assert.Equal(ToolbarDockEdge.Left, model.Snapshot.Preferences.ToolbarDockEdge);
        Assert.Equal(saves + 1, store.SaveCount);
        Assert.Equal([shape], model.Elements(Primary));
        Assert.Equal(WorkspaceMode.Draw, model.Snapshot.Mode);

        WorkspaceModel reopened = Create(store: new MemoryPreferencesStore(store.Data));
        Assert.Equal(ToolbarOrientation.Vertical, reopened.Snapshot.Preferences.ToolbarOrientation);
        Assert.Equal(ToolbarDockEdge.Left, reopened.Snapshot.Preferences.ToolbarDockEdge);

        model.DockToolbar(ToolbarDockEdge.Bottom);
        Assert.Equal(ToolbarOrientation.Horizontal, model.Snapshot.Preferences.ToolbarOrientation);
        Assert.Equal(ToolbarDockEdge.Bottom, model.Snapshot.Preferences.ToolbarDockEdge);
        model.SetToolbarOrientation(ToolbarOrientation.Vertical);
        Assert.Equal(ToolbarDockEdge.Automatic, model.Snapshot.Preferences.ToolbarDockEdge);
        model.SetToolbarDockEdge(ToolbarDockEdge.Right);
        model.SetToolbarOrientation(ToolbarOrientation.Vertical);
        Assert.Equal(ToolbarDockEdge.Right, model.Snapshot.Preferences.ToolbarDockEdge);
        model.SetToolbarOrientation(ToolbarOrientation.Horizontal);
        Assert.Equal(ToolbarDockEdge.Automatic, model.Snapshot.Preferences.ToolbarDockEdge);
    }
}
