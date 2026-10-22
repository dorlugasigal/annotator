namespace Annotator.Platform.Displays;

/// <summary>A rectangle in physical pixels of the virtual screen (Per-Monitor v2 coordinates).</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(PixelRect other) =>
        other.Left >= Left && other.Top >= Top && other.Right <= Right && other.Bottom <= Bottom;

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public static PixelRect FromSize(int left, int top, int width, int height) => new(left, top, left + width, top + height);
}

/// <summary>
/// One monitor. <see cref="Bounds"/> is the whole monitor, including the taskbar;
/// <see cref="WorkArea"/> excludes the taskbar and app bars (the Mac visible frame).
/// </summary>
/// <param name="Handle">The HMONITOR. Windows may issue new handles after a display change.</param>
/// <param name="DeviceName">The GDI device name, for example <c>\\.\DISPLAY1</c>.</param>
/// <param name="Dpi">The effective DPI: 96 at 100% scale, 120 at 125%.</param>
public sealed record DisplayInfo(nint Handle, string DeviceName, PixelRect Bounds, PixelRect WorkArea, uint Dpi, bool IsPrimary)
{
    /// <summary>The scale factor, for example 1.25 at 120 DPI.</summary>
    public double Scale => Dpi / 96.0;
}
