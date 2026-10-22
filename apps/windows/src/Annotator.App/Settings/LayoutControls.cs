using System.Numerics;
using Annotator.App.Pickers;
using Annotator.App.Shell;
using Annotator.Core;
using Annotator.Workspace.Motion;
using Annotator.Workspace.Toolbar;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Glyphs = Annotator.App.Icons.Glyphs;
using WinRect = Windows.Foundation.Rect;

namespace Annotator.App.Settings;

/// <summary>
/// Port of ToolbarSizePicker: the Expanded and Minimal cards, 250 by 128 points and 12 apart, each with a small toolbar, its
/// name, a check mark when chosen and what it means. The chosen card's accent ring moves to the other card on the smooth
/// spring. UI Automation reads the group "Toolbar size" with the buttons "Expanded toolbar" and "Minimal toolbar".
/// </summary>
internal sealed class ToolbarSizePicker
{
    private readonly ParityButton[] _cards = new ParityButton[2];
    private readonly Rectangle[] _outlines = new Rectangle[2];
    private readonly Border[] _fills = new Border[2];
    private readonly FrameworkElement[] _checks = new FrameworkElement[2];
    private readonly MiniToolbar[] _minis = new MiniToolbar[2];
    private ToolbarSize? _shown;

    public ToolbarSizePicker(Action<ToolbarSize> choose)
    {
        Root = new AccessibleGroup("Toolbar size") { HorizontalAlignment = HorizontalAlignment.Left };
        var row = new MacStack { Orientation = Orientation.Horizontal, Spacing = 12 };
        ToolbarSize[] sizes = [ToolbarSize.Expanded, ToolbarSize.Minimal];
        for (int index = 0; index < sizes.Length; index++)
        {
            ToolbarSize size = sizes[index];
            bool expanded = size == ToolbarSize.Expanded;
            var content = new MacStack { Orientation = Orientation.Vertical, Spacing = 8, Margin = new Thickness(12) };
            _minis[index] = new MiniToolbar(size);
            content.Children.Add(new Grid { MinHeight = 46, Width = 226, Children = { _minis[index].Root } });
            var titleRow = new Grid { Width = 226 };
            titleRow.Children.Add(SettingsText.Label(expanded ? "Expanded" : "Minimal", 13, "AnnotatorPrimaryText", FontWeights.SemiBold));
            FrameworkElement check = SettingsSymbols.Filled("checkmark.circle.fill", 15, SettingsColors.Accent);
            check.HorizontalAlignment = HorizontalAlignment.Right;
            check.VerticalAlignment = VerticalAlignment.Center;
            _checks[index] = check;
            titleRow.Children.Add(check);
            content.Children.Add(titleRow);
            var caption = new Library.WrappedText(expanded ? "Larger buttons, with each key shown under its tool."
                : "Smaller buttons and no key labels. Hold the pointer over a tool to see its key.", 10, "AnnotatorSecondaryText")
            {
                Width = 226,
            };
            AutomationProperties.SetAccessibilityView(caption.Block, AccessibilityView.Raw);
            content.Children.Add(new ExactLines(caption, SettingsText.LineHeight(10)));
            _fills[index] = new Border { CornerRadius = new CornerRadius(12) };
            _outlines[index] = new Rectangle { RadiusX = 12, RadiusY = 12 };
            var surface = new Grid { Width = 250, IsHitTestVisible = false };
            surface.Children.Add(_fills[index]);
            surface.Children.Add(content);
            surface.Children.Add(_outlines[index]);
            var card = new ParityButton
            {
                Style = AppResources.Style("AnnotatorMenuRowButton"),
                CornerRadius = new CornerRadius(12),
                Content = surface,
                Width = 250,
                UseSystemFocusVisuals = true,
                AccessibleSelected = false,
            };
            card.Resources["AnnotatorMenuHoverBrush"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            AutomationProperties.SetName(card, expanded ? "Expanded toolbar" : "Minimal toolbar");
            AutomationProperties.SetAutomationId(card, "toolbar-size-" + size.RawValue);
            card.Click += (_, _) => choose(size);
            _cards[index] = card;
            row.Children.Add(card);
        }
        Root.Children.Add(row);
    }

    public AccessibleGroup Root { get; }

    /// <summary>Shows the chosen size: the accent ring, the fill and the check mark.</summary>
    public void Show(ToolbarSize size)
    {
        if (_shown == size)
        {
            return;
        }
        bool moving = _shown is not null;
        _shown = size;
        for (int index = 0; index < _cards.Length; index++)
        {
            bool selected = (index == 0) == (size == ToolbarSize.Expanded);
            _cards[index].AccessibleSelected = selected;
            _fills[index].Background = SettingsColors.Primary(selected ? 0.05 : 0.03);
            _outlines[index].Stroke = selected ? SettingsColors.AccentBrush() : SettingsColors.Primary(0.08);
            _outlines[index].StrokeThickness = selected ? 2 : 1;
            _minis[index].Highlight(selected);
            _checks[index].Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            if (moving && selected)
            {
                // The ring and the check mark arrive with the smooth spring (Mac matchedGeometryEffect and .scale transition).
                MotionSamples ring = ShortcutEditorMotion.Ring(PageMotion.ReducesMotion);
                PageMotion.Fade(_outlines[index], 0, 1, ring);
                if (PageMotion.ReducesMotion)
                {
                    PageMotion.Fade(_checks[index], 0, 1, ring);
                }
                else
                {
                    PageMotion.Scale(_checks[index], ring, share => share);
                }
            }
        }
    }
}

/// <summary>
/// Port of MiniToolbar: five tools (Select, Rectangle, Arrow, Pen and Text) with their keys in the Expanded card, a divider
/// and three squares, on the window background with a small shadow. The Pen's slot shows the accent while its card is chosen.
/// </summary>
internal sealed class MiniToolbar
{
    private static readonly (Tool Tool, string Key)[] s_tools = [(Tool.Select, "1"), (Tool.Rectangle, "2"), (Tool.Arrow, "5"), (Tool.Pen, "7"), (Tool.Text, "8")];
    private readonly Border _penSlot = new() { CornerRadius = new CornerRadius(4) };

    public MiniToolbar(ToolbarSize size)
    {
        bool minimal = size == ToolbarSize.Minimal;
        var row = new MacStack
        {
            Orientation = Orientation.Horizontal,
            Spacing = minimal ? 3 : 5,
            Margin = new Thickness(minimal ? 6 : 9, minimal ? 4 : 6, minimal ? 6 : 9, minimal ? 4 : 6),
        };
        for (int index = 0; index < s_tools.Length; index++)
        {
            (Tool tool, string key) = s_tools[index];
            var slot = new Grid { Width = minimal ? 15 : 20, Height = minimal ? 16 : 24 };
            if (index == 3)
            {
                slot.Children.Add(_penSlot);
            }
            var stack = new MacStack { Orientation = Orientation.Vertical, Spacing = 1, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            FrameworkElement glyph = Glyphs.ForTool(tool, minimal ? 10 : 12);
            glyph.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(glyph);
            if (!minimal)
            {
                TextBlock label = SettingsText.Label(key, 6.5, "AnnotatorSecondaryText");
                label.LineHeight = 8;
                label.Height = SettingsText.Scaled ? double.NaN : 8;
                label.HorizontalAlignment = HorizontalAlignment.Center;
                stack.Children.Add(label);
            }
            slot.Children.Add(stack);
            row.Children.Add(slot);
        }
        row.Children.Add(new Rectangle { Width = 1, Height = minimal ? 12 : 18, Fill = SettingsColors.Primary(0.15), VerticalAlignment = VerticalAlignment.Center });
        for (int index = 0; index < 3; index++)
        {
            double square = minimal ? 7 : 9;
            row.Children.Add(new Border { Width = square, Height = square, CornerRadius = new CornerRadius(2), Background = SettingsColors.Primary(0.25), VerticalAlignment = VerticalAlignment.Center });
        }
        double corner = minimal ? 6 : 8;
        Root = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        // Mac .shadow(color: .black.opacity(0.12), radius: 3, y: 1), drawn as a soft rim one point below.
        Root.Children.Add(new Border { CornerRadius = new CornerRadius(corner), Background = SettingsColors.Black(0.06), Margin = new Thickness(-1, 0, -1, -2) });
        Root.Children.Add(new Border { CornerRadius = new CornerRadius(corner), Background = SettingsColors.Window, BorderBrush = SettingsColors.Primary(0.08), BorderThickness = new Thickness(1) });
        Root.Children.Add(row);
        AutomationProperties.SetAccessibilityView(Root, AccessibilityView.Raw);
    }

    public Grid Root { get; }

    public void Highlight(bool highlighted) => _penSlot.Background = highlighted ? SettingsColors.AccentBrush(0.2) : null;
}

/// <summary>
/// The Direction picker (Mac Picker with .pickerStyle(.segmented) and .frame(width: 260)): Horizontal and Vertical, 86 points
/// each, centered in 260 points. UI Automation reads the radio group "Direction" with radio buttons whose identifiers are
/// the Mac's symbol names.
/// </summary>
internal sealed class DirectionPicker
{
    private readonly SegmentButton[] _segments = new SegmentButton[2];
    private readonly Border _selection = new() { CornerRadius = new CornerRadius(5), Background = PickerColors.SegmentSelected, Width = 86, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };

    public DirectionPicker(Action<ToolbarOrientation> choose)
    {
        var group = new SegmentGroup("Direction") { Width = 172, Height = 24, HorizontalAlignment = HorizontalAlignment.Center };
        group.Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = PickerColors.ControlFill, IsHitTestVisible = false });
        group.Children.Add(_selection);
        (string Title, string Symbol, ToolbarOrientation Orientation)[] choices =
        [
            ("Horizontal", "rectangle.split.3x1", ToolbarOrientation.Horizontal),
            ("Vertical", "rectangle.split.1x3", ToolbarOrientation.Vertical),
        ];
        for (int index = 0; index < choices.Length; index++)
        {
            (string title, string symbol, ToolbarOrientation orientation) = choices[index];
            TextBlock label = SettingsText.Label(title, 13);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            var segment = new SegmentButton
            {
                Style = AppResources.Style("AnnotatorPlainButton"),
                CornerRadius = new CornerRadius(5),
                Content = label,
                Group = group,
                Width = 86,
                HorizontalAlignment = HorizontalAlignment.Left,
                // The whole segment's height, as the Mac reports its segments.
                VerticalAlignment = VerticalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(index * 86, 0, 0, 0),
            };
            AutomationProperties.SetName(segment, title);
            AutomationProperties.SetAutomationId(segment, symbol);
            segment.Chosen += () => choose(orientation);
            segment.KeyDown += (_, args) =>
            {
                if (args.Key is VirtualKey.Left or VirtualKey.Right)
                {
                    args.Handled = true;
                    choose(args.Key == VirtualKey.Left ? ToolbarOrientation.Horizontal : ToolbarOrientation.Vertical);
                    _segments[args.Key == VirtualKey.Left ? 0 : 1].Focus(FocusState.Keyboard);
                }
            };
            _segments[index] = segment;
            group.Segments.Add(segment);
            group.Children.Add(segment);
        }
        group.TabFocusNavigation = Microsoft.UI.Xaml.Input.KeyboardNavigationMode.Once;
        Root = new Grid { Width = 260, Height = 24, Children = { group } };
    }

    public Grid Root { get; }

    public void Show(ToolbarOrientation orientation)
    {
        int selected = orientation == ToolbarOrientation.Vertical ? 1 : 0;
        for (int index = 0; index < _segments.Length; index++)
        {
            _segments[index].IsChosen = index == selected;
        }
        _selection.Margin = new Thickness(selected * 86, 0, 0, 0);
    }
}

/// <summary>
/// The Mac's regular pop-up button (Picker with .labelsHidden().fixedSize()): the chosen item's title and the up and down
/// chevrons on a 24-point control fill, with the items in a menu that checks the chosen one. UI Automation reads a combo box
/// named like the Mac's picker whose value is the chosen title.
/// </summary>
internal sealed partial class ChoicePopUp : Button
{
    private readonly TextBlock _title;
    private IReadOnlyList<(string Title, Action Choose)> _items = [];
    private string _value = string.Empty;

    public ChoicePopUp(string name, double width)
    {
        Style = AppResources.Style("AnnotatorPopUpButton");
        Width = width;
        Height = 24;
        CornerRadius = new CornerRadius(6);
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Left;
        _title = SettingsText.Label(string.Empty, 13);
        _title.VerticalAlignment = VerticalAlignment.Center;
        _title.Margin = new Thickness(10, 0, 0, 0);
        FrameworkElement chevrons = Glyphs.ForSymbol("chevron.up.chevron.down", 15);
        chevrons.HorizontalAlignment = HorizontalAlignment.Right;
        chevrons.VerticalAlignment = VerticalAlignment.Center;
        chevrons.Margin = new Thickness(0, 0, 4, 0);
        Content = new Grid { IsHitTestVisible = false, Children = { _title, chevrons } };
        AutomationProperties.SetName(this, name);
        Click += (_, _) => Open();
    }

    /// <summary>Shows the chosen title and the items the menu offers.</summary>
    public void Show(string value, IReadOnlyList<(string Title, Action Choose)> items)
    {
        _items = items;
        if (_value == value)
        {
            return;
        }
        string before = _value;
        _value = value;
        _title.Text = value;
        if (before.Length > 0 && FrameworkElementAutomationPeer.FromElement(this) is AutomationPeer peer)
        {
            peer.RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, before, value);
        }
    }

    private void Open()
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        foreach ((string title, Action choose) in _items)
        {
            var item = new ToggleMenuFlyoutItem { Text = title, IsChecked = title == _value };
            item.Click += (_, _) => choose();
            menu.Items.Add(item);
        }
        menu.ShowAt(this);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed partial class Peer(ChoicePopUp owner) : ButtonAutomationPeer(owner), IValueProvider
    {
        public bool IsReadOnly => true;

        public string Value => owner._value;

        public void SetValue(string value) => throw new InvalidOperationException("Choose from the pop-up menu.");

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ComboBox;

        protected override string GetClassNameCore() => "ComboBox";

        protected override IList<AutomationPeer> GetChildrenCore() => new AutomationPeerList();

        protected override object? GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Value ? this : base.GetPatternCore(patternInterface);

        protected override WinRect GetBoundingRectangleCore() => AutomationFrames.Of(owner, base.GetBoundingRectangleCore());
    }
}
