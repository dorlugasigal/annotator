using System.Numerics;
using Annotator.App.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WinRect = Windows.Foundation.Rect;
using WinSize = Windows.Foundation.Size;

namespace Annotator.App.Tutorial;

/// <summary>
/// The Mac's widths for the tutorial cards' buttons (reference/ux-reference/macos/tutorial), so the controls after them keep
/// the Mac's places although Segoe UI's words are narrower or wider (D-4), as Settings does. Check boxes keep their own
/// width: Windows' box and gap are wider than the Mac's, and a Mac width would cut their words.
/// </summary>
internal static class CardWidths
{
    private static readonly Dictionary<string, double> s_widths = new(StringComparer.Ordinal)
    {
        ["Start tutorial"] = 100,
        ["Not now"] = 74.5,
        ["Show me"] = 79.5,
        ["Skip step"] = 80.5,
        ["Open Settings"] = 110.5,
        ["Done"] = 56,
    };

    public static double? Of(string title) => s_widths.TryGetValue(title, out double width) ? width : null;

    /// <summary>
    /// The step count's width ("Step 4 of 10" in the caption font with monospaced digits), so the progress track beside it
    /// keeps the Mac's length.
    /// </summary>
    public static double StepCount(int step) => step >= 10 ? 66.5 : 60.5;

    /// <summary>The widest line of the finish card's "Change any shortcut…" sentence on the Mac, which wraps it.</summary>
    public const double ChangeShortcuts = 245;
}

/// <summary>
/// An element of a card that UI Automation reads as one named custom control without children (AppKit's AXUnknown for a
/// SwiftUI view with <c>accessibilityElement(children: .ignore)</c> or <c>.combine</c>). <see cref="Bounds"/> gives its frame
/// in its own coordinates, such as the union of the parts SwiftUI reports; without it, the frame is the element's layout.
/// </summary>
internal sealed partial class CardElement : Grid
{
    public CardElement(string name) => AutomationProperties.SetName(this, name);

    public Func<WinRect>? Bounds { get; set; }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    /// <summary>
    /// The frame of <paramref name="box"/> in physical pixels of the window's content, rounded like
    /// <see cref="Shell.AutomationFrames"/> but not cut at the window's edges: SwiftUI reports frames that overhang the card.
    /// </summary>
    public static WinRect Frame(FrameworkElement element, WinRect box, WinRect fallback)
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
        double left = Math.Round(dips.Left * scale), top = Math.Round(dips.Top * scale);
        return new WinRect(left, top, Math.Round(dips.Right * scale) - left, Math.Round(dips.Bottom * scale) - top);
    }

    /// <summary>The union of <paramref name="parts"/>' layouts in <paramref name="owner"/>'s coordinates.</summary>
    public static WinRect Union(FrameworkElement owner, IEnumerable<FrameworkElement> parts)
    {
        WinRect? union = null;
        foreach (FrameworkElement part in parts)
        {
            if (part.Visibility != Visibility.Visible || part.ActualWidth <= 0 || part.ActualHeight <= 0)
            {
                continue;
            }
            WinRect box;
            try
            {
                box = part.TransformToVisual(owner).TransformBounds(new WinRect(0, 0, part.ActualWidth, part.ActualHeight));
            }
            catch (ArgumentException)
            {
                continue;
            }
            if (union is { } known)
            {
                known.Union(box);
                union = known;
            }
            else
            {
                union = box;
            }
        }
        return union ?? new WinRect(0, 0, owner.ActualWidth, owner.ActualHeight);
    }

    private sealed partial class Peer(CardElement owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;

        protected override string GetClassNameCore() => "TutorialElement";

        protected override bool IsControlElementCore() => AutomationProperties.GetAccessibilityView(owner) != AccessibilityView.Raw;

        protected override bool IsContentElementCore() => AutomationProperties.GetAccessibilityView(owner) == AccessibilityView.Content;

        protected override IList<AutomationPeer> GetChildrenCore() => new AutomationPeerList();

        protected override WinRect GetBoundingRectangleCore() =>
            Frame(owner, owner.Bounds?.Invoke() ?? new WinRect(0, 0, owner.ActualWidth, owner.ActualHeight), base.GetBoundingRectangleCore());
    }
}

/// <summary>
/// The Mac's TutorialCheckBadge: an accent circle whose white check draws itself, at <c>size</c> points. The circle grows from
/// 60% as the check starts.
/// </summary>
internal sealed class TutorialCheck
{
    private const double CheckLength = 1.6;
    private readonly Ellipse _circle;
    private readonly Polyline _check;
    private readonly double _length;
    private readonly double _size;

    public TutorialCheck(double size)
    {
        _size = size;
        _circle = new Ellipse { Width = size, Height = size, Fill = CardParts.Accent(1) };
        // Mac TutorialCheckShape inside size × 0.26 of padding.
        double inset = size * 0.26, side = size - 2 * inset;
        var points = new Windows.Foundation.Point[]
        {
            new(inset, inset + side * 0.55), new(inset + side * 0.38, inset + side - side * 0.08), new(inset + side, inset + side * 0.1),
        };
        _length = Distance(points[0], points[1]) + Distance(points[1], points[2]);
        _check = new Polyline
        {
            Stroke = new SolidColorBrush(ContrastTheme.Pick(ContrastColor.HighlightText, Microsoft.UI.Colors.White)),
            StrokeThickness = Math.Max(1.5, size * 0.13),
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeDashCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        foreach (Windows.Foundation.Point point in points)
        {
            _check.Points.Add(point);
        }
        Root = new Grid { Width = size, Height = size };
        Root.Children.Add(_circle);
        Root.Children.Add(_check);
        AutomationProperties.SetAccessibilityView(Root, AccessibilityView.Raw);
        Draw(1);
    }

    public Grid Root { get; }

    /// <summary>How much of the check is drawn, from 0 to 1; the circle grows with it.</summary>
    public void Draw(double drawn)
    {
        drawn = Math.Clamp(drawn, 0, 1);
        _circle.CenterPoint = new Vector3((float)(_size / 2), (float)(_size / 2), 0);
        double scale = 0.6 + 0.4 * Math.Min(1, drawn * CheckLength);
        _circle.Scale = new Vector3((float)scale, (float)scale, 1);
        double units = _length / _check.StrokeThickness;
        _check.StrokeDashArray = drawn >= 1 ? null : new DoubleCollection { Math.Max(0.0001, units * drawn), units * 2 };
        _check.Opacity = drawn > 0 ? 1 : 0;
    }

    private static double Distance(Windows.Foundation.Point a, Windows.Foundation.Point b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}

/// <summary>
/// The title's check (Mac TutorialTitleCheck): its 16 points are there from the start, so a finished step never changes the
/// card's size. Screen readers find "Done" only once the step is done.
/// </summary>
internal sealed class TitleCheck
{
    public TitleCheck()
    {
        Root = new CardElement(Annotator.Workspace.FirstUse.TutorialText.Done)
        {
            Width = 16,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
        };
        Root.Children.Add(Check.Root);
        AutomationProperties.SetAccessibilityView(Root, AccessibilityView.Raw);
    }

    public CardElement Root { get; }

    public TutorialCheck Check { get; } = new(16);

    /// <summary>The step is done: the check shows and screen readers find it.</summary>
    public void Reveal()
    {
        Root.Opacity = 1;
        AutomationProperties.SetAccessibilityView(Root, AccessibilityView.Content);
    }
}

/// <summary>A goal's mark (Mac TutorialGoalMark): an empty 14-point circle, then a check that pops in, in a 22-point frame.</summary>
internal sealed class GoalMark
{
    private readonly Ellipse _ring;

    public GoalMark(bool done, bool dark)
    {
        _ring = new Ellipse { Width = 14, Height = 14, Stroke = CardParts.Line(dark, 0.3), StrokeThickness = 1.5 };
        Check = new TutorialCheck(14);
        Root = new Grid { Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center };
        Root.Children.Add(_ring);
        Root.Children.Add(Check.Root);
        AutomationProperties.SetAccessibilityView(Root, AccessibilityView.Raw);
        Show(done);
    }

    public Grid Root { get; }

    public TutorialCheck Check { get; }

    /// <summary>The visible 14-point mark, which starts the goal's frame (SwiftUI reports the circle, not its frame).</summary>
    public FrameworkElement Visible => _ring;

    public void Show(bool done)
    {
        _ring.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
        Check.Root.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>
/// Lays out a line's words and keycaps like text (Mac TutorialLineFlow): left to right, onto a new row when the next word
/// doesn't fit, each centered on its row. Words and keycaps written together stay on one row.
/// </summary>
internal sealed partial class LineFlow : Panel
{
    private readonly List<bool> _spaceBefore = [];

    public double Space { get; init; }

    public double LineSpacing { get; init; } = 2;

    public void Add(UIElement element, bool spaceBefore)
    {
        Children.Add(element);
        _spaceBefore.Add(spaceBefore);
    }

    protected override WinSize MeasureOverride(WinSize availableSize)
    {
        foreach (UIElement child in Children)
        {
            child.Measure(new WinSize(double.PositiveInfinity, double.PositiveInfinity));
        }
        List<Row> rows = Arrange(availableSize.Width);
        return new WinSize(rows.Count == 0 ? 0 : rows.Max(row => row.Width),
                           rows.Sum(row => row.Height) + LineSpacing * Math.Max(0, rows.Count - 1));
    }

    protected override WinSize ArrangeOverride(WinSize finalSize)
    {
        double y = 0;
        foreach (Row row in Arrange(finalSize.Width))
        {
            foreach ((int index, double x) in row.Items)
            {
                UIElement child = Children[index];
                WinSize size = child.DesiredSize;
                child.Arrange(new WinRect(x, y + (row.Height - size.Height) / 2, size.Width, size.Height));
            }
            y += row.Height + LineSpacing;
        }
        return finalSize;
    }

    private List<Row> Arrange(double width)
    {
        double limit = (double.IsFinite(width) ? width : double.PositiveInfinity) + 0.5;
        var chunks = new List<List<int>>();
        for (int index = 0; index < Children.Count; index++)
        {
            if (chunks.Count == 0 || _spaceBefore[index])
            {
                chunks.Add([index]);
            }
            else
            {
                chunks[^1].Add(index);
            }
        }
        var rows = new List<Row>();
        var row = new Row();
        foreach (List<int> chunk in chunks)
        {
            double chunkWidth = chunk.Sum(index => Children[index].DesiredSize.Width);
            if (row.Items.Count > 0 && row.Width + Space + chunkWidth > limit)
            {
                rows.Add(row);
                row = new Row();
            }
            double x = row.Items.Count == 0 ? 0 : row.Width + Space;
            foreach (int index in chunk)
            {
                row.Items.Add((index, x));
                x += Children[index].DesiredSize.Width;
                row.Height = Math.Max(row.Height, Children[index].DesiredSize.Height);
            }
            row.Width = x;
        }
        if (row.Items.Count > 0)
        {
            rows.Add(row);
        }
        return rows;
    }

    private sealed class Row
    {
        public List<(int Index, double X)> Items { get; } = [];

        public double Width { get; set; }

        public double Height { get; set; }
    }
}

/// <summary>
/// A check box at the Mac's toggle size: Windows draws a 20-point box, and negative margins give it the Mac's 16.5 points
/// in the layout; UI Automation reports the Mac's frame too.
/// </summary>
internal sealed partial class CardCheckBox : CheckBox
{
    public const double MacHeight = 16.5;

    public CardCheckBox(string title)
    {
        Content = title;
        FontSize = 13;
        Height = 20;
        Margin = new Thickness(0, -(20 - MacHeight) / 2, 0, -(20 - MacHeight) / 2);
        MinHeight = 0;
        MinWidth = 0;
        Padding = new Thickness(6, 0, 0, 0);
        VerticalAlignment = VerticalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetName(this, title);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed partial class Peer(CardCheckBox owner) : CheckBoxAutomationPeer(owner)
    {
        protected override WinRect GetBoundingRectangleCore() =>
            CardElement.Frame(owner, new WinRect(0, (owner.ActualHeight - MacHeight) / 2, owner.ActualWidth, MacHeight), base.GetBoundingRectangleCore());
    }
}
