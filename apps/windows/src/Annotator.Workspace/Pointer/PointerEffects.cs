using Annotator.Core;

namespace Annotator.Workspace.Pointer;

/// <summary>
/// What the overlays draw for Spotlight and Cursor halo, decided as the Mac's WorkspaceModel, CursorClickAnimation and
/// SelectionOverlayView decide it, without clocks or windows: <see cref="Update"/> takes each workspace snapshot,
/// <see cref="NoteMove"/> and <see cref="NoteClick"/> take pointer activity, and the queries answer for a time on a steady
/// clock in seconds. <see cref="NeedsFrames"/> says when the host must step it every display frame, and
/// <see cref="NextChange"/> when something changes without frames (the halo hides after a move or a click). With neither,
/// the host does no work. Use it on one thread.
/// </summary>
public sealed class PointerEffects
{
    private ulong? _serial;
    private bool _shown;
    private double? _radius;
    private double? _clickStarted;
    private bool _clickAnimated;

    public SpotlightFade Spotlight { get; } = new();

    /// <summary>The workspace shows (Draw or Use apps, not hidden): only then do the overlays draw pointer effects.</summary>
    public bool Shown => _shown;

    public bool SpotlightOn { get; private set; }

    public bool HaloOn { get; private set; }

    /// <summary>The saved Pointer settings (Mac cursorHighlightSettings).</summary>
    public CursorHighlightSettings Settings { get; private set; } = new();

    /// <summary>Mac lastPointerMovement, or null.</summary>
    public double? LastMovement { get; private set; }

    /// <summary>Mac lastPointerClick, or null.</summary>
    public double? LastClick { get; private set; }

    /// <summary>Mac cursorClickLocation: where the last click happened, in desktop pixels, or null.</summary>
    public Point? ClickLocation { get; private set; }

    /// <summary>Mac pointer monitors: pointer activity matters while the workspace shows and an effect is on.</summary>
    public bool Watching => _shown && (SpotlightOn || HaloOn);

    /// <summary>The radius to draw in points: the saved Spotlight size, or the size it is easing through.</summary>
    public double SpotlightRadius => Spotlight.Radius(_radius ?? 130);

    /// <summary>Whether the overlays draw the spotlight's dimming now: shown, with some strength.</summary>
    public bool SpotlightVisible => _shown && Spotlight.IsVisible;

    /// <summary>
    /// Takes a workspace snapshot, as the Mac's property observers do: Spotlight fades toward its setting, a new size eases
    /// in, and every change of Spotlight or Cursor halo (<see cref="WorkspaceSnapshot.PointerEffectsSerial"/>), a hide and a
    /// show forget the halo's last movement, click and click animation (Mac stopPointerMonitors). A hidden workspace lands the
    /// spotlight on its end state (the Mac's frame callback returns false).
    /// </summary>
    public void Update(WorkspaceSnapshot snapshot, bool reducesMotion)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        bool shown = snapshot.Surfaces != SurfaceMode.Hidden;
        double radius = snapshot.Preferences.SpotlightRadius;
        if (_radius is { } previous && previous != radius)
        {
            Spotlight.SetRadius(radius, previous, reducesMotion);
        }
        _radius = radius;
        Spotlight.SetVisible(snapshot.Spotlight);
        if (_serial != snapshot.PointerEffectsSerial || shown != _shown)
        {
            Forget();
        }
        _serial = snapshot.PointerEffectsSerial;
        _shown = shown;
        SpotlightOn = snapshot.Spotlight;
        HaloOn = snapshot.CursorHalo;
        Settings = snapshot.CursorHighlight;
        if (!shown)
        {
            Spotlight.Finish();
        }
    }

    /// <summary>Mac notePointerEvent for a movement: the halo's Moving visibility counts from now.</summary>
    public void NoteMove(double now)
    {
        if (Watching)
        {
            LastMovement = now;
        }
    }

    /// <summary>
    /// Mac notePointerEvent for a left or right press at <paramref name="location"/> (desktop pixels): with the halo on, the
    /// click effect starts, animated unless it is Off or <paramref name="reducesMotion"/>, which show the static emphasis.
    /// </summary>
    public void NoteClick(double now, Point location, bool reducesMotion)
    {
        if (!Watching)
        {
            return;
        }
        LastClick = now;
        ClickLocation = location;
        if (!HaloOn)
        {
            return;
        }
        _clickStarted = now;
        _clickAnimated = Settings.ResolvedClickEffect != CursorClickEffect.Off && !reducesMotion;
    }

    /// <summary>Mac pointerClickAnimation.progress: 0 to 1 through the 0.45-second click, else null.</summary>
    public double? ClickProgress(double now) => CursorHighlightSettings.ClickProgress(now, _clickStarted);

    /// <summary>Mac cursorHighlightVisible: the halo is on, the workspace shows, and its visibility setting allows it now.</summary>
    public bool HaloVisible(double now) => _shown && HaloOn && Settings.IsVisible(now, LastMovement, LastClick);

    /// <summary>Whether the host must call <see cref="Advance"/> on every display frame: a fade, a size change or an animated click.</summary>
    public bool NeedsFrames(double now) =>
        (_shown && Spotlight.IsAnimating) || (_shown && HaloOn && _clickAnimated && ClickProgress(now) is not null);

    /// <summary>One display frame: the spotlight's fade and size move on (Mac SpotlightFade.step).</summary>
    public void Advance(double now)
    {
        if (_shown)
        {
            Spotlight.Step(now);
        }
        else
        {
            Spotlight.Finish();
        }
    }

    /// <summary>
    /// The next time after <paramref name="now"/> when the halo changes without frames: it hides 0.75 s after the last
    /// movement (Moving) or 0.45 s after a click (Moving, Clicks), and a static click emphasis ends after 0.45 s. Null when
    /// nothing is pending.
    /// </summary>
    public double? NextChange(double now)
    {
        if (!_shown || !HaloOn)
        {
            return null;
        }
        double? next = null;
        void Consider(double? time)
        {
            if (time is { } value && value > now && (next is null || value < next))
            {
                next = value;
            }
        }
        if (_clickStarted is { } started)
        {
            Consider(started + CursorHighlightSettings.ClickDuration);
        }
        switch (Settings.Visibility)
        {
            case CursorHighlightVisibility.Moving:
                Consider(LastMovement + 0.75);
                Consider(LastClick + CursorHighlightSettings.ClickDuration);
                break;
            case CursorHighlightVisibility.Clicks:
                Consider(LastClick + CursorHighlightSettings.ClickDuration);
                break;
        }
        return next;
    }

    /// <summary>Mac stopPointerMonitors: forgets the last movement, click and click animation.</summary>
    private void Forget()
    {
        LastMovement = null;
        LastClick = null;
        ClickLocation = null;
        _clickStarted = null;
        _clickAnimated = false;
    }
}
