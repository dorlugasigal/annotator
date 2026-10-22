using System.Collections.Immutable;
using Annotator.Core;

namespace Annotator.Workspace.Toolbar;

/// <summary>The main part of the full properties panel (the branches of ContextualInspector.swift).</summary>
public enum InspectorBody
{
    /// <summary>The adaptive property layout with <see cref="InspectorPlan.Items"/>.</summary>
    Layout,

    /// <summary>Select with nothing selected: "Select an object to edit its properties."</summary>
    SelectHint,

    /// <summary>An image: the "Image opacity" block with its slider, percentage and hint.</summary>
    Image,

    /// <summary>Effects and covers (CoverPropertiesView) and the caption about readable details.</summary>
    Cover,
}

/// <summary>One top-level item of the adaptive property layout, in the Mac's order.</summary>
public enum InspectorItem
{
    /// <summary>The stroke picker, and the fill picker for fill-capable tools, side by side (10 points apart).</summary>
    Colors,

    StrokeColor,

    FillColor,

    /// <summary>"Width" (or "Eraser size"): three sample widths, the slider and the precise field.</summary>
    Width,

    /// <summary>The opacity slider and field with its caption.</summary>
    Opacity,

    Pattern,

    Roughness,

    /// <summary>"Fill style": only while a visible fill is set.</summary>
    FillStyle,

    /// <summary>Sharp or rounded corners of rectangles and diamonds.</summary>
    Edges,

    /// <summary>The grid's rows and columns fields.</summary>
    GridSize,

    /// <summary>The axes' centered switch, tick count and its explanation.</summary>
    Axes,

    Pressure,

    SmartDraw,

    /// <summary>The counter's or ruler's hint (<see cref="InspectorPlan.Caption"/>).</summary>
    Caption,

    Font,

    TextSize,

    Weight,

    Alignment,
}

/// <summary>
/// What the full properties panel shows for a workspace state: a port of the branching in ContextualInspector.swift.
/// The app turns the plan into controls; the plan changes only when the set of controls changes.
/// </summary>
/// <param name="Context">"Editing …" while objects are selected (Mac selected-object-context), or null.</param>
/// <param name="Arrow">The arrow controls (Start, End, Arrow type and Head size) follow the body.</param>
/// <param name="Caption">The counter's or ruler's hint for <see cref="InspectorItem.Caption"/>.</param>
public sealed record InspectorPlan(Tool Tool, string? Context, InspectorBody Body, ImmutableArray<InspectorItem> Items, bool Arrow,
                                   string? Caption)
{
    public const string SelectHintText = "Select an object to edit its properties.";
    public const string CoverCaption = "Blur, pixelation and distortion can leave details readable. Remove private information before sharing.";
    public const string ImageHint = "Drag handles to resize or rotate. Delete removes the selected image.";
    public const string CounterCaption = "Click to add sequential numbered markers.";
    public const string RulerCaption = "Drag to measure the distance in source pixels.";

    /// <summary>A key that changes exactly when the controls change, for rebuilding them.</summary>
    public string Key => $"{Tool}|{Context is not null}|{Body}|{string.Join(',', Items)}|{Arrow}";

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
        return new InspectorPlan(tool, context, body, items.ToImmutable(), tool == Tool.Arrow, caption);
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

    /// <summary>The Width block's sample widths (Mac widthControls): larger ones for the eraser.</summary>
    public static ImmutableArray<double> WidthChoices(Tool tool) => tool == Tool.Eraser ? [8, 16, 28] : [2, 4, 7];

    /// <summary>The Width block's title.</summary>
    public static string WidthTitle(Tool tool) => tool == Tool.Eraser ? "Eraser size" : "Width";

    /// <summary>The roughness choices: Clean, Sketchy and Wild.</summary>
    public static ImmutableArray<(double Value, string Name)> RoughnessChoices { get; } = [(0, "Clean"), (2, "Sketchy"), (6, "Wild")];

    /// <summary>The head size choices, as fractions of the full head.</summary>
    public static ImmutableArray<double> HeadSizeChoices { get; } = [0.5, 0.75, 1];

    /// <summary>The text sizes of the Size block.</summary>
    public static ImmutableArray<double> FontSizeChoices { get; } = [18, 28, 44];

    /// <summary>The corner radius the Edges block's rounded choice sets.</summary>
    public const double RoundedCornerRadius = 14;

    /// <summary>"75%": the head size as the Mac shows it next to its slider.</summary>
    public static string HeadSizeText(double? headScale) => $"{(int)((headScale ?? 1) * 100)}%";

    /// <summary>The axes' explanation under the tick count (Mac ContextualInspector).</summary>
    public static string AxesCaption(long? tickCount) => tickCount is null
        ? "Legacy tick spacing. Set a count for equal intervals from the origin; 0 hides ticks."
        : "Evenly spaced in each direction. More ticks reduce spacing; 0 hides ticks.";

    /// <summary>A name with its first letter in upper case, as Swift's capitalized does for the one-word raw values.</summary>
    public static string Capitalized(string rawValue) =>
        rawValue.Length == 0 ? rawValue : string.Concat(char.ToUpperInvariant(rawValue[0]).ToString(), rawValue[1..].ToLowerInvariant());
}

/// <summary>
/// Port of AdaptivePropertyLayout.frames: items flow left to right with <paramref name="spacing"/> between them and wrap
/// to a new row, <paramref name="spacing"/> below the tallest item of the row, when the next one would pass
/// <paramref name="width"/>. An item wider than the width takes the whole width.
/// </summary>
public static class PropertyLayout
{
    public const double Spacing = 14;

    public static Rect[] Frames(ReadOnlySpan<(double Width, double Height)> sizes, double width, double spacing = Spacing)
    {
        var frames = new Rect[sizes.Length];
        double x = 0, y = 0, rowHeight = 0;
        for (int index = 0; index < sizes.Length; index++)
        {
            double itemWidth = Math.Min(width, sizes[index].Width);
            if (x > 0 && x + itemWidth > width)
            {
                x = 0;
                y += rowHeight + spacing;
                rowHeight = 0;
            }
            frames[index] = new Rect(x, y, itemWidth, sizes[index].Height);
            x += itemWidth + spacing;
            rowHeight = Math.Max(rowHeight, sizes[index].Height);
        }
        return frames;
    }
}
