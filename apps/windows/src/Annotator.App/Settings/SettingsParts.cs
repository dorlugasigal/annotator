using Annotator.App.Pickers;
using Annotator.App.Shell;
using Annotator.App.Toolbar;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinColor = Windows.UI.Color;
using WinRect = Windows.Foundation.Rect;

namespace Annotator.App.Settings;

/// <summary>
/// UI Automation frames for Settings: from the layout like <see cref="AutomationFrames"/>, but not cut at the window's
/// edges. AppKit reports the frames of a scroll view's whole content, including what lies below the visible part, and so
/// does Settings (WIN-PAR-06, the reference's default-size states).
/// </summary>
internal static class SettingsFrames
{
    public static WinRect Of(FrameworkElement element, WinRect fallback) =>
        Of(element, new WinRect(0, 0, element.ActualWidth, element.ActualHeight), fallback);

    public static WinRect Of(FrameworkElement element, WinRect box, WinRect fallback)
    {
        if (!element.IsLoaded || element.XamlRoot is not { } root || element.Visibility == Visibility.Collapsed)
        {
            return fallback;
        }
        WinRect dips;
        try
        {
            dips = element.TransformToVisual(null).TransformBounds(box);
        }
        catch (ArgumentException)
        {
            return fallback;
        }
        double scale = root.RasterizationScale;
        double left = Math.Round(dips.Left * scale), top = Math.Round(dips.Top * scale);
        double right = Math.Max(left, Math.Round(dips.Right * scale)), bottom = Math.Max(top, Math.Round(dips.Bottom * scale));
        return new WinRect(left, top, right - left, bottom - top);
    }
}

/// <summary>
/// Colors of Settings from the Mac capture (reference/ux-reference/macos/settings and design-values.json) for the app's theme,
/// resolved when a page is built; the window builds its pages again when the theme changes. In a Windows contrast theme each
/// takes the theme's color for its role instead (WIN-UI-07): cards and tabs are the window color inside window-text outlines,
/// links are the link color, and status colors are the window text color.
/// </summary>
internal static class SettingsLook
{
    public static bool Dark => PickerColors.Dark;

    /// <summary>The tab view's box and the Form's cards (#F7F7F7 and #252525 in the capture).</summary>
    public static SolidColorBrush Panel => ContrastTheme.Pick(ContrastColor.Window, Solid(Dark ? 0x25 : 0xF7));

    /// <summary>The tab view's outline (#E6E6E6 and #404040).</summary>
    public static SolidColorBrush PanelStroke => ContrastTheme.Pick(ContrastColor.WindowText, Solid(Dark ? 0x40 : 0xE6));

    /// <summary>A separator between Form rows (#EBEBEB and #2F2F2F).</summary>
    public static SolidColorBrush RowSeparator => ContrastTheme.Pick(ContrastColor.WindowText, Solid(Dark ? 0x2F : 0xEB));

    /// <summary>The window background (windowBackgroundColor).</summary>
    public static Brush Window => AppResources.Brush("AnnotatorWindowBackgroundBrush");

    /// <summary>The tab strip's track: 8% black or 9% white.</summary>
    public static SolidColorBrush TabTrack => ContrastTheme.Pick(ContrastColor.Window, PickerColors.Overlay(Dark ? 0.09 : 0.08));

    /// <summary>The selected tab: white, or 22% white on the dark theme; outlined in a contrast theme.</summary>
    public static SolidColorBrush TabSelected => ContrastTheme.Pick(ContrastColor.Window,
        Dark ? new SolidColorBrush(WinColor.FromArgb(0x38, 255, 255, 255)) : new SolidColorBrush(WinColor.FromArgb(255, 255, 255, 255)));

    /// <summary>linkColor; the contrast theme's link color.</summary>
    public static SolidColorBrush Link => ContrastTheme.Pick(ContrastColor.Hotlight,
        new SolidColorBrush(Dark ? WinColor.FromArgb(255, 0x41, 0x9C, 0xFF) : WinColor.FromArgb(255, 0x00, 0x68, 0xDA)));

    /// <summary>systemGreen.</summary>
    public static SolidColorBrush Green => ContrastTheme.Pick(ContrastColor.WindowText,
        new SolidColorBrush(Dark ? WinColor.FromArgb(255, 0x30, 0xD1, 0x58) : WinColor.FromArgb(255, 0x34, 0xC7, 0x59)));

    /// <summary>systemBlue (SwiftUI's Color.blue).</summary>
    public static WinColor Blue => ContrastTheme.Pick(ContrastColor.WindowText, Dark ? WinColor.FromArgb(255, 0x00, 0x91, 0xFF) : WinColor.FromArgb(255, 0x00, 0x88, 0xFF));

    /// <summary>systemOrange (SwiftUI's Color.orange).</summary>
    public static WinColor Orange => ContrastTheme.Pick(ContrastColor.WindowText, Dark ? WinColor.FromArgb(255, 0xFF, 0x92, 0x30) : WinColor.FromArgb(255, 0xFF, 0x8D, 0x28));

    /// <summary>systemRed.</summary>
    public static SolidColorBrush Red => ContrastTheme.Pick(ContrastColor.WindowText,
        new SolidColorBrush(Dark ? WinColor.FromArgb(255, 0xFF, 0x42, 0x45) : WinColor.FromArgb(255, 0xFF, 0x38, 0x3C)));

    /// <summary>CoffeeSupportView.accent: (0.58, 0.29, 0.10).</summary>
    public static WinColor Coffee => ContrastTheme.Pick(ContrastColor.WindowText, WinColor.FromArgb(255, 148, 74, 26));

    /// <summary>A tint of <paramref name="color"/> for a box behind text; the window color in a contrast theme.</summary>
    public static SolidColorBrush Tint(WinColor color, double opacity) => ContrastTheme.Pick(ContrastColor.Window, new SolidColorBrush(color) { Opacity = opacity });

    public static SolidColorBrush Solid(byte gray) => new(WinColor.FromArgb(255, gray, gray, gray));

    private static SolidColorBrush Solid(int gray) => Solid((byte)gray);
}

/// <summary>
/// Static text for UI Automation, as the Mac's AXStaticText (or AXHeading): one TextBlock in the Mac's line height, read
/// as one text element whose frame is the text's layout frame. Text that wraps gets a maximum width.
/// </summary>
internal sealed partial class FormText : Grid, IExactSize
{
    private Windows.Foundation.Size? _exact;

    public FormText(string text, double size, string style = "AnnotatorPrimaryText", Windows.UI.Text.FontWeight? weight = null,
                    double? wrapWidth = null, bool heading = false)
    {
        LineHeight = SettingsMetrics.LineHeight(size);
        Block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            Style = AppResources.Style(style),
            LineHeight = LineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextWrapping = wrapWidth is null ? TextWrapping.NoWrap : TextWrapping.Wrap,
            IsTextSelectionEnabled = false,
        };
        AutomationProperties.SetAccessibilityView(Block, AccessibilityView.Raw);
        if (wrapWidth is { } width)
        {
            // Wrapped text is as wide as its longest line, as AppKit reports a wrapped Text's frame.
            Block.MaxWidth = width;
        }
        Children.Add(Block);
        IsHeading = heading;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        if (heading)
        {
            AutomationProperties.SetHeadingLevel(this, AutomationHeadingLevel.Level2);
        }
    }

    public TextBlock Block { get; }

    public bool IsHeading { get; }

    /// <summary>The Mac's line height for the text's size.</summary>
    public double LineHeight { get; }

    /// <summary>The text's exact height: whole lines of <see cref="LineHeight"/>, or the minimum height when taller.</summary>
    public Windows.Foundation.Size? ExactSize => _exact;

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        Windows.Foundation.Size desired = base.MeasureOverride(availableSize);
        // The text's rounded height divided into whole lines; a Label's icon can be taller than one line.
        double height = Math.Max(1, Math.Round(Block.DesiredSize.Height / Block.LineHeight)) * Block.LineHeight;
        foreach (UIElement child in Children)
        {
            if (child != Block && child is FrameworkElement { Height: var icon } && !double.IsNaN(icon))
            {
                height = Math.Max(height, icon);
            }
        }
        _exact = new Windows.Foundation.Size(desired.Width, height);
        return desired;
    }

    public string Text
    {
        get => Block.Text;
        set
        {
            string before = Block.Text;
            Block.Text = value;
            if (before != value && FrameworkElementAutomationPeer.FromElement(this) is AutomationPeer peer)
            {
                peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, before, value);
            }
        }
    }

    /// <summary>The frame UI Automation reports, in this element's coordinates; empty for the whole element.</summary>
    public WinRect Bounds { get; set; }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed partial class Peer(FormText owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => "TextBlock";

        protected override string GetNameCore() => owner.Block.Text;

        protected override string GetLocalizedControlTypeCore() => owner.IsHeading ? "heading" : base.GetLocalizedControlTypeCore();

        protected override bool IsControlElementCore() => true;

        protected override IList<AutomationPeer> GetChildrenCore() => new AutomationPeerList();

        protected override WinRect GetBoundingRectangleCore() =>
            owner.Bounds is { Width: > 0, Height: > 0 } bounds
                ? SettingsFrames.Of(owner, bounds, base.GetBoundingRectangleCore())
                : SettingsFrames.Of(owner, base.GetBoundingRectangleCore());
    }
}

/// <summary>The Mac's text metrics in Settings: line heights per SF size from the reference frames (D-4 keeps Segoe UI).</summary>
internal static class SettingsMetrics
{
    public static double LineHeight(double size) => size switch
    {
        <= 13 => ToolbarParts.LineHeight(size),
        <= 15 => 18,
        <= 17 => 20.5,
        <= 22 => 26,
        _ => 31,
    };
}

/// <summary>How a <see cref="SettingsButton"/> looks (the Mac's button styles).</summary>
internal enum ButtonLook
{
    /// <summary>.bordered: a rounded 8% fill.</summary>
    Bordered,

    /// <summary>.borderedProminent: the accent fill with white words.</summary>
    Prominent,

    /// <summary>.borderless: words and an icon in the secondary color.</summary>
    Borderless,

    /// <summary>.link: link-colored words; UI Automation reads a link.</summary>
    Link,
}

/// <summary>
/// A Settings push button with the Mac's size and look: bordered, prominent, borderless or link. UI Automation reads a
/// button (a link for <see cref="ButtonLook.Link"/>) named by its title, framed like the Mac's button.
/// </summary>
internal sealed partial class SettingsButton : Button
{
    private readonly ButtonLook _look;
    private readonly Border _surface;
    private readonly TextBlock _title;
    private readonly FrameworkElement? _icon;
    private readonly string _iconSymbol = string.Empty;
    private readonly double _fontSize;
    private readonly SolidColorBrush? _tint;

    /// <param name="width">The Mac's width for the title, or null for the title's own width plus the Mac's padding.</param>
    /// <param name="tint">The prominent fill (the accent by default) or the borderless words' color.</param>
    public SettingsButton(string title, ButtonLook look, double? width = null, double height = 24, string? symbol = null,
                          double fontSize = 13, WinColor? tint = null)
    {
        _look = look;
        _fontSize = fontSize;
        Style = AppResources.Style("AnnotatorPlainButton");
        Height = height;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        _tint = tint is { } color ? new SolidColorBrush(color) : null;
        _title = new TextBlock
        {
            Text = title,
            FontSize = fontSize,
            LineHeight = SettingsMetrics.LineHeight(fontSize),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            IsHitTestVisible = false,
        };
        AutomationProperties.SetAccessibilityView(_title, AccessibilityView.Raw);
        var row = new MacStack { Orientation = Orientation.Horizontal, Spacing = look == ButtonLook.Borderless ? 5 : 7, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (symbol is not null)
        {
            _iconSymbol = symbol;
            _icon = Icon(symbol);
            row.Children.Add(_icon);
        }
        row.Children.Add(_title);
        _surface = new Border
        {
            CornerRadius = new CornerRadius(look is ButtonLook.Bordered or ButtonLook.Prominent ? (height >= 24 ? 6 : 5) : 0),
            Child = row,
        };
        Content = _surface;
        CornerRadius = _surface.CornerRadius;
        Width = width ?? NaturalWidth(title, look, fontSize, symbol is not null);
        AutomationProperties.SetName(this, title);
        IsEnabledChanged += (_, _) => ShowEnabled();
        Loaded += (_, _) => ShowEnabled();
        ShowEnabled();
    }

    public string Title => _title.Text;

    /// <summary>Changes the title, the name UI Automation reads and the width: <paramref name="width"/>, or the natural one.</summary>
    public void Retitle(string title, double? width = null)
    {
        if (_title.Text == title)
        {
            return;
        }
        _title.Text = title;
        AutomationProperties.SetName(this, title);
        Width = width ?? NaturalWidth(title, _look, _fontSize, _icon is not null);
    }

    /// <summary>The width SwiftUI gives a regular button: its title plus 12 points on each side, in half points.</summary>
    public static double NaturalWidth(string title, ButtonLook look, double fontSize = 13, bool icon = false)
    {
        var probe = new TextBlock { Text = title, FontSize = fontSize };
        probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double padding = look is ButtonLook.Link or ButtonLook.Borderless ? 0 : 24;
        return Math.Ceiling((probe.DesiredSize.Width + padding + (icon ? 23 : 0)) * 2) / 2;
    }

    private FrameworkElement Icon(string symbol)
    {
        string style = !IsEnabled ? "AnnotatorGlyphDisabled"
            : _look == ButtonLook.Prominent ? "AnnotatorGlyphOnColor"
            : _look == ButtonLook.Borderless ? "AnnotatorGlyphSecondary"
            : "AnnotatorGlyph";
        FrameworkElement glyph = Icons.Glyphs.ForSymbol(symbol, 16, style);
        glyph.VerticalAlignment = VerticalAlignment.Center;
        return glyph;
    }

    private void ShowEnabled()
    {
        bool on = IsEnabled;
        switch (_look)
        {
            case ButtonLook.Bordered when _tint is { } tint && on && !ContrastTheme.IsOn:
                // A bordered button with .tint: the tint at 5% behind words in the tint (the capture's permission buttons).
                _surface.Background = new SolidColorBrush(tint.Color) { Opacity = 0.05 };
                _title.Foreground = tint;
                break;
            case ButtonLook.Bordered:
                _surface.Background = on ? PickerColors.ControlFill : Library.LibraryColors.DisabledControlFill;
                _title.ClearValue(TextBlock.ForegroundProperty);
                _title.Style = AppResources.Style(on ? "AnnotatorPrimaryText" : "AnnotatorDisabledText");
                ContrastTheme.ButtonLook(_surface, on);
                if (ContrastTheme.IsOn && on)
                {
                    _title.Foreground = ContrastTheme.Brush(ContrastColor.ButtonText);
                }
                break;
            case ButtonLook.Prominent:
                // A contrast theme fills the default button with Highlight and HighlightText words.
                _surface.Background = on ? ContrastTheme.Pick(ContrastColor.Highlight, _tint ?? AppResources.Brush("AnnotatorAccentBrush")) : Library.LibraryColors.DisabledControlFill;
                _title.Foreground = on ? ContrastTheme.Pick(ContrastColor.HighlightText, new SolidColorBrush(Microsoft.UI.Colors.White)) : AppResources.Brush("AnnotatorDisabledLabelBrush");
                if (!on)
                {
                    ContrastTheme.ButtonLook(_surface, enabled: false);
                }
                break;
            case ButtonLook.Borderless:
                _title.Style = AppResources.Style(on ? "AnnotatorSecondaryText" : "AnnotatorDisabledText");
                break;
            case ButtonLook.Link:
                _title.Foreground = on ? SettingsLook.Link : AppResources.Brush("AnnotatorDisabledLabelBrush");
                break;
        }
        if (_icon is not null && _surface.Child is MacStack row)
        {
            row.Children[0] = Icon(_iconSymbol);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed partial class Peer(SettingsButton owner) : ButtonAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() =>
            owner._look == ButtonLook.Link ? AutomationControlType.Hyperlink : AutomationControlType.Button;

        protected override string GetClassNameCore() => owner._look == ButtonLook.Link ? "Hyperlink" : "Button";

        protected override IList<AutomationPeer> GetChildrenCore() => new AutomationPeerList();

        protected override WinRect GetBoundingRectangleCore() => SettingsFrames.Of(owner, base.GetBoundingRectangleCore());
    }
}

/// <summary>
/// The Mac's Menu shown as a bordered button with a chevron (Profiles, Support info): a menu button in UI Automation with the
/// Mac's help, framed like the Mac's.
/// </summary>
internal sealed partial class SettingsMenuButton : DropDownButton
{
    public SettingsMenuButton(string title, double width, string help)
    {
        Style = AppResources.Style("AnnotatorPlainButton");
        Width = width;
        Height = 24;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        CornerRadius = new CornerRadius(6);
        var title_ = new TextBlock
        {
            Text = title,
            FontSize = 13,
            LineHeight = 16,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            VerticalAlignment = VerticalAlignment.Center,
            Style = AppResources.Style("AnnotatorPrimaryText"),
            Margin = new Thickness(16, 0, 0, 0),
            IsHitTestVisible = false,
        };
        AutomationProperties.SetAccessibilityView(title_, AccessibilityView.Raw);
        FrameworkElement chevron = Icons.Glyphs.ForSymbol("chevron.down", 12, "AnnotatorGlyph", strokeWidth: 2.5);
        chevron.HorizontalAlignment = HorizontalAlignment.Right;
        chevron.VerticalAlignment = VerticalAlignment.Center;
        chevron.Margin = new Thickness(0, 0, 11, 0);
        var content = new Grid { Background = PickerColors.ControlFill, CornerRadius = new CornerRadius(6), IsHitTestVisible = false };
        if (ContrastTheme.IsOn)
        {
            // The contrast theme's button look: ButtonFace inside a ButtonText outline.
            content.BorderBrush = ContrastTheme.Brush(ContrastColor.ButtonText);
            content.BorderThickness = new Thickness(1);
            title_.Foreground = ContrastTheme.Brush(ContrastColor.ButtonText);
        }
        content.Children.Add(title_);
        content.Children.Add(chevron);
        Content = content;
        Menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
        Flyout = Menu;
        AutomationProperties.SetName(this, title);
        AutomationProperties.SetHelpText(this, help);
        ToolTipService.SetToolTip(this, help);
    }

    public MenuFlyout Menu { get; }

    public void Add(string text, Action? action)
    {
        var item = new MenuFlyoutItem { Text = text, IsEnabled = action is not null };
        if (action is not null)
        {
            item.Click += (_, _) => action();
        }
        Menu.Items.Add(item);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed partial class Peer(SettingsMenuButton owner) : DropDownButtonAutomationPeer(owner)
    {
        protected override IList<AutomationPeer> GetChildrenCore() => new AutomationPeerList();

        protected override WinRect GetBoundingRectangleCore() => SettingsFrames.Of(owner, base.GetBoundingRectangleCore());
    }
}

/// <summary>
/// A Mac Label in Settings: an icon and words read as one static text whose frame covers both, as AppKit reports a
/// SwiftUI Label.
/// </summary>
internal static class SettingsLabel
{
    public static FormText Make(string symbol, string text, double size, string textStyle, string glyphStyle, double iconSize,
                                double spacing, Windows.UI.Text.FontWeight? weight = null, Brush? foreground = null, double? wrapWidth = null,
                                WinColor? iconColor = null)
    {
        var label = new FormText(text, size, textStyle, weight, wrapWidth);
        if (foreground is not null)
        {
            label.Block.Foreground = foreground;
        }
        FrameworkElement icon = iconColor is { } color ? SettingsIcons.Make(symbol, iconSize, color) : Icons.Glyphs.ForSymbol(symbol, iconSize, glyphStyle);
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        icon.VerticalAlignment = wrapWidth is null ? VerticalAlignment.Center : VerticalAlignment.Top;
        label.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(iconSize + spacing) });
        label.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        label.Children.Insert(0, icon);
        SetColumn(label.Block, 1);
        return label;
    }

    private static void SetColumn(FrameworkElement element, int column) => Grid.SetColumn(element, column);
}
