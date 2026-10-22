using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Motion;
using Annotator.Workspace.Zoom;

namespace Annotator.Workspace.Tests.Zoom;

/// <summary>
/// The displayed zoom (Mac LivePresentation: apply, ramps, retarget and the cursor-anchored viewport), Alt+scroll's rules
/// (Mac ZoomGestureMonitor and ToolbarZoomActivation) and the pointer lens's geometry (Mac renderLens).
/// </summary>
public sealed class ScreenZoomDisplayTests
{
    private static readonly Size Display = new(1280, 720);

    [Fact]
    public void RampKeepsTheDisplayedFactorSeparateAndLandsWhenInterrupted()
    {
        // Mac testScreenZoomRampKeepsDisplayedFactorSeparateAndLandsWhenInterrupted.
        var zoom = new ScreenZoomDisplay();
        Assert.True(zoom.Apply(new PresentationSettings(PresentationMode.Screen, 2), enteringFrom: 1, animatesExit: false, zoomVisible: false));
        Assert.True(zoom.IsRampingScreenZoom);
        Assert.False(zoom.NeedsFrames, "The entrance waits for the first zoomed frame.");
        Assert.Equal(1, zoom.Magnification);
        Assert.False(zoom.Apply(new PresentationSettings(PresentationMode.Screen, 2), null, false, false));
        Assert.True(zoom.IsRampingScreenZoom, "Applying the same target keeps the ramp.");
        Viewport viewport = zoom.ScreenViewport(Display, new Point(300, 200))!.Value;
        Assert.Equal(Display.Width / zoom.Magnification, viewport.Source.Width, 9);

        Assert.True(zoom.RetargetZoomRamp(2.4));
        Assert.True(zoom.IsRampingScreenZoom, "Scrolling during an entrance changes its target.");
        Assert.Equal(1, zoom.Magnification);

        zoom.SetMagnification(2.5);
        Assert.False(zoom.IsRampingScreenZoom, "A direct zoom change cancels the ramp.");
        Assert.Equal(2.5, zoom.Magnification);
        Assert.False(zoom.RetargetZoomRamp(3), "Without a ramp, scrolling sets the zoom directly.");

        zoom.Apply(new PresentationSettings(PresentationMode.Screen, 3), enteringFrom: 2.5, animatesExit: false, zoomVisible: true);
        Assert.Equal(2.5, zoom.Magnification);
        zoom.EndRamp();
        Assert.False(zoom.IsRampingScreenZoom);
        Assert.Equal(3, zoom.Magnification);

        Assert.True(zoom.Apply(new PresentationSettings(PresentationMode.Off, 3), null, animatesExit: true, zoomVisible: false));
        Assert.Equal(PresentationMode.Off, zoom.Mode);
        Assert.False(zoom.IsExitingScreenZoom, "Without a visible zoomed frame, stopping is immediate.");

        zoom.Apply(new PresentationSettings(PresentationMode.Screen, 2), enteringFrom: 1, animatesExit: false, zoomVisible: false);
        zoom.SetMode(PresentationMode.Off);
        Assert.False(zoom.IsRampingScreenZoom);
        Assert.Equal(PresentationMode.Off, zoom.Mode);
    }

    [Fact]
    public void EntranceRampsIn260MillisecondsOnTheEnterCurve()
    {
        var zoom = new ScreenZoomDisplay();
        zoom.Apply(new PresentationSettings(PresentationMode.Screen, 1.5), enteringFrom: 1, animatesExit: false, zoomVisible: false);
        zoom.FirstFramePresented();
        Assert.True(zoom.NeedsFrames);
        var samples = new List<double>();
        for (int millisecond = 0; millisecond <= 300; millisecond += 5)
        {
            if (!zoom.Step(10 + millisecond / 1000.0))
            {
                break;
            }
            samples.Add(zoom.Magnification);
            double expected = 1 + 0.5 * MotionCurves.Enter.Progress(millisecond / 1000.0 / MotionTiming.ZoomInDuration);
            Assert.Equal(expected, zoom.Magnification, 9);
        }
        Assert.Equal(samples.Order(), samples);
        Assert.Contains(samples, value => value > 1.01 && value < 1.49);
        Assert.False(zoom.IsRampingScreenZoom);
        Assert.Equal(1.5, zoom.Magnification);
        Assert.InRange(samples.Count, 52, 53);
    }

    [Fact]
    public void ExitRampsTo1xIn200MillisecondsThenTurnsOff()
    {
        var zoom = new ScreenZoomDisplay();
        int exits = 0;
        zoom.ExitFinished += () => exits++;
        zoom.Apply(new PresentationSettings(PresentationMode.Screen, 4), null, false, zoomVisible: true);
        Assert.False(zoom.Apply(new PresentationSettings(PresentationMode.Off, 4), null, animatesExit: true, zoomVisible: true));
        Assert.True(zoom.IsExitingScreenZoom);
        Assert.Equal(PresentationMode.Screen, zoom.Mode);
        Assert.True(zoom.NeedsFrames);
        Assert.False(zoom.Apply(new PresentationSettings(PresentationMode.Off, 4), null, true, true), "An exit keeps running.");
        var samples = new List<double>();
        double now = 5;
        while (zoom.Step(now))
        {
            samples.Add(zoom.Magnification);
            now += 0.005;
        }
        Assert.Equal(samples.OrderDescending(), samples);
        Assert.Contains(samples, value => value > 1.01 && value < 3.99);
        Assert.Equal(PresentationMode.Off, zoom.Mode);
        Assert.Equal(4, zoom.Magnification);
        Assert.Equal(1, exits);
        Assert.InRange(samples.Count, 40, 41);
        Assert.Equal(1 + 3 * (1 - MotionCurves.Standard.Progress(0.1 / MotionTiming.ZoomOutDuration)), samples[20], 9);
    }

    [Fact]
    public void StartingAgainDuringTheExitContinuesFromWhatIsShown()
    {
        var zoom = new ScreenZoomDisplay();
        int exits = 0;
        zoom.ExitFinished += () => exits++;
        zoom.Apply(new PresentationSettings(PresentationMode.Screen, 3), null, false, true);
        zoom.Apply(new PresentationSettings(PresentationMode.Off, 3), null, true, true);
        zoom.Step(1);
        zoom.Step(1.1);
        double shown = zoom.Magnification;
        Assert.InRange(shown, 1.01, 2.99);
        Assert.True(zoom.Apply(new PresentationSettings(PresentationMode.Screen, 1.5), enteringFrom: zoom.DisplayedScreenZoomFactor, false, true) is false);
        Assert.False(zoom.IsExitingScreenZoom);
        Assert.Equal(shown, zoom.Magnification, 9);
        Assert.Equal(0, exits);
        Assert.True(zoom.IsRampingScreenZoom);
        zoom.EndRamp();
        Assert.Equal(1.5, zoom.Magnification);
        Assert.Equal(PresentationMode.Screen, zoom.Mode);
    }

    [Fact]
    public void ViewportKeepsThePointerOnItsSourcePointAndFreezesForText()
    {
        // Mac checkViewport(): outputPoint(sourcePoint(pointer)) is the pointer, and the source is the display / zoom.
        var zoom = new ScreenZoomDisplay();
        zoom.Apply(new PresentationSettings(PresentationMode.Screen, 4), null, false, true);
        foreach (Point pointer in new[] { new Point(0, 0), new Point(640, 360), new Point(1279, 719), new Point(-50, 900) })
        {
            Viewport viewport = zoom.ScreenViewport(Display, pointer)!.Value;
            Assert.Equal(Display.Width / 4, viewport.Source.Width, 9);
            Point clamped = new(Math.Clamp(pointer.X, 0, Display.Width), Math.Clamp(pointer.Y, 0, Display.Height));
            Assert.Equal(clamped.X, viewport.SourcePoint(clamped).X, 9);
            Assert.Equal(clamped.Y, viewport.SourcePoint(clamped).Y, 9);
            Assert.Equal(clamped.X * (1 - 1.0 / 4), viewport.Source.X, 9);
        }
        Viewport frozen = zoom.FreezeViewportForText(Display, new Point(100, 100))!.Value;
        Assert.True(zoom.IsFrozen);
        Assert.Equal(frozen, zoom.ScreenViewport(Display, new Point(900, 600)));
        zoom.ReleaseTextViewport();
        Assert.NotEqual(frozen, zoom.ScreenViewport(Display, new Point(900, 600)));
        zoom.SetMode(PresentationMode.Off);
        Assert.Null(zoom.ScreenViewport(Display, new Point(900, 600)));
    }

    [Fact]
    public void AltScrollTakesOnlyAltAndConvertsNotchesToTheMacsPoints()
    {
        // Mac testGlobalAndLocalParserLeaveSystemZoomModifiersAlone and zoomScrollDelta's 12 points per line.
        Assert.Equal(12, ZoomScroll.Delta(120, alt: true, control: false, shift: false, windows: false));
        Assert.Equal(-12, ZoomScroll.Delta(-120, true, false, false, false));
        Assert.Equal(1, ZoomScroll.Delta(10, true, false, false, false));
        Assert.Null(ZoomScroll.Delta(0, true, false, false, false));
        Assert.Null(ZoomScroll.Delta(120, false, false, false, false));
        Assert.Null(ZoomScroll.Delta(120, true, true, false, false));
        Assert.Null(ZoomScroll.Delta(120, true, false, true, false));
        Assert.Null(ZoomScroll.Delta(120, true, false, false, true));
        Assert.Equal(2 * Math.Exp(12 * 0.012), ZoomScroll.Next(2, 12), 12);
        Assert.Equal(16, ZoomScroll.Next(15, 80));
        Assert.Equal(ZoomScroll.Next(2, 80), ZoomScroll.Next(2, 1000));
        Assert.Equal(1, ZoomScroll.Next(1.01, -50));
    }

    [Fact]
    public void ActivationTakesScrollsUpAndZoomTakesScrollsOverItsDisplay()
    {
        // Mac testToolbarOptionScrollActivationRunsOnlyWhileVisibleAndIdle and testDisplayRoutingUsesTheScrollEventsPosition.
        Assert.True(ZoomScrollState.ActivationMonitors(toolbarVisible: true, workspaceHidden: false, visibilityAnimating: false, screenZoomEnabled: false));
        Assert.False(ZoomScrollState.ActivationMonitors(false, false, false, false));
        Assert.False(ZoomScrollState.ActivationMonitors(true, true, false, false));
        Assert.False(ZoomScrollState.ActivationMonitors(true, false, true, false));
        Assert.False(ZoomScrollState.ActivationMonitors(true, false, false, true));
        var activation = new ZoomScrollState(ZoomScrollRole.Activation);
        Assert.True(activation.Consumes(8, 5000, 5000));
        Assert.False(activation.Consumes(-8, 0, 0));
        var zoom = new ZoomScrollState(ZoomScrollRole.Zoom, -1440, -200, 0, 700);
        Assert.True(zoom.Consumes(8, -600, 300));
        Assert.True(zoom.Consumes(-8, -1440, -200));
        Assert.False(zoom.Consumes(8, 800, 400));
        Assert.False(zoom.Consumes(8, 0, 300));
        Assert.False(ZoomScrollState.Off.Consumes(8, 0, 0));
    }

    [Fact]
    public void LensSitsUpAndRightOfThePointerInsideTheWorkArea()
    {
        var work = new Rect(0, 0, 1280, 672);
        Assert.Equal(new Point(128, 32), PointerLens.Origin(new Point(100, 300), work));
        // Near the top, the lens stays inside the work area; near the right edge too.
        Assert.Equal(new Point(1040, 0), PointerLens.Origin(new Point(1270, 50), work));
        Assert.Equal(new Point(0, 0), PointerLens.Origin(new Point(-100, -100), work));
        Assert.Equal(new Point(1040, 432), PointerLens.Origin(new Point(5000, 5000), work));
    }

    [Fact]
    public void LensMagnifiesASquareAroundThePointerAndMarksTheRealSourcePoint()
    {
        Viewport middle = PointerLens.SourceViewport(new Point(640, 360), Display, 2);
        Assert.Equal(new Rect(580, 300, 120, 120), middle.Source);
        Assert.Equal(new Rect(0, 0, 240, 240), middle.Output);
        Assert.Equal(new Point(120, 120), middle.OutputPoint(new Point(640, 360)));
        // At an edge the crop stays on the display, so the crosshair moves to the real source point.
        Viewport corner = PointerLens.SourceViewport(new Point(10, 700), Display, 4);
        Assert.Equal(new Rect(0, 660, 60, 60), corner.Source);
        Assert.Equal(new Point(40, 160), corner.OutputPoint(new Point(10, 700)));
    }
}
