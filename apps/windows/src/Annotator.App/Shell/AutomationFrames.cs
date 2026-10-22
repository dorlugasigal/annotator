using Microsoft.UI.Xaml;
using WinRect = Windows.Foundation.Rect;

namespace Annotator.App.Shell;

/// <summary>
/// UI Automation frames taken from the layout and rounded to the nearest pixel, for the app's own automation peers
/// (WIN-A11Y-01, WIN-PAR-06). WinUI's frames can be a pixel short at 125%: it truncates positions such as 409.6
/// DIPs that float arithmetic stores as 409.59999, and it cuts an element at the edge of a parent it overhangs,
/// although the element is drawn there. The frame is kept inside the window.
/// </summary>
internal static class AutomationFrames
{
    /// <summary>
    /// Moves a window's content by 1/64 of a point, far less than a pixel, so that WinUI's own frames (text, fields
    /// and menus) land on the pixel the content is drawn on instead of the one before it.
    /// </summary>
    public static void Nudge(UIElement root) =>
        root.RenderTransform = new Microsoft.UI.Xaml.Media.TranslateTransform { X = 1.0 / 64, Y = 1.0 / 64 };

    /// <summary>The frame of <paramref name="element"/>, or <paramref name="fallback"/> before it is laid out.</summary>
    public static WinRect Of(FrameworkElement element, WinRect fallback) =>
        Of(element, new WinRect(0, 0, element.ActualWidth, element.ActualHeight), fallback);

    /// <summary>
    /// The frame of <paramref name="box"/>, given in the element's own coordinates, in the physical pixels of the
    /// window's content (WinUI adds the window's position).
    /// </summary>
    public static WinRect Of(FrameworkElement element, WinRect box, WinRect fallback)
    {
        if (!element.IsLoaded || element.XamlRoot is not { } root)
        {
            return fallback;
        }
        WinRect dips;
        try
        {
            dips = element.TransformToVisual(null).TransformBounds(box);
        }
        catch (ArgumentException)
        {
            return fallback;
        }
        double scale = root.RasterizationScale;
        double width = Math.Round(root.Size.Width * scale), height = Math.Round(root.Size.Height * scale);
        double left = Math.Clamp(Math.Round(dips.Left * scale), 0, width);
        double top = Math.Clamp(Math.Round(dips.Top * scale), 0, height);
        double right = Math.Clamp(Math.Round(dips.Right * scale), left, width);
        double bottom = Math.Clamp(Math.Round(dips.Bottom * scale), top, height);
        return new WinRect(left, top, right - left, bottom - top);
    }
}
