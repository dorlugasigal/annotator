using Annotator.App.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Annotator.App.Library;

/// <summary>
/// SwiftUI's HStack with one flexible child, as the Focus section's Spotlight size row has: children with a width keep
/// it, the child without one takes what is left, and each is centered in the row's height. Children are placed in exact
/// points and rounded to pixels once (<see cref="MacStack.ArrangeExact"/>), so a Grid's per-column rounding does not
/// add up along the row at 125%.
/// </summary>
internal sealed partial class FlexibleRow : Panel
{
    public double Spacing { get; init; }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        double flexible = Flexible(width);
        double height = 0;
        foreach (UIElement child in Children)
        {
            child.Measure(new Size(IsFlexible(child) ? flexible : double.PositiveInfinity, availableSize.Height));
            height = Math.Max(height, MacStack.Extent(child, horizontal: true).Across);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double scale = XamlRoot?.RasterizationScale ?? 1;
        Point residual = MacStack.InheritedResidual(this);
        double flexible = Flexible(finalSize.Width);
        double x = 0;
        foreach (UIElement child in Children)
        {
            (double along, double across) = MacStack.Extent(child, horizontal: true);
            double width = IsFlexible(child) ? flexible : along;
            double height = Math.Min(across, finalSize.Height);
            Thickness margin = child is FrameworkElement element ? element.Margin : default;
            MacStack.ArrangeExact(child, x + residual.X, (finalSize.Height - height) / 2 + residual.Y, width, height, margin, scale);
            x += width + Spacing;
        }
        return finalSize;
    }

    private static bool IsFlexible(UIElement child) => child is FrameworkElement { Width: var width } && double.IsNaN(width);

    /// <summary>The width left for the flexible child: the row less the other children and the spacing.</summary>
    private double Flexible(double width)
    {
        double used = Spacing * Math.Max(0, Children.Count - 1);
        foreach (UIElement child in Children)
        {
            if (!IsFlexible(child) && child is FrameworkElement element)
            {
                used += element.Width + element.Margin.Left + element.Margin.Right;
            }
        }
        return Math.Max(0, width - used);
    }
}
