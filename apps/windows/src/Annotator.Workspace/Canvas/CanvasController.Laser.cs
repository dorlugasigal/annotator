using Annotator.Core;

namespace Annotator.Workspace.Canvas;

/// <summary>
/// The laser pointer on one display (Mac DrawingCanvas's laser pointer): a press with the Laser pointer tool starts a
/// trail in the tool's color and width, the drag draws it and letting go lets it fade. The trail never changes the
/// document, adds no Undo step and keeps the tool. It shows above the drawings, through screen zoom like them, and not
/// while the drawings are hidden. Frames come from <see cref="AdvanceTransitions"/> while any of it moves or fades.
/// </summary>
public sealed partial class CanvasController
{
    private LaserTrail _laser;
    private uint? _laserPointer;
    private double _laserTime;
    private Rect? _laserDrawn;
    private long _laserSampleTime;

    /// <summary>
    /// The trail keeps at most one position this many milliseconds apart, plus the newest of each event: about as many as
    /// the Mac gets, one per display frame. Windows gives up to 1,000 a second; every position kept is drawn on every frame
    /// until it fades, and a stroke keeps at most <see cref="LaserTrail.MaximumSamples"/>, so a long Fade would lose its tail.
    /// </summary>
    private const long LaserSampleSpacing = 8;

    /// <summary>Whether the laser pointer's trail is still being drawn or fading (Mac isLaserFading).</summary>
    public bool IsLaserFading => LaserMoves;

    /// <summary>The trail needs frames: something shows and it is not just the dot under a resting, held pointer.</summary>
    private bool LaserMoves => !_laser.IsEmpty && !_laser.IsResting;

    /// <summary>Mac mouseDown with the laser pointer: a new stroke under the pointer, in the tool's color and width.</summary>
    private void PressLaser(in PointerEvent e)
    {
        double now = _options.Clock();
        Style style = DrawingStyle;
        _laser.Lifetime = Workspace.Preferences.LaserFade ?? LaserTrail.DefaultLifetime;
        _laser.Press(e.Position, now, style.Color, style.Width);
        _laserPointer = e.PointerId;
        _laserSampleTime = e.Samples[^1].Timestamp;
        StepLaser(now);
    }

    /// <summary>
    /// Mac mouseDragged while the laser draws: the trail follows the samples of the move, at most one every
    /// <see cref="LaserSampleSpacing"/> milliseconds and always the newest. While frames already come
    /// (<see cref="CanvasLiveState.HasTransitions"/>), the next frame draws it, as the Mac's needsDisplay waits for the
    /// display: drawing the whole trail for every pointer message as well kept the overlay thread so busy that Windows
    /// merged the moves into a few long, straight steps.
    /// </summary>
    private void MoveLaser(in PointerEvent e)
    {
        double now = _options.Clock();
        ReadOnlySpan<PointerSample> samples = e.Samples;
        for (int index = 0; index < samples.Length; index++)
        {
            long time = samples[index].Timestamp;
            if (index < samples.Length - 1 && time - _laserSampleTime < LaserSampleSpacing)
            {
                continue;
            }
            _laser.Move(samples[index].Position, now);
            _laserSampleTime = time;
        }
        if (_state.HasTransitions && LaserMoves)
        {
            return;
        }
        StepLaser(now);
    }

    /// <summary>
    /// Mac mouseUp while the laser draws, and cancelGesture: the stroke ends at <paramref name="point"/>, if given, and fades.
    /// </summary>
    private void ReleaseLaser(Point? point)
    {
        _laserPointer = null;
        if (!_laser.IsDrawing)
        {
            return;
        }
        double now = _options.Clock();
        _laser.Release(point, now);
        StepLaser(now);
    }

    /// <summary>Mac runLaser and stepLaser: the trail at <paramref name="now"/>, redrawn where it was and where it is now.</summary>
    private void StepLaser(double now)
    {
        if (AdvanceLaser(now, out Rect? dirty))
        {
            Publish(CanvasChangeKinds.Transitions, dirty);
        }
    }

    /// <summary>
    /// Mac stepLaser without the redraw: drops what faded by <paramref name="now"/> and draws the trail at that time from now
    /// on. <paramref name="dirty"/> is where it was and where it is now. Hidden drawings clear it at once. False when nothing
    /// changed.
    /// </summary>
    private bool AdvanceLaser(double now, out Rect? dirty)
    {
        dirty = null;
        if (_workspace is not { WorkspaceHidden: false } || _snapshot is null)
        {
            return ClearLaser(out dirty);
        }
        if (_laser.IsEmpty && _laserDrawn is null)
        {
            return false;
        }
        _laser.Prune(now);
        _laserTime = now;
        Rect? current = _laser.Bounds(now);
        dirty = _laserDrawn is { } drawn ? current is { } shown ? drawn.Union(shown) : drawn : current;
        _laserDrawn = current;
        if (_laser.IsEmpty)
        {
            _laserPointer = null;
        }
        return true;
    }

    /// <summary>Mac clearLaser: removes the trail at once, for example when the drawings hide or the display goes away.</summary>
    private bool ClearLaser(out Rect? dirty)
    {
        dirty = _laserDrawn;
        _laserPointer = null;
        if (_laser.IsEmpty && _laserDrawn is null)
        {
            return false;
        }
        _laser = default;
        _laserDrawn = null;
        return true;
    }

    /// <summary>
    /// The workspace changed: hidden drawings or a display that left the workspace clear the trail, and leaving Draw or
    /// choosing another tool lets a held stroke go, so it fades (Mac setDrawing(false) and choose(_:) cancel the gesture).
    /// </summary>
    private void FollowLaser()
    {
        if (_snapshot is null || Workspace.WorkspaceHidden)
        {
            ClearLaser(out _);
            return;
        }
        if (_laser.IsDrawing && (!IsDrawing || Workspace.Tool != Tool.Laser))
        {
            _laserPointer = null;
            double now = _options.Clock();
            _laser.Release(null, now);
            AdvanceLaser(now, out _);
        }
    }
}
