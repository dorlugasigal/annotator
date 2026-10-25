using Annotator.App.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WinRect = Windows.Foundation.Rect;

namespace Annotator.App.Pickers;

/// <summary>
/// One segment of a Mac segmented picker: a radio button in UI Automation (the Mac's AXRadioButton with value 1 when
/// selected), drawn by its content. Choosing it raises <see cref="Chosen"/>; the owner then sets <see cref="IsChosen"/>.
/// </summary>
internal sealed partial class SegmentButton : Button
{
    private bool _chosen;

    public SegmentButton() => Click += (_, _) => Chosen?.Invoke();

    public event Action? Chosen;

    /// <summary>The group whose selection this segment belongs to, for UI Automation.</summary>
    public FrameworkElement? Group { get; set; }

    public bool IsChosen
    {
        get => _chosen;
        set
        {
            if (_chosen == value)
            {
                return;
            }
            _chosen = value;
            if (FrameworkElementAutomationPeer.FromElement(this) is AutomationPeer peer)
            {
                peer.RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, !value, value);
            }
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed partial class Peer(SegmentButton owner) : ButtonAutomationPeer(owner), ISelectionItemProvider
    {
        public bool IsSelected => owner._chosen;

        public IRawElementProviderSimple? SelectionContainer =>
            owner.Group is { } group && FrameworkElementAutomationPeer.FromElement(group) is { } peer ? ProviderFromPeer(peer) : null;

        public void Select()
        {
            if (owner.IsEnabled && !owner._chosen)
            {
                owner.Chosen?.Invoke();
            }
        }

        public void AddToSelection() => Select();

        public void RemoveFromSelection()
        {
        }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.RadioButton;

        protected override string GetClassNameCore() => "RadioButton";

        // A radio button is chosen through SelectionItem, not Invoke.
        protected override object? GetPatternCore(PatternInterface patternInterface) => patternInterface switch
        {
            PatternInterface.SelectionItem => this,
            PatternInterface.Invoke => null,
            _ => base.GetPatternCore(patternInterface),
        };

        protected override WinRect GetBoundingRectangleCore() => AutomationFrames.Of(owner, base.GetBoundingRectangleCore());
    }
}

/// <summary>A named group of <see cref="SegmentButton"/>s with the Selection pattern: the Mac's AXRadioGroup.</summary>
internal sealed partial class SegmentGroup : Grid
{
    public SegmentGroup(string name) => AutomationProperties.SetName(this, name);

    public List<SegmentButton> Segments { get; } = [];

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed partial class Peer(SegmentGroup owner) : FrameworkElementAutomationPeer(owner), ISelectionProvider
    {
        public bool CanSelectMultiple => false;

        public bool IsSelectionRequired => true;

        public IRawElementProviderSimple[] GetSelection() =>
        [
            .. owner.Segments.Where(segment => segment.IsChosen)
                .Select(segment => FrameworkElementAutomationPeer.FromElement(segment) ?? FrameworkElementAutomationPeer.CreatePeerForElement(segment))
                .Where(peer => peer is not null)
                .Select(peer => ProviderFromPeer(peer)),
        ];

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

        protected override string GetClassNameCore() => "Group";

        protected override bool IsControlElementCore() => true;

        protected override object? GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Selection ? this : base.GetPatternCore(patternInterface);

        protected override WinRect GetBoundingRectangleCore() => AutomationFrames.Of(owner, base.GetBoundingRectangleCore());
    }
}

/// <summary>
/// The Mac's LazyVGrid of 42-point swatch buttons: five 38-point columns 12 points apart, centered in the popover's
/// 252-point content width, each swatch overhanging its column by 2 points on both sides; rows 12 points apart. In UI
/// Automation it is a group with the Grid pattern (the Mac's AXOpaqueProviderGroup), framed around its swatches.
/// </summary>
internal sealed partial class SwatchGrid : Panel
{
    public const double Cell = 42;
    private const double Ring = 2;
    private const double ColumnPitch = 50;
    private const double RowPitch = 54;
    private const double Inset = 5;
    private const double ContentWidth = 252;

    public int Columns { get; init; } = 5;

    public int Rows => (Children.Count + Columns - 1) / Columns;

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in Children)
        {
            child.Measure(new Size(Cell, Cell));
        }
        int rows = Rows;
        double height = rows == 0 ? 0 : rows * Cell + (rows - 1) * (RowPitch - Cell);
        // Whole pixels, rounded down: layout rounding would take three rows (187.5 pixels at 125%) up a pixel and move
        // the text below the grid further from the Mac's place than the swatches are.
        double scale = XamlRoot?.RasterizationScale ?? 1;
        return new Size(ContentWidth, Math.Floor(height * scale + 1e-6) / scale);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (int index = 0; index < Children.Count; index++)
        {
            Children[index].Arrange(new WinRect(Inset + index % Columns * ColumnPitch, index / Columns * RowPitch, Cell, Cell));
        }
        return finalSize;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed partial class Peer(SwatchGrid owner) : FrameworkElementAutomationPeer(owner), IGridProvider
    {
        public int RowCount => owner.Rows;

        public int ColumnCount => Math.Min(owner.Columns, owner.Children.Count);

        public IRawElementProviderSimple? GetItem(int row, int column)
        {
            int index = row * owner.Columns + column;
            return row >= 0 && column >= 0 && column < owner.Columns && index < owner.Children.Count &&
                FrameworkElementAutomationPeer.FromElement(owner.Children[index]) is { } peer ? ProviderFromPeer(peer) : null;
        }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

        protected override string GetClassNameCore() => "Group";

        protected override bool IsControlElementCore() => true;

        protected override object? GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Grid ? this : base.GetPatternCore(patternInterface);

        /// <summary>
        /// The union of the swatches' frames, as the Mac reports its grid: a chosen swatch with its ring, the others without.
        /// The union is taken in the grid's exact points and rounded to pixels once; the union of rounded frames was more
        /// than a point taller than the Mac's at 125%.
        /// </summary>
        protected override WinRect GetBoundingRectangleCore()
        {
            double left = double.PositiveInfinity, top = double.PositiveInfinity, right = double.NegativeInfinity, bottom = double.NegativeInfinity;
            for (int index = 0; index < owner.Children.Count; index++)
            {
                if (owner.Children[index] is not FrameworkElement element || element.Visibility == Visibility.Collapsed)
                {
                    continue;
                }
                // Without its ring, a swatch is 2 points inside its 42-point cell.
                double inset = element is ParityButton { BoundsElement: { IsLoaded: true } } ? Ring : 0;
                double x = Inset + index % owner.Columns * ColumnPitch + inset, y = index / owner.Columns * RowPitch + inset;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x + Cell - inset * 2);
                bottom = Math.Max(bottom, y + Cell - inset * 2);
            }
            WinRect whole = AutomationFrames.Of(owner, base.GetBoundingRectangleCore());
            return right > left && bottom > top ? AutomationFrames.Of(owner, new WinRect(left, top, right - left, bottom - top), whole) : whole;
        }
    }
}

/// <summary>
/// The Mac's DisclosureGroup header ("Shades"): a button that shows or hides the section below it. UI Automation reads it
/// as an expander, a group with the ExpandCollapse pattern (the Mac's AXDisclosureTriangle), framed around its label line.
/// </summary>
internal sealed partial class DisclosureButton : Button
{
    private bool _expanded;

    /// <summary>The label line inside the button, in the button's own coordinates; empty for the whole button.</summary>
    public WinRect LabelBand { get; set; }

    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value)
            {
                return;
            }
            _expanded = value;
            if (FrameworkElementAutomationPeer.FromElement(this) is AutomationPeer peer)
            {
                peer.RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty,
                    value ? ExpandCollapseState.Collapsed : ExpandCollapseState.Expanded, value ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed);
            }
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed partial class Peer(DisclosureButton owner) : ButtonAutomationPeer(owner), IExpandCollapseProvider
    {
        public ExpandCollapseState ExpandCollapseState => owner._expanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;

        public void Expand()
        {
            if (!owner._expanded)
            {
                ((IInvokeProvider)this).Invoke();
            }
        }

        public void Collapse()
        {
            if (owner._expanded)
            {
                ((IInvokeProvider)this).Invoke();
            }
        }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

        protected override string GetClassNameCore() => "Expander";

        protected override object? GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.ExpandCollapse ? this : base.GetPatternCore(patternInterface);

        protected override WinRect GetBoundingRectangleCore() =>
            owner.LabelBand is { Width: > 0, Height: > 0 } band
                ? AutomationFrames.Of(owner, band, base.GetBoundingRectangleCore())
                : AutomationFrames.Of(owner, base.GetBoundingRectangleCore());
    }
}

/// <summary>
/// The Mac's checkbox Toggle, drawn by its content at the Mac's size: a check box in UI Automation with the Toggle
/// pattern (value 1 when on). Clicking it raises <see cref="Toggled"/>; the owner then sets <see cref="IsOn"/>.
/// </summary>
internal sealed partial class ParityCheckBox : Button
{
    private bool _on;

    public ParityCheckBox() => Click += (_, _) => Toggled?.Invoke(!_on);

    public event Action<bool>? Toggled;

    public bool IsOn
    {
        get => _on;
        set
        {
            if (_on == value)
            {
                return;
            }
            _on = value;
            if (FrameworkElementAutomationPeer.FromElement(this) is AutomationPeer peer)
            {
                peer.RaisePropertyChangedEvent(TogglePatternIdentifiers.ToggleStateProperty,
                    value ? ToggleState.Off : ToggleState.On, value ? ToggleState.On : ToggleState.Off);
            }
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed partial class Peer(ParityCheckBox owner) : ButtonAutomationPeer(owner), IToggleProvider
    {
        public ToggleState ToggleState => owner._on ? ToggleState.On : ToggleState.Off;

        public void Toggle()
        {
            if (owner.IsEnabled)
            {
                owner.Toggled?.Invoke(!owner._on);
            }
        }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.CheckBox;

        protected override string GetClassNameCore() => "CheckBox";

        protected override object? GetPatternCore(PatternInterface patternInterface) => patternInterface switch
        {
            PatternInterface.Toggle => this,
            PatternInterface.Invoke => null,
            _ => base.GetPatternCore(patternInterface),
        };

        protected override WinRect GetBoundingRectangleCore() => AutomationFrames.Of(owner, base.GetBoundingRectangleCore());
    }
}

/// <summary>
/// Colors of the popover that App.xaml does not name, from the Mac capture (design-values.json and the images). In a Windows
/// contrast theme each takes the theme's color for its role instead (WIN-UI-07).
/// </summary>
internal static class PickerColors
{
    /// <summary>True when the app shows its dark theme (the window background is dark).</summary>
    public static bool Dark =>
        AppResources.Brush("AnnotatorWindowBackgroundBrush") is SolidColorBrush { Color: var color } && color.R + color.G + color.B < 384;

    /// <summary>SwiftUI's Color.primary (labelColor, 85% black or white) at <paramref name="opacity"/>; the window text color in a contrast theme.</summary>
    public static SolidColorBrush Primary(double opacity) => ContrastTheme.Pick(ContrastColor.WindowText, Overlay(0.85 * opacity));

    /// <summary>Black on the light theme or white on the dark theme at <paramref name="alpha"/>.</summary>
    public static SolidColorBrush Overlay(double alpha)
    {
        byte value = Dark ? (byte)255 : (byte)0;
        return new SolidColorBrush(Windows.UI.Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), value, value, value));
    }

    /// <summary>The disclosure triangle: 25% black on the light theme, white on the dark one (#BFBFBF and #FFFFFF in the capture).</summary>
    public static SolidColorBrush Disclosure => ContrastTheme.Pick(ContrastColor.WindowText,
        new SolidColorBrush(Dark ? Windows.UI.Color.FromArgb(255, 255, 255, 255) : Windows.UI.Color.FromArgb(0x40, 0, 0, 0)));

    /// <summary>The fill of bordered buttons and the segmented picker: 8% black or white (#EBEBEB and #303030 in the capture).</summary>
    public static SolidColorBrush ControlFill => ContrastTheme.Pick(ContrastColor.ButtonFace, Overlay(0x14 / 255.0));

    /// <summary>
    /// The selected segment over the control fill (#CDCDCD and #4A4A4A in the capture); the window color in a contrast theme,
    /// which outlines the segment instead (<see cref="ContrastTheme.Outline"/>).
    /// </summary>
    public static SolidColorBrush SegmentSelected => ContrastTheme.Pick(ContrastColor.Window, Overlay(0x21 / 255.0));

    /// <summary>The hex field's background: Color.primary at 4.5%.</summary>
    public static SolidColorBrush FieldFill => ContrastTheme.Pick(ContrastColor.Window, Overlay(0.85 * 0.045));

    /// <summary>The swatch outline: Color.primary at 20%.</summary>
    public static SolidColorBrush SwatchStroke => Primary(0.2);

    /// <summary>The Mac's TransparencyGrid squares: systemGray at 40% over white.</summary>
    public static Windows.UI.Color GridGray => Dark ? Windows.UI.Color.FromArgb(255, 0xD6, 0xD6, 0xD8) : Windows.UI.Color.FromArgb(255, 0xD2, 0xD2, 0xD4);
}
