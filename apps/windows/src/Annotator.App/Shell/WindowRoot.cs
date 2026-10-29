using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Annotator.App.Shell;

/// <summary>
/// The root of a window sized to its content, measured at the content's own size. At 125% WinUI can hand a window's
/// content a pixel less than the window (its height in points is stored as 123.19999 and rounded down), which would
/// cut the last row of pixels off the content.
/// </summary>
internal sealed partial class WindowRoot : Grid
{
    private InputSystemCursor? _move;

    /// <summary>
    /// Shows Windows' move cursor over the whole window while it is dragged (the Mac's closed hand), or gives every element
    /// its own cursor back. Elements that set their own cursor keep it.
    /// </summary>
    public void ShowMoveCursor(bool moving) =>
        ProtectedCursor = moving ? _move ??= InputSystemCursor.Create(InputSystemCursorShape.SizeAll) : null;

    protected override Size MeasureOverride(Size availableSize) =>
        base.MeasureOverride(new Size(double.PositiveInfinity, double.PositiveInfinity));
}
