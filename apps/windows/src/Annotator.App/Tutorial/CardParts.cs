using System.Numerics;
using Annotator.App.Icons;
using Annotator.App.Shell;
using Annotator.App.Toolbar;
using Annotator.Workspace.FirstUse;
using Annotator.Workspace.Shortcuts;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using FontWeight = Windows.UI.Text.FontWeight;
using WinRect = Windows.Foundation.Rect;

namespace Annotator.App.Tutorial;

/// <summary>
/// The tutorial cards' building blocks in the Mac's SwiftUI sizes: text styles, keycaps, the disclosure, the information
/// cards and the check badge. Colors come from the theme the card shows when it builds them; the card builds its content
/// again when the theme changes.
/// </summary>
internal static class CardParts
{
    public const string Primary = "AnnotatorPrimaryText";
    public const string Secondary = "AnnotatorSecondaryText";

    /// <summary>The Mac's one-line height for SwiftUI's text styles: title 26, title3 18, body and headline 16, callout 15, caption 13.</summary>
    public static double LineHeight(double size) => size switch
    {
        <= 10 => 13,
        <= 12 => 15,
        <= 13 => 16,
        <= 15 => 18,
        <= 22 => 26,
        _ => Math.Ceiling(size * 1.2),
    };

    /// <summary>Static text in the Mac's line height, wrapping across the page.</summary>
    public static TextBlock Text(string text, double size, string style = Primary, FontWeight? weight = null, bool wrap = true) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight ?? FontWeights.Normal,
        Style = AppResources.Style(style),
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        LineHeight = LineHeight(size),
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    /// <summary>The theme's label color at <paramref name="opacity"/> (SwiftUI Color.primary.opacity).</summary>
    public static SolidColorBrush PrimaryTint(bool dark, double opacity) =>
        new(dark ? Color.FromArgb(Alpha(opacity), 255, 255, 255) : Color.FromArgb(Alpha(opacity), 0, 0, 0));

    /// <summary>A line in the label color at <paramref name="opacity"/>; the window text color in a contrast theme.</summary>
    public static SolidColorBrush Line(bool dark, double opacity) => ContrastTheme.Pick(ContrastColor.WindowText, PrimaryTint(dark, opacity));

    /// <summary>A fill in the label color at <paramref name="opacity"/>; the window color in a contrast theme.</summary>
    public static SolidColorBrush Fill(bool dark, double opacity) => ContrastTheme.Pick(ContrastColor.Window, PrimaryTint(dark, opacity));

    /// <summary>The accent (SwiftUI Color.accentColor) at <paramref name="opacity"/>; the whole Highlight color in a contrast theme.</summary>
    public static SolidColorBrush Accent(double opacity)
    {
        Color accent = AppResources.Brush("AnnotatorAccentBrush") is SolidColorBrush brush ? brush.Color : Color.FromArgb(255, 0, 122, 255);
        return new SolidColorBrush(Color.FromArgb(ContrastTheme.IsOn ? (byte)255 : Alpha(opacity * accent.A / 255.0), accent.R, accent.G, accent.B));
    }

    /// <summary>A faint accent fill behind words; the window color in a contrast theme, which outlines it instead.</summary>
    public static SolidColorBrush AccentFill(double opacity) => ContrastTheme.Pick(ContrastColor.Window, Accent(opacity));

    /// <summary>SwiftUI's green (design-values.json: #34C759 light, #30D158 dark); the window text color in a contrast theme.</summary>
    public static Color Green(bool dark) => ContrastTheme.Pick(ContrastColor.WindowText, dark ? Color.FromArgb(255, 0x30, 0xD1, 0x58) : Color.FromArgb(255, 0x34, 0xC7, 0x59));

    public static byte Alpha(double opacity) => (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);

    /// <summary>
    /// A Mac keycap (ShortcutKeycaps.swift): monospaced 13-point semibold text, 8 points of padding, at least 28 by 28, a
    /// 6-point corner, an 18% outline and a hard 2-point shadow at 8%. UI Automation reads it as one static text named by
    /// <paramref name="name"/> when given, framed like the Mac's keycap with its outline's outer half point, or as its text,
    /// unless <paramref name="hidden"/> (keys of a shortcut read as one element).
    /// </summary>
    public static FrameworkElement Keycap(string text, bool dark, string? name = null, bool hidden = false)
    {
        var label = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Style = AppResources.Style(Primary),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            LineHeight = 16,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        if (hidden || name is not null)
        {
            AutomationProperties.SetAccessibilityView(label, AccessibilityView.Raw);
        }
        var face = new Border
        {
            MinWidth = 28,
            Height = 28,
            Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(6),
            Background = AppResources.Brush("AnnotatorWindowBackgroundBrush"),
            BorderBrush = Line(dark, 0.18),
            BorderThickness = new Thickness(1),
            Child = label,
        };
        Grid key;
        if (name is not null)
        {
            var named = new AccessibleText(name);
            named.SizeChanged += (_, args) => named.Bounds = new WinRect(-0.5, -0.5, args.NewSize.Width + 1, args.NewSize.Height + 1);
            key = named;
        }
        else
        {
            key = new Grid();
        }
        key.VerticalAlignment = VerticalAlignment.Center;
        // The hard shadow; a contrast theme has none.
        key.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(ContrastTheme.IsOn ? (byte)0 : Alpha(0.08), 0, 0, 0)),
            Margin = new Thickness(0, 2, 0, -2),
            IsHitTestVisible = false,
        });
        key.Children.Add(face);
        return key;
    }

    /// <summary>The key names of a Windows shortcut, modifiers first: "Ctrl", "Shift", "2".</summary>
    public static IReadOnlyList<string> Keys(ShortcutChord chord)
    {
        var keys = new List<string>();
        if (chord.Modifiers.HasFlag(ChordModifiers.Ctrl))
        {
            keys.Add("Ctrl");
        }
        if (chord.Modifiers.HasFlag(ChordModifiers.Alt))
        {
            keys.Add("Alt");
        }
        if (chord.Modifiers.HasFlag(ChordModifiers.Shift))
        {
            keys.Add("Shift");
        }
        if (chord.Modifiers.HasFlag(ChordModifiers.Win))
        {
            keys.Add("Win");
        }
        keys.Add(chord.Key.ToUpperInvariant());
        return keys;
    }

    /// <summary>
    /// A shortcut's keycaps 5 points apart (Mac ShortcutKeycaps), read by UI Automation as one element named like
    /// "Ctrl + Shift + 2", as Settings' keycaps read; each key names itself in a tooltip.
    /// </summary>
    public static FrameworkElement Shortcut(ShortcutChord chord, bool dark)
    {
        IReadOnlyList<string> keys = Keys(chord);
        string spoken = string.Join(" + ", keys);
        var row = new MacStack { Orientation = Orientation.Horizontal, Spacing = 5 };
        foreach (string key in keys)
        {
            FrameworkElement cap = Keycap(key, dark, hidden: true);
            ToolTipService.SetToolTip(cap, key);
            row.Children.Add(cap);
        }
        var group = new CardShortcut { VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(group, spoken);
        AutomationProperties.SetHelpText(group, spoken);
        ToolTipService.SetToolTip(group, spoken);
        group.Children.Add(row);
        return group;
    }

    /// <summary>A Mac-sized push button (regular size, 24 points tall); prominent uses the accent, like borderedProminent.</summary>
    public static Button PushButton(string title, bool prominent = false)
    {
        var button = new Button
        {
            Content = title,
            FontSize = 13,
            Height = 24,
            MinHeight = 0,
            MinWidth = 0,
            Padding = new Thickness(12, 0, 12, 1),
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (prominent)
        {
            button.Style = AppResources.Style("AccentButtonStyle");
            button.Height = 24;
            button.MinHeight = 0;
            button.Padding = new Thickness(12, 0, 12, 1);
        }
        AutomationProperties.SetName(button, title);
        return button;
    }

    /// <summary>
    /// A plain check box with a 13-point label (Mac Toggle in the default macOS checkbox style). Windows draws a 20-point
    /// box; negative margins give it the Mac's <paramref name="height"/> in the layout, so the rows around it keep the
    /// Mac's places.
    /// </summary>
    public static CheckBox CheckBox(string title, double height = 16) => new()
    {
        Content = title,
        FontSize = 13,
        Height = 20,
        Margin = new Thickness(0, -(20 - height) / 2, 0, -(20 - height) / 2),
        MinHeight = 0,
        MinWidth = 0,
        Padding = new Thickness(6, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    /// <summary>A Mac SF Symbol as its Windows icon, read as an image with the Mac's label and the symbol's name.</summary>
    public static FrameworkElement Symbol(string symbol, double size, string style, string label, Color? tint = null)
    {
        var image = new AccessibleImage(label, symbol, help: null)
        {
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        FrameworkElement glyph = Glyphs.ForSymbol(symbol, size, style);
        if (tint is { } color && glyph is Viewbox { Child: Canvas { Children: [Microsoft.UI.Xaml.Shapes.Path path, ..] } })
        {
            path.Stroke = new SolidColorBrush(color);
            if (path.Fill is not null)
            {
                path.Fill = new SolidColorBrush(color);
            }
        }
        image.Children.Add(glyph);
        return image;
    }

    /// <summary>Sets the element's scale around its center (SwiftUI scaleEffect).</summary>
    public static void ScaleAroundCenter(UIElement element, double scale)
    {
        if (element is FrameworkElement framework)
        {
            element.CenterPoint = new Vector3((float)(framework.ActualWidth / 2), (float)(framework.ActualHeight / 2), 0);
        }
        element.Scale = new Vector3((float)scale, (float)scale, 1);
    }
}

/// <summary>
/// The Mac's DisclosureGroup: a chevron and a title that open text below them. UI Automation reads it as a group with the
/// ExpandCollapse pattern (the Mac's disclosure triangle) named by its title.
/// </summary>
internal sealed partial class CardDisclosure : Button
{
    private readonly FrameworkElement _chevron;
    private bool _expanded;

    public CardDisclosure(string title, string identifier, FrameworkElement details, bool expanded)
    {
        Details = details;
        Style = AppResources.Style("AnnotatorPlainButton");
        HorizontalAlignment = HorizontalAlignment.Left;
        Padding = new Thickness(0);
        _chevron = Glyphs.ForSymbol("chevron.right", 10, "AnnotatorGlyphSecondary", 2.5);
        _chevron.VerticalAlignment = VerticalAlignment.Center;
        var label = new MacStack { Orientation = Orientation.Horizontal, Spacing = 6 };
        label.Children.Add(_chevron);
        TextBlock text = CardParts.Text(title, 13, wrap: false);
        // The disclosure reads as one element named by its title.
        AutomationProperties.SetAccessibilityView(text, AccessibilityView.Raw);
        label.Children.Add(text);
        Content = label;
        AutomationProperties.SetName(this, title);
        AutomationProperties.SetAutomationId(this, identifier);
        Click += (_, _) => Expanded = !Expanded;
        _chevron.Loaded += (_, _) => Turn();
        Expanded = expanded;
    }

    /// <summary>The text the disclosure opens; the page shows it below the disclosure while expanded.</summary>
    public FrameworkElement Details { get; }

    public event Action<bool>? ExpandedChanged;

    public bool Expanded
    {
        get => _expanded;
        set
        {
            bool old = _expanded;
            _expanded = value;
            Details.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            Turn();
            if (old != value)
            {
                if (FrameworkElementAutomationPeer.FromElement(this) is AutomationPeer peer)
                {
                    peer.RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty,
                        old ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed,
                        value ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed);
                }
                ExpandedChanged?.Invoke(value);
            }
        }
    }

    private void Turn()
    {
        _chevron.CenterPoint = new Vector3((float)(_chevron.ActualWidth / 2), (float)(_chevron.ActualHeight / 2), 0);
        _chevron.Rotation = _expanded ? 90 : 0;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed partial class Peer(CardDisclosure owner) : ButtonAutomationPeer(owner), IExpandCollapseProvider
    {
        public ExpandCollapseState ExpandCollapseState => owner.Expanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;

        public void Expand() => owner.Expanded = true;

        public void Collapse() => owner.Expanded = false;

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

        protected override string GetClassNameCore() => "Disclosure";

        protected override object? GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.ExpandCollapse ? this : base.GetPatternCore(patternInterface);

        protected override WinRect GetBoundingRectangleCore() => AutomationFrames.Of(owner, base.GetBoundingRectangleCore());
    }
}

/// <summary>
/// The check badge (Mac TutorialCheckBadge): a green check drawing itself in a 22-point circle that grows
/// from 60%, then a word. UI Automation reads one element named by <paramref name="spoken"/>.
/// </summary>
internal sealed class CheckBadge
{
    private const double CheckLength = 1.6;
    private readonly Ellipse _circle;
    private readonly Polyline _check;
    private readonly double _length;

    public CheckBadge(string word, string spoken, bool dark)
    {
        Color green = CardParts.Green(dark);
        _circle = new Ellipse
        {
            Width = 22,
            Height = 22,
            Fill = ContrastTheme.Pick(ContrastColor.Window, new SolidColorBrush(Color.FromArgb(CardParts.Alpha(0.16), green.R, green.G, green.B))),
        };
        // The check in the 10-point square inside 6 points of padding.
        var points = new Windows.Foundation.Point[] { new(6, 6 + 5 + 0.4), new(6 + 3.8, 6 + 10 - 0.8), new(16, 6 + 1.2) };
        _length = Distance(points[0], points[1]) + Distance(points[1], points[2]);
        _check = new Polyline
        {
            Stroke = new SolidColorBrush(green),
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeDashCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        foreach (Windows.Foundation.Point point in points)
        {
            _check.Points.Add(point);
        }
        var mark = new Grid { Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center };
        mark.Children.Add(_circle);
        mark.Children.Add(_check);
        var row = new MacStack { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(mark);
        TextBlock text = CardParts.Text(word, 12, CardParts.Secondary, FontWeights.Medium, wrap: false);
        text.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAccessibilityView(text, AccessibilityView.Raw);
        row.Children.Add(text);
        Root = new AccessibleGroup(spoken) { VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAccessibilityView(row, AccessibilityView.Raw);
        Root.Children.Add(row);
        Draw(1);
    }

    public AccessibleGroup Root { get; }

    /// <summary>How much of the check is drawn, from 0 to 1; the circle grows with it.</summary>
    public void Draw(double drawn)
    {
        drawn = Math.Clamp(drawn, 0, 1);
        CardParts.ScaleAroundCenter(_circle, 0.6 + 0.4 * Math.Min(1, drawn * CheckLength));
        _circle.CenterPoint = new Vector3(11, 11, 0);
        double units = _length / _check.StrokeThickness;
        _check.StrokeDashArray = drawn >= 1 ? null : new DoubleCollection { Math.Max(0.0001, units * drawn), units * 2 };
        _check.Opacity = drawn > 0 ? 1 : 0;
    }

    private static double Distance(Windows.Foundation.Point a, Windows.Foundation.Point b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}

/// <summary>
/// One information card (Mac TutorialInfoCard): a tinted 32-point icon tile, a headline and detail with optional notes, and a button
/// or the ready badge on the right; 12 points of padding on a 4% fill with an 8% outline and a 10-point corner.
/// </summary>
internal sealed class InfoCard
{
    public InfoCard(string symbol, string symbolLabel, string title, string detail, IReadOnlyList<string> notes, bool ready, bool dark,
                    FrameworkElement trailing)
    {
        Color tint = ready ? CardParts.Green(dark) : CardParts.Accent(1).Color;
        var tile = new Grid { Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center };
        tile.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = ContrastTheme.Pick(ContrastColor.Window, new SolidColorBrush(Color.FromArgb(CardParts.Alpha(0.14), tint.R, tint.G, tint.B))),
        });
        FrameworkElement icon = CardParts.Symbol(symbol, 18, "AnnotatorGlyphAccent", symbolLabel, ready ? tint : null);
        tile.Children.Add(icon);
        var texts = new MacStack { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(CardParts.Text(title, 13, weight: FontWeights.SemiBold));
        texts.Children.Add(CardParts.Text(detail, 12, CardParts.Secondary));
        foreach (string note in notes)
        {
            texts.Children.Add(CardParts.Text(note, 10, CardParts.Secondary));
        }
        trailing.VerticalAlignment = VerticalAlignment.Center;
        trailing.Margin = new Thickness(8, 0, 0, 0);
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(tile);
        Grid.SetColumn(texts, 1);
        row.Children.Add(texts);
        Grid.SetColumn(trailing, 2);
        row.Children.Add(trailing);
        Root = new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(10),
            Background = CardParts.Fill(dark, 0.04),
            BorderBrush = CardParts.Line(dark, 0.08),
            BorderThickness = new Thickness(1),
            Child = row,
        };
    }

    public Border Root { get; }

    /// <summary>The card settling in (Mac cardArrival): opacity, a downward offset in points and a slight scale.</summary>
    public void Arrive((double Opacity, double Offset, double Scale) arrival)
    {
        Root.Opacity = arrival.Opacity;
        Root.Translation = new Vector3(0, (float)arrival.Offset, 0);
        CardParts.ScaleAroundCenter(Root, arrival.Scale);
    }
}

/// <summary>
/// A shortcut's keycaps as one element (the Mac's ShortcutKeycaps view, an unknown role with its label): a custom control
/// named with the keys, framed like the Mac's keycaps with their outline's outer half point.
/// </summary>
internal sealed partial class CardShortcut : Grid
{
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed partial class Peer(CardShortcut owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;

        protected override string GetLocalizedControlTypeCore() => "shortcut";

        protected override string GetClassNameCore() => "ShortcutKeycaps";

        protected override bool IsControlElementCore() => true;

        protected override IList<AutomationPeer> GetChildrenCore() => new AutomationPeerList();

        protected override WinRect GetBoundingRectangleCore() =>
            AutomationFrames.Of(owner, new WinRect(-0.5, -0.5, owner.ActualWidth + 1, owner.ActualHeight + 1), base.GetBoundingRectangleCore());
    }
}