using Annotator.App.Pickers;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Annotator.App.Toolbar;

internal sealed partial class ToolbarWindow
{
    /// <summary>
    /// The one way to open the color popover (WIN-UI-02): for the stroke or the fill of the current tool and selection,
    /// on the page <paramref name="request"/> names, placed below <paramref name="anchor"/> (or above it when there is no
    /// room). The quick row's Stroke and Fill buttons and the full properties panel call it with their own button.
    /// </summary>
    public void OpenColorPicker(ColorPickerRequest request, FrameworkElement anchor)
    {
        _interacted();
        RectInt32 rect = Anchor(anchor);
        PointInt32 window = AppWindow.Position;
        if (_model.Snapshot.Preferences.ToolbarOrientation != Annotator.Core.ToolbarOrientation.Vertical)
        {
            // Below or above the whole horizontal toolbar, centered on the button, so the popover never covers the bar;
            // the Mac's arrow puts its popover about that far from the button.
            rect = new RectInt32(rect.X, window.Y, rect.Width, AppWindow.Size.Height);
        }
        _openColorPicker(request, rect);
    }
}
