using Annotator.Core;

namespace Annotator.Workspace.Zoom;

/// <summary>What one display shows of zoom now: the displayed mode and factor, and whether screen zoom ramps out to 1x.</summary>
public sealed record DisplayedZoom(PresentationMode Mode, double Magnification, bool Exiting);

/// <summary>
/// What each display shows of zoom, written by the overlay thread whenever it changes and read by the app thread, as the
/// Mac's workspace reads its presentation's displayed factor (Mac <c>presentation?.magnification</c> and
/// <c>isExitingScreenZoom</c>). Thread-safe.
/// </summary>
public sealed class DisplayedZoomBoard
{
    private readonly Lock _gate = new();
    private readonly Dictionary<DisplayId, DisplayedZoom> _states = [];

    /// <summary>Records what <paramref name="display"/> shows; null when it shows no zoom.</summary>
    public void Set(DisplayId display, DisplayedZoom? state)
    {
        lock (_gate)
        {
            if (state is null)
            {
                _states.Remove(display);
            }
            else
            {
                _states[display] = state;
            }
        }
    }

    public DisplayedZoom? Get(DisplayId display)
    {
        lock (_gate)
        {
            return _states.TryGetValue(display, out DisplayedZoom? state) ? state : null;
        }
    }

    /// <summary>Whether any display shows screen zoom, including a zoom-out ramp.</summary>
    public bool AnyScreenZoom
    {
        get
        {
            lock (_gate)
            {
                foreach (DisplayedZoom state in _states.Values)
                {
                    if (state.Mode == PresentationMode.Screen)
                    {
                        return true;
                    }
                }
                return false;
            }
        }
    }
}
