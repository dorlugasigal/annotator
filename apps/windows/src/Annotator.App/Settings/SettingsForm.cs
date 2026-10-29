using Annotator.App.Shell;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Annotator.App.Settings;

/// <summary>
/// One row of a grouped Form section (SwiftUI's .formStyle(.grouped) on macOS), in exact points: 10 points of padding above
/// and below the content, at least 36 points high, the leading content 10 points from the card's edge and the trailing
/// control 10 points from the other edge (a switch 9). A toggle row's content counts 16.5 points and a pop-up's 17.25, as
/// SwiftUI lays them out; the taller control is centered on that line, as in the Mac capture.
/// </summary>
internal sealed partial class FormRow : Panel, IExactSize
{
    public const double Padding = 10;
    public const double MinimumHeight = 36;
    private Size? _exact;

    public FormRow(FrameworkElement leading, FrameworkElement? trailing = null, double? line = null, double trailingInset = Padding)
    {
        Leading = leading;
        Trailing = trailing;
        Line = line;
        TrailingInset = trailingInset;
        Children.Add(leading);
        if (trailing is not null)
        {
            Children.Add(trailing);
        }
    }

    public FrameworkElement Leading { get; }

    public FrameworkElement? Trailing { get; }

    /// <summary>The content's layout height when it differs from the controls' (16.5 for a toggle, 17.25 for a pop-up).</summary>
    public double? Line { get; }

    public double TrailingInset { get; }

    /// <summary>Puts the leading content at the top of the content area instead of centering it (multi-line content).</summary>
    public bool TopAligned { get; init; }

    /// <summary>
    /// The row also keeps 10 points below a trailing control that is taller than its <see cref="Line"/>, as SwiftUI grows
    /// a Form row around a stepper, segmented picker or swatches.
    /// </summary>
    public bool FitsTrailing { get; init; }

    public Size? ExactSize => _exact;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 704 : availableSize.Width;
        double trailingWidth = 0;
        if (Trailing is not null)
        {
            Trailing.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            trailingWidth = WidthOf(Trailing) + 8;
        }
        Leading.Measure(new Size(Math.Max(0, width - Padding - TrailingInset - trailingWidth), double.PositiveInfinity));
        double content = Line ?? Math.Max(HeightOf(Leading), Trailing is null ? 0 : HeightOf(Trailing));
        double height = Math.Max(MinimumHeight, content + 2 * Padding);
        if (FitsTrailing && Trailing is not null && Line is { } line)
        {
            double trailingHeight = HeightOf(Trailing);
            height = Math.Max(height, Padding + (line - trailingHeight) / 2 + trailingHeight + Padding);
        }
        var size = new Size(width, height);
        _exact = size;
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double scale = XamlRoot?.RasterizationScale ?? 1;
        Point residual = MacStack.InheritedResidual(this);
        double height = finalSize.Height;
        double leadingHeight = HeightOf(Leading);
        // A toggle's or pop-up's words sit at the top of their line, and the control is centered on that line, as in the
        // Mac capture; other content is centered in the row.
        double leadingTop = TopAligned || Line is not null ? Padding : (height - leadingHeight) / 2;
        MacStack.ArrangeExact(Leading, residual.X + Padding, residual.Y + leadingTop,
            Math.Max(0, finalSize.Width - Padding - TrailingInset), leadingHeight, default, scale);
        if (Trailing is not null)
        {
            double trailingHeight = HeightOf(Trailing);
            double trailingWidth = WidthOf(Trailing);
            double trailingTop = Line is { } line ? Padding + (line - trailingHeight) / 2 : (height - trailingHeight) / 2;
            MacStack.ArrangeExact(Trailing, residual.X + finalSize.Width - TrailingInset - trailingWidth,
                residual.Y + trailingTop, trailingWidth, trailingHeight, default, scale);
        }
        return finalSize;
    }

    private static double WidthOf(FrameworkElement element) => !double.IsNaN(element.Width) ? element.Width : element.DesiredSize.Width;

    private static double HeightOf(FrameworkElement element) => !double.IsNaN(element.Height) ? element.Height : MacStack.Extent(element, horizontal: false).Along;
}

/// <summary>
/// SwiftUI's padding around one child, in exact points. WinUI rounds an element's slot and its margin to pixels
/// separately, so a 10-point margin at 125% (12.5 pixels) moved content by half a pixel more on every level; here the
/// child is placed at its exact offset and rounded once, like a stack's children.
/// </summary>
internal sealed partial class Pad : Panel, IExactSize
{
    private Size? _exact;

    public Pad(FrameworkElement child, Thickness padding)
    {
        Child = child;
        Padding = padding;
        Children.Add(child);
    }

    public FrameworkElement Child { get; }

    public Thickness Padding { get; }

    public Size? ExactSize => _exact;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = Math.Max(0, availableSize.Width - Padding.Left - Padding.Right);
        double height = Math.Max(0, availableSize.Height - Padding.Top - Padding.Bottom);
        Child.Measure(new Size(width, height));
        (double along, double across) = MacStack.Extent(Child, horizontal: false);
        var size = new Size(across + Padding.Left + Padding.Right, along + Padding.Top + Padding.Bottom);
        _exact = size;
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double scale = XamlRoot?.RasterizationScale ?? 1;
        Point residual = MacStack.InheritedResidual(this);
        (double along, double across) = MacStack.Extent(Child, horizontal: false);
        double width = Child.HorizontalAlignment == HorizontalAlignment.Stretch ? Math.Max(0, finalSize.Width - Padding.Left - Padding.Right) : across;
        MacStack.ArrangeExact(Child, residual.X + Padding.Left, residual.Y + Padding.Top, width, along, Child.Margin, scale);
        return finalSize;
    }
}

/// <summary>
/// A grouped Form's readable width: SwiftUI keeps the cards at most <see cref="SettingsForm.CardWidth"/> points wide and
/// centers them when the window is wider, as the 1420-point Settings window is.
/// </summary>
internal sealed partial class FormColumn : Panel, IExactSize
{
    private Size? _exact;

    public FormColumn(FrameworkElement child)
    {
        Child = child;
        Children.Add(child);
    }

    public FrameworkElement Child { get; }

    public Size? ExactSize => _exact;

    private static double ColumnWidth(double available) =>
        double.IsInfinity(available) ? SettingsForm.CardWidth : Math.Min(available, SettingsForm.CardWidth);

    protected override Size MeasureOverride(Size availableSize)
    {
        double column = ColumnWidth(availableSize.Width);
        Child.Measure(new Size(column, availableSize.Height));
        double height = MacStack.Extent(Child, horizontal: false).Along;
        var size = new Size(double.IsInfinity(availableSize.Width) ? column : availableSize.Width, height);
        _exact = size;
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double scale = XamlRoot?.RasterizationScale ?? 1;
        Point residual = MacStack.InheritedResidual(this);
        double column = ColumnWidth(finalSize.Width);
        double height = MacStack.Extent(Child, horizontal: false).Along;
        MacStack.ArrangeExact(Child, residual.X + (finalSize.Width - column) / 2, residual.Y, column, height, Child.Margin, scale);
        return finalSize;
    }
}

/// <summary>Builds the grouped Form of Settings > General: section headers, rounded cards, rows and separators.</summary>
internal static class SettingsForm
{
    /// <summary>The Form's inset from the scroll area's edges and its first header's distance from the top.</summary>
    public const double Inset = 45;

    public const double CardRadius = 10;

    /// <summary>The widest a card gets; a wider window centers it (<see cref="FormColumn"/>).</summary>
    public const double CardWidth = 704;

    /// <summary>The width text gets in a row of the default 704-point card: the card less 10 points on each side.</summary>
    public const double RowWidth = 684;

    /// <summary>A section header above its card: semibold 13-point text, 10 points from the card's edge and 10 above it.</summary>
    public static FrameworkElement Header(string title, bool first) =>
        new Pad(Heading(title), new Thickness(FormRow.Padding, first ? 0 : 30, 0, 10)) { HorizontalAlignment = HorizontalAlignment.Left };

    /// <summary>A section heading: semibold 13-point text, read as a heading (a nested Section's title inside its card).</summary>
    public static FormText Heading(string title) => new(title, 13, weight: FontWeights.SemiBold, heading: true);

    /// <summary>A rounded card with its rows, separated by 1-point lines inset 10 points on both sides.</summary>
    /// <remarks>A Grid rather than a Border, so the stacks around it read the exact height of its rows.</remarks>
    public static FrameworkElement Card(IEnumerable<FrameworkElement> rows, double top = 0)
    {
        var stack = new MacStack();
        bool first = true;
        foreach (FrameworkElement row in rows)
        {
            if (!first)
            {
                stack.Children.Add(new Pad(new Rectangle { Height = 1, Fill = SettingsLook.RowSeparator, HorizontalAlignment = HorizontalAlignment.Stretch },
                    new Thickness(FormRow.Padding, 0, FormRow.Padding, 0)));
            }
            stack.Children.Add(row);
            first = false;
        }
        var card = new Grid
        {
            Background = SettingsLook.Panel,
            CornerRadius = new CornerRadius(CardRadius),
        };
        card.Children.Add(stack);
        return top == 0 ? card : new Pad(card, new Thickness(0, top, 0, 0));
    }

    /// <summary>A row with text on the left and a switch on the right (the Mac's Toggle in a Form).</summary>
    public static FormRow Toggle(string title, SettingsSwitch control, bool enabled = true)
    {
        FormText label = Body(title, enabled);
        return new FormRow(label, control, line: 16.5, trailingInset: 9);
    }

    /// <summary>A row with text on the left and a pop-up on the right (the Mac's Picker in a Form).</summary>
    /// <remarks>The Mac's pop-up rows measure 37 and 37.5 points in the capture; 17.25 keeps both within a quarter point.</remarks>
    public static FormRow PopUp(string title, SettingsPopUp control) => new(Body(title), control, line: 17.25);

    /// <summary>A row with one control or text.</summary>
    public static FormRow Single(FrameworkElement content, bool topAligned = false) => new(content) { TopAligned = topAligned };

    /// <summary>Body text (13 points) in the label color, or the disabled color.</summary>
    public static FormText Body(string text, bool enabled = true) => new(text, 13, enabled ? "AnnotatorPrimaryText" : "AnnotatorDisabledText");

    /// <summary>Caption text (10 points) in the secondary color; it wraps at the row's width when <paramref name="wrapWidth"/> is set.</summary>
    public static FormText Caption(string text, double? wrapWidth = null) => new(text, 10, "AnnotatorSecondaryText", wrapWidth: wrapWidth);

    /// <summary>
    /// The scroll area of a Form or a page: vertical scrolling only, with the scroll bar Windows shows on demand. The
    /// content sits in a stack with its padding, so it lands on the pixels nearest the Mac's points.
    /// </summary>
    public static ScrollViewer Scroll(FrameworkElement content, Thickness padding = default)
    {
        var host = new MacStack { HorizontalAlignment = HorizontalAlignment.Stretch };
        host.Children.Add(padding == default ? content : new Pad(content, padding) { HorizontalAlignment = HorizontalAlignment.Stretch });
        return new ScrollViewer
        {
            Content = host,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ZoomMode = ZoomMode.Disabled,
            IsTabStop = false,
        };
    }
}
