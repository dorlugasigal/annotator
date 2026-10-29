using System.Collections.Immutable;
using Annotator.Core;

namespace Annotator.Workspace.Toolbar;

/// <summary>The main part of the full properties panel (the branches of ContextualInspector.swift).</summary>
public enum InspectorBody
{
    /// <summary>
    /// <see cref="InspectorPlan.Items"/> on one row (<see cref="PropertyLayout.Row"/>), or in a column beside a vertical
    /// toolbar. The row never wraps: the toolbar grows sideways to fit it.
    /// </summary>
    Layout,

    /// <summary>Select with nothing selected: "Select an object to edit its properties."</summary>
    SelectHint,

    /// <summary>An image: the "Image opacity" block with its slider, percentage and hint.</summary>
    Image,

    /// <summary>Effects and covers (CoverPropertiesView) and the caption about readable details.</summary>
    Cover,
}

/// <summary>One top-level item of the properties row, in the Mac's order.</summary>
public enum InspectorItem
{
    /// <summary>The stroke picker, and the fill picker for fill-capable tools, side by side (10 points apart).</summary>
    Colors,

    StrokeColor,

    FillColor,

    /// <summary>"Width" (or "Eraser size"): just the slider, with its value beside it ("3 pt").</summary>
    Width,

    /// <summary>The opacity slider and field with its caption.</summary>
    Opacity,

    /// <summary>One button that steps through Solid, Dashed and Dotted (<see cref="InspectorPlan.PatternChoices"/>), or the
    /// three side by side (<see cref="InspectorPlan.ShowsAllChoices"/>).</summary>
    Pattern,

    /// <summary>One button that steps through Clean, Sketchy and Wild (<see cref="InspectorPlan.RoughnessChoices"/>), or the
    /// three side by side.</summary>
    Roughness,

    /// <summary>"Fill style": one button that steps through the fill patterns, or all of them side by side; only while a
    /// visible fill is set.</summary>
    FillStyle,

    /// <summary>"Edges": the Rounded corners switch of rectangles and diamonds, or Sharp corners and Rounded corners side
    /// by side.</summary>
    Edges,

    /// <summary>The grid's rows and columns fields.</summary>
    GridSize,

    /// <summary>The axes' centered switch and tick count; the explanation is the tick count's help.</summary>
    Axes,

    /// <summary>"Pressure": the Ink pressure switch, or Constant pressure and Ink pressure side by side.</summary>
    Pressure,

    SmartDraw,

    /// <summary>The counter's, ruler's or laser pointer's hint (<see cref="InspectorPlan.Caption"/>).</summary>
    Caption,

    /// <summary>One button that switches between System and Mono (<see cref="InspectorPlan.FontChoices"/>), or the two
    /// side by side.</summary>
    Font,

    /// <summary>"Size": just the slider, with its value beside it ("28 pt").</summary>
    TextSize,

    Weight,

    /// <summary>One button that steps through Left, Center and Right, or the three side by side.</summary>
    Alignment,
}

/// <summary>
/// What the full properties panel shows for a workspace state: a port of the branching in ContextualInspector.swift.
/// The app turns the plan into controls; the plan changes only when the set of controls changes.
/// </summary>
/// <param name="Context">"Editing …" while objects are selected (Mac selected-object-context), or null.</param>
/// <param name="Arrow">The arrow controls (Start, End, the Curved arrow switch or both arrow types, and the Head size
/// slider) end the row as one group, or follow the column when compact.</param>
/// <param name="Caption">The counter's, ruler's or laser pointer's hint for <see cref="InspectorItem.Caption"/>.</param>
public sealed record InspectorPlan(Tool Tool, string? Context, InspectorBody Body, ImmutableArray<InspectorItem> Items, bool Arrow,
                                   string? Caption)
{
    public const string SelectHintText = "Select an object to edit its properties.";
    public const string CoverCaption = "Blurred details can still be readable. Remove private info before you share.";
    public const string ImageHint = "Drag handles to resize or rotate. Delete removes the selected image.";
    public const string CounterCaption = "Click to add sequential numbered markers.";
    public const string RulerCaption = "Drag to measure the distance in source pixels.";
    public const string LaserCaption = "Drag to point. The trail fades on its own.";

    /// <summary>Properties that show all their choices side by side instead of one button (Mac showsAllChoices).</summary>
    public ImmutableArray<PropertyChoice> AllChoices { get; init; } = [];

    /// <summary>A key that changes exactly when the controls change, for rebuilding them.</summary>
    public string Key => $"{Tool}|{Context is not null}|{Body}|{string.Join(',', Items)}|{Arrow}|{string.Join(',', AllChoices)}";

    /// <summary>Whether <paramref name="choice"/> shows all its choices side by side.</summary>
    public bool ShowsAllChoices(PropertyChoice choice) => AllChoices.Contains(choice);

    /// <summary>The arrow type block's title: "Curved" for the switch, "Arrow type" for both choices.</summary>
    public string ArrowTypeTitle => ShowsAllChoices(PropertyChoice.ArrowType) ? "Arrow type" : "Curved";

    /// <summary>
    /// The names of a property's buttons when it shows all its choices (Mac SampleOptions and the font and alignment
    /// buttons): "Solid", "Sharp corners", "System font", "Align left".
    /// </summary>
    public static ImmutableArray<string> ChoiceNames(PropertyChoice choice) => choice switch
    {
        PropertyChoice.Font => [.. choice.Choices.Select(name => $"{name} font")],
        PropertyChoice.Alignment => [.. choice.Choices.Select(name => $"Align {name.ToLowerInvariant()}")],
        _ => [.. choice.Choices],
    };

    public static InspectorPlan For(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Tool tool = snapshot.EffectiveTool;
        int count = snapshot.SelectionCount;
        string? context = ContextText(snapshot);
        var items = ImmutableArray.CreateBuilder<InspectorItem>();
        InspectorBody body = InspectorBody.Layout;
        string? caption = null;
        if (tool == Tool.Select && count > 0)
        {
            items.Add(InspectorItem.StrokeColor);
            if (snapshot.SelectionSupportsFill)
            {
                items.Add(InspectorItem.FillColor);
            }
            items.Add(InspectorItem.Width);
            items.Add(InspectorItem.Opacity);
        }
        else if (tool == Tool.Select)
        {
            body = InspectorBody.SelectHint;
        }
        else if (tool == Tool.Image)
        {
            body = InspectorBody.Image;
        }
        else if (tool.IsProtection)
        {
            body = InspectorBody.Cover;
        }
        else if (tool is Tool.Counter or Tool.Ruler)
        {
            items.Add(InspectorItem.StrokeColor);
            if (tool == Tool.Ruler)
            {
                items.Add(InspectorItem.Width);
            }
            items.Add(InspectorItem.Opacity);
            items.Add(InspectorItem.Caption);
            caption = tool == Tool.Counter ? CounterCaption : RulerCaption;
        }
        else if (tool == Tool.Eraser)
        {
            items.Add(InspectorItem.Width);
        }
        else if (tool == Tool.Laser)
        {
            items.Add(InspectorItem.StrokeColor);
            items.Add(InspectorItem.Width);
            items.Add(InspectorItem.Caption);
            caption = LaserCaption;
        }
        else if (tool == Tool.Text)
        {
            items.AddRange(InspectorItem.StrokeColor, InspectorItem.Font, InspectorItem.TextSize, InspectorItem.Weight, InspectorItem.Alignment);
        }
        else
        {
            items.Add(InspectorItem.Colors);
            items.Add(InspectorItem.Width);
            if (tool != Tool.Highlighter)
            {
                items.Add(InspectorItem.Pattern);
                items.Add(InspectorItem.Roughness);
            }
            if (tool.SupportsFill && (snapshot.Style.Fill?.Alpha ?? 0) > 0)
            {
                items.Add(InspectorItem.FillStyle);
            }
            if (tool is Tool.Rectangle or Tool.Diamond)
            {
                items.Add(InspectorItem.Edges);
            }
            if (tool == Tool.Grid)
            {
                items.Add(InspectorItem.GridSize);
            }
            if (tool == Tool.Axes)
            {
                items.Add(InspectorItem.Axes);
            }
            if (tool == Tool.Pen)
            {
                items.Add(InspectorItem.Pressure);
                items.Add(InspectorItem.SmartDraw);
            }
        }
        return new InspectorPlan(tool, context, body, items.ToImmutable(), tool == Tool.Arrow, caption)
        {
            AllChoices = [.. PropertyChoice.AllCases.Where(snapshot.ShowsAllChoices)],
        };
    }

    /// <summary>The "Editing …" line for a selection (Mac selected-object-context), or null without one.</summary>
    public static string? ContextText(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        int count = snapshot.SelectionCount;
        return count == 0 ? null
            : snapshot.SelectionIsWorkspaceWide ? $"Editing {count} objects across displays"
            : count == 1 ? $"Editing {snapshot.EffectiveTool.Label}"
            : $"Editing {count} selected objects";
    }

    /// <summary>The Width block's title.</summary>
    public static string WidthTitle(Tool tool) => tool == Tool.Eraser ? "Eraser size" : "Width";

    /// <summary>The roughness choices: Clean, Sketchy and Wild.</summary>
    public static ImmutableArray<(double Value, string Name)> RoughnessChoices { get; } = [(0, "Clean"), (2, "Sketchy"), (6, "Wild")];

    /// <summary>The pattern choices, as the Mac's capitalized raw values: Solid, Dashed and Dotted.</summary>
    public static ImmutableArray<(StrokePattern Value, string Name)> PatternChoices { get; } =
        [.. StrokePattern.AllCases.Select(pattern => (pattern, Capitalized(pattern.RawValue)))];

    /// <summary>The fill style choices: Solid, Hatch and Crosshatch.</summary>
    public static ImmutableArray<(FillPattern Value, string Name)> FillStyleChoices { get; } =
        [.. FillPattern.AllCases.Select(pattern => (pattern, Capitalized(pattern.RawValue)))];

    /// <summary>The font choices: System and Mono, by their saved font names.</summary>
    public static ImmutableArray<(string Value, string Name)> FontChoices { get; } = [("system", "System"), ("monospace", "Mono")];

    /// <summary>The alignment choices: Left, Center and Right.</summary>
    public static ImmutableArray<(TextAlignment Value, string Name)> AlignmentChoices { get; } =
        [.. TextAlignment.AllCases.Select(alignment => (alignment, Capitalized(alignment.RawValue)))];

    /// <summary>
    /// Mac CycleOptions: the choice after <paramref name="current"/>, wrapping to the first. A value that isn't one of the
    /// choices counts as the first, as the button shows it.
    /// </summary>
    public static int NextChoice<T>(ImmutableArray<(T Value, string Name)> choices, T current)
    {
        int index = Math.Max(0, choices.IndexOf(choices.FirstOrDefault(choice => EqualityComparer<T>.Default.Equals(choice.Value, current))));
        return (index + 1) % choices.Length;
    }

    /// <summary>Mac CycleOptions' help: "Pattern: Solid. Click for dashed."</summary>
    public static string CycleHelp(string name, string current, string next) => $"{name}: {current}. Click for {next.ToLowerInvariant()}.";

    /// <summary>Mac CycleOptions' accessibilityHint, which screen readers read instead of the tooltip: "Changes to dashed".</summary>
    public static string CycleHint(string next) => $"Changes to {next.ToLowerInvariant()}";

    /// <summary>The corner radius the Rounded corners switch sets.</summary>
    public const double RoundedCornerRadius = 14;

    /// <summary>"75%": the head size as the Mac shows it next to its slider.</summary>
    public static string HeadSizeText(double? headScale) => $"{(int)((headScale ?? 1) * 100)}%";

    /// <summary>The axes' explanation, the tick count's help (Mac ContextualInspector).</summary>
    public static string AxesCaption(long? tickCount) => tickCount is null
        ? "Legacy tick spacing. Set a count for equal intervals from the origin; 0 hides ticks."
        : "Evenly spaced in each direction. More ticks reduce spacing; 0 hides ticks.";

    /// <summary>A name with its first letter in upper case, as Swift's capitalized does for the one-word raw values.</summary>
    public static string Capitalized(string rawValue) =>
        rawValue.Length == 0 ? rawValue : string.Concat(char.ToUpperInvariant(rawValue[0]).ToString(), rawValue[1..].ToLowerInvariant());
}

/// <summary>
/// The properties row (Mac ContextualInspector with <c>HStackLayout(alignment: .propertyControl)</c>): items sit left to
/// right, <paramref name="spacing"/> apart, never wrapping, with the middles of their controls on one line. A named block's
/// control sits below its name; an item without a name is its own control, so its middle is its centre.
/// </summary>
public static class PropertyLayout
{
    public const double Spacing = 14;

    /// <summary>The frames of the items, and the row's size. <c>Middle</c> is the item's control middle from its top.</summary>
    public static (Rect[] Frames, double Width, double Height) Row(ReadOnlySpan<(double Width, double Height, double Middle)> items,
                                                                 double spacing = Spacing)
    {
        double above = 0, below = 0;
        foreach (var item in items)
        {
            above = Math.Max(above, item.Middle);
            below = Math.Max(below, item.Height - item.Middle);
        }
        var frames = new Rect[items.Length];
        double x = 0;
        for (int index = 0; index < items.Length; index++)
        {
            frames[index] = new Rect(x, above - items[index].Middle, items[index].Width, items[index].Height);
            x += items[index].Width + (index + 1 < items.Length ? spacing : 0);
        }
        return (frames, x, above + below);
    }
}
