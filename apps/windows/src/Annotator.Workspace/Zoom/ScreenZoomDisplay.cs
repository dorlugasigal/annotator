using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Motion;

namespace Annotator.Workspace.Zoom;

/// <summary>
/// What one display shows of screen zoom (port of the displayed state in the Mac's <c>LivePresentation</c>): the displayed
/// mode and factor, which drawing, hit testing and captured pixels share, and the 260 ms entrance and 200 ms exit ramps. The
/// document's presentation changes at once; this follows it with motion. Platform-neutral and single-threaded: the overlay
/// thread owns it and calls <see cref="Step"/> on each compositor frame while <see cref="NeedsFrames"/> is true.
/// </summary>
public sealed class ScreenZoomDisplay
{
    private PresentationMode _mode;
    private double _magnification = 2;
    private Ramp? _ramp;
    private bool _running;
    private Viewport? _frozen;

    /// <summary>Raised when an exit ramp lands at 1x and zoom turns off, or when an exit is interrupted (Mac screenZoomExitFinished).</summary>
    public event Action? ExitFinished;

    /// <summary>The displayed mode (Mac presentationMode).</summary>
    public PresentationMode Mode => _mode;

    /// <summary>The displayed factor that drawing, hit testing and captured pixels share (Mac magnification).</summary>
    public double Magnification => _magnification;

    public bool IsRampingScreenZoom => _ramp is not null;

    public bool IsExitingScreenZoom => _ramp?.ExitZoom is not null;

    /// <summary>The factor on screen while screen zoom shows, so a new start continues from what the person sees.</summary>
    public double? DisplayedScreenZoomFactor => _mode == PresentationMode.Screen ? _magnification : null;

    /// <summary>A ramp waits for frames: the host calls <see cref="Step"/> with the compositor's time until it returns false.</summary>
    public bool NeedsFrames => _ramp is not null && _running;

    /// <summary>Text editing holds the viewport still (Mac freezeViewportForText).</summary>
    public bool IsFrozen => _frozen is not null;

    /// <summary>Sets the displayed mode at once, landing any ramp first (Mac presentationMode's setter).</summary>
    public void SetMode(PresentationMode mode)
    {
        EndRamp();
        _mode = mode;
    }

    /// <summary>Sets the displayed factor at once, landing any ramp first (Mac magnification's setter).</summary>
    public void SetMagnification(double magnification)
    {
        EndRamp();
        _magnification = magnification;
    }

    /// <summary>
    /// Mac apply(_:enteringFrom:animatesExit:): the document's presentation, applied to what is shown. With
    /// <paramref name="enteringFrom"/>, the displayed factor ramps from it to the new zoom over 260 ms; with
    /// <paramref name="animatesExit"/>, a visible screen zoom ramps to 1x over 200 ms before it turns off.
    /// <paramref name="zoomVisible"/> says whether a zoomed frame is on screen (Mac screenZoomIsVisible): an entrance waits
    /// for the first one (<see cref="FirstFramePresented"/>), so the whole ramp is seen, and an exit without one is
    /// immediate. Returns whether the displayed mode changed.
    /// </summary>
    public bool Apply(PresentationSettings settings, double? enteringFrom, bool animatesExit, bool zoomVisible)
    {
        bool interruptedExit = false;
        try
        {
            if (_ramp is { } ramp)
            {
                if (ramp.ExitZoom is not null && settings.Mode == PresentationMode.Off)
                {
                    return false;
                }
                if (ramp.ExitZoom is null && enteringFrom is null && settings.Mode == PresentationMode.Screen && settings.Zoom == ramp.To)
                {
                    return false;
                }
                _ramp = null;
                _running = false;
                interruptedExit = ramp.ExitZoom is not null && settings.Mode != PresentationMode.Screen;
            }
            PresentationMode previous = _mode;
            double current = _magnification;
            if (previous == PresentationMode.Screen && settings.Mode == PresentationMode.Off && animatesExit && zoomVisible && current > 1.001)
            {
                StartRamp(new Ramp(current, 1, MotionTiming.ZoomOutDuration, MotionCurves.Standard, settings.Zoom), zoomVisible);
                return false;
            }
            _mode = settings.Mode;
            _magnification = settings.Zoom;
            if (settings.Mode == PresentationMode.Screen && enteringFrom is { } start && double.IsFinite(start) && start >= 1 && start <= 16
                && Math.Abs(start - settings.Zoom) > 0.001)
            {
                _magnification = start;
                StartRamp(new Ramp(start, settings.Zoom, MotionTiming.ZoomInDuration, MotionCurves.Enter, null), zoomVisible);
            }
            return previous != _mode;
        }
        finally
        {
            if (interruptedExit)
            {
                ExitFinished?.Invoke();
            }
        }
    }

    /// <summary>The first zoomed frame is on screen: an entrance that waited for it starts now (Mac runZoomRamp on firstPresentation).</summary>
    public void FirstFramePresented()
    {
        if (_ramp is not null)
        {
            _running = true;
        }
    }

    /// <summary>
    /// Mac retargetZoomRamp(to:): points a running zoom-in ramp at a new target, easing on from what is on screen. Returns
    /// false when no zoom-in ramp runs.
    /// </summary>
    public bool RetargetZoomRamp(double zoom)
    {
        if (_ramp is not { ExitZoom: null } ramp || !double.IsFinite(zoom))
        {
            return false;
        }
        _ramp = new Ramp(ramp.Began is null ? ramp.From : _magnification, Math.Min(16, Math.Max(1, zoom)), ramp.Duration, ramp.Curve, null);
        return true;
    }

    /// <summary>
    /// Mac stepZoomRamp(at:): moves the ramp to <paramref name="now"/> seconds (any steady clock); the first step starts it.
    /// <paramref name="stop"/> (the workspace hid, or zoom is blocked) lands it at once. Returns false once nothing moves.
    /// </summary>
    public bool Step(double now, bool stop = false)
    {
        if (_ramp is not { } ramp)
        {
            _running = false;
            return false;
        }
        double began = ramp.Began ?? now;
        double elapsed = ramp.Duration > 0 ? Math.Min(1, Math.Max(0, (now - began) / ramp.Duration)) : 1;
        if (!(elapsed < 1) || stop)
        {
            EndRamp();
            return false;
        }
        _ramp = ramp with { Began = began };
        _magnification = Math.Min(16, Math.Max(1, ramp.From + (ramp.To - ramp.From) * ramp.Curve.Progress(elapsed)));
        return true;
    }

    /// <summary>Lands an unfinished ramp on its final state: the target factor, or zoom off after an exit (Mac endZoomRamp).</summary>
    public void EndRamp()
    {
        if (_ramp is not { } ramp)
        {
            return;
        }
        _ramp = null;
        _running = false;
        if (ramp.ExitZoom is { } exitZoom)
        {
            _mode = PresentationMode.Off;
            _magnification = exitZoom;
            ExitFinished?.Invoke();
        }
        else
        {
            _magnification = ramp.To;
        }
    }

    /// <summary>
    /// Mac screenViewport: while screen zoom shows, the cursor-anchored viewport of a display of <paramref name="size"/> points
    /// for the pointer at <paramref name="pointer"/> (display points), or the viewport held still while text is edited.
    /// </summary>
    public Viewport? ScreenViewport(Size size, Point pointer)
    {
        if (_mode != PresentationMode.Screen)
        {
            return null;
        }
        if (_frozen is { } frozen)
        {
            return frozen;
        }
        if (!(size.Width > 0) || !(size.Height > 0) || !pointer.IsFinite)
        {
            return null;
        }
        return Viewport.CursorAnchored(new Rect(0, 0, size.Width, size.Height), _magnification, pointer);
    }

    /// <summary>Mac freezeViewportForText: holds the current viewport still while text is edited and returns it.</summary>
    public Viewport? FreezeViewportForText(Size size, Point pointer)
    {
        _frozen = null;
        _frozen = ScreenViewport(size, pointer);
        return _frozen;
    }

    /// <summary>Mac releaseTextViewport.</summary>
    public void ReleaseTextViewport() => _frozen = null;

    private void StartRamp(Ramp ramp, bool zoomVisible)
    {
        _ramp = ramp;
        // An entrance waits for the first zoomed frame, so the whole ramp is visible.
        _running = ramp.ExitZoom is not null || zoomVisible;
    }

    /// <param name="ExitZoom">For a zoom-out ramp: the document zoom to keep once the display reaches 1x and zoom turns off.</param>
    /// <param name="Began">The time of the first step, once it ran.</param>
    private sealed record Ramp(double From, double To, double Duration, CubicBezier Curve, double? ExitZoom, double? Began = null);
}

/// <summary>Where the pointer lens shows and what it magnifies (Mac LivePresentation.renderLens).</summary>
public static class PointerLens
{
    /// <summary>The lens is a 240-point square with 18-point corners and a 2-point accent border.</summary>
    public const double Diameter = 240;

    public const double CornerRadius = 18;

    public const double BorderWidth = 2;

    /// <summary>The lens sits 28 points right of the pointer and 28 points above it.</summary>
    public const double PointerOffset = 28;

    /// <summary>The lens fades in over 120 ms with the fade curve when it first appears.</summary>
    public const double FadeInDuration = 0.12;

    /// <summary>The crosshair's arms are 6 points long: a 3-point stroke of black at 70%, then a 1-point white stroke.</summary>
    public const double CrosshairArm = 6;

    /// <summary>
    /// The lens's top-left corner in desktop points for the pointer at <paramref name="pointer"/>: 28 points right of and
    /// above the pointer, kept inside <paramref name="workArea"/> (the Mac's visible frame). Windows measures y downward, so
    /// the Mac's "28 above" puts the lens's bottom edge 28 points above the pointer.
    /// </summary>
    public static Point Origin(Point pointer, Rect workArea) => new(
        Math.Min(Math.Max(pointer.X + PointerOffset, workArea.X), workArea.MaxX - Diameter),
        Math.Min(Math.Max(pointer.Y - PointerOffset - Diameter, workArea.Y), workArea.MaxY - Diameter));

    /// <summary>
    /// The viewport from the display's points around <paramref name="local"/> (the pointer in display points) to the lens's
    /// 240 points: a square of 240 / zoom points centered on the pointer, kept inside the display.
    /// </summary>
    public static Viewport SourceViewport(Point local, Size display, double magnification)
    {
        double crop = Diameter / magnification;
        var source = new Rect(
            Math.Max(0, Math.Min(local.X - crop / 2, display.Width - crop)),
            Math.Max(0, Math.Min(local.Y - crop / 2, display.Height - crop)),
            Math.Min(crop, display.Width), Math.Min(crop, display.Height));
        return new Viewport(new Rect(0, 0, Diameter, Diameter), source);
    }
}
