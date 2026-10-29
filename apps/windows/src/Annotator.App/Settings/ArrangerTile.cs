using Annotator.App.Pickers;
using Annotator.App.Shell;
using Annotator.Core;
using Annotator.Workspace.Motion;
using Annotator.Workspace.Settings;
using Annotator.Workspace.Toolbar;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Glyphs = Annotator.App.Icons.Glyphs;
using WinRect = Windows.Foundation.Rect;

namespace Annotator.App.Settings;

/// <summary>What an arranger tile does for the arranger: its key, its badge, its context menu and its keys.</summary>
internal interface IArrangerTileHost
{
    void Toggle(RecordingTarget target);

    void Add(ShortcutCommand item);

    void Remove(ShortcutCommand item);

    void Move(ShortcutCommand item, int delta);

    /// <summary>The toolbar's items now, for the position words and the menu's Move commands.</summary>
    IReadOnlyList<ShortcutCommand> Order { get; }
}

/// <summary>
/// Port of ArrangerTile: one tool or button in the arranger, a toolbar button (<c>inToolbar</c>) or a "More tools" tile with
/// its title. Its key chip sits under the glyph. Hovering shows the add or remove badge; while it is dragged its slot shows a
/// dashed placeholder. Keyboard focus draws the Mac's accent ring; Left and Right move a toolbar item, Delete removes it and
/// Enter adds a tray tile. Its context menu has the Mac's commands, which also stand for the VoiceOver actions (WIN-A11Y-02).
/// UI Automation reads a group named after the item, with its position as value and the Mac's hint as help.
/// </summary>
internal sealed partial class ArrangerTile : UserControl
{
    private readonly IArrangerTileHost _host;
    private readonly Grid _root = new();
    private readonly Border _fill = new() { CornerRadius = new CornerRadius(10) };
    private readonly Border _focus = new() { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(2), Opacity = 0, IsHitTestVisible = false };
    private readonly MacStack _content = new() { Orientation = Orientation.Vertical, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _placeholder = new() { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Canvas _badgeLayer = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Width = 0, Height = 0 };
    private FrameworkElement? _badge;
    private readonly AccessibleElement? _menuName;
    private bool _hovering;
    private bool _placeholderShown;

    public ArrangerTile(IArrangerTileHost host, ShortcutCommand item, bool inToolbar, ToolbarMetrics metrics, bool floating = false)
    {
        _host = host;
        Item = item;
        InToolbar = inToolbar;
        Floating = floating;
        IsTabStop = !floating;
        UseSystemFocusVisuals = false;
        Width = inToolbar ? metrics.ButtonWidth + 4 : 78;
        Height = inToolbar ? (metrics.Minimal ? 50 : 60) : 84;
        if (inToolbar)
        {
            double glyph = metrics.GlyphSize;
            _content.Children.Add(Center(CommandGlyph(item, glyph), metrics.Minimal ? 24 : 30));
        }
        else
        {
            _content.Children.Add(Center(CommandGlyph(item, 20), 24));
            TextBlock title = SettingsText.Static(item.Label, 10, "AnnotatorPrimaryText", FontWeights.Medium);
            title.TextWrapping = TextWrapping.Wrap;
            title.Height = double.NaN;
            title.MaxLines = 2;
            title.TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center;
            title.HorizontalAlignment = HorizontalAlignment.Center;
            title.VerticalAlignment = VerticalAlignment.Center;
            title.MaxWidth = 78;
            // Larger Windows text makes the title area and the tile taller instead of clipping (WIN-A11Y-03).
            var titleArea = new Grid { Width = 78, Children = { title } };
            if (SettingsText.Scaled)
            {
                titleArea.MinHeight = 26;
                MinHeight = Height;
                Height = double.NaN;
            }
            else
            {
                titleArea.Height = 26;
            }
            _content.Children.Add(titleArea);
        }
        Chip = new KeyChip(RecordingTarget.Local(item), item.Label, compact: true, host.Toggle);
        if (item.Action is { IsMenu: true })
        {
            // The Spotlight and Zoom menus open with a click and have no key; in the toolbar a chevron stands where a
            // tool shows its key (Mac ArrangerTile).
            if (inToolbar && metrics.ShowsKeyLabels)
            {
                FrameworkElement chevron = Glyphs.ForSymbol("chevron.down", 9, "AnnotatorGlyphSecondary");
                chevron.HorizontalAlignment = HorizontalAlignment.Center;
                _content.Children.Add(chevron);
            }
            if (!inToolbar)
            {
                // With no key below it, SwiftUI reports the tile's name over the whole tile.
                foreach (TextBlock words in _content.Children.OfType<Grid>().SelectMany(area => area.Children.OfType<TextBlock>()))
                {
                    AutomationProperties.SetAccessibilityView(words, AccessibilityView.Raw);
                }
                _menuName = new AccessibleElement(AutomationControlType.Text) { InSettings = true, IsHitTestVisible = false };
                AutomationProperties.SetName(_menuName, item.Label);
            }
        }
        else
        {
            _content.Children.Add(Chip.Button);
        }
        _focus.BorderBrush = SettingsColors.AccentBrush();
        _placeholder.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = SettingsColors.AccentBrush(0.08) });
        _placeholder.Children.Add(new Rectangle
        {
            RadiusX = 10,
            RadiusY = 10,
            Stroke = SettingsColors.AccentBrush(0.6),
            StrokeThickness = 1.5,
            StrokeDashArray = [4 / 1.5, 3 / 1.5],
        });
        _root.Children.Add(_fill);
        // The content is centered by spacers in a stack, not by the grid: the grid rounds the centering offset to a pixel,
        // which put the key buttons of More tools 1.1 points lower than the Mac's at 125%; the stack passes on its rounding.
        var centered = new MacStack();
        centered.Children.Add(new MacSpacer());
        centered.Children.Add(_content);
        centered.Children.Add(new MacSpacer());
        _root.Children.Add(centered);
        if (_menuName is not null)
        {
            _root.Children.Add(_menuName);
        }
        _root.Children.Add(_focus);
        _root.Children.Add(_placeholder);
        _root.Children.Add(_badgeLayer);
        Content = _root;
        ShowFill();
        if (floating)
        {
            IsHitTestVisible = false;
            AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
            return;
        }
        AutomationProperties.SetName(this, item.Label);
        AutomationProperties.SetAutomationId(this, ToolbarArranger.Identifier(item, inToolbar));
        ToolTipService.SetToolTip(this, inToolbar ? ToolbarArranger.ToolbarHelp(item) : ToolbarArranger.TrayHelp(item));
        ContextFlyout = Menu();
        PointerEntered += (_, _) => Hover(true);
        PointerExited += (_, _) => Hover(false);
        GotFocus += (_, _) => _focus.Opacity = FocusState == FocusState.Keyboard ? 1 : 0;
        LostFocus += (_, _) => _focus.Opacity = 0;
        KeyDown += OnKeyDown;
    }

    public ShortcutCommand Item { get; }

    public bool InToolbar { get; }

    public bool Floating { get; }

    public KeyChip Chip { get; }

    /// <summary>The item is being dragged: its slot shows the dashed placeholder and its content hides.</summary>
    public bool Placeholder
    {
        get => _placeholderShown;
        set
        {
            if (_placeholderShown == value)
            {
                return;
            }
            _placeholderShown = value;
            _placeholder.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            _content.Opacity = value ? 0 : 1;
            _fill.Opacity = value ? 0 : 1;
            if (value)
            {
                HideBadge();
            }
        }
    }

    /// <summary>The floating copy's background during a drag (Mac floatingItem: the window background behind the tile).</summary>
    public void ShowLifted()
    {
        _fill.Background = SettingsColors.Window;
        _fill.Opacity = 1;
    }

    /// <summary>The red minus that shows on the floating copy while letting go would remove it.</summary>
    public void ShowRemoving(bool removing)
    {
        if (removing)
        {
            ShowBadge(BadgeKind.Removing);
        }
        else
        {
            HideBadge();
        }
    }

    /// <summary>The tile's place in the toolbar changed: UI Automation hears the new position.</summary>
    public void Refresh()
    {
        if (FrameworkElementAutomationPeer.FromElement(this) is AutomationPeer peer)
        {
            peer.RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, string.Empty, Value);
        }
    }

    /// <summary>"Position 3 of 11" in the toolbar, or "Not in the toolbar".</summary>
    public string Value
    {
        get
        {
            if (!InToolbar)
            {
                return ToolbarArranger.TrayValue;
            }
            int index = IndexOf(_host.Order, Item);
            return ToolbarArranger.Position(Math.Max(0, index), _host.Order.Count);
        }
    }

    public string Hint
    {
        get
        {
            if (!InToolbar)
            {
                return ToolbarArranger.TrayHintText;
            }
            int index = IndexOf(_host.Order, Item);
            return ToolbarArranger.ToolbarHint(Math.Max(0, index), _host.Order.Count);
        }
    }

    /// <summary>Mac CommandGlyph: a tool's drawn glyph, or a button's symbol at 88% of the size.</summary>
    public static FrameworkElement CommandGlyph(ShortcutCommand item, double size) => item.Tool is { } tool
        ? Glyphs.ForTool(tool, size)
        : Center(Glyphs.ForSymbol(item.Action!.Value.Symbol, size * 0.88), size, size);

    private static FrameworkElement Center(FrameworkElement glyph, double height, double width = double.NaN)
    {
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        return new Grid { Height = height, Width = width, IsHitTestVisible = false, Children = { glyph } };
    }

    private static int IndexOf(IReadOnlyList<ShortcutCommand> order, ShortcutCommand item)
    {
        for (int index = 0; index < order.Count; index++)
        {
            if (order[index] == item)
            {
                return index;
            }
        }
        return -1;
    }

    private void ShowFill() => _fill.Background = InToolbar
        ? SettingsColors.Primary(_hovering ? 0.06 : 0)
        : SettingsColors.Primary(_hovering ? 0.08 : 0.045);

    private void Hover(bool hovering)
    {
        _hovering = hovering;
        ShowFill();
        if (hovering && !_placeholderShown)
        {
            ShowBadge(InToolbar ? BadgeKind.Remove : BadgeKind.Add);
        }
        else
        {
            HideBadge();
        }
    }

    private enum BadgeKind
    {
        Add,
        Remove,
        Removing,
    }

    /// <summary>
    /// Mac badgeView: a green plus or red minus disc, 16 points with a white inside, 6 points out from the top-right corner;
    /// it scales up from 30% as it appears. Select has no remove badge.
    /// </summary>
    private void ShowBadge(BadgeKind kind)
    {
        HideBadge();
        if (kind == BadgeKind.Remove && Item == ToolbarArranger.Select)
        {
            return;
        }
        var disc = new Grid { Width = 16, Height = 16 };
        disc.Children.Add(new Ellipse { Fill = SettingsColors.Black(0.2), Margin = new Thickness(0.5, 1.5, 0.5, -0.5) });
        disc.Children.Add(new Ellipse { Fill = new SolidColorBrush(kind == BadgeKind.Add ? SettingsColors.Green : SettingsColors.Red) });
        var white = new SolidColorBrush(Microsoft.UI.Colors.White);
        disc.Children.Add(new Rectangle { Width = 8, Height = 2, RadiusX = 1, RadiusY = 1, Fill = white });
        if (kind == BadgeKind.Add)
        {
            disc.Children.Add(new Rectangle { Width = 2, Height = 8, RadiusX = 1, RadiusY = 1, Fill = white });
        }
        FrameworkElement badge = disc;
        if (kind != BadgeKind.Removing)
        {
            string title = kind == BadgeKind.Add ? $"Add {Item.Label} to toolbar" : $"Remove {Item.Label} from toolbar";
            var button = new ParityButton
            {
                Style = AppResources.Style("AnnotatorMenuRowButton"),
                CornerRadius = new CornerRadius(8),
                Content = disc,
                Width = 16,
                Height = 16,
                AllowFocusOnInteraction = false,
            };
            button.Resources["AnnotatorMenuHoverBrush"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            AutomationProperties.SetName(button, title);
            ToolTipService.SetToolTip(button, kind == BadgeKind.Add ? $"Add {Item.Label} to your toolbar" : $"Remove {Item.Label} from your toolbar");
            button.Click += (_, _) =>
            {
                if (kind == BadgeKind.Add)
                {
                    _host.Add(Item);
                }
                else
                {
                    _host.Remove(Item);
                }
            };
            badge = button;
        }
        else
        {
            badge.IsHitTestVisible = false;
        }
        // Mac .overlay(alignment: .topTrailing) with .offset(x: 6, y: -6): the 16-point badge reaches 6 points past the corner.
        Canvas.SetLeft(badge, -16 + 6);
        Canvas.SetTop(badge, -6);
        _badge = badge;
        _badgeLayer.Children.Add(badge);
        if (PageMotion.ReducesMotion)
        {
            PageMotion.Fade(badge, 0, 1, ShortcutEditorMotion.Badge(reduceMotion: true));
        }
        else
        {
            badge.Loaded += (_, _) =>
            {
                PageMotion.Scale(badge, ShortcutEditorMotion.Badge(reduceMotion: false), share => 0.3 + 0.7 * share);
                PageMotion.Fade(badge, 0, 1, ShortcutEditorMotion.Badge(reduceMotion: false));
            };
        }
    }

    private void HideBadge()
    {
        if (_badge is { } badge)
        {
            _badgeLayer.Children.Remove(badge);
            _badge = null;
        }
    }

    /// <summary>
    /// The Mac's context menu with the VoiceOver actions' names (WIN-A11Y-02). The items exist before the menu opens, so UI
    /// Automation and the accessibility checks see them; opening it updates which moves are possible.
    /// </summary>
    private MenuFlyout Menu()
    {
        var menu = new MenuFlyout();
        if (InToolbar)
        {
            MenuFlyoutItem left = Library.LibraryParts.Item("Move left", () => _host.Move(Item, -1));
            MenuFlyoutItem right = Library.LibraryParts.Item("Move right", () => _host.Move(Item, 1));
            menu.Items.Add(left);
            menu.Items.Add(right);
            menu.Items.Add(new MenuFlyoutSeparator());
            if (Item.Action is not { IsMenu: true })
            {
                menu.Items.Add(Library.LibraryParts.Item("Change key…", () => _host.Toggle(RecordingTarget.Local(Item))));
            }
            if (Item != ToolbarArranger.Select)
            {
                menu.Items.Add(Library.LibraryParts.Item("Remove from toolbar", () => _host.Remove(Item)));
            }
            menu.Opening += (_, _) =>
            {
                int index = IndexOf(_host.Order, Item);
                left.IsEnabled = index > 0;
                right.IsEnabled = index >= 0 && index < _host.Order.Count - 1;
            };
        }
        else
        {
            menu.Items.Add(Library.LibraryParts.Item("Add to toolbar", () => _host.Add(Item)));
            if (Item.Action is not { IsMenu: true })
            {
                menu.Items.Add(Library.LibraryParts.Item("Change key…", () => _host.Toggle(RecordingTarget.Local(Item))));
            }
        }
        return menu;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!ReferenceEquals(args.OriginalSource, this))
        {
            return;
        }
        switch (args.Key)
        {
            case VirtualKey.Left when InToolbar:
                _host.Move(Item, -1);
                break;
            case VirtualKey.Right when InToolbar:
                _host.Move(Item, 1);
                break;
            case VirtualKey.Delete when InToolbar:
            case VirtualKey.Back when InToolbar:
                _host.Remove(Item);
                break;
            case VirtualKey.Enter when !InToolbar:
                _host.Add(Item);
                break;
            default:
                return;
        }
        args.Handled = true;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed partial class Peer(ArrangerTile owner) : FrameworkElementAutomationPeer(owner), IValueProvider
    {
        public bool IsReadOnly => true;

        public string Value => owner.Value;

        public void SetValue(string value) => throw new InvalidOperationException("Drag the tile, or use Move left and Move right.");

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

        protected override string GetClassNameCore() => "Group";

        protected override bool IsControlElementCore() => !owner.Floating;

        protected override string GetHelpTextCore() => owner.Hint;

        protected override object? GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Value ? this : base.GetPatternCore(patternInterface);

        protected override WinRect GetBoundingRectangleCore() => AutomationFrames.Of(owner, base.GetBoundingRectangleCore());
    }
}
