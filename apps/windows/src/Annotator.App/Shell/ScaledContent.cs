using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Annotator.App.Shell;

/// <summary>
/// SwiftUI's scaleEffect in a layout that reports the scaled size (Mac ScaledPropertiesLayout): the child is laid out
/// <c>1 / Factor</c> times wider than the room it gets and drawn <see cref="Factor"/> times smaller from the top left. Input
/// and UI Automation follow the drawn size. Unlike Viewbox, the scale is exact, whatever the child's desired size.
/// </summary>
internal sealed partial class ScaledContent : Panel
{
    public double Factor { get; init; } = 1;

    public UIElement? Child
    {
        get => Children.Count > 0 ? Children[0] : null;
        init
        {
            Children.Clear();
            if (value is not null)
            {
                Children.Add(value);
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child)
        {
            return default;
        }
        if (child.RenderTransform is not ScaleTransform)
        {
            child.RenderTransform = new ScaleTransform { ScaleX = Factor, ScaleY = Factor };
        }
        child.Measure(new Size(availableSize.Width / Factor, availableSize.Height / Factor));
        return new Size(child.DesiredSize.Width * Factor, child.DesiredSize.Height * Factor);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is { } child)
        {
            child.Arrange(new Rect(0, 0, finalSize.Width / Factor, child.DesiredSize.Height));
        }
        return finalSize;
    }
}
