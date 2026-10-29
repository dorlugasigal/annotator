using Annotator.Core;

namespace Annotator.Workspace.Toolbar;

/// <summary>
/// What a press on the toolbar landed on, for each element from the one under the pointer out to the toolbar (the press's
/// ancestry): the Mac marks sliders and number fields with ToolbarPressMarker.
/// </summary>
public enum ToolbarPressTarget
{
    /// <summary>Anything else: a button, a name, a menu or the background.</summary>
    Other,

    /// <summary>A slider, which follows the pointer while it is held.</summary>
    Slider,

    /// <summary>A text field that can be typed in.</summary>
    TextField,

    /// <summary>A number field.</summary>
    NumberField,
}

/// <summary>What a pointer move did to a press that waits to become a hold.</summary>
public enum ToolbarHoldMove
{
    /// <summary>No press of that pointer waits.</summary>
    None,

    /// <summary>The pointer stayed within <see cref="ToolbarHold.Slop"/> points: the press keeps waiting.</summary>
    Still,

    /// <summary>
    /// The pointer moved further: the press no longer waits, and the host picks the toolbar up from the press, as a hold
    /// does (the background's own drag starts by itself).
    /// </summary>
    Moved,
}

/// <summary>
/// The decisions of Mac InspectorPanel's press and drag or press and hold (<see cref="ToolbarHold"/>), without a window. A
/// left press anywhere on the toolbar waits up to <see cref="ToolbarHold.Delay"/> seconds: letting go first leaves the
/// press to the control under the pointer, and moving further than <see cref="ToolbarHold.Slop"/> points or staying still
/// that long picks the toolbar up from where the press started, so the control never gets it. Sliders and editable fields get their
/// press at once, because holding them is how people use them. Positions are in points of the toolbar window; the host runs
/// the timer and moves the toolbar (<see cref="ToolbarDocking.Hold"/>). Use it on one thread.
/// </summary>
public sealed class ToolbarHoldGesture
{
    private Point? _press;
    private uint _pointer;

    /// <summary>A press waits to become a hold (Mac isHoldingPress).</summary>
    public bool IsWaiting => _press is not null;

    /// <summary>
    /// Mac keepsPressAtOnce: a press on a slider, a text field or a number field, or anywhere inside one, gets its press at
    /// once, because holding one is how people use it.
    /// </summary>
    public static bool KeepsPressAtOnce(IEnumerable<ToolbarPressTarget> ancestry)
    {
        ArgumentNullException.ThrowIfNull(ancestry);
        return ancestry.Any(target => target != ToolbarPressTarget.Other);
    }

    /// <summary>
    /// <see cref="Press(uint, Point, bool, bool)"/> for a press whose <paramref name="ancestry"/> runs from the element under
    /// the pointer out to the toolbar: a press inside a slider or a field never waits.
    /// </summary>
    public bool Press(uint pointer, Point point, bool allowed, IEnumerable<ToolbarPressTarget> ancestry) =>
        Press(pointer, point, allowed, KeepsPressAtOnce(ancestry));

    /// <summary>
    /// Mac holdPress for leftMouseDown: <paramref name="pointer"/> went down at <paramref name="point"/>. Returns true when the
    /// press waits for the hold, so the host starts its <see cref="ToolbarHold.Delay"/> timer again. A press that still
    /// waits is dropped first: its release went elsewhere, so it can't pick the toolbar up later. The new press waits only
    /// when holding is <paramref name="allowed"/> now (the toolbar shows, rests and isn't being dragged) and it isn't on a
    /// control that <paramref name="keepsPressAtOnce"/> (a slider or an editable field).
    /// </summary>
    public bool Press(uint pointer, Point point, bool allowed, bool keepsPressAtOnce)
    {
        _press = null;
        if (!allowed || keepsPressAtOnce)
        {
            return false;
        }
        _press = point;
        _pointer = pointer;
        return true;
    }

    /// <summary>Mac holdPress for leftMouseDragged: a small wobble still waits; a larger move picks the toolbar up.</summary>
    public ToolbarHoldMove Move(uint pointer, Point point)
    {
        if (_press is not { } press || pointer != _pointer)
        {
            return ToolbarHoldMove.None;
        }
        if (ToolbarHold.IsStill(point.X - press.X, point.Y - press.Y))
        {
            return ToolbarHoldMove.Still;
        }
        _press = null;
        return ToolbarHoldMove.Moved;
    }

    /// <summary>
    /// Mac holdPress for leftMouseUp: the button went up, or the press was cancelled, before the hold ended. The press stays
    /// the control's. Returns true when a press of <paramref name="pointer"/> was waiting.
    /// </summary>
    public bool Release(uint pointer)
    {
        if (_press is null || pointer != _pointer)
        {
            return false;
        }
        _press = null;
        return true;
    }

    /// <summary>
    /// Mac finishHold: the delay passed. Returns where the press started when the toolbar is picked up now, with the button
    /// still down and holding still <paramref name="allowed"/>; otherwise null, and the press stays the control's (a slow
    /// click). The press no longer waits either way.
    /// </summary>
    public Point? Elapse(bool buttonDown, bool allowed)
    {
        if (_press is not { } press)
        {
            return null;
        }
        _press = null;
        return buttonDown && allowed ? press : null;
    }

    /// <summary>Mac clearHold: the toolbar hid or closed; nothing waits.</summary>
    public void Cancel() => _press = null;
}
