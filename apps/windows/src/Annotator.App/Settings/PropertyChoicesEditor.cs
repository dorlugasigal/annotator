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
    private readonly WorkspaceModel _model;
    private readonly HyperlinkButton _reset;
    private readonly Dictionary<(PropertyChoice Choice, bool All), (Border Ring, TextBlock Caption, FrameworkElement Check, ParityButton Button)> _options = [];
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
        }
    }

    private FrameworkElement Card(PropertyChoice choice)
    {
        var card = new Grid { CornerRadius = new CornerRadius(12), Background = SettingsColors.Primary(0.03), Padding = new Thickness(10) };
        card.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderBrush = SettingsColors.Primary(0.07),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(-10),
            IsHitTestVisible = false,
        });
        var stack = new MacStack { Orientation = Orientation.Vertical, Spacing = 8 };
        var label = new MacStack { Orientation = Orientation.Horizontal, Spacing = 5 };
        FrameworkElement icon = Glyphs.ForSymbol(Symbol(choice), 12);
        icon.VerticalAlignment = VerticalAlignment.Center;
        label.Children.Add(icon);
        label.Children.Add(SettingsText.Static(choice.Label, 12, "AnnotatorPrimaryText", FontWeights.SemiBold));
        stack.Children.Add(label);
        var options = new MacStack { Orientation = Orientation.Horizontal, Spacing = 8 };
        options.Children.Add(Option(choice, all: false));
        options.Children.Add(Option(choice, all: true));
        stack.Children.Add(options);
        card.Children.Add(stack);
        var group = new AccessibleGroup(choice.Label, $"property-choice-{choice.RawValue}");
        group.Children.Add(card);
        return group;
    }

    private FrameworkElement Option(PropertyChoice choice, bool all)
    {
        string names = List(choice.Choices);
        var preview = new Grid { MinWidth = 52, MinHeight = 44, Padding = new Thickness(8, 6, 8, 6), IsHitTestVisible = false };
        preview.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = SettingsColors.Window, Margin = new Thickness(-8, -6, -8, -6) });
        var ring = new Border { CornerRadius = new CornerRadius(10), Margin = new Thickness(-8, -6, -8, -6) };
        FrameworkElement face = Preview(choice, all);
        face.HorizontalAlignment = HorizontalAlignment.Center;
        face.VerticalAlignment = VerticalAlignment.Center;
        preview.Children.Add(face);
        preview.Children.Add(ring);
        AutomationProperties.SetAccessibilityView(preview, AccessibilityView.Raw);
        TextBlock caption = SettingsText.Static(all ? "All choices" : "One button", 11, "AnnotatorSecondaryText");
        caption.VerticalAlignment = VerticalAlignment.Center;
        FrameworkElement check = Glyphs.ForSymbol("checkmark.circle.fill", 11, "AnnotatorGlyphAccent");
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
        };
        AutomationProperties.SetName(button, $"{choice.Label}: {(all ? "all choices" : "one button")}");
        AutomationProperties.SetAutomationId(button, $"property-choice-{choice.RawValue}-{(all ? "all" : "one")}");
        ToolTipService.SetToolTip(button, all ? $"{names}, side by side." : $"One button. Click it to go to the next choice: {names}.");
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
