using Annotator.App.Shell;
using Annotator.Workspace.Library;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using WinRect = Windows.Foundation.Rect;

namespace Annotator.App.Library;

/// <summary>
/// The Tools window's tile grid (Mac LazyVGrid with adaptive columns from 68 to 96 points, 8 points apart, in rows of
/// 76-point tiles): as many columns as fit its scroll area less the trailing padding, and less the scroller when the rows
/// are taller than the area. UI Automation reads it as a group with the Grid pattern (the Mac's AXOpaqueProviderGroup),
/// framed around its rows.
/// </summary>
internal sealed partial class TileGrid : Panel
{
    private double _viewportWidth = ToolLibraryLayout.ViewportWidth(ToolLibraryLayout.DefaultWidth);
    private double _viewportHeight = 160;

    /// <summary>The scroll area's size in points, which decides the columns and whether the grid scrolls.</summary>
    public void SetViewport(double width, double height)
    {
        if (width == _viewportWidth && height == _viewportHeight)
        {
            return;
        }
        _viewportWidth = width;
        _viewportHeight = height;
        InvalidateMeasure();
    }

    /// <summary>The grid for the current tiles and scroll area.</summary>
    public GridPlan Plan => ToolLibraryLayout.Grid(Children.Count, _viewportWidth, _viewportHeight);

    protected override Size MeasureOverride(Size availableSize)
    {
        GridPlan plan = Plan;
        foreach (UIElement child in Children)
        {
            child.Measure(new Size(plan.Columns.Width, ToolLibraryLayout.TileHeight));
        }
        return new Size(plan.GridWidth, plan.ContentHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        GridPlan plan = Plan;
        double scale = XamlRoot?.RasterizationScale ?? 1;
        for (int index = 0; index < Children.Count; index++)
        {
            int row = index / plan.Columns.Count, column = index % plan.Columns.Count;
            MacStack.ArrangeExact(Children[index], plan.Columns.X(column), row * (ToolLibraryLayout.TileHeight + ToolLibraryLayout.TileSpacing),
                plan.Columns.Width, ToolLibraryLayout.TileHeight, default, scale);
        }
        return finalSize;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed partial class Peer(TileGrid owner) : FrameworkElementAutomationPeer(owner), IGridProvider
    {
        public int RowCount => owner.Plan.Rows;

        public int ColumnCount => Math.Min(owner.Plan.Columns.Count, owner.Children.Count);

        public IRawElementProviderSimple? GetItem(int row, int column)
        {
            int columns = owner.Plan.Columns.Count;
            int index = row * columns + column;
            if (row < 0 || column < 0 || column >= columns || index >= owner.Children.Count)
            {
                return null;
            }
            // A tile with an explanation sits in a slot that shows the tooltip; its button is the grid item.
            UIElement child = owner.Children[index] is Border { Child: UIElement button } ? button : owner.Children[index];
            return FrameworkElementAutomationPeer.FromElement(child) is { } peer ? ProviderFromPeer(peer) : null;
        }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

        protected override string GetClassNameCore() => "Group";

        protected override bool IsControlElementCore() => true;

        protected override object? GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Grid ? this : base.GetPatternCore(patternInterface);

        protected override WinRect GetBoundingRectangleCore() => AutomationFrames.Of(owner, base.GetBoundingRectangleCore());
    }
}
