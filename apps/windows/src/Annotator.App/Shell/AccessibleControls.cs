using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using WinRect = Windows.Foundation.Rect;

namespace Annotator.App.Shell;

/// <summary>
/// A button whose UI Automation element carries what the Mac's accessibility element carries (WIN-A11Y-01): an
/// optional read-only value (for example a mode, a color or a shortcut), an optional selected state for tool
/// buttons, and the frame of its visible content where SwiftUI reports that instead of the whole button (plain
/// icon buttons). The click target stays the whole button.
/// </summary>
internal sealed partial class ParityButton : Button
{
    private string? _accessibleValue;
    private bool? _accessibleSelected;

    /// <summary>The Value pattern's value, or null for no Value pattern.</summary>
    public string? AccessibleValue
    {
        get => _accessibleValue;
        set
        {
            if (_accessibleValue == value)
            {
                return;
            }
            string? old = _accessibleValue;
            _accessibleValue = value;
            if (FrameworkElementAutomationPeer.FromElement(this) is AutomationPeer peer && old is not null && value is not null)
            {
                peer.RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, old, value);
            }
        }
    }

    /// <summary>The SelectionItem pattern's state (the Mac's isSelected trait), or null for no SelectionItem pattern.</summary>
    public bool? AccessibleSelected
    {
        get => _accessibleSelected;
        set
        {
            if (_accessibleSelected == value)
            {
                return;
            }
            bool? old = _accessibleSelected;
            _accessibleSelected = value;
            if (FrameworkElementAutomationPeer.FromElement(this) is AutomationPeer peer && old is { } before && value is { } after)
            {
                peer.RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, before, after);
            }
        }
    }

    /// <summary>The visible content whose frame UI Automation reports, or null for the whole button.</summary>
    public FrameworkElement? BoundsElement { get; set; }

    /// <summary>UI Automation reads the button as one element, as SwiftUI reads a button whose label is a picture and words.</summary>
    public bool HidesChildren { get; init; }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed partial class Peer(ParityButton owner) : ButtonAutomationPeer(owner), IValueProvider, ISelectionItemProvider
    {
        public bool IsReadOnly => true;

        public string Value => owner.AccessibleValue ?? string.Empty;

        public bool IsSelected => owner.AccessibleSelected == true;

        public IRawElementProviderSimple? SelectionContainer => null;

        public void SetValue(string value) => throw new InvalidOperationException("The value is read-only.");

        public void AddToSelection() => Select();

        public void RemoveFromSelection()
        {
        }

        public void Select()
        {
            if (owner.IsEnabled && owner.AccessibleSelected == false)
            {
                ((IInvokeProvider)this).Invoke();
            }
        }

        protected override object? GetPatternCore(PatternInterface patternInterface) => patternInterface switch
        {
            PatternInterface.Value when owner.AccessibleValue is not null => this,
            PatternInterface.SelectionItem when owner.AccessibleSelected is not null => this,
            _ => base.GetPatternCore(patternInterface),
        };

        protected override WinRect GetBoundingRectangleCore() =>
            AutomationFrames.Of(owner.BoundsElement is { IsLoaded: true } content ? content : owner, base.GetBoundingRectangleCore());

        protected override IList<AutomationPeer> GetChildrenCore() => owner.HidesChildren ? new AutomationPeerList() : base.GetChildrenCore();
    }
}

/// <summary>A toggle button that reports the frame of its visible content, like <see cref="ParityButton"/>.</summary>
internal sealed partial class ParityToggleButton : ToggleButton
{
    public FrameworkElement? BoundsElement { get; set; }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed partial class Peer(ParityToggleButton owner) : ToggleButtonAutomationPeer(owner)
    {
        protected override WinRect GetBoundingRectangleCore() =>
            AutomationFrames.Of(owner.BoundsElement is { IsLoaded: true } content ? content : owner, base.GetBoundingRectangleCore());
    }
}

/// <summary>
/// Children handed to WinUI from an automation peer. Under Native AOT a list crosses into WinUI only as a partial
/// type, for which CsWinRT generates the interface tables: a plain List fails with InvalidCastException.
/// </summary>
internal sealed partial class AutomationPeerList : List<AutomationPeer>;

/// <summary>A grid that UI Automation reads as a named group (the Mac's accessibilityElement(children: .contain)).</summary>
internal sealed partial class AccessibleGroup : Grid
{
    public AccessibleGroup(string name, string? identifier = null)
    {
        AutomationProperties.SetName(this, name);
        if (identifier is not null)
        {
            AutomationProperties.SetAutomationId(this, identifier);
        }
    }

    /// <summary>
    /// The group's frame is the union of its children's frames, as SwiftUI reports a group that contains its children
    /// (accessibilityElement(children: .contain)), rather than its own layout.
    /// </summary>
    public bool BoundsFromChildren { get; set; }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed partial class Peer(AccessibleGroup owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

        protected override string GetClassNameCore() => "Group";

        protected override bool IsControlElementCore() => true;

        protected override WinRect GetBoundingRectangleCore()
        {
            WinRect own = AutomationFrames.Of(owner, base.GetBoundingRectangleCore());
            if (!owner.BoundsFromChildren || GetChildren() is not { Count: > 0 } children)
            {
                return own;
            }
            double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
            foreach (AutomationPeer child in children)
            {
                WinRect frame = child.GetBoundingRectangle();
                if (frame.Width <= 0 && frame.Height <= 0)
                {
                    continue;
                }
                left = Math.Min(left, frame.Left);
                top = Math.Min(top, frame.Top);
                right = Math.Max(right, frame.Right);
                bottom = Math.Max(bottom, frame.Bottom);
            }
            return left > right ? own : new WinRect(left, top, right - left, bottom - top);
        }
    }
}

/// <summary>
/// A grid that UI Automation reads as one element of its <see cref="ControlType"/> without children, as SwiftUI reads
/// accessibilityElement(children: .ignore) or (children: .combine): Custom for the Mac's AXUnknown, Text for its
/// AXStaticText. Its name, help and item status are the attached AutomationProperties.
/// </summary>
internal sealed partial class AccessibleElement(AutomationControlType type) : Grid
{
    public AutomationControlType ControlType { get; } = type;

    /// <summary>
    /// Points the frame reaches past the layout on every side, where SwiftUI reports drawing that overflows it (the
    /// break countdown's ripple strokes).
    /// </summary>
    public double Overhang { get; set; }

    /// <summary>Reports the frame inside a scrolled Settings page without cutting it at the window's edges.</summary>
    public bool InSettings { get; init; }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed partial class Peer(AccessibleElement owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => owner.ControlType;

        protected override string GetClassNameCore() => owner.ControlType == AutomationControlType.Text ? "TextBlock" : "Grid";

        protected override bool IsControlElementCore() => true;

        protected override IList<AutomationPeer> GetChildrenCore() => new AutomationPeerList();

        protected override WinRect GetBoundingRectangleCore()
        {
            double overhang = owner.Overhang;
            var box = new WinRect(-overhang, -overhang, owner.ActualWidth + 2 * overhang, owner.ActualHeight + 2 * overhang);
            return owner.InSettings
                ? Annotator.App.Settings.SettingsFrames.Of(owner, box, base.GetBoundingRectangleCore())
                : AutomationFrames.Of(owner, box, base.GetBoundingRectangleCore());
        }
    }
}

/// <summary>A grid that UI Automation reads as a named image, such as the shared-style link mark.</summary>
internal sealed partial class AccessibleImage : Grid
{
    public AccessibleImage(string name, string? identifier, string? help)
    {
        AutomationProperties.SetName(this, name);
        if (identifier is not null)
        {
            AutomationProperties.SetAutomationId(this, identifier);
        }
        if (help is not null)
        {
            AutomationProperties.SetHelpText(this, help);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed partial class Peer(AccessibleImage owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

        protected override string GetClassNameCore() => "Image";

        protected override bool IsControlElementCore() => true;

        protected override WinRect GetBoundingRectangleCore() => AutomationFrames.Of(owner, base.GetBoundingRectangleCore());

        protected override IList<AutomationPeer> GetChildrenCore() => new AutomationPeerList();
    }
}

/// <summary>
/// A slider whose knob is a UI Automation child (a Thumb), like the Mac slider's AXValueIndicator; WinUI's slider
/// exposes no children.
/// </summary>
internal sealed partial class ParitySlider : Slider
{
    private FrameworkElement? _knob;

    /// <summary>The height of the band around the track that UI Automation reports, or 0 for the whole slider.</summary>
    public double Band { get; init; }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        // As a FrameworkElement: under Native AOT the template part comes back as a Control, not a Thumb.
        _knob = GetTemplateChild("HorizontalThumb") as FrameworkElement;
        if (_knob is { } knob)
        {
            knob.Loaded += (_, _) => FitKnob(knob);
        }
    }

    /// <summary>
    /// The Windows thumb draws its ring 2 points outside its box. Drawn inside it, the knob takes the Mac knob's
    /// 20 by 16 points on screen and in UI Automation, and never reaches past the ends of the track.
    /// </summary>
    private static void FitKnob(FrameworkElement knob)
    {
        if (VisualTreeHelper.GetChildrenCount(knob) > 0 && VisualTreeHelper.GetChild(knob, 0) is Border ring)
        {
            ring.Margin = new Thickness(0);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed partial class Peer(ParitySlider owner) : SliderAutomationPeer(owner)
    {
        private KnobPeer? _knob;

        private KnobPeer? Knob => owner._knob is { } knob ? _knob ??= new KnobPeer(knob) : null;

        protected override IList<AutomationPeer> GetChildrenCore() => Knob is { } knob ? new AutomationPeerList { knob } : new AutomationPeerList();

        /// <summary>The band around the track that the slider takes in the layout (<see cref="Band"/>).</summary>
        protected override WinRect GetBoundingRectangleCore()
        {
            double band = owner.Band > 0 ? Math.Min(owner.Band, owner.ActualHeight) : owner.ActualHeight;
            return AutomationFrames.Of(owner, new WinRect(0, (owner.ActualHeight - band) / 2, owner.ActualWidth, band), base.GetBoundingRectangleCore());
        }

        protected override object? NavigateCore(AutomationNavigationDirection direction) =>
            direction is AutomationNavigationDirection.FirstChild or AutomationNavigationDirection.LastChild ? Knob : base.NavigateCore(direction);
    }

    /// <summary>The knob as a control element: WinUI's own thumb peer is not one, so screen readers skip it.</summary>
    private sealed partial class KnobPeer(FrameworkElement knob) : FrameworkElementAutomationPeer(knob)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Thumb;

        protected override string GetClassNameCore() => "Thumb";

        protected override string GetAutomationIdCore() => string.Empty;

        protected override bool IsControlElementCore() => true;

        protected override bool IsContentElementCore() => false;

        protected override IList<AutomationPeer> GetChildrenCore() => new AutomationPeerList();

        protected override WinRect GetBoundingRectangleCore() =>
            Owner is FrameworkElement element ? AutomationFrames.Of(element, base.GetBoundingRectangleCore()) : base.GetBoundingRectangleCore();
    }
}