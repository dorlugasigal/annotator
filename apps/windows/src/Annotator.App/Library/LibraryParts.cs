using Annotator.App.Pickers;
using Annotator.App.Shell;
using Annotator.App.Toolbar;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WinColor = Windows.UI.Color;

namespace Annotator.App.Library;

/// <summary>
/// Colors of the Tools window from the Mac capture (reference/ux-reference/macos/tools-window and design-values.json), for the
/// app's current theme: tiles, the segmented picker's separators, the rounded text field and bordered buttons. In a Windows
/// contrast theme each takes the theme's color for its role instead (WIN-UI-07).
/// </summary>
internal static class LibraryColors
{
    /// <summary>A tile: Color.secondary at 7% (secondaryLabelColor is 50% black or 55% white).</summary>
    public static SolidColorBrush Tile => ContrastTheme.Pick(ContrastColor.ButtonFace, PickerColors.Overlay(PickerColors.Dark ? 0.55 * 0.07 : 0.5 * 0.07));

    /// <summary>The current tool's tile: the accent at 16%; outlined in a contrast theme (<see cref="ContrastTheme.Outline"/>).</summary>
    public static SolidColorBrush SelectedTile => ContrastTheme.Pick(ContrastColor.ButtonFace, new SolidColorBrush(Accent) { Opacity = 0.16 });

    /// <summary>A separator between two unselected segments (#B4B4B4 and #646464 over the picker's fill in the capture).</summary>
    public static SolidColorBrush SegmentSeparator => ContrastTheme.Pick(ContrastColor.WindowText, PickerColors.Overlay(PickerColors.Dark ? 0.251 : 0.234));

    /// <summary>A disabled bordered button's fill: half the control fill (#F5F5F5 and #262626 in the capture).</summary>
    public static SolidColorBrush DisabledControlFill => ContrastTheme.Pick(ContrastColor.Window, PickerColors.Overlay(0x0A / 255.0));

    /// <summary>The rounded text field's fill and outline (#FFFFFF and #161616, #D9D9D9 and #333333 in the capture).</summary>
    public static SolidColorBrush FieldFill => ContrastTheme.Pick(ContrastColor.Window,
        new SolidColorBrush(PickerColors.Dark ? WinColor.FromArgb(255, 0x16, 0x16, 0x16) : WinColor.FromArgb(255, 255, 255, 255)));

    public static SolidColorBrush FieldStroke => ContrastTheme.Pick(ContrastColor.WindowText,
        new SolidColorBrush(PickerColors.Dark ? WinColor.FromArgb(255, 0x33, 0x33, 0x33) : WinColor.FromArgb(255, 0xD9, 0xD9, 0xD9)));

    /// <summary>A saved shape's thumbnail background: Color.gray at 18%, part of the picture of the shape.</summary>
    public static SolidColorBrush ThumbnailFill => new(WinColor.FromArgb((byte)Math.Round(0.18 * 255), 0x8E, 0x8E, 0x93));

    public static WinColor Accent => AppResources.Brush("AnnotatorAccentBrush") is SolidColorBrush { Color: var color } ? color : WinColor.FromArgb(255, 0, 0x7A, 0xFF);
}

/// <summary>Text, buttons and menus of the Tools window at the Mac's sizes.</summary>
internal static class LibraryParts
{
    /// <summary>One line of text with the Mac's line height; UI Automation reads it as static text.</summary>
    public static TextBlock Text(string text, double size, string style, Windows.UI.Text.FontWeight? weight = null, double macWidth = 0)
    {
        TextBlock block = PickerText.Line(text, size, style, weight);
        block.MinWidth = macWidth;
        return block;
    }

    /// <summary>Text that belongs to a control's look; UI Automation reads the control instead.</summary>
    public static TextBlock Label(string text, double size, string style, Windows.UI.Text.FontWeight? weight = null)
    {
        TextBlock block = PickerText.Line(text, size, style, weight);
        block.IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(block, AccessibilityView.Raw);
        return block;
    }

    /// <summary>
    /// The Mac's bordered push button (.bordered at regular or small size): a rounded fill with the title centered,
    /// <paramref name="width"/> by <paramref name="height"/> points. A disabled button keeps half the fill and gray words.
    /// </summary>
    public static ParityButton Bordered(string title, double width, double height, double fontSize, string? style = null)
    {
        TextBlock label = Label(title, fontSize, style ?? "AnnotatorPrimaryText");
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var surface = new Border { CornerRadius = new CornerRadius(height >= 24 ? 6 : 5), Background = PickerColors.ControlFill, Child = label };
        var button = new ParityButton
        {
            Style = AppResources.Style("AnnotatorPlainButton"),
            Width = width,
            Height = height,
            CornerRadius = surface.CornerRadius,
            Content = surface,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(button, title);
        // WinUI does not raise IsEnabledChanged before the button is in the window, so it also shows its state on loading.
        button.IsEnabledChanged += (_, _) => ShowEnabled(button, surface, label, style ?? "AnnotatorPrimaryText");
        button.Loaded += (_, _) => ShowEnabled(button, surface, label, style ?? "AnnotatorPrimaryText");
        return button;
    }

    /// <summary>The width SwiftUI gives a regular bordered button: its title plus 12 points on each side, in half points.</summary>
    public static double RegularWidth(string title)
    {
        TextBlock probe = PickerText.Line(title, 13, "AnnotatorPrimaryText");
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Ceiling((probe.DesiredSize.Width + 24) * 2) / 2;
    }

    private static void ShowEnabled(ParityButton button, Border surface, TextBlock label, string style)
    {
        surface.Background = button.IsEnabled ? PickerColors.ControlFill : LibraryColors.DisabledControlFill;
        label.Style = AppResources.Style(button.IsEnabled ? style : "AnnotatorDisabledText");
        ContrastTheme.ButtonLook(surface, button.IsEnabled);
        if (ContrastTheme.IsOn && button.IsEnabled)
        {
            label.Foreground = ContrastTheme.Brush(ContrastColor.ButtonText);
        }
    }

    /// <summary>
    /// The Mac's borderless menu button (Menu with .menuStyle(.borderlessButton)): the title at 13 points and a small chevron,
    /// <paramref name="width"/> by 16 points. UI Automation reads it as a menu button with the title as its name.
    /// </summary>
    public static (DropDownButton Button, TextBlock Title, MacStack Row, MenuFlyout Menu) Menu(string title, double width)
    {
        TextBlock text = Label(title, 13, "AnnotatorPrimaryText");
        FrameworkElement chevron = ChevronDown("AnnotatorGlyph");
        var row = new MacStack { Orientation = Orientation.Horizontal, Spacing = 5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(text);
        row.Children.Add(chevron);
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedLeft };
        var button = new DropDownButton
        {
            Style = AppResources.Style("AnnotatorMenuButton"),
            Content = row,
            Flyout = menu,
            Width = width,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(button, title);
        return (button, text, row, menu);
    }

    /// <summary>A small chevron.down, as the Mac's borderless menu draws its indicator.</summary>
    public static FrameworkElement ChevronDown(string style)
    {
        FrameworkElement glyph = Annotator.App.Icons.Glyphs.ForSymbol("chevron.down", 10, style);
        glyph.VerticalAlignment = VerticalAlignment.Center;
        return glyph;
    }

    /// <summary>Shows a menu button as unavailable or available, with gray words and chevron when unavailable.</summary>
    public static void ShowMenuEnabled(DropDownButton button, TextBlock title, Panel row, bool enabled)
    {
        button.IsEnabled = enabled;
        title.Style = AppResources.Style(enabled ? "AnnotatorPrimaryText" : "AnnotatorDisabledText");
        row.Children[1] = ChevronDown(enabled ? "AnnotatorGlyph" : "AnnotatorGlyphDisabled");
    }

    /// <summary>A menu item; a disabled one says so through its state, as the Mac's menus dim commands that cannot run.</summary>
    public static MenuFlyoutItem Item(string text, Action? action, bool enabled = true)
    {
        var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled && action is not null };
        if (action is not null)
        {
            item.Click += (_, _) => action();
        }
        return item;
    }

    public static SolidColorBrush Transparent => new(Microsoft.UI.Colors.Transparent);

    public static Windows.UI.Text.FontWeight Medium => FontWeights.Medium;
}
