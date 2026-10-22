using Windows.Win32;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;
using WinColor = Windows.UI.Color;

namespace Annotator.App.Shell;

/// <summary>The system colors of a Windows contrast theme that app windows use (Settings > Accessibility > Contrast themes).</summary>
internal enum ContrastColor
{
    /// <summary>Backgrounds of windows, panes and popups.</summary>
    Window = 5,

    /// <summary>Text, glyphs, borders and anything that cannot be used.</summary>
    WindowText = 8,

    /// <summary>The background of selected, hovered and pressed controls.</summary>
    Highlight = 13,

    /// <summary>Text and glyphs on <see cref="Highlight"/>.</summary>
    HighlightText = 14,

    /// <summary>Button backgrounds.</summary>
    ButtonFace = 15,

    /// <summary>Unavailable text and controls.</summary>
    GrayText = 17,

    /// <summary>Text and glyphs of buttons and other controls that can be used.</summary>
    ButtonText = 18,

    /// <summary>Links.</summary>
    Hotlight = 26,
}

/// <summary>
/// Windows contrast themes for app windows (WIN-UI-07, WIN-A11Y-03): whether one is on, and its colors. While one is on, app
/// windows use the HighContrast resources of App.xaml and these colors for what they draw themselves; drawings, the colors
/// people choose for them and pictures of them keep their own colors. Development builds can simulate a theme for checks
/// (the UX reference's <c>--contrast</c>), which never changes the Windows setting.
/// </summary>
internal static class ContrastTheme
{
    private static IReadOnlyDictionary<int, uint>? s_simulated;

    /// <summary>Development only: shows app windows with these system colors (GetSysColor index to ARGB), as in a contrast theme.</summary>
    public static void Simulate(IEnumerable<(int Index, uint Color)> colors) =>
        s_simulated = colors.ToDictionary(entry => entry.Index, entry => entry.Color);

    /// <summary>True while a Windows contrast theme is on (or simulated).</summary>
    public static unsafe bool IsOn
    {
        get
        {
            if (s_simulated is not null)
            {
                return true;
            }
            var contrast = new HIGHCONTRASTW { cbSize = (uint)sizeof(HIGHCONTRASTW) };
            return PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETHIGHCONTRAST, contrast.cbSize, &contrast, 0) &&
                (contrast.dwFlags & HIGHCONTRASTW_FLAGS.HCF_HIGHCONTRASTON) != 0;
        }
    }

    /// <summary>The contrast theme's <paramref name="color"/>; meaningful while <see cref="IsOn"/>.</summary>
    public static WinColor Color(ContrastColor color)
    {
        if (s_simulated is { } simulated && simulated.TryGetValue((int)color, out uint argb))
        {
            return WinColor.FromArgb(0xFF, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        }
        uint bgr = PInvoke.GetSysColor((SYS_COLOR_INDEX)(int)color);
        return WinColor.FromArgb(0xFF, (byte)bgr, (byte)(bgr >> 8), (byte)(bgr >> 16));
    }

    /// <summary>A brush of the contrast theme's <paramref name="color"/>.</summary>
    public static Microsoft.UI.Xaml.Media.SolidColorBrush Brush(ContrastColor color) => new(Color(color));

    /// <summary>The contrast theme's <paramref name="color"/> while one is on, else the Mac's <paramref name="mac"/>.</summary>
    public static Microsoft.UI.Xaml.Media.SolidColorBrush Pick(ContrastColor color, Microsoft.UI.Xaml.Media.SolidColorBrush mac) => IsOn ? Brush(color) : mac;

    /// <summary>The contrast theme's <paramref name="color"/> while one is on, else the Mac's <paramref name="mac"/>.</summary>
    public static Microsoft.UI.Xaml.Media.Brush Pick(ContrastColor color, Microsoft.UI.Xaml.Media.Brush mac) => IsOn ? Brush(color) : mac;

    /// <summary>The contrast theme's <paramref name="color"/> while one is on, else the Mac's <paramref name="mac"/>.</summary>
    public static WinColor Pick(ContrastColor color, WinColor mac) => IsOn ? Color(color) : mac;

    /// <summary>
    /// In a contrast theme, outlines a selected or active part with Highlight instead of tinting it, so the glyphs and words
    /// on it keep the window text color; does nothing otherwise.
    /// </summary>
    public static void Outline(Microsoft.UI.Xaml.Controls.Border border, bool selected)
    {
        if (IsOn)
        {
            border.BorderBrush = selected ? Brush(ContrastColor.Highlight) : null;
            border.BorderThickness = new Microsoft.UI.Xaml.Thickness(selected ? 2 : 0);
        }
    }

    /// <summary>In a contrast theme, gives a button's surface the theme's button look: ButtonFace inside a ButtonText outline.</summary>
    public static void ButtonLook(Microsoft.UI.Xaml.Controls.Border surface, bool enabled = true)
    {
        if (IsOn)
        {
            surface.Background = Brush(ContrastColor.ButtonFace);
            surface.BorderBrush = Brush(enabled ? ContrastColor.ButtonText : ContrastColor.GrayText);
            surface.BorderThickness = new Microsoft.UI.Xaml.Thickness(1);
        }
    }
}
