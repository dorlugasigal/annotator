using Annotator.App.Shell;
using Annotator.Workspace.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;
using WinRect = Windows.Foundation.Rect;

namespace Annotator.App.Settings;

/// <summary>
/// The Mac's grouped TabView in Settings (CustomizationView with InlineSettingsTabs): a rounded box with the six tabs as a
/// segmented strip across its top edge, and the chosen tab's page inside, 3 points in from the box. UI Automation reads a
/// tab group (the Mac's AXTabGroup) with the six tabs, named and identified like the Mac's, then the page.
/// </summary>
internal sealed partial class SettingsTabView : Panel
{
    /// <summary>The box's top below the tab group's frame; the strip straddles it.</summary>
    public const double BoxTop = 10;

    /// <summary>The page's inset from the box (3 points on the sides and bottom, 17 below the box's top).</summary>
    public static readonly Thickness PageInset = new(3, 27, 3, 3);

    private readonly Border _box;
    private readonly SettingsTabStrip _strip;
    private readonly Grid _page = new();

    public SettingsTabView(Action<SettingsTab> choose)
    {
        _box = new Border
        {
            Background = SettingsLook.Panel,
            BorderBrush = SettingsLook.PanelStroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
        };
        _strip = new SettingsTabStrip(this, choose);
        Children.Add(_box);
        Children.Add(_strip);
        Children.Add(_page);
    }

    public SettingsTab Selected => _strip.Selected;

    /// <summary>The height its parent gave it before pixel rounding, when the parent knows it.</summary>
    public double? ExactHeight { get; set; }

    public void Select(SettingsTab tab) => _strip.Select(tab);

    /// <summary>Shows <paramref name="page"/> inside the box, or nothing for a tab whose page is not set.</summary>
    public void ShowPage(FrameworkElement? page)
    {
        _page.Children.Clear();
        if (page is not null)
        {
            page.HorizontalAlignment = HorizontalAlignment.Stretch;
            page.VerticalAlignment = VerticalAlignment.Stretch;
            _page.Children.Add(page);
        }
    }

    public void FocusSelectedTab() => _strip.FocusSelected();

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? 622 : availableSize.Height;
        _box.Measure(new Size(width, Math.Max(0, height - BoxTop)));
        _strip.Measure(new Size(double.PositiveInfinity, SettingsTabStrip.StripHeight));
        _page.Measure(new Size(Math.Max(0, width - PageInset.Left - PageInset.Right), Math.Max(0, height - PageInset.Top - PageInset.Bottom)));
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double scale = XamlRoot?.RasterizationScale ?? 1;
        Point residual = MacStack.InheritedResidual(this);
        finalSize = new Size(finalSize.Width, ExactHeight ?? finalSize.Height);
        MacStack.ArrangeExact(_box, residual.X, residual.Y + BoxTop, finalSize.Width, Math.Max(0, finalSize.Height - BoxTop), default, scale);
        double stripWidth = SettingsTabStrip.StripWidth;
        MacStack.ArrangeExact(_strip, residual.X + (finalSize.Width - stripWidth) / 2, residual.Y - 1.5, stripWidth, SettingsTabStrip.StripHeight, default, scale);
        MacStack.ArrangeExact(_page, residual.X + PageInset.Left, residual.Y + PageInset.Top,
            Math.Max(0, finalSize.Width - PageInset.Left - PageInset.Right), Math.Max(0, finalSize.Height - PageInset.Top - PageInset.Bottom), default, scale);
        return finalSize;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    internal SettingsTabItem SelectedItem => _strip.Item(_strip.Selected);

    private sealed partial class Peer(SettingsTabView owner) : FrameworkElementAutomationPeer(owner), ISelectionProvider
    {
        public bool CanSelectMultiple => false;

        public bool IsSelectionRequired => true;

        public IRawElementProviderSimple[] GetSelection() =>
            FrameworkElementAutomationPeer.FromElement(owner.SelectedItem) is { } item ? [ProviderFromPeer(item)] : [];

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Tab;

        protected override string GetClassNameCore() => "TabView";

        protected override bool IsControlElementCore() => true;

        protected override object? GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Selection ? this : base.GetPatternCore(patternInterface);

        protected override WinRect GetBoundingRectangleCore() => SettingsFrames.Of(owner, base.GetBoundingRectangleCore());
    }
}

/// <summary>
/// The tabs as one segmented strip, 24 points high: a track with the chosen tab's pill, each pill as wide as the Mac's
/// (the title plus 11.5 points on each side in SF Pro), 5.5 points apart. Left and Right move between tabs.
/// </summary>
internal sealed partial class SettingsTabStrip : Panel
{
    public const double StripHeight = 24;
    private const double Inset = 2.5;
    private const double Gap = 5.5;
    private const double PillHeight = 20;

    /// <summary>The Mac's pill widths, from the selected tab's highlight in each reference state.</summary>
    private static readonly double[] s_pills = [71, 144, 67, 93, 97, 51.5, 60];

    private readonly Border _backing;
    private readonly Border _track;
    private readonly Border _pill;
    private readonly List<SettingsTabItem> _items = [];
    private readonly Action<SettingsTab> _choose;

    public SettingsTabStrip(SettingsTabView owner, Action<SettingsTab> choose)
    {
        _choose = choose;
        // The track is see-through and straddles the box's top edge; this backing hides that edge behind the titles.
        _backing = new Border { Background = SettingsLook.Window, CornerRadius = new CornerRadius(8), IsHitTestVisible = false };
        _track = new Border { Background = SettingsLook.TabTrack, CornerRadius = new CornerRadius(8) };
        _pill = new Border
        {
            Background = SettingsLook.TabSelected,
            CornerRadius = new CornerRadius(6),
            // A contrast theme outlines the selected tab with Highlight.
            BorderBrush = ContrastTheme.Pick(ContrastColor.Highlight, Pickers.PickerColors.Overlay(0.06)),
            BorderThickness = new Thickness(ContrastTheme.IsOn ? 2 : 0.5),
        };
        if (ContrastTheme.IsOn)
        {
            _track.BorderBrush = ContrastTheme.Brush(ContrastColor.WindowText);
            _track.BorderThickness = new Thickness(1);
        }
        Children.Add(_backing);
        Children.Add(_track);
        Children.Add(_pill);
        foreach (SettingsTab tab in SettingsTab.AllCases)
        {
            var item = new SettingsTabItem(owner, tab);
            item.Click += (_, _) => choose(tab);
            item.KeyDown += OnKeyDown;
            _items.Add(item);
            Children.Add(item);
        }
        Select(SettingsTab.General);
    }

    public static double StripWidth => s_pills.Sum() + Gap * (s_pills.Length - 1) + Inset * 2;

    public SettingsTab Selected { get; private set; }

    public SettingsTabItem Item(SettingsTab tab) => _items[(int)tab];

    public void Select(SettingsTab tab)
    {
        SettingsTab before = Selected;
        Selected = tab;
        foreach (SettingsTabItem item in _items)
        {
            item.IsChosen = item.Tab == tab;
        }
        InvalidateArrange();
        _ = before;
    }

    public void FocusSelected() => _items[(int)Selected].Focus(FocusState.Programmatic);

    protected override Size MeasureOverride(Size availableSize)
    {
        _backing.Measure(new Size(StripWidth, StripHeight));
        _track.Measure(new Size(StripWidth, StripHeight));
        _pill.Measure(new Size(s_pills[0], PillHeight));
        for (int index = 0; index < _items.Count; index++)
        {
            _items[index].Measure(new Size(s_pills[index], PillHeight));
        }
        return new Size(StripWidth, StripHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double scale = XamlRoot?.RasterizationScale ?? 1;
        Point residual = MacStack.InheritedResidual(this);
        MacStack.ArrangeExact(_backing, residual.X, residual.Y, StripWidth, StripHeight, default, scale);
        MacStack.ArrangeExact(_track, residual.X, residual.Y, StripWidth, StripHeight, default, scale);
        double x = Inset;
        for (int index = 0; index < _items.Count; index++)
        {
            if (index == (int)Selected)
            {
                MacStack.ArrangeExact(_pill, residual.X + x, residual.Y + (StripHeight - PillHeight) / 2, s_pills[index], PillHeight, default, scale);
            }
            MacStack.ArrangeExact(_items[index], residual.X + x, residual.Y + (StripHeight - PillHeight) / 2, s_pills[index], PillHeight, default, scale);
            x += s_pills[index] + Gap;
        }
        return finalSize;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        int step = args.Key switch
        {
            VirtualKey.Left => -1,
            VirtualKey.Right => 1,
            _ => 0,
        };
        if (step == 0)
        {
            return;
        }
        args.Handled = true;
        int next = Math.Clamp((int)Selected + step, 0, _items.Count - 1);
        _choose((SettingsTab)next);
        _items[next].Focus(FocusState.Keyboard);
    }
}

/// <summary>One tab: its title centered in its pill. UI Automation reads a tab item named and identified like the Mac's tab.</summary>
internal sealed partial class SettingsTabItem : Button
{
    private readonly SettingsTabView _owner;
    private bool _chosen;

    public SettingsTabItem(SettingsTabView owner, SettingsTab tab)
    {
        _owner = owner;
        Tab = tab;
        Style = AppResources.Style("AnnotatorPlainButton");
        CornerRadius = new CornerRadius(6);
        HorizontalContentAlignment = HorizontalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Padding = new Thickness(0);
        var title = new TextBlock
        {
            Text = tab.Title,
            FontSize = 13,
            LineHeight = 16,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Style = AppResources.Style("AnnotatorPrimaryText"),
            IsHitTestVisible = false,
        };
        AutomationProperties.SetAccessibilityView(title, AccessibilityView.Raw);
        Content = title;
        AutomationProperties.SetName(this, tab.Title);
        AutomationProperties.SetAutomationId(this, tab.Symbol);
    }

    public SettingsTab Tab { get; }

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

    private sealed partial class Peer(SettingsTabItem owner) : ButtonAutomationPeer(owner), ISelectionItemProvider
    {
        public bool IsSelected => owner._chosen;

        public IRawElementProviderSimple? SelectionContainer =>
            FrameworkElementAutomationPeer.FromElement(owner._owner) is { } container ? ProviderFromPeer(container) : null;

        public void Select()
        {
            if (owner.IsEnabled && !owner._chosen)
            {
                ((IInvokeProvider)this).Invoke();
            }
        }

        public void AddToSelection() => Select();

        public void RemoveFromSelection()
        {
        }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.TabItem;

        protected override string GetClassNameCore() => "TabItem";

        protected override IList<AutomationPeer> GetChildrenCore() => new AutomationPeerList();

        protected override object? GetPatternCore(PatternInterface patternInterface) => patternInterface switch
        {
            PatternInterface.SelectionItem => this,
            _ => base.GetPatternCore(patternInterface),
        };

        protected override WinRect GetBoundingRectangleCore() => SettingsFrames.Of(owner, base.GetBoundingRectangleCore());
    }
}
