using Annotator.Core.Motion;

namespace Annotator.Workspace.Pointer;

/// <summary>
/// Port of the Mac <c>SpotlightFade</c> (SelectionOverlay.swift) without its display ticker: the spotlight's dimming fades
/// in over 0.18 s and out over 0.14 s on the fade curve, each scaled by the share of the change still to go, and a new size
/// eases over 0.12 s on the standard curve. The host calls <see cref="Step"/> once per display frame while
/// <see cref="IsAnimating"/>, and <see cref="Finish"/> where the Mac's frame callback returns false (a hidden workspace),
/// so a resting spotlight, on or off, needs no frames.
/// </summary>
public sealed class SpotlightFade
{
    private double _intensityFrom;
    private double? _intensityStart;
    private double _radiusFrom;
    private double? _radiusTarget;
    private double? _displayedRadius;
    private double? _radiusStart;
    private bool _fading;
    private bool _resizing;

    /// <summary>The dimming's strength now, 0 to 1: the spotlight's opacity.</summary>
    public double Intensity { get; private set; }

    /// <summary>Whether the spotlight is turned on: the fade's target.</summary>
    public bool Visible { get; private set; }

    /// <summary>Whether anything is drawn: the dimming has some strength.</summary>
    public bool IsVisible => Intensity > 0;

    /// <summary>Whether a fade or a size change still needs frames.</summary>
    public bool IsAnimating => _fading || _resizing;

    /// <summary>Starts fading toward <paramref name="visible"/> from the strength shown now.</summary>
    public void SetVisible(bool visible)
    {
        if (visible == Visible)
        {
            return;
        }
        Visible = visible;
        _intensityFrom = Intensity;
        _intensityStart = null;
        _fading = true;
    }

    /// <summary>
    /// Animates from the size on screen to <paramref name="radius"/> points. Without a visible spotlight, or with Reduce Motion,
    /// the size changes at once. <paramref name="previous"/> is the size before the change.
    /// </summary>
    public void SetRadius(double radius, double previous, bool reducesMotion)
    {
        if (!double.IsFinite(radius))
        {
            return;
        }
        double current = _displayedRadius ?? previous;
        _radiusTarget = radius;
        if (!IsVisible || reducesMotion || !double.IsFinite(current) || current == radius)
        {
            _displayedRadius = radius;
            _resizing = false;
            return;
        }
        _radiusFrom = current;
        _displayedRadius = current;
        _radiusStart = null;
        _resizing = true;
    }

    /// <summary>The radius to draw for the workspace's current <paramref name="target"/>. A target set elsewhere is shown at once.</summary>
    public double Radius(double target) => _radiusTarget == target ? _displayedRadius ?? target : target;

    /// <summary>Lands on the end state at once: full or no strength, and the target size.</summary>
    public void Finish()
    {
        Intensity = Visible ? 1 : 0;
        if (_radiusTarget is { } target)
        {
            _displayedRadius = target;
        }
        _fading = false;
        _resizing = false;
    }

    /// <summary>
    /// One display frame at <paramref name="now"/> seconds on a steady clock. The first frame of a change shows its start, as
    /// the Mac's does. Returns whether more frames are needed.
    /// </summary>
    public bool Step(double now)
    {
        if (_fading)
        {
            double start = _intensityStart ?? now;
            _intensityStart = start;
            double target = Visible ? 1 : 0;
            double duration = (Visible ? MotionTiming.SpotlightFadeInDuration : MotionTiming.SpotlightFadeOutDuration)
                * Math.Max(0.001, Math.Abs(target - _intensityFrom));
            double time = Math.Min(1, Math.Max(0, (now - start) / duration));
            Intensity = _intensityFrom + (target - _intensityFrom) * MotionCurves.Fade.Progress(time);
            if (time >= 1)
            {
                Intensity = target;
                _fading = false;
            }
        }
        if (_resizing && _radiusTarget is { } radiusTarget)
        {
            double start = _radiusStart ?? now;
            _radiusStart = start;
            double time = Math.Min(1, Math.Max(0, (now - start) / MotionTiming.SpotlightResizeDuration));
            _displayedRadius = _radiusFrom + (radiusTarget - _radiusFrom) * MotionCurves.Standard.Progress(time);
            if (time >= 1)
            {
                _displayedRadius = radiusTarget;
                _resizing = false;
            }
        }
        return IsAnimating;
    }
}
