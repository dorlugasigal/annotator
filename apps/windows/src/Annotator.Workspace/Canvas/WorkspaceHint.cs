using System.Globalization;
using Annotator.Core;
using Annotator.Core.Motion;
using Annotator.Workspace.Shortcuts;

namespace Annotator.Workspace.Canvas;

/// <summary>Zoom facts for the shortcut hint (<see cref="WorkspaceSnapshot.ZoomHint"/>).</summary>
/// <param name="Active">Screen zoom is on.</param>
/// <param name="Magnification">The zoom factor shown while zoom is on.</param>
/// <param name="ScrollStatus">The zoom scroll line shown while zoom is on (Mac zoomScrollStatus).</param>
public sealed record ZoomHint(bool Active, double Magnification, string ScrollStatus);

/// <summary>The shortcut hint at the canvas's top-left corner (Mac WorkspaceHint, text and visibility only).</summary>
public static class WorkspaceHint
{
    /// <summary>Mac WorkspaceHint.closeLabel: the ✕ that turns tips off.</summary>
    public const string CloseLabel = "Turn off tips";

    /// <summary>Mac WorkspaceHint.closeHelp.</summary>
    public const string CloseHelp = "Turn them back on in Settings > General.";

    /// <summary>
    /// Mac WorkspaceHint.text(model:) with Windows key names: the mode and what Escape does now on one line, the highlighter's
    /// line, and with <paramref name="zoom"/> (<see cref="WorkspaceSnapshot.ZoomHint"/>) active, the zoom and scroll lines.
    /// Windows needs no permission for Alt+scroll (WIN-INP-08). Like the Mac's since its 2026-09-24 interface pass, the tip
    /// shows no status message; the workspace keeps it (<see cref="WorkspaceSnapshot.Status"/>).
    /// </summary>
    public static string Text(WorkspaceSnapshot workspace, bool hasTextEditor, bool hasActiveEdit, ZoomHint? zoom = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        string draw = WindowsShortcuts.Label(GlobalShortcutAction.ResumeDrawing, workspace.Preferences);
        string zoomKey = WindowsShortcuts.Label(GlobalShortcutAction.LiveZoom, workspace.Preferences);
        bool drawing = workspace.Mode == WorkspaceMode.Draw;
        bool zooming = zoom?.Active == true;
        string first = drawing ? "Drawing" : $"Using apps  ·  {draw}: draw";
        if (hasTextEditor)
        {
            first += "  ·  Escape finishes text";
        }
        else if (hasActiveEdit)
        {
            first += "  ·  Escape cancels the edit";
        }
        else if (drawing && !zooming)
        {
            first += "  ·  Escape: use apps";
        }
        var lines = new List<string> { first };
        if (drawing && workspace.Tool == Tool.Highlighter)
        {
            lines.Add("Hold Shift for a straight highlight.");
        }
        if (zoom is { Active: true } active)
        {
            lines.Add($"Zoom {active.Magnification.ToString("0.0", CultureInfo.InvariantCulture)}×  ·  {zoomKey}: stop");
            lines.Add(active.ScrollStatus);
        }
        return string.Join('\n', lines);
    }

    /// <summary>
    /// The wake key of the tip (Mac HintWake) for <paramref name="workspace"/> on <paramref name="display"/>: whether it can
    /// show (<see cref="IsVisible"/>), the mode, the highlighter while drawing, zoom and the text editor.
    /// </summary>
    public static HintWake Wake(WorkspaceSnapshot workspace, DisplaySnapshot? display, bool hasTextEditor)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        bool drawing = workspace.Mode == WorkspaceMode.Draw;
        return new HintWake(display is not null && IsVisible(workspace, display), drawing, drawing && workspace.Tool == Tool.Highlighter,
            workspace.ZoomHint?.Active == true, hasTextEditor);
    }

    /// <summary>Mac refreshHint: shown on the editing target unless hints are off, the workspace is hidden or screen zoom is on.</summary>
    public static bool IsVisible(WorkspaceSnapshot workspace, DisplaySnapshot display)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(display);
        return workspace.Preferences.ShowShortcutHints != false && !workspace.WorkspaceHidden && display.IsActive
            && display.Document.Presentation.Mode != PresentationMode.Screen;
    }
}

/// <summary>
/// What wakes the on-screen tip (Mac HintWake): it wakes when one of these changes (it can show, the mode, the highlighter,
/// zoom or a text editor), then fades out after <see cref="MotionTiming.HintDwellDuration"/>. Strokes and zoom values
/// update the text without waking it.
/// </summary>
public readonly record struct HintWake(bool Available, bool Drawing, bool Highlighter, bool Zooming, bool EditingText);

/// <summary>Mac HintSchedule: when the tip shows, from the times its <see cref="HintWake"/> changed. Times are in seconds.</summary>
public sealed class HintSchedule
{
    private HintWake? _last;

    /// <summary>When the tip last woke, or negative infinity.</summary>
    public double WokeAt { get; private set; } = double.NegativeInfinity;

    /// <summary>When the tip that shows now should fade out.</summary>
    public double HidesAt => WokeAt + MotionTiming.HintDwellDuration;

    /// <summary>Whether the tip shows at <paramref name="now"/> for <paramref name="wake"/>.</summary>
    public bool Shows(HintWake wake, double now)
    {
        if (wake != _last)
        {
            _last = wake;
            if (wake.Available)
            {
                WokeAt = now;
            }
        }
        return wake.Available && now - WokeAt < MotionTiming.HintDwellDuration;
    }
}

/// <summary>The canvas's UI Automation texts (Mac DrawingCanvas accessibility, WIN-A11Y-01).</summary>
public static class CanvasAccessibility
{
    public const string Label = "Annotation canvas";

    public const string Help = "Select an object, then use arrow keys to move it. Hold Shift for ten-point steps. Escape releases drawing input.";

    /// <summary>The custom action that raises <see cref="CanvasRequest.FocusTools"/>.</summary>
    public const string FocusToolsAction = "Focus drawing tools";

    /// <summary>Mac refreshAccessibilitySummary, for example "1 object, 0 selected. Drawing."</summary>
    public static string Summary(int objects, int selected, bool drawing) =>
        $"{objects} {(objects == 1 ? "object" : "objects")}, {selected} selected. {(drawing ? "Drawing" : "Using apps")}.";

    /// <summary>
    /// The objects the summary counts: the document's, and an object still being drawn or a new text still being typed,
    /// which the Mac adds to its document when the gesture starts (the UX reference's attach-hint state counts the arrow
    /// being drawn: "7 objects").
    /// </summary>
    public static int ObjectCount(ElementList document, in CanvasLiveState state)
    {
        int count = document.Count;
        Guid? draft = null;
        if (state.Draft is { } drawn && !document.Contains(drawn.Id))
        {
            draft = drawn.Id;
            count++;
        }
        if (state.Text is { IsLabel: false } text && text.ElementId != draft && !document.Contains(text.ElementId))
        {
            count++;
        }
        return count;
    }

    /// <summary>
    /// What the summary counts on <paramref name="canvas"/> (Mac session.document): the objects of the document as the canvas
    /// shows it, with an object still being drawn (<see cref="ObjectCount"/>), and the selected ones. While text is edited
    /// that document holds the text being typed, selected, as the Mac's does once its editor opens.
    /// </summary>
    public static (int Objects, int Selected) Counts(CanvasController canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        CanvasDocument? document = canvas.Document;
        return (ObjectCount(document?.Elements ?? ElementList.Empty, canvas.State), document?.Selection.Count ?? 0);
    }
}
