using Annotator.Workspace.Toolbar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Annotator.App.Shell;

/// <summary>
/// SwiftUI's AdaptivePropertyLayout (AdaptivePropertyLayout.swift) in exact points: items flow left to right,
/// <see cref="Spacing"/> apart, and wrap to a new row when the next would pass <see cref="FrameworkElement.Width"/>.
/// Items keep their own sizes and the top of their row. Positions are rounded to pixels once, as in <see cref="MacStack"/>.
/// </summary>
internal sealed partial class MacFlow : Panel
{
    private Rect[] _frames = [];

    public double Spacing { get; init; } = PropertyLayout.Spacing;

    /// <summary>
    /// Without a <see cref="FrameworkElement.Width"/>, a flow inside another flow is as wide as its items in one row, up to
    /// this width (SwiftUI proposes min(width, ideal) to a nested AdaptivePropertyLayout).
    /// </summary>
    public double Limit { get; init; } = double.PositiveInfinity;

    /// <summary>The exact size of the last measure, before WinUI rounds it to pixels.</summary>
    public Size? ExactSize { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var sizes = new List<(double Width, double Height)>(Children.Count);
        foreach (UIElement child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            (double along, double across) = MacStack.Extent(child, horizontal: true);
            sizes.Add((along, across));
        }
        double flow = !double.IsNaN(Width) ? Width
            : double.IsFinite(availableSize.Width) ? availableSize.Width
            : Math.Min(Limit, sizes.Sum(size => size.Width) + Math.Max(0, sizes.Count - 1) * Spacing);
        Annotator.Core.Rect[] frames = PropertyLayout.Frames(sizes.ToArray(), Math.Max(1, flow), Spacing);
        _frames = [.. frames.Select(frame => new Rect(frame.X, frame.Y, frame.Width, frame.Height))];
        double height = 0;
        foreach (Rect frame in _frames)
        {
            height = Math.Max(height, frame.Y + frame.Height);
        }
        var size = new Size(flow, height);
        ExactSize = size;
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double scale = XamlRoot?.RasterizationScale ?? 1;
        Point residual = MacStack.InheritedResidual(this);
        int index = 0;
        foreach (UIElement child in Children)
        {
            if (child.Visibility == Visibility.Collapsed || index >= _frames.Length)
            {
                continue;
            }
            Rect frame = _frames[index++];
            Thickness margin = child is FrameworkElement element ? element.Margin : default;
            MacStack.ArrangeExact(child, frame.X + residual.X, frame.Y + residual.Y, frame.Width, frame.Height, margin, scale);
        }
        return finalSize;
    }
}
