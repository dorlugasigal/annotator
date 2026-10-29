using Annotator.Core;

namespace Annotator.Workspace;

public sealed partial class WorkspaceModel
{
    private bool _spotlight;
    private bool _cursorHalo;
    private ulong _pointerEffectsSerial;

    /// <summary>
    /// Mac <c>spotlight</c>: dims every display except a circle around the pointer. Turning it on with no drawing session
    /// opens one in Use apps, as the Mac's updatePointerEffects does; while the workspace is hidden it only changes the
    /// setting.
    /// </summary>
    public void SetSpotlight(bool on)
    {
        using ChangeScope scope = Change();
        _spotlight = on;
        UpdatePointerEffects();
    }

    /// <summary>Mac <c>cursorHalo</c>: the highlight around the real pointer, with the saved Pointer settings.</summary>
    public void SetCursorHalo(bool on)
    {
        using ChangeScope scope = Change();
        _cursorHalo = on;
        UpdatePointerEffects();
    }

    /// <summary>
    /// Mac <c>spotlightRadius</c>: the Spotlight size slider (50 to 350 points), saved with the preferences. A visible
    /// spotlight eases to the new size (<see cref="Pointer.SpotlightFade"/>).
    /// </summary>
    public void SetSpotlightRadius(double radius)
    {
        if (!double.IsFinite(radius))
        {
            return;
        }
        using ChangeScope scope = Change();
        _preferences.SpotlightRadius = Math.Clamp(radius, 50, 350);
        PersistPreferences();
    }

    /// <summary>Mac <c>laserFade</c>: seconds the laser pointer's trail stays before it fades.</summary>
    public double LaserFade => _preferences.LaserFade ?? LaserTrail.DefaultLifetime;

    /// <summary>
    /// Mac setLaserFade: saves the laser pointer's Fade, to a tenth of a second, from <see cref="LaserTrail.MinimumLifetime"/>
    /// to <see cref="LaserTrail.MaximumLifetime"/>. The next stroke uses it; the default saves nothing.
    /// </summary>
    public void SetLaserFade(double seconds)
    {
        if (!double.IsFinite(seconds))
        {
            return;
        }
        double rounded = Math.Round(Math.Clamp(seconds, LaserTrail.MinimumLifetime, LaserTrail.MaximumLifetime) * 10,
            MidpointRounding.AwayFromZero) / 10;
        if (rounded == LaserFade)
        {
            return;
        }
        using ChangeScope scope = Change();
        _preferences.LaserFade = rounded == LaserTrail.DefaultLifetime ? null : rounded;
        PersistPreferences();
    }

    /// <summary>
    /// Mac setCursorHighlight: saves the cursor halo's shape, outline, color, size, border, opacity, glow, click effect and
    /// visibility. Values outside the Mac's limits are refused with its message and nothing changes.
    /// </summary>
    public void SetCursorHighlight(CursorHighlightSettings settings)
    {
        using ChangeScope scope = Change();
        try
        {
            settings.Validate();
        }
        catch (CanvasError error)
        {
            Report(error);
            return;
        }
        _preferences.CursorHighlight = settings;
        PersistPreferences();
    }

    /// <summary>
    /// Mac updatePointerEffects: every change of Spotlight or Cursor halo restarts the pointer watching on the overlays
    /// (<see cref="WorkspaceSnapshot.PointerEffectsSerial"/>), and an effect that turns on without a drawing session opens one
    /// in Use apps.
    /// </summary>
    private void UpdatePointerEffects()
    {
        _pointerEffectsSerial++;
        if (_hidden || !(_spotlight || _cursorHalo) || _active is not null)
        {
            return;
        }
        BeginDrawing(recordsUsage: false);
        UseApps();
    }
}
