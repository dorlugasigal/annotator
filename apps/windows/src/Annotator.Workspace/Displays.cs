using Annotator.Core;

namespace Annotator.Workspace;

/// <summary>
/// A display's identity, stable while the display stays connected: the platform's display name
/// (for example <c>\\.\DISPLAY1</c> on Windows).
/// </summary>
public readonly record struct DisplayId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>
/// One connected display, without platform types. <see cref="DesktopFrame"/> and <see cref="DesktopWorkArea"/>
/// use the platform's desktop units with a top-left origin and y pointing down (physical pixels on Windows).
/// <see cref="Scale"/> is desktop units per point. Positions on a display are points from its top-left corner
/// (WIN-CORE-06), so a display with another scale keeps positions in points (WIN-OVR-05).
/// </summary>
public sealed record DisplayDescriptor
{
    public DisplayDescriptor(DisplayId id, Rect desktopFrame, Rect desktopWorkArea, double scale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id.Value, nameof(id));
        if (!IsUsable(desktopFrame) || !IsUsable(desktopWorkArea))
        {
            throw CanvasError.InvalidDocument("The display geometry is unavailable.");
        }
        if (!double.IsFinite(scale) || scale <= 0)
        {
            throw CanvasError.InvalidDocument("The display scale is unavailable.");
        }
        Id = id;
        DesktopFrame = desktopFrame;
        DesktopWorkArea = desktopWorkArea;
        Scale = scale;
    }

    public DisplayId Id { get; }

    /// <summary>The whole display, taskbar included, in desktop units.</summary>
    public Rect DesktopFrame { get; }

    /// <summary>The area outside the taskbar and app bars (the Mac visible frame), in desktop units.</summary>
    public Rect DesktopWorkArea { get; }

    /// <summary>Desktop units per point, for example 1.25 at 125% scale on Windows.</summary>
    public double Scale { get; }

    /// <summary>The whole display in points, with its origin at the display's top-left corner.</summary>
    public Rect Bounds => new(0, 0, DesktopFrame.Width / Scale, DesktopFrame.Height / Scale);

    /// <summary>The work area in points of this display.</summary>
    public Rect WorkArea => new(
        (DesktopWorkArea.X - DesktopFrame.X) / Scale,
        (DesktopWorkArea.Y - DesktopFrame.Y) / Scale,
        DesktopWorkArea.Width / Scale,
        DesktopWorkArea.Height / Scale);

    /// <summary>Converts a point on this display to desktop units.</summary>
    public Point ToDesktop(Point local) => new(DesktopFrame.X + local.X * Scale, DesktopFrame.Y + local.Y * Scale);

    /// <summary>Converts a desktop position to points on this display (it may lie outside the display).</summary>
    public Point ToLocal(Point desktop) => new((desktop.X - DesktopFrame.X) / Scale, (desktop.Y - DesktopFrame.Y) / Scale);

    /// <summary>True when the desktop position lies on this display (right and bottom edges excluded).</summary>
    public bool ContainsDesktopPoint(Point desktop) =>
        desktop.X >= DesktopFrame.X && desktop.X < DesktopFrame.MaxX && desktop.Y >= DesktopFrame.Y && desktop.Y < DesktopFrame.MaxY;

    private static bool IsUsable(Rect rect) =>
        double.IsFinite(rect.X) && double.IsFinite(rect.Y) && double.IsFinite(rect.Width) && double.IsFinite(rect.Height)
        && rect.Width > 0 && rect.Height > 0;
}

/// <summary>
/// The version of one display's document as the workspace published it. A canvas edit names the version it
/// started from, and the workspace accepts it only while the display is still at that version. An accepted
/// edit moves the display to <see cref="Next"/>, so a canvas can chain its next edit on the version it
/// predicts. Every other change gives the display a fresh version that no canvas can predict, so an edit built
/// on a rejected or cancelled one is never accepted.
/// </summary>
public readonly record struct DocumentVersion(ulong Value)
{
    /// <summary>The version an accepted edit based on this version produces.</summary>
    public DocumentVersion Next => new(Value + 1);

    /// <summary>Fresh versions use the high 32 bits; chained canvas edits count in the low 32 bits.</summary>
    internal static DocumentVersion Fresh(uint epoch) => new((ulong)epoch << 32);

    public override string ToString() => $"{Value >> 32}.{Value & 0xFFFF_FFFF}";
}
