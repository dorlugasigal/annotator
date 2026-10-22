using Annotator.Core;
using Annotator.Workspace.Breaks;

namespace Annotator.Workspace;

/// <summary>The break timer's settings and its preparation (specification 014).</summary>
public sealed partial class WorkspaceModel
{
    /// <summary>The saved break settings, or the defaults.</summary>
    public BreakTimerSettings BreakTimerSettings => _preferences.BreakTimer ?? new BreakTimerSettings();

    /// <summary>
    /// Mac setBreakTimerSettings: saves the break's length, title, look and locking. Values outside the Mac's limits are
    /// refused with its message and nothing changes.
    /// </summary>
    public void SetBreakTimerSettings(BreakTimerSettings settings)
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
        _preferences.BreakTimer = settings;
        PersistPreferences();
    }

    /// <summary>
    /// Mac prepareForBreak: the break screen covers everything, so work that would run unseen under it stops. Zoom stops,
    /// region sharing stops to its cover and drawing input is released; drawings, boards and history stay.
    /// </summary>
    public void PrepareForBreak()
    {
        using ChangeScope scope = Change();
        StopZoom();
        if (_sharing is { } sharing && sharing.State.IsCapturing)
        {
            sharing.StopForSafety(BreakCopy.SharingPaused);
        }
        foreach (Canvas canvas in _canvases.Values)
        {
            CommitTextIfNeeded(canvas);
        }
        if (_mode == WorkspaceMode.Draw)
        {
            UseApps();
        }
    }
}
