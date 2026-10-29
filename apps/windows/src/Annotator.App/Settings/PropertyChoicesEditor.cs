using Annotator.App.Shell;
using Annotator.App.Toolbar;
using Annotator.Core;
using Annotator.Workspace;
using Annotator.Workspace.Toolbar;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using CoreTextAlignment = Annotator.Core.TextAlignment;
using Glyphs = Annotator.App.Icons.Glyphs;

namespace Annotator.App.Settings;

/// <summary>
/// Port of the Mac PropertyChoicesEditor, "Your properties": each property with a few choices shows as one button that
/// steps through them, or as all its choices side by side. Both ways are drawn with the toolbar's own controls; click one
/// to use it. The selected way has the accent ring and a check mark.
/// </summary>
internal sealed class PropertyChoicesEditor
{
    private const double OptionHeight = 75;
    private readonly WorkspaceModel _model;
    private readonly HyperlinkButton _reset;
    private readonly Dictionary<(PropertyChoice Choice, bool All), (Border Ring, TextBlock Caption, FrameworkElement Check, ParityButton Button)> _options = [];
    private readonly Dictionary<PropertyChoice, Grid> _cards = [];
    private readonly SolidColorBrush _ink = new(PropertiesInk.Color());

    public PropertyChoicesEditor(WorkspaceModel model)
    {
        _model = model;
        Root = new AccessibleGroup(string.Empty, "property-choices") { HorizontalAlignment = HorizontalAlignment.Stretch };
        Root.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = SettingsColors.Primary(0.03),
            BorderBrush = SettingsColors.Primary(0.06),
            BorderThickness = new Thickness(1),
        });
        var layout = new MacStack { Orientation = Orientation.Vertical, Spacing = 14, Margin = new Thickness(16) };
        var header = new Grid();
        var titles = new MacStack { Orientation = Orientation.Vertical, Spacing = 3, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 200, 0) };
        titles.Children.Add(SettingsText.Static("Your properties", 15, "AnnotatorPrimaryText", FontWeights.SemiBold));
        titles.Children.Add(SettingsText.Static(
            "One button saves room: click it to go to the next choice. All choices puts them side by side, so each one is a click away.",
            12, "AnnotatorSecondaryText"));
        header.Children.Add(titles);
        _reset = new HyperlinkButton
        {
            Content = "Use one button everywhere",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Padding = new Thickness(0),
            FontSize = 13,
        };
        AutomationProperties.SetAutomationId(_reset, "reset-property-choices");
        _reset.Click += (_, _) => _model.ResetPropertyChoices();
        header.Children.Add(_reset);
        layout.Children.Add(header);
        var cards = new FlowPanel { Spacing = 10, LineSpacing = 10 };
        foreach (PropertyChoice choice in PropertyChoice.AllCases)
        {
            cards.Children.Add(Card(choice));
        }
        layout.Children.Add(cards);
        Root.Children.Add(layout);
        Root.ActualThemeChanged += (_, _) => _ink.Color = PropertiesInk.Color();
    }

    public AccessibleGroup Root { get; }

    /// <summary>Shows which way each property is drawn now.</summary>
    public void Apply(WorkspaceSnapshot snapshot)
    {
        WorkspacePreferences preferences = snapshot.Preferences;
        _reset.IsEnabled = preferences.ExpandedPropertyChoices is not null;
        foreach (((PropertyChoice choice, bool all), (Border ring, TextBlock caption, FrameworkElement check, ParityButton button)) in _options)
        {
            bool selected = preferences.ShowsAllChoices(choice) == all;
            ring.BorderBrush = selected ? SettingsColors.AccentBrush() : SettingsColors.Primary(0.1);
            ring.BorderThickness = new Thickness(selected ? 2 : 1);
            caption.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            caption.Style = AppResources.Style(selected ? "AnnotatorPrimaryText" : "AnnotatorSecondaryText");
            check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            button.AccessibleSelected = selected;
            button.AccessibleValue = selected ? "Selected" : string.Empty;
            if (!SettingsText.Scaled)
            {
                button.Width = OptionWidth(choice, all, selected);
            }
        }
        if (!SettingsText.Scaled)
        {
            foreach ((PropertyChoice choice, Grid card) in _cards)
            {
                bool all = preferences.ShowsAllChoices(choice);
                card.Width = 10 + OptionWidth(choice, false, !all) + 8 + OptionWidth(choice, true, all) + 10;
            }
        }
    }

    /// <summary>
    /// An option's width on the Mac, set exactly so the cards line up with the Mac's: all choices are 36-point buttons 5 apart
    /// with 8 points of padding on each side; one button is the 52-point preview with its padding, or SF Pro's semibold
    /// "One button" and the check mark (72.5 points, D-4) while it's chosen.
    /// </summary>
    private static double OptionWidth(PropertyChoice choice, bool all, bool selected) =>
        all ? 16 + choice.ChoiceCount * 36 + (choice.ChoiceCount - 1) * 5 : selected ? 72.5 : 68;

    private FrameworkElement Card(PropertyChoice choice)
    {
        var card = new Grid { CornerRadius = new CornerRadius(12), Background = SettingsColors.Primary(0.03), Padding = new Thickness(10) };
        if (!SettingsText.Scaled)
        {
            // 10 points of padding, the 15-point title, 8 points and the options, so the next line starts where the Mac's does.
            card.Height = 10 + 15 + 8 + OptionHeight + 10;
        }
        card.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderBrush = SettingsColors.Primary(0.07),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(-10),
            IsHitTestVisible = false,
        });
        var stack = new MacStack { Orientation = Orientation.Vertical, Spacing = 8 };
        // SwiftUI's Label: UI Automation reads the symbol and the words as one static text.
        var title = new AccessibleElement(AutomationControlType.Text) { InSettings = true, HorizontalAlignment = HorizontalAlignment.Left };
        if (!SettingsText.Scaled)
        {
            title.Height = 15;
        }
        AutomationProperties.SetName(title, choice.Label);
        var label = new MacStack { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        FrameworkElement icon = Glyphs.ForSymbol(Symbol(choice), 12);
        icon.VerticalAlignment = VerticalAlignment.Center;
        label.Children.Add(icon);
        TextBlock words = SettingsText.Static(choice.Label, 12, "AnnotatorPrimaryText", FontWeights.SemiBold);
        words.VerticalAlignment = VerticalAlignment.Center;
        label.Children.Add(words);
        title.Children.Add(label);
        stack.Children.Add(title);
        var options = new MacStack { Orientation = Orientation.Horizontal, Spacing = 8 };
        options.Children.Add(Option(choice, all: false));
        options.Children.Add(Option(choice, all: true));
        stack.Children.Add(options);
        card.Children.Add(stack);
        _cards[choice] = card;
        var group = new AccessibleGroup(choice.Label, $"property-choice-{choice.RawValue}");
        group.Children.Add(card);
        return group;
    }

    private FrameworkElement Option(PropertyChoice choice, bool all)
    {
        string names = List(choice.Choices);
        // frame(minWidth: 52, minHeight: 44), then 8 and 6 points of padding around the background and ring.
        var preview = new Grid { Padding = new Thickness(8, 6, 8, 6), IsHitTestVisible = false };
        preview.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = SettingsColors.Window, Margin = new Thickness(-8, -6, -8, -6) });
        var ring = new Border { CornerRadius = new CornerRadius(10), Margin = new Thickness(-8, -6, -8, -6) };
        FrameworkElement face = Preview(choice, all);
        face.HorizontalAlignment = HorizontalAlignment.Center;
        face.VerticalAlignment = VerticalAlignment.Center;
        preview.Children.Add(new Grid { MinWidth = 52, MinHeight = 44, Children = { face } });
        preview.Children.Add(ring);
        // The Mac's .caption: 10 points.
        TextBlock caption = SettingsText.Static(all ? "All choices" : "One button", 10, "AnnotatorSecondaryText");
        caption.VerticalAlignment = VerticalAlignment.Center;
        FrameworkElement check = Glyphs.ForSymbol("checkmark.circle.fill", 10, "AnnotatorGlyphAccent");
        check.VerticalAlignment = VerticalAlignment.Center;
        check.Visibility = Visibility.Collapsed;
        var captionRow = new MacStack { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        captionRow.Children.Add(caption);
        captionRow.Children.Add(check);
        var content = new MacStack { Orientation = Orientation.Vertical, Spacing = 6, IsHitTestVisible = false };
        content.Children.Add(preview);
        content.Children.Add(captionRow);
        var button = new ParityButton
        {
            Style = AppResources.Style("AnnotatorPlainButton"),
            Content = content,
            Padding = new Thickness(0),
            HidesChildren = true,
        };
        if (!SettingsText.Scaled)
        {
            // The preview (44 and 12 points of padding), 6 points, and one 13-point caption line, as on the Mac.
            button.Height = OptionHeight;
        }
        string help = all ? $"{names}, side by side." : $"One button. Click it to go to the next choice: {names}.";
        AutomationProperties.SetName(button, $"{choice.Label}: {(all ? "all choices" : "one button")}");
        AutomationProperties.SetAutomationId(button, $"property-choice-{choice.RawValue}-{(all ? "all" : "one")}");
        AutomationProperties.SetHelpText(button, help);
        ToolTipService.SetToolTip(button, help);
        button.Click += (_, _) => _model.SetShowsAllChoices(choice, all);
        _options[(choice, all)] = (ring, caption, check, button);
        return button;
    }

    /// <summary>The control as the properties row draws it; one button shows the first choice.</summary>
    private FrameworkElement Preview(PropertyChoice choice, bool all)
    {
        InspectorSample[] samples = choice switch
        {
            PropertyChoice.Pattern => [.. StrokePattern.AllCases.Select(InspectorSample.ForPattern)],
            PropertyChoice.Roughness => [InspectorSample.Roughness(0), InspectorSample.Roughness(2), InspectorSample.Roughness(6)],
            PropertyChoice.FillStyle => [.. FillPattern.AllCases.Select(InspectorSample.Fill)],
            PropertyChoice.Edges => [InspectorSample.Edges(false), InspectorSample.Edges(true)],
            PropertyChoice.ArrowType => [InspectorSample.Route(false), InspectorSample.Route(true)],
            PropertyChoice.Pressure => [InspectorSample.Pressure(false), InspectorSample.Pressure(true)],
            _ => [],
        };
        Func<int, UIElement> face = choice switch
        {
            PropertyChoice.Font => index => Text("Aa", index == 0 ? "Segoe UI" : "Cascadia Mono, Consolas, Courier New", all ? 17 : 16),
            PropertyChoice.Alignment => index => Symbol(AlignmentSymbol(CoreTextAlignment.AllCases[index]), all ? 16 : 15),
            _ => index => PropertiesPanel.SamplePicture(samples[index], _ink),
        };
        int count = choice.ChoiceCount;
        if (all)
        {
            var row = new MacStack { Orientation = Orientation.Horizontal, Spacing = 5 };
            for (int index = 0; index < count; index++)
            {
                (ParityButton sample, Action<bool> select) = PropertiesPanel.Choice(choice.Choices[index], face(index));
                select(index == 0);
                row.Children.Add(sample);
            }
            return row;
        }
        if (choice is PropertyChoice.Edges or PropertyChoice.ArrowType or PropertyChoice.Pressure)
        {
            // An on and off switch shows the feature's own sample (Mac InspectorToggle).
            (ParityButton toggle, Action<bool> _) = PropertiesPanel.Choice(choice.Choices[1], face(1));
            return toggle;
        }
        var cycle = new Grid();
        cycle.Children.Add(new Grid { Width = 36, Height = 36, Children = { face(0) } });
        var dots = new MacStack
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 3),
        };
        for (int index = 0; index < count; index++)
        {
            dots.Children.Add(new Ellipse
            {
                Width = 3.5,
                Height = 3.5,
                Fill = index == 0 ? AppResources.Brush("AnnotatorAccentBrush") : AppResources.Brush("AnnotatorControlStrokeBrush"),
            });
        }
        cycle.Children.Add(dots);
        (ParityButton button, Action<bool> _) = PropertiesPanel.Choice(choice.Label, cycle, choice: false);
        return button;
    }

    private static UIElement Text(string text, string family, double size)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontFamily = new FontFamily(family),
            Style = AppResources.Style("AnnotatorPrimaryText"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return block;
    }

    private static UIElement Symbol(string symbol, double size)
    {
        FrameworkElement glyph = Glyphs.ForSymbol(symbol, size);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        return glyph;
    }

    private static string AlignmentSymbol(CoreTextAlignment alignment) => alignment switch
    {
        CoreTextAlignment.Left => "text.alignleft",
        CoreTextAlignment.Center => "text.aligncenter",
        _ => "text.alignright",
    };

    /// <summary>The property's symbol, as its block in the properties row (Mac PropertyChoicesEditor.symbol).</summary>
    private static string Symbol(PropertyChoice choice) => choice switch
    {
        PropertyChoice.Pattern => "line.3.horizontal",
        PropertyChoice.Roughness => "scribble",
        PropertyChoice.FillStyle => "square.lefthalf.filled",
        PropertyChoice.Edges => "rectangle",
        PropertyChoice.ArrowType => "arrow.turn.up.right",
        PropertyChoice.Pressure => "pencil.tip",
        PropertyChoice.Font => "textformat",
        _ => "text.alignleft",
    };

    /// <summary>"Solid, Dashed and Dotted".</summary>
    private static string List(IReadOnlyList<string> names) =>
        names.Count < 2 ? string.Concat(names) : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
}
