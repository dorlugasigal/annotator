using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Annotator.App.Shell;

/// <summary>
/// SwiftUI's HStack and VStack: children one after another with a fixed spacing, laid out in exact points and rounded
/// to pixels once, where each child ends up on screen. StackPanel rounds every child's size and every nested
/// panel's position on its own, so the errors add up along a row and down the nesting: at 125% a 34-point button is
/// 42.5 pixels, and a toolbar row drifted by more than a point. Here a child's exact size wins over its rounded
/// desired size (an explicit Width, a nested stack's exact size, or a minimum size that rounding only just passed),
/// and each stack places its children from its own exact position. Collapsed children take no space or spacing.
/// </summary>
internal sealed partial class MacStack : Panel
{
    /// <summary>For each child a stack arranged: where the child should be, less where it is after pixel rounding.</summary>
    private static readonly ConditionalWeakTable<UIElement, StrongBox<Point>> s_residuals = new();

    private Size? _exact;

    public Orientation Orientation { get; init; } = Orientation.Vertical;

    public double Spacing { get; init; }

    /// <summary>The exact size of the last measure, before WinUI rounds it to pixels (layout diagnostics).</summary>
    public Size? ExactSize => _exact;

    protected override Size MeasureOverride(Size availableSize)
    {
        bool horizontal = Orientation == Orientation.Horizontal;
        Size constraint = horizontal ? new Size(double.PositiveInfinity, availableSize.Height) : new Size(availableSize.Width, double.PositiveInfinity);
        double along = 0, across = 0;
        int count = 0;
        foreach (UIElement child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }
            child.Measure(constraint);
            (double length, double breadth) = Extent(child, horizontal);
            along += length + (count > 0 ? Spacing : 0);
            across = Math.Max(across, breadth);
            count++;
        }
        Size size = horizontal ? new Size(along, across) : new Size(across, along);
        _exact = size;
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        bool horizontal = Orientation == Orientation.Horizontal;
        double scale = XamlRoot?.RasterizationScale ?? 1;
        Point residual = InheritedResidual(this);
        double crossSize = horizontal ? finalSize.Height : finalSize.Width;
        // Spacers share the length the other children leave free (SwiftUI's Spacer).
        double used = 0;
        int count = 0, spacers = 0;
        foreach (UIElement child in Children)
        {
            if (child.Visibility != Visibility.Collapsed)
            {
                used += Extent(child, horizontal).Along + (count++ > 0 ? Spacing : 0);
                spacers += child is MacSpacer ? 1 : 0;
            }
        }
        double share = spacers > 0 ? Math.Max(0, (horizontal ? finalSize.Width : finalSize.Height) - used) / spacers : 0;
        double offset = 0;
        foreach (UIElement child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }
            (double length, double breadth) = Extent(child, horizontal);
            if (child is MacSpacer)
            {
                length += share;
            }
            double start = 0;
            double size = crossSize;
            Thickness margin = default;
            if (child is FrameworkElement element)
            {
                margin = element.Margin;
                bool stretch = horizontal ? element.VerticalAlignment == VerticalAlignment.Stretch : element.HorizontalAlignment == HorizontalAlignment.Stretch;
                bool center = horizontal ? element.VerticalAlignment == VerticalAlignment.Center : element.HorizontalAlignment == HorizontalAlignment.Center;
                bool end = horizontal ? element.VerticalAlignment == VerticalAlignment.Bottom : element.HorizontalAlignment == HorizontalAlignment.Right;
                if (!stretch)
                {
                    size = breadth;
                    start = center ? (crossSize - breadth) / 2 : end ? crossSize - breadth : 0;
                }
            }
            // WinUI rounds the child's position inside this stack to whole pixels. Rounding the exact position, with
            // this stack's own rounding error added back, puts the child on the pixel nearest its exact place.
            double x = (horizontal ? offset : start) + residual.X;
            double y = (horizontal ? start : offset) + residual.Y;
            ArrangeExact(child, x, y, horizontal ? length : size, horizontal ? size : length, margin, scale);
            offset += length + Spacing;
        }
        return finalSize;
    }

    /// <summary>
    /// Arranges <paramref name="child"/> at the exact position (<paramref name="x"/>, <paramref name="y"/>), which
    /// includes its container's own rounding error, on the nearest pixel, and records the child's rounding error for
    /// the stacks inside it.
    /// </summary>
    internal static void ArrangeExact(UIElement child, double x, double y, double width, double height, Thickness margin, double scale)
    {
        double left = Round(x + margin.Left, scale) - margin.Left;
        double top = Round(y + margin.Top, scale) - margin.Top;
        s_residuals.AddOrUpdate(child, new StrongBox<Point>(new Point(x - left, y - top)));
        child.Arrange(new Rect(left, top, width, height));
    }

    private static double Round(double value, double scale) => Math.Round(value * scale, MidpointRounding.AwayFromZero) / scale;

    /// <summary>
    /// The rounding error of <paramref name="panel"/>: the one its parent stack recorded, passed through any containers
    /// in between that put their content at their own origin. Zero on an axis where something in between moves the content.
    /// </summary>
    internal static Point InheritedResidual(UIElement panel)
    {
        bool passX = true, passY = true;
        UIElement current = panel;
        for (int depth = 0; depth < 8 && (passX || passY); depth++)
        {
            if (s_residuals.TryGetValue(current, out StrongBox<Point>? recorded))
            {
                return new Point(passX ? recorded.Value.X : 0, passY ? recorded.Value.Y : 0);
            }
            if (VisualTreeHelper.GetParent(current) is not UIElement parent)
            {
                break;
            }
            if (current is FrameworkElement element)
            {
                passX &= element.Margin.Left == 0 && element.HorizontalAlignment is HorizontalAlignment.Left or HorizontalAlignment.Stretch;
                passY &= element.Margin.Top == 0 && element.VerticalAlignment is VerticalAlignment.Top or VerticalAlignment.Stretch;
            }
            Thickness inset = Inset(parent);
            passX &= inset.Left == 0;
            passY &= inset.Top == 0;
            switch (parent)
            {
                case Canvas:
                    passX &= Canvas.GetLeft(current) == 0;
                    passY &= Canvas.GetTop(current) == 0;
                    break;
                case Grid when current is FrameworkElement cell:
                    passX &= Grid.GetColumn(cell) == 0;
                    passY &= Grid.GetRow(cell) == 0;
                    break;
                case Panel and not MacStack and not PropertyRow:
                    passX = passY = false;
                    break;
            }
            current = parent;
        }
        return default;
    }

    /// <summary>Where a container starts its content: its border and padding.</summary>
    private static Thickness Inset(UIElement container)
    {
        static Thickness Add(Thickness first, Thickness second) =>
            new(first.Left + second.Left, first.Top + second.Top, first.Right + second.Right, first.Bottom + second.Bottom);
        return container switch
        {
            Control control => Add(control.Padding, control.BorderThickness),
            ContentPresenter presenter => Add(presenter.Padding, presenter.BorderThickness),
            Border border => Add(border.Padding, border.BorderThickness),
            Grid grid => Add(grid.Padding, grid.BorderThickness),
            StackPanel panel => Add(panel.Padding, panel.BorderThickness),
            _ => default,
        };
    }

    /// <summary>
    /// A child's exact length along the stack and its breadth across it, margins included. An explicit size wins over
    /// the desired size, then the exact size of a nested stack (directly or as the only content of a container), then
    /// a minimum size that the desired size only exceeds by pixel rounding.
    /// </summary>
    internal static (double Along, double Across) Extent(UIElement child, bool horizontal)
    {
        Size desired = child.DesiredSize;
        if (child is not FrameworkElement element)
        {
            return horizontal ? (desired.Width, desired.Height) : (desired.Height, desired.Width);
        }
        Thickness margin = element.Margin;
        Size? exact = Exact(child);
        double width = !double.IsNaN(element.Width) ? element.Width + margin.Left + margin.Right
            : exact is { } inner ? inner.Width + margin.Left + margin.Right
            : Snapped(desired.Width, element.MinWidth + margin.Left + margin.Right, element);
        double height = !double.IsNaN(element.Height) ? element.Height + margin.Top + margin.Bottom
            : exact is { } box ? box.Height + margin.Top + margin.Bottom
            : Snapped(desired.Height, element.MinHeight + margin.Top + margin.Bottom, element);
        return horizontal ? (width, height) : (height, width);
    }

    /// <summary>
    /// The exact size of a stack, or of a container whose only content is a stack, without its margins: its explicit
    /// size, or its content's exact size within its minimum and maximum. Panels that know their exact size say so
    /// through <see cref="IExactSize"/>.
    /// </summary>
    private static Size? Exact(UIElement element)
    {
        Size size;
        if (element is MacStack { _exact: { } measured })
        {
            size = measured;
        }
        else if (element is IExactSize { ExactSize: { } known })
        {
            size = known;
        }
        else
        {
            UIElement? content = element switch
            {
                ContentControl control => control.Content as UIElement,
                Grid { Children.Count: 1 } grid => grid.Children[0],
                _ => null,
            };
            if (content is null || element is MacStack || !IsZero(Inset(element)) || Exact(content) is not { } inner)
            {
                return null;
            }
            Thickness margin = content is FrameworkElement framework ? framework.Margin : default;
            size = new Size(inner.Width + margin.Left + margin.Right, inner.Height + margin.Top + margin.Bottom);
        }
        if (element is FrameworkElement sized)
        {
            size = new Size(
                !double.IsNaN(sized.Width) ? sized.Width : Math.Max(Math.Min(size.Width, sized.MaxWidth), sized.MinWidth),
                !double.IsNaN(sized.Height) ? sized.Height : Math.Max(Math.Min(size.Height, sized.MaxHeight), sized.MinHeight));
        }
        return size;
    }

    private static bool IsZero(Thickness thickness) =>
        thickness.Left == 0 && thickness.Top == 0 && thickness.Right == 0 && thickness.Bottom == 0;

    private static double Snapped(double desired, double minimum, FrameworkElement element)
    {
        double pixel = 1 / (element.XamlRoot?.RasterizationScale ?? 1);
        return minimum > 0 && desired >= minimum && desired - minimum < pixel ? minimum : desired;
    }
}

/// <summary>SwiftUI's Spacer in a <see cref="MacStack"/>: an empty child that takes the length the others leave free.</summary>
internal sealed partial class MacSpacer : Panel;

/// <summary>
/// An element that knows its exact size from its last measure, before WinUI rounds it to pixels, so the stacks it is in
/// add exact points and do not drift by a fraction of a pixel per row.
/// </summary>
internal interface IExactSize
{
    Size? ExactSize { get; }
}
