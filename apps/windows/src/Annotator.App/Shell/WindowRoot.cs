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
    protected override Size MeasureOverride(Size availableSize) =>
        base.MeasureOverride(new Size(double.PositiveInfinity, double.PositiveInfinity));
}
