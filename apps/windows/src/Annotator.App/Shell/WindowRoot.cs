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
    private MoveShield? _shield;

    /// <summary>
    /// Shows Windows' move cursor over the whole window while it is dragged (the Mac's closed hand), or gives every element
    /// its own cursor back. WinUI takes the cursor from the element under the pointer, not from the one that captured it, so
    /// a transparent shield above the controls carries it while the window moves.
    /// </summary>
    public void ShowMoveCursor(bool moving)
    {
        ProtectedCursor = moving ? _move ??= InputSystemCursor.Create(InputSystemCursorShape.SizeAll) : null;
        if (moving)
        {
            _shield ??= new MoveShield(_move!);
            if (!Children.Contains(_shield))
            {
                Children.Add(_shield);
            }
        }
        else if (_shield is not null)
        {
            Children.Remove(_shield);
        }
    }

    protected override Size MeasureOverride(Size availableSize) =>
        base.MeasureOverride(new Size(double.PositiveInfinity, double.PositiveInfinity));

    /// <summary>An empty, transparent layer over the whole window that shows the move cursor; screen readers skip it.</summary>
    private sealed partial class MoveShield : Grid
    {
        public MoveShield(InputSystemCursor cursor)
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            ProtectedCursor = cursor;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        }

        protected override Size MeasureOverride(Size availableSize) => default;
    }
}
