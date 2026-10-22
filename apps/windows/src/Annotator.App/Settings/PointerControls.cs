using Annotator.App.Shell;
using Annotator.App.Toolbar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Glyphs = Annotator.App.Icons.Glyphs;

namespace Annotator.App.Settings;

/// <summary>
/// A Mac pop-up button (SwiftUI's default Picker in a form): the chosen title with the up and down chevrons, and a menu
/// of the titles with the chosen one checked. UI Automation reads it as the Mac's AXPopUpButton: a combo box whose value is
/// the chosen title. A choice raises <see cref="Chosen"/>; the owner then sets <see cref="Index"/>.
/// </summary>
internal sealed partial class PopUpChoice : Button
{
    private readonly string[] _titles;
    private readonly TextBlock _title;
    private int _index;

    public PopUpChoice(string name, string[] titles, double width)
    {
        _titles = titles;
        Style = AppResources.Style("AnnotatorPopUpButton");
        Width = width;
        Height = 24;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Left;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _title = ToolbarParts.Label(titles[0], 13, "AnnotatorPrimaryText");
        _title.VerticalAlignment = VerticalAlignment.Center;
        _title.Margin = new Thickness(12, 0, 0, 0);
        FrameworkElement chevrons = Glyphs.ForSymbol("chevron.up.chevron.down", 16);
        chevrons.HorizontalAlignment = HorizontalAlignment.Right;
        chevrons.VerticalAlignment = VerticalAlignment.Center;
        chevrons.Margin = new Thickness(0, 0, 6, 0);
        var content = new Grid { IsHitTestVisible = false };
        content.Children.Add(_title);
        content.Children.Add(chevrons);
        Content = content;
        AutomationProperties.SetName(this, name);
        Click += (_, _) => Open();
    }

    /// <summary>Raised with the index of the title the person chose.</summary>
    public event Action<int>? Chosen;

    public int Index
    {
        get => _index;
        set
        {
            if (_index == value)
            {
                return;
            }
            string before = _titles[_index];
            _index = value;
            _title.Text = _titles[value];
            if (FrameworkElementAutomationPeer.FromElement(this) is AutomationPeer peer)
            {
                peer.RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, before, _titles[value]);
            }
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private void Open()
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        for (int index = 0; index < _titles.Length; index++)
        {
            int choice = index;
            var item = new ToggleMenuFlyoutItem { Text = _titles[index], IsChecked = index == _index };
            item.Click += (_, _) => Chosen?.Invoke(choice);
            menu.Items.Add(item);
        }
        menu.ShowAt(this);
    }

    private sealed partial class Peer(PopUpChoice owner) : ButtonAutomationPeer(owner), IValueProvider
    {
        public bool IsReadOnly => true;

        public string Value => owner._titles[owner._index];

        public void SetValue(string value) => throw new InvalidOperationException("Choose from the pop-up button's menu.");

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ComboBox;

        protected override string GetClassNameCore() => "ComboBox";

        protected override IList<AutomationPeer> GetChildrenCore() => new AutomationPeerList();

        protected override object? GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Value ? this : base.GetPatternCore(patternInterface);

        protected override Windows.Foundation.Rect GetBoundingRectangleCore() => AutomationFrames.Of(owner, base.GetBoundingRectangleCore());
    }
}

/// <summary>
/// The Mac's LazyVGrid of four flexible columns, 8 points apart, in rows of 49-point tiles: the halo shapes. UI Automation
/// reads it as a group with the Grid pattern (the Mac's AXOpaqueProviderGroup).
/// </summary>
internal sealed partial class HaloShapeGrid : Panel
{
    public const int Columns = 4;
    public const double Spacing = 8;
    public const double TileHeight = 49;

    private int Rows => (Children.Count + Columns - 1) / Columns;

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : Columns * 124.5 + (Columns - 1) * Spacing;
        double column = Math.Max(0, (width - (Columns - 1) * Spacing) / Columns);
        foreach (UIElement child in Children)
        {
            child.Measure(new Windows.Foundation.Size(column, TileHeight));
        }
        return new Windows.Foundation.Size(width, Math.Max(0, Rows * TileHeight + (Rows - 1) * Spacing));
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size finalSize)
    {
        double column = Math.Max(0, (finalSize.Width - (Columns - 1) * Spacing) / Columns);
        double scale = XamlRoot?.RasterizationScale ?? 1;
        for (int index = 0; index < Children.Count; index++)
        {
            MacStack.ArrangeExact(Children[index], index % Columns * (column + Spacing), index / Columns * (TileHeight + Spacing),
                column, TileHeight, default, scale);
        }
        return finalSize;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed partial class Peer(HaloShapeGrid owner) : FrameworkElementAutomationPeer(owner), IGridProvider
    {
        public int RowCount => owner.Rows;

        public int ColumnCount => Math.Min(Columns, owner.Children.Count);

        public IRawElementProviderSimple? GetItem(int row, int column)
        {
            int index = row * Columns + column;
            return index >= 0 && index < owner.Children.Count && owner.Children[index] is FrameworkElement child &&
                   (FrameworkElementAutomationPeer.FromElement(child) ?? FrameworkElementAutomationPeer.CreatePeerForElement(child)) is { } peer
                ? ProviderFromPeer(peer)
                : null;
        }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

        protected override string GetClassNameCore() => "Group";

        protected override bool IsControlElementCore() => true;

        protected override object? GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Grid ? this : base.GetPatternCore(patternInterface);

        protected override Windows.Foundation.Rect GetBoundingRectangleCore() => AutomationFrames.Of(owner, base.GetBoundingRectangleCore());
    }
}
