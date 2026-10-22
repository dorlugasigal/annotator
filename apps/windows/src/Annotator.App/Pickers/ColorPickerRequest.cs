namespace Annotator.App.Pickers;

/// <summary>The page the color popover opens on.</summary>
internal enum ColorPickerPage
{
    /// <summary>The Mac's rule: Gradient when the paint has a gradient, otherwise Solid.</summary>
    Current,

    /// <summary>The palette, shades, color wheel and hex field.</summary>
    Solid,

    /// <summary>The gradient presets.</summary>
    Gradient,
}

/// <summary>
/// What the color popover (the Mac's InspectorColorPalette) edits: the stroke or the fill paint of the current tool and
/// selection, and the page it opens on. Open it with <c>ToolbarWindow.OpenColorPicker</c>, which places it next to the
/// control that asked; the toolbar's Stroke and Fill buttons and the properties panel both use that one entry point.
/// </summary>
/// <param name="Fill">True for the fill paint, false for the stroke.</param>
internal readonly record struct ColorPickerRequest(bool Fill, ColorPickerPage Page = ColorPickerPage.Current)
{
    /// <summary>The stroke color, on the page its paint calls for.</summary>
    public static ColorPickerRequest Stroke => new(false);

    /// <summary>The fill color, on the page its paint calls for.</summary>
    public static ColorPickerRequest FillColor => new(true);

    /// <summary>The gradient presets of the stroke or the fill.</summary>
    public static ColorPickerRequest Gradient(bool fill) => new(fill, ColorPickerPage.Gradient);
}
