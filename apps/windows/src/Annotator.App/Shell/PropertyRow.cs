using Annotator.Workspace.Toolbar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Annotator.App.Shell;

/// <summary>
/// The properties row (Mac ContextualInspector's <c>HStackLayout(alignment: .propertyControl, spacing: 14)</c>) in exact
/// points: items sit left to right, <see cref="Spacing"/> apart, and never wrap. The middles of their controls share one
/// line: a named block or a labelled color well has its control at the bottom (<see cref="SetTitled"/>), a nested row
/// lines up on its own controls, and any other item on its centre (<see cref="PropertyLayout.Row"/>). Positions are
/// rounded to pixels once, as in <see cref="MacStack"/>.
/// </summary>
internal sealed partial class PropertyRow : Panel, IExactSize
{
    /// <summary>The height of a block's control: the 36-point buttons, wells and slider frames.</summary>
    public const double ControlHeight = 36;

    public static readonly DependencyProperty TitledProperty =
        DependencyProperty.RegisterAttached("Titled", typeof(bool), typeof(PropertyRow), new PropertyMetadata(false));

    private Rect[] _frames = [];

    public double Spacing { get; init; } = PropertyLayout.Spacing;

    /// <summary>UI Automation reads the items by rows, titles before controls, as SwiftUI does inside a ScrollView.</summary>
    public bool ReadsByRows { get; set; }

    /// <summary>The middle of the row's controls from its top, after measuring.</summary>
    public double Middle { get; private set; }

    /// <summary>The exact size of the last measure, before WinUI rounds it to pixels.</summary>
    public Size? ExactSize { get; private set; }

    /// <summary>Marks an item whose control is its bottom 36 points, below its name (Mac PropertyBlock and InspectorColorPicker).</summary>
    public static void SetTitled(UIElement element, bool value) => element.SetValue(TitledProperty, value);

    public static bool GetTitled(UIElement element) => (bool)element.GetValue(TitledProperty);

    /// <summary>Where an item's control middle is, from its top.</summary>
    private static double MiddleOf(UIElement child, double height) => child switch
    {
        PropertyRow row => row.Middle,
        Panel { Children: [PropertyRow row] } => row.Middle,
        _ when GetTitled(child) => height - ControlHeight / 2,
        _ => height / 2,
    };

    protected override Size MeasureOverride(Size availableSize)
    {
        var items = new List<(double Width, double Height, double Middle)>(Children.Count);
        foreach (UIElement child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            (double width, double height) = MacStack.Extent(child, horizontal: true);
            items.Add((width, height, MiddleOf(child, height)));
        }
        (Annotator.Core.Rect[] frames, double rowWidth, double rowHeight) = PropertyLayout.Row(items.ToArray(), Spacing);
        _frames = [.. frames.Select(frame => new Rect(frame.X, frame.Y, frame.Width, frame.Height))];
        Middle = 0;
        for (int index = 0; index < items.Count; index++)
        {
            Middle = Math.Max(Middle, items[index].Middle);
        }
        var size = new Size(rowWidth, rowHeight);
        ExactSize = size;
        return size;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

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

    /// <summary>
    /// Not an element itself: UI Automation reads the row's items in order, each block's title before its control, as
    /// SwiftUI reads an HStack. Without it, <see cref="ReadingOrderGrid"/> would sort every title above every control.
    /// Inside a ScrollView SwiftUI reads by rows instead (<see cref="ReadsByRows"/>).
    /// </summary>
    private sealed partial class Peer(PropertyRow owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;

        protected override IList<AutomationPeer> GetChildrenCore() =>
            owner.ReadsByRows ? ReadingOrderGrid.Sort(Flattened(this)) : base.GetChildrenCore();

        /// <summary>The children, with nested rows' items in their place: SwiftUI sorts all of them together.</summary>
        private static AutomationPeerList Flattened(Peer peer)
        {
            var items = new AutomationPeerList();
            foreach (AutomationPeer child in peer.BaseChildren() ?? [])
            {
                if (child is Peer nested)
                {
                    items.AddRange(Flattened(nested));
                }
                else
                {
                    items.Add(child);
                }
            }
            return items;
        }

        private IList<AutomationPeer>? BaseChildren() => base.GetChildrenCore();
    }
}
