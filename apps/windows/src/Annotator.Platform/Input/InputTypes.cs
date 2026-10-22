using System.Numerics;

namespace Annotator.Platform.Input;

public enum PointerInputKind
{
    /// <summary>The pointer came over the overlay.</summary>
    Enter,

    /// <summary>A button went down or a pen or finger touched.</summary>
    Down,

    /// <summary>The pointer moved; <see cref="PointerInput.Samples"/> holds every coalesced sample.</summary>
    Move,

    /// <summary>A button went up or a pen or finger lifted.</summary>
    Up,

    /// <summary>The pointer left the overlay.</summary>
    Leave,

    /// <summary>Vertical wheel; see <see cref="PointerInput.WheelDelta"/>.</summary>
    Wheel,

    /// <summary>Horizontal wheel; see <see cref="PointerInput.WheelDelta"/>.</summary>
    HorizontalWheel,

    /// <summary>
    /// Another window or the system took the pointer: cancel the gesture in progress. It can
    /// arrive more than once for one gesture, so cancelling must be harmless when nothing is active.
    /// </summary>
    Canceled,
}

public enum PointerDeviceKind
{
    Mouse,
    Pen,
    Touch,
    Touchpad,
}

[Flags]
public enum PointerButtons
{
    None = 0,

    /// <summary>The left mouse button, the pen tip or a touch contact.</summary>
    Left = 1,

    /// <summary>The right mouse button or the pen barrel button.</summary>
    Right = 2,

    Middle = 4,
    X1 = 8,
    X2 = 16,
}

[Flags]
public enum ModifierKeys
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
    Windows = 8,
}

[Flags]
public enum PenState
{
    None = 0,
    Barrel = 1,
    Inverted = 2,
    Eraser = 4,
}

/// <summary>
/// One pointer position. <see cref="Position"/> is in physical pixels from the overlay's
/// top-left corner (the monitor's top-left corner). <see cref="Pressure"/> runs from 0 to 1
/// and is meaningful only when <see cref="PointerInput.HasPressure"/> is true.
/// <see cref="Time"/> is the message time in milliseconds.
/// </summary>
public readonly record struct PointerSample(Vector2 Position, float Pressure, uint Time);

/// <summary>A pointer message from an overlay. Valid only during the sink call.</summary>
public readonly ref struct PointerInput
{
    public PointerInputKind Kind { get; init; }

    public PointerDeviceKind Device { get; init; }

    /// <summary>The Windows pointer id. Mouse messages without pointer input use 1.</summary>
    public uint PointerId { get; init; }

    /// <summary>The buttons held after this message.</summary>
    public PointerButtons Buttons { get; init; }

    /// <summary>The button that went down or up in this message, or None.</summary>
    public PointerButtons ChangedButton { get; init; }

    public ModifierKeys Modifiers { get; init; }

    public PenState Pen { get; init; }

    public bool HasPressure { get; init; }

    /// <summary>Wheel distance in WHEEL_DELTA units (120 per notch) for wheel messages, otherwise 0.</summary>
    public int WheelDelta { get; init; }

    /// <summary>Every sample since the previous message, oldest first. Never empty.</summary>
    public ReadOnlySpan<PointerSample> Samples { get; init; }

    public PointerSample Current => Samples[^1];
}

public enum KeyInputKind
{
    KeyDown,
    KeyUp,

    /// <summary>A character from WM_CHAR or WM_SYSCHAR after keyboard-layout translation.</summary>
    Character,
}

/// <summary>A key message from an overlay.</summary>
/// <param name="VirtualKey">The Windows virtual-key code; 0 for <see cref="KeyInputKind.Character"/>.</param>
/// <param name="IsSystemKey">True for WM_SYSKEYDOWN, WM_SYSKEYUP and WM_SYSCHAR (Alt held or F10).</param>
/// <param name="Character">The UTF-16 code unit for <see cref="KeyInputKind.Character"/>, otherwise '\0'.</param>
public readonly record struct KeyInput(
    KeyInputKind Kind,
    int VirtualKey,
    int ScanCode,
    bool IsExtendedKey,
    bool IsRepeat,
    bool IsSystemKey,
    ModifierKeys Modifiers,
    char Character);
