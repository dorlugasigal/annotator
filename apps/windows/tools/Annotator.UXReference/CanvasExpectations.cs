using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Annotator.UXReference;

/// <summary>
/// What the canvas states expect beyond their JSON. A reference without an accessibility tree gets one from the Mac source
/// (apps/macos/Annotator/DrawingCanvas.swift): the canvas is a group labeled "Annotation canvas" whose value is its
/// summary (refreshAccessibilitySummary) and whose help is the arrow-key help; its only child here is the Use apps
/// button (interactButton, labeled "Use apps", at AppKit's accessibility frame 15, 13, 152, 30). The summary counts the
/// objects of the Mac's document, which holds an object still being drawn. A recorded tree keeps its words, except the
/// Use apps button's label (see <see cref="WithSourceLabels"/>).
/// </summary>
internal static class CanvasExpectations
{
    public const string Source = "the Mac source (DrawingCanvas.swift), because the reference JSON has no accessibility tree";

    /// <summary>The words on the canvas's Use apps button (its title) and its accessibility label in DrawingCanvas.swift.</summary>
    private const string UseAppsTitle = "Use apps · Esc";

    private const string UseAppsLabel = "Use apps";

    public const string UseAppsLabelNote =
        "The Use apps button's label is \"Use apps\" from DrawingCanvas.swift (setAccessibilityLabel); the Mac recorder wrote only its title (Mac follow-up).";

    private const string Help = "Select an object, then use arrow keys to move it. Hold Shift for ten-point steps. Escape releases drawing input.";

    public static ReferenceState WithAccessibility(ReferenceState reference)
    {
        IReadOnlyList<CanvasElement> elements = reference.Elements ?? [];
        int objects = elements.Count;
        int selected = elements.Count(element => element.Selected);
        string summary = string.Create(CultureInfo.InvariantCulture,
            $"{objects} {(objects == 1 ? "object" : "objects")}, {selected} selected. Drawing.");
        var canvas = new AxNode
        {
            Role = "AXGroup",
            Label = "Annotation canvas",
            Value = JsonValue.Create(summary),
            Help = Help,
            Frame = [0, 0, reference.Width, reference.Height],
        };
        canvas.Children.Add(new AxNode { Role = "AXButton", Label = UseAppsLabel, Frame = [15, 13, 152, 30] });
        return reference with { Accessibility = [canvas], DerivedAccessibility = Source };
    }

    /// <summary>
    /// The recorded tree with the Use apps button's label from the Mac source: the Mac's recorder writes the button with
    /// its title and no label, although the source labels it "Use apps", which VoiceOver reads. Windows keeps that label.
    /// </summary>
    public static ReferenceState WithSourceLabels(ReferenceState reference)
    {
        bool changed = false;
        List<AxNode> nodes = [.. (reference.Accessibility ?? []).Select(node => Relabel(node, ref changed))];
        return changed ? reference with { Accessibility = nodes, SourceNote = UseAppsLabelNote } : reference;
    }

    private static AxNode Relabel(AxNode node, ref bool changed)
    {
        bool button = node is { Role: "AXButton", Label: null, Title: UseAppsTitle };
        changed |= button;
        var copy = new AxNode
        {
            Role = node.Role,
            Label = button ? UseAppsLabel : node.Label,
            Identifier = node.Identifier,
            Value = node.Value,
            Help = node.Help,
            Title = node.Title,
            Frame = node.Frame,
            Enabled = node.Enabled,
        };
        foreach (AxNode child in node.Children)
        {
            copy.Children.Add(Relabel(child, ref changed));
        }
        return copy;
    }

    /// <summary>
    /// Compares the objects the Windows canvas shows with the reference's, in order: tool, lock and selection exactly,
    /// frames within the tolerance and rotation within 1e-6. Returns one line per difference.
    /// </summary>
    public static List<string> CompareElements(IReadOnlyList<CanvasElement>? reference, JsonArray? windows)
    {
        var differences = new List<string>();
        if (reference is null)
        {
            return differences;
        }
        List<CanvasElement> actual = windows is null ? [] : [.. windows.Select(item => CanvasElement.FromJson(item!.AsObject()))];
        for (int index = 0; index < Math.Max(reference.Count, actual.Count); index++)
        {
            if (index >= actual.Count)
            {
                differences.Add($"object {index + 1}: missing on Windows; reference {reference[index].Describe()}");
                continue;
            }
            if (index >= reference.Count)
            {
                differences.Add($"object {index + 1}: Windows only: {actual[index].Describe()}");
                continue;
            }
            CanvasElement want = reference[index], have = actual[index];
            var notes = new List<string>();
            if (want.Tool != have.Tool)
            {
                notes.Add($"tool {have.Tool}, reference {want.Tool}");
            }
            double[] delta = [.. have.Frame.Zip(want.Frame, (a, b) => a - b)];
            if (delta.Any(value => Math.Abs(value) > Comparer.Tolerance))
            {
                notes.Add($"frame {Comparer.Frame(have.Frame)}, reference {Comparer.Frame(want.Frame)}");
            }
            if (Math.Abs(want.Rotation - have.Rotation) > 1e-6)
            {
                notes.Add($"rotation {have.Rotation:0.###}, reference {want.Rotation:0.###}");
            }
            if (want.Locked != have.Locked)
            {
                notes.Add($"locked {have.Locked}, reference {want.Locked}");
            }
            if (want.Selected != have.Selected)
            {
                notes.Add($"selected {have.Selected}, reference {want.Selected}");
            }
            if (notes.Count > 0)
            {
                differences.Add($"object {index + 1} ({want.Tool}): {string.Join("; ", notes)}");
            }
        }
        return differences;
    }

    public static string Report(IReadOnlyList<CanvasElement>? reference, JsonArray? windows, List<string> differences)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Objects: {reference?.Count ?? 0} in the reference, {windows?.Count ?? 0} on Windows, {differences.Count} differences");
        foreach (string difference in differences)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  ~ {difference}");
        }
        return text.ToString();
    }
}
