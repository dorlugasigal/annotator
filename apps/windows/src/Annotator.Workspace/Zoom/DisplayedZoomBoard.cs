using Annotator.Core;

namespace Annotator.Workspace.Zoom;

/// <summary>What one display shows of zoom now: the displayed mode and factor, and whether screen zoom ramps out to 1x.</summary>
public sealed record DisplayedZoom(PresentationMode Mode, double Magnification, bool Exiting);

/// <summary>
/// What each display shows of zoom, written by the overlay thread whenever it changes and read by the app thread, as the
/// Mac's workspace reads its presentation's displayed factor and viewport (Mac <c>presentation?.magnification</c>,
/// <c>isExitingScreenZoom</c> and <c>screenViewport</c>). Thread-safe.
/// </summary>
public sealed class DisplayedZoomBoard
{
    private readonly Lock _gate = new();
    private readonly Dictionary<DisplayId, DisplayedZoom> _states = [];
    private readonly Dictionary<DisplayId, Rect> _sources = [];

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

    /// <summary>Records the part of <paramref name="display"/> that screen zoom shows (display points); null without screen zoom.</summary>
    public void SetSource(DisplayId display, Rect? source)
    {
        lock (_gate)
        {
            if (source is { } shown)
            {
                _sources[display] = shown;
            }
            else
            {
                _sources.Remove(display);
            }
        }
    }

    /// <summary>The part of <paramref name="display"/> that screen zoom shows now (Mac screenViewport.source), or null.</summary>
    public Rect? Source(DisplayId display)
    {
        lock (_gate)
        {
            return _sources.TryGetValue(display, out Rect source) ? source : null;
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
