using Annotator.App.Pickers;
using Annotator.App.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using WinRect = Windows.Foundation.Rect;

namespace Annotator.App.Settings;

/// <summary>
/// The Mac's switch Toggle in a Form row (38 by 18 points): a check box in UI Automation with the Toggle pattern (the
/// Mac's AXCheckBox, subrole AXSwitch, value 1 when on), which Narrator calls a toggle switch. Clicking or Space raises
/// <see cref="Toggled"/>; the owner then sets <see cref="IsOn"/> from the workspace.
/// </summary>
internal sealed partial class SettingsSwitch : Button
{
    public const double TrackWidth = 38;
    public const double TrackHeight = 18;
    private readonly Border _track = new() { Width = TrackWidth, Height = TrackHeight, CornerRadius = new CornerRadius(TrackHeight / 2) };
    private readonly Border _knob = new() { Width = 22, Height = 14, CornerRadius = new CornerRadius(7), HorizontalAlignment = HorizontalAlignment.Left };
    private bool _on;

    public SettingsSwitch(string name)
    {
        Style = AppResources.Style("AnnotatorPlainButton");
        Width = TrackWidth;
        Height = TrackHeight;
        CornerRadius = new CornerRadius(TrackHeight / 2);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        _knob.VerticalAlignment = VerticalAlignment.Center;
        _track.BorderThickness = new Thickness(0.5);
        var host = new Grid { IsHitTestVisible = false };
        host.Children.Add(_track);
        host.Children.Add(_knob);
        Content = host;
        AutomationProperties.SetName(this, name);
        Click += (_, _) => Toggled?.Invoke(!_on);
        IsEnabledChanged += (_, _) => Show();
        Loaded += (_, _) => Show();
        Show();
    }

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
            Show();
            if (FrameworkElementAutomationPeer.FromElement(this) is AutomationPeer peer)
            {
                peer.RaisePropertyChangedEvent(TogglePatternIdentifiers.ToggleStateProperty,
                    value ? ToggleState.Off : ToggleState.On, value ? ToggleState.On : ToggleState.Off);
            }
        }
    }

    /// <summary>
    /// On: the accent track with the knob at the right. Off: a gray track with the knob at the left. Disabled dims both. A
    /// contrast theme draws the track in the window color with a window-text outline and knob, filled with Highlight while on.
    /// </summary>
    private void Show()
    {
        bool dark = SettingsLook.Dark;
        if (ContrastTheme.IsOn)
        {
            ContrastColor line = IsEnabled ? ContrastColor.WindowText : ContrastColor.GrayText;
            _track.Background = ContrastTheme.Brush(_on && IsEnabled ? ContrastColor.Highlight : ContrastColor.Window);
            _track.BorderBrush = ContrastTheme.Brush(_on && IsEnabled ? ContrastColor.Highlight : line);
            _track.BorderThickness = new Thickness(1);
            _knob.Background = ContrastTheme.Brush(_on && IsEnabled ? ContrastColor.HighlightText : line);
            _knob.BorderBrush = null;
            _knob.Margin = new Thickness(_on ? TrackWidth - 22 - 2 : 2, 0, 0, 0);
            Opacity = 1;
            return;
        }
        _track.Background = _on ? AppResources.Brush("AnnotatorAccentBrush") : PickerColors.Overlay(dark ? 0.14 : 0.09);
        _track.BorderBrush = PickerColors.Overlay(dark ? 0.08 : 0.06);
        _track.BorderThickness = new Thickness(0.5);
        _knob.Background = new SolidColorBrush(Microsoft.UI.Colors.White);
        _knob.BorderBrush = PickerColors.Overlay(0.12);
        _knob.BorderThickness = new Thickness(0.5);
        _knob.Margin = new Thickness(_on ? TrackWidth - 22 - 2 : 2, 0, 0, 0);
        Opacity = IsEnabled ? 1 : 0.45;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed partial class Peer(SettingsSwitch owner) : ButtonAutomationPeer(owner), IToggleProvider
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

        protected override string GetClassNameCore() => "ToggleSwitch";

        protected override string GetLocalizedControlTypeCore() => "toggle switch";

        protected override IList<AutomationPeer> GetChildrenCore() => new AutomationPeerList();

        protected override object? GetPatternCore(PatternInterface patternInterface) => patternInterface switch
        {
            PatternInterface.Toggle => this,
            PatternInterface.Invoke => null,
            _ => base.GetPatternCore(patternInterface),
        };

        protected override WinRect GetBoundingRectangleCore() => SettingsFrames.Of(owner, base.GetBoundingRectangleCore());
    }
}

/// <summary>
/// The Mac's Form Picker (a pop-up button): the chosen title and a round chevron badge, 24 points high and as wide as the
/// title needs. UI Automation reads the Mac's AXPopUpButton: a combo box whose value is the chosen title. Its menu checks
/// the chosen item; a choice raises <see cref="Chosen"/>.
/// </summary>
internal sealed partial class SettingsPopUp : Button
{
    /// <summary>The Mac's pop-up widths for the titles of the reference states; others use the title's width.</summary>
    private static readonly Dictionary<string, double> s_macWidths = new(StringComparer.Ordinal) { ["System"] = 92.5, ["Gather"] = 89 };
    private const double Chrome = 48.5;
    private readonly IReadOnlyList<string> _titles;
    private readonly TextBlock _title;
    private int _chosen;

    public SettingsPopUp(string name, IReadOnlyList<string> titles, int chosen)
    {
        _titles = titles;
        _chosen = chosen;
        Style = AppResources.Style("AnnotatorPlainButton");
        Height = 24;
        CornerRadius = new CornerRadius(6);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        _title = new TextBlock
        {
            Text = titles[chosen],
            FontSize = 13,
            LineHeight = 16,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 36.25, 0),
            Style = AppResources.Style("AnnotatorPrimaryText"),
            IsHitTestVisible = false,
        };
        AutomationProperties.SetAccessibilityView(_title, AccessibilityView.Raw);
        FrameworkElement chevrons = Icons.Glyphs.ForSymbol("chevron.up.chevron.down", 14, "AnnotatorGlyph", strokeWidth: 2.4);
        chevrons.HorizontalAlignment = HorizontalAlignment.Center;
        chevrons.VerticalAlignment = VerticalAlignment.Center;
        var badge = new Grid
        {
            Width = 19.5,
            Height = 19.5,
            CornerRadius = new CornerRadius(9.75),
            Background = ContrastTheme.Pick(ContrastColor.ButtonFace, PickerColors.Overlay(SettingsLook.Dark ? 0.12 : 0.07)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 3.75, 0),
            IsHitTestVisible = false,
        };
        if (ContrastTheme.IsOn)
        {
            badge.BorderBrush = ContrastTheme.Brush(ContrastColor.ButtonText);
            badge.BorderThickness = new Thickness(1);
        }
        badge.Children.Add(chevrons);
        var content = new Grid { IsHitTestVisible = false };
        content.Children.Add(_title);
        content.Children.Add(badge);
        Content = content;
        Width = WidthFor(titles[chosen]);
        AutomationProperties.SetName(this, name);
        Click += (_, _) => Open();
        IsEnabledChanged += (_, _) => Opacity = IsEnabled ? 1 : 0.45;
    }

    public event Action<int>? Chosen;

    public int ChosenIndex
    {
        get => _chosen;
        set
        {
            if (_chosen == value)
            {
                return;
            }
            string before = _titles[_chosen];
            _chosen = value;
            _title.Text = _titles[value];
            Width = WidthFor(_titles[value]);
            if (FrameworkElementAutomationPeer.FromElement(this) is AutomationPeer peer)
            {
                peer.RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, before, _titles[value]);
            }
        }
    }

    /// <summary>The pop-up's width for a title: the Mac's where the reference has it, else the title's width and the chrome.</summary>
    public static double WidthFor(string title)
    {
        if (s_macWidths.TryGetValue(title, out double width))
        {
            return width;
        }
        var probe = new TextBlock { Text = title, FontSize = 13 };
        probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Ceiling((probe.DesiredSize.Width + Chrome) * 2) / 2;
    }

    private void Open()
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };
        for (int index = 0; index < _titles.Count; index++)
        {
            int choice = index;
            var item = new ToggleMenuFlyoutItem { Text = _titles[index], IsChecked = index == _chosen };
            item.Click += (_, _) => Chosen?.Invoke(choice);
            menu.Items.Add(item);
        }
        menu.ShowAt(this);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed partial class Peer(SettingsPopUp owner) : ButtonAutomationPeer(owner), IValueProvider
    {
        public bool IsReadOnly => true;

        public string Value => owner._titles[owner._chosen];

        public void SetValue(string value) => throw new InvalidOperationException("Choose from the pop-up menu.");

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ComboBox;

        protected override string GetClassNameCore() => "ComboBox";

        protected override IList<AutomationPeer> GetChildrenCore() => new AutomationPeerList();

        protected override object? GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Value ? this : base.GetPatternCore(patternInterface);

        protected override WinRect GetBoundingRectangleCore() => SettingsFrames.Of(owner, base.GetBoundingRectangleCore());
    }
}

/// <summary>
/// The Mac's DisclosureGroup: a chevron and a title that show or hide the content below. The header is an expander in UI
/// Automation (a group with ExpandCollapse, the Mac's AXDisclosureTriangle) whose frame starts 22.5 points before the
/// content's leading edge and ends just after the title, as AppKit reports it. The content is built when first shown.
/// </summary>
internal sealed partial class SettingsDisclosure : Grid
{
    private readonly MacStack _stack = new();
    private readonly Header _header;
    private readonly Func<FrameworkElement> _content;
    private readonly FrameworkElement _chevron;
    private FrameworkElement? _built;

    /// <param name="fontSize">13 for body text, 12 for callout text; the header is that line plus 8 points.</param>
    /// <param name="contentTop">The space above the content (the Mac's padding(.top, ...)).</param>
    public SettingsDisclosure(string title, string? identifier, double fontSize, Func<FrameworkElement> content, double contentTop = 10)
    {
        _content = content;
        ContentTop = contentTop;
        double line = SettingsMetrics.LineHeight(fontSize);
        var text = new TextBlock
        {
            Text = title,
            FontSize = fontSize,
            LineHeight = line,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            VerticalAlignment = VerticalAlignment.Center,
            Style = AppResources.Style("AnnotatorPrimaryText"),
            Margin = new Thickness(12, 0, 0, 0),
            IsHitTestVisible = false,
        };
        AutomationProperties.SetAccessibilityView(text, AccessibilityView.Raw);
        _chevron = Icons.Glyphs.ForSymbol("chevron.right", 10, "AnnotatorGlyphSecondary", strokeWidth: 2.6);
        _chevron.HorizontalAlignment = HorizontalAlignment.Left;
        _chevron.VerticalAlignment = VerticalAlignment.Center;
        _chevron.Margin = new Thickness(-0.75, 0, 0, 0);
        _chevron.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        var face = new Grid { Height = line + 8, IsHitTestVisible = false };
        face.Children.Add(_chevron);
        face.Children.Add(text);
        _header = new Header(text, line, SettingsWidths.Disclosure(title))
        {
            Style = AppResources.Style("AnnotatorPlainButton"),
            Content = face,
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Height = line + 8,
            CornerRadius = new CornerRadius(4),
        };
        AutomationProperties.SetName(_header, title);
        if (identifier is not null)
        {
            AutomationProperties.SetAutomationId(_header, identifier);
        }
        _header.Click += (_, _) => IsExpanded = !IsExpanded;
        _stack.Children.Add(_header);
        Children.Add(_stack);
    }

    public double ContentTop { get; }

    public bool IsExpanded
    {
        get => _header.Expanded;
        set
        {
            if (_header.Expanded == value)
            {
                return;
            }
            _header.Expanded = value;
            _chevron.RenderTransform = value ? new RotateTransform { Angle = 90 } : null;
            if (value && _built is null)
            {
                _built = _content();
                _built.Margin = new Thickness(12, ContentTop, 0, 0);
                _stack.Children.Add(_built);
            }
            if (_built is not null)
            {
                _built.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            }
            Expanded?.Invoke(value);
            InvalidateUp(this);
            if (FrameworkElementAutomationPeer.FromElement(_header) is AutomationPeer peer)
            {
                peer.RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty,
                    value ? ExpandCollapseState.Collapsed : ExpandCollapseState.Expanded, value ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed);
            }
        }
    }

    /// <summary>Raised when the group opens or closes, for content that loads when first shown.</summary>
    public event Action<bool>? Expanded;

    /// <summary>MacStack does not measure again when a nested child changes, so the stacks above are told.</summary>
    private static void InvalidateUp(UIElement element)
    {
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement ui)
            {
                ui.InvalidateMeasure();
            }
        }
    }

    private sealed partial class Header(TextBlock title, double line, double? macWidth) : Button
    {
        private readonly TextBlock _title = title;
        private readonly double _line = line;
        private readonly double? _macWidth = macWidth;

        public bool Expanded { get; set; }

        protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

        private sealed partial class Peer(Header owner) : ButtonAutomationPeer(owner), IExpandCollapseProvider
        {
            public ExpandCollapseState ExpandCollapseState => owner.Expanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;

            public void Expand()
            {
                if (!owner.Expanded)
                {
                    ((IInvokeProvider)this).Invoke();
                }
            }

            public void Collapse()
            {
                if (owner.Expanded)
                {
                    ((IInvokeProvider)this).Invoke();
                }
            }

            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

            protected override string GetClassNameCore() => "Expander";

            protected override IList<AutomationPeer> GetChildrenCore() => new AutomationPeerList();

            protected override object? GetPatternCore(PatternInterface patternInterface) =>
                patternInterface == PatternInterface.ExpandCollapse ? this : base.GetPatternCore(patternInterface);

            /// <summary>
            /// From 22.5 points before the content's edge to just after the title, on the title's line; titles the Mac shares
            /// keep the Mac's width (D-4).
            /// </summary>
            protected override WinRect GetBoundingRectangleCore()
            {
                double width = owner._macWidth ?? 22.5 + 12 + owner._title.ActualWidth + 1.5;
                return SettingsFrames.Of(owner, new WinRect(-22.5, 4, width, owner._line), base.GetBoundingRectangleCore());
            }
        }
    }
}
