using System.Globalization;
using Annotator.App.Shell;
using Annotator.Workspace;
using Annotator.Workspace.Toolbar;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Glyphs = Annotator.App.Icons.Glyphs;

namespace Annotator.App.Toolbar;

/// <summary>
/// Mac LiveZoomControls and PendingZoomControl: the toolbar's live zoom level with its minus and plus buttons, and the orange
/// Cancel control while a zoom request starts. Sizes are the Mac's points.
/// </summary>
internal static class ZoomControls
{
    /// <summary>
    /// Mac LiveZoomControls: "Live zoom level" with Decrease and Increase (×1.25, 1x to 16x) and the level as "2.0×".
    /// <paramref name="compact"/> is the vertical rail's 44-point column with the level below the buttons; otherwise the
    /// level sits between them in a 145-point row. Values that change go to <paramref name="updates"/>.
    /// </summary>
    public static FrameworkElement Level(WorkspaceModel model, bool compact, List<Action<WorkspaceSnapshot>> updates, Action interacted)
    {
        double width = compact ? 20 : 26;
        (ParityButton minus, Action<bool> showMinus) = Step(model, "Decrease live zoom", "minus", width, interacted, value => Math.Max(1, value / 1.25));
        (ParityButton plus, Action<bool> showPlus) = Step(model, "Increase live zoom", "plus", width, interacted, value => Math.Min(16, value * 1.25));
        TextBlock level = ToolbarParts.Text(Format(model.Snapshot.Magnification), 10, "AnnotatorPrimaryText");
        level.HorizontalAlignment = HorizontalAlignment.Center;
        level.TextAlignment = TextAlignment.Center;
        // Mac monospacedDigit(): the level keeps its width as it changes, the width of "2.0×" in SF Pro at 10 points.
        Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(level, FontNumeralAlignment.Tabular);
        level.MinWidth = 22.5;
        var buttons = new MacStack { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
        buttons.Children.Add(minus);
        if (!compact)
        {
            var slot = new Grid { Width = 40, VerticalAlignment = VerticalAlignment.Center };
            slot.Children.Add(level);
            buttons.Children.Add(slot);
        }
        buttons.Children.Add(plus);
        var content = new MacStack { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(buttons);
        if (compact)
        {
            content.Children.Add(level);
        }
        var group = new AccessibleGroup("Live zoom level") { Width = compact ? 44 : 145, BoundsFromChildren = true };
        group.Children.Add(content);
        updates.Add(state =>
        {
            double value = state.Magnification;
            level.Text = Format(value);
            showMinus(value > 1);
            showPlus(value < 16);
        });
        return group;
    }

    /// <summary>
    /// Mac PendingZoomControl: the orange Cancel control while zoom starts, 44 points wide on the compact rail ("Zoom", the
    /// phase, Cancel) and 130 otherwise (an hourglass with "Zoom starting" over "Cancel").
    /// </summary>
    public static ParityButton Pending(WorkspaceModel model, PendingZoomPresentation pending, bool compact, Action interacted)
    {
        var stack = new MacStack { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (compact)
        {
            stack.Children.Add(Centered(ToolbarParts.Label("Zoom", 9, "AnnotatorOrangeText", FontWeights.Medium)));
            stack.Children.Add(Centered(ToolbarParts.Label(pending.Phase, 8, "AnnotatorOrangeText")));
            var cancel = new MacStack { Orientation = Orientation.Horizontal, Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center };
            FrameworkElement mark = Glyphs.ForSymbol("xmark", 7, "AnnotatorGlyphOrange");
            mark.VerticalAlignment = VerticalAlignment.Center;
            cancel.Children.Add(mark);
            cancel.Children.Add(ToolbarParts.Label("Cancel", 8, "AnnotatorOrangeText", FontWeights.Medium));
            stack.Children.Add(cancel);
        }
        else
        {
            var title = new MacStack { Orientation = Orientation.Horizontal, Spacing = 5, HorizontalAlignment = HorizontalAlignment.Center };
            FrameworkElement glass = Glyphs.ForSymbol("hourglass", 12, "AnnotatorGlyphOrange");
            glass.VerticalAlignment = VerticalAlignment.Center;
            title.Children.Add(glass);
            title.Children.Add(ToolbarParts.Label(pending.Title, 11, "AnnotatorOrangeText", FontWeights.Medium));
            stack.Children.Add(title);
            stack.Children.Add(Centered(ToolbarParts.Label("Cancel", 10, "AnnotatorOrangeText")));
        }
        var surface = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = AppResources.Brush("AnnotatorPendingStripFillBrush"),
            Child = stack,
        };
        ParityButton button = ToolbarParts.Button(compact ? 44 : 130, 44, surface, "Cancel pending zoom", 8);
        button.AccessibleValue = pending.Detail;
        AutomationProperties.SetAutomationId(button, "cancel-pending-zoom");
        ToolbarParts.Help(button, $"{pending.Detail}. Click to cancel the zoom request.");
        button.Click += (_, _) =>
        {
            interacted();
            model.CancelPendingZoom();
        };
        return button;
    }

    /// <summary>The level as the Mac formats it: one decimal and a multiplication sign.</summary>
    public static string Format(double magnification) => string.Create(CultureInfo.InvariantCulture, $"{magnification:0.0}×");

    /// <summary>A plain symbol button; the returned action enables it and draws its symbol dimmed while it is disabled.</summary>
    private static (ParityButton Button, Action<bool> Show) Step(WorkspaceModel model, string name, string symbol, double width, Action interacted,
                                                                Func<double, double> next)
    {
        var host = new ContentControl { IsTabStop = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        ParityButton button = ToolbarParts.Button(width, 26, host, name);
        AutomationProperties.SetAutomationId(button, symbol);
        button.Click += (_, _) =>
        {
            interacted();
            model.SetMagnification(next(model.Snapshot.Magnification));
        };
        bool? shown = null;
        void Show(bool enabled)
        {
            button.IsEnabled = enabled;
            if (shown == enabled)
            {
                return;
            }
            shown = enabled;
            FrameworkElement glyph = ToolbarParts.SymbolGlyph(symbol, style: enabled ? "AnnotatorGlyph" : "AnnotatorGlyphDisabled");
            host.Content = glyph;
            // Accessibility reports the symbol's bounds, as on the Mac.
            button.BoundsElement = glyph;
        }
        Show(true);
        return (button, Show);
    }

    private static FrameworkElement Centered(FrameworkElement element)
    {
        element.HorizontalAlignment = HorizontalAlignment.Center;
        return element;
    }
}
