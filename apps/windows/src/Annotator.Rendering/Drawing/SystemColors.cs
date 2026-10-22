using Annotator.Core;
using Annotator.Core.Scene;

namespace Annotator.Rendering.Drawing;

/// <summary>Resolves the appearance-dependent <see cref="SystemColor"/> paints for the current theme.</summary>
public interface ISystemColorResolver
{
    RGBA Resolve(SystemColor color);
}

/// <summary>Fixed colors, for tests and exports that must not depend on the person's theme.</summary>
public sealed class FixedSystemColors(RGBA accent, RGBA windowBackground, RGBA red) : ISystemColorResolver
{
    /// <summary>Windows 11's default blue accent with the light theme's window background and critical red.</summary>
    public static FixedSystemColors Light { get; } = new(Rgb(0x00, 0x5F, 0xB8), Rgb(0xF3, 0xF3, 0xF3), Rgb(0xC4, 0x2B, 0x1C));

    /// <summary>The dark theme's accent, window background and critical red.</summary>
    public static FixedSystemColors Dark { get; } = new(Rgb(0x60, 0xCD, 0xFF), Rgb(0x20, 0x20, 0x20), Rgb(0xFF, 0x99, 0xA4));

    public RGBA Resolve(SystemColor color) => color switch
    {
        SystemColor.Accent => accent,
        SystemColor.WindowBackground => windowBackground,
        SystemColor.Red => red,
        _ => throw new ArgumentOutOfRangeException(nameof(color), color, "Unknown system color."),
    };

    internal static RGBA Rgb(int red, int green, int blue) => new(red / 255.0, green / 255.0, blue / 255.0);
}

/// <summary>
/// The person's theme: the accent color from Windows settings (or a fixed accent the app chooses), and the window
/// background and critical red of the light or dark app theme (WinUI's <c>SolidBackgroundFillColorBase</c> and
/// <c>SystemFillColorCritical</c>), the Windows counterparts of AppKit's <c>controlAccentColor</c>,
/// <c>windowBackgroundColor</c> and <c>systemRed</c>. Call <see cref="Refresh"/> when Windows reports a color or theme
/// change, then redraw.
/// </summary>
public sealed class WindowsSystemColors : ISystemColorResolver
{
    private readonly Windows.UI.ViewManagement.UISettings settings = new();
    private readonly RGBA? fixedAccent;
    private FixedSystemColors current;

    /// <param name="accent">The accent to use instead of the one in Windows settings, or null to follow Windows.</param>
    public WindowsSystemColors(RGBA? accent = null)
    {
        fixedAccent = accent;
        current = Read();
    }

    public bool IsDark { get; private set; }

    public RGBA Resolve(SystemColor color) => current.Resolve(color);

    /// <summary>Reads the colors again; returns true when they changed.</summary>
    public bool Refresh()
    {
        var next = Read();
        var changed = next.Resolve(SystemColor.Accent) != current.Resolve(SystemColor.Accent)
            || next.Resolve(SystemColor.WindowBackground) != current.Resolve(SystemColor.WindowBackground);
        current = next;
        return changed;
    }

    private FixedSystemColors Read()
    {
        var background = settings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
        // The app theme's background is black in dark mode and white in light mode.
        IsDark = background.R + background.G + background.B < 3 * 128;
        var theme = IsDark ? FixedSystemColors.Dark : FixedSystemColors.Light;
        RGBA accent = fixedAccent ?? Accent(settings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent));
        return new FixedSystemColors(accent, theme.Resolve(SystemColor.WindowBackground), theme.Resolve(SystemColor.Red));
    }

    private static RGBA Accent(Windows.UI.Color color) => FixedSystemColors.Rgb(color.R, color.G, color.B);
}
