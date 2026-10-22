using Annotator.Core;

namespace Annotator.Workspace.Canvas;

/// <summary>The device behind a pointer event.</summary>
public enum PointerDevice
{
    Mouse,

    /// <summary>A pen or stylus. Its pressure shapes pressure strokes (WIN-INP-01).</summary>
    Pen,

    /// <summary>One finger on a touch screen. It draws like a mouse (D-9).</summary>
    Touch,
}

/// <summary>Pointer buttons. A pen tip and a touch contact are <see cref="Primary"/>; a pen barrel button is <see cref="Secondary"/>.</summary>
[Flags]
public enum PointerButtons
{
    None = 0,
    Primary = 1,
    Secondary = 2,
    Middle = 4,
}

/// <summary>Modifier keys of the canvas keyboard mapping (spec 013): Command and Control become Ctrl, Option becomes Alt.</summary>
[Flags]
public enum KeyModifiers
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
    Alt = 4,
}

public enum PointerPhase
{
    /// <summary>A button went down, or a pen or finger touched.</summary>
    Down,

    /// <summary>The pointer moved, with or without a button held.</summary>
    Move,

    /// <summary>A button went up, or a pen or finger lifted.</summary>
    Up,

    /// <summary>Another window or the system took the pointer: the gesture in progress ends without a change.</summary>
    Cancel,

    /// <summary>The pointer left the canvas.</summary>
    Leave,
}

/// <summary>
/// One pointer position in points from the display's top-left corner (WIN-CORE-06). <paramref name="Pressure"/>
/// runs from 0 to 1 and counts only when <see cref="PointerEvent.HasPressure"/> is true. <paramref name="Timestamp"/>
/// is in milliseconds from any fixed origin.
/// </summary>
public readonly record struct PointerSample(Point Position, double Pressure = 1, long Timestamp = 0);

/// <summary>
/// A pointer event for one display's canvas. <see cref="Samples"/> holds every sample since the previous event of
/// the same pointer, oldest first (the coalesced history), so no sample is lost (WIN-INP-01). Valid only during
/// the call that receives it.
/// </summary>
public readonly ref struct PointerEvent
{
    public PointerPhase Phase { get; init; }

    public PointerDevice Device { get; init; }

    /// <summary>Identifies the pointer; a gesture follows the pointer that started it.</summary>
    public uint PointerId { get; init; }

    /// <summary>The buttons held after this event.</summary>
    public PointerButtons Buttons { get; init; }

    /// <summary>The button that went down or up in this event, or <see cref="PointerButtons.None"/>.</summary>
    public PointerButtons ChangedButton { get; init; }

    public KeyModifiers Modifiers { get; init; }

    /// <summary>True when the device reports pressure (most pens).</summary>
    public bool HasPressure { get; init; }

    /// <summary>Every sample since the previous event, oldest first. An event without samples is ignored.</summary>
    public ReadOnlySpan<PointerSample> Samples { get; init; }

    /// <summary>The newest sample.</summary>
    public PointerSample Current => Samples[^1];

    public Point Position => Samples[^1].Position;
}

/// <summary>
/// A key press on a canvas. <paramref name="VirtualKey"/> is the Windows virtual-key code (see <see cref="VirtualKeys"/>);
/// <paramref name="Text"/> is the character the active keyboard layout produces for the key without modifiers
/// (WIN-INP-03, the Mac's charactersIgnoringModifiers), or null when the key produces none.
/// </summary>
public readonly record struct KeyEvent(int VirtualKey, KeyModifiers Modifiers = KeyModifiers.None, string? Text = null, bool IsRepeat = false);

/// <summary>The Windows virtual-key codes the canvas uses. Letters are their uppercase character codes.</summary>
public static class VirtualKeys
{
    public const int Back = 0x08;
    public const int Tab = 0x09;
    public const int Enter = 0x0D;
    public const int Escape = 0x1B;
    public const int Space = 0x20;
    public const int End = 0x23;
    public const int Home = 0x24;
    public const int Left = 0x25;
    public const int Up = 0x26;
    public const int Right = 0x27;
    public const int Down = 0x28;
    public const int Insert = 0x2D;
    public const int Delete = 0x2E;
    public const int A = 0x41;
    public const int B = 0x42;
    public const int C = 0x43;
    public const int D = 0x44;
    public const int G = 0x47;
    public const int L = 0x4C;
    public const int O = 0x4F;
    public const int S = 0x53;
    public const int V = 0x56;
    public const int W = 0x57;
    public const int X = 0x58;
    public const int Y = 0x59;
    public const int Z = 0x5A;

    /// <summary>The comma key (VK_OEM_COMMA) on most layouts.</summary>
    public const int OemComma = 0xBC;

    /// <summary>VK_PROCESSKEY: an input method took the key; the key itself is not for the canvas.</summary>
    public const int ProcessKey = 0xE5;

    /// <summary>VK_PACKET: a character sent without a key (the touch keyboard, SendInput); the character follows.</summary>
    public const int Packet = 0xE7;
}
