using System.Globalization;
using Annotator.Core;
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
    public const string Footer = "Turn off these tips in Settings > General.";

    /// <summary>
    /// Mac WorkspaceHint.text(model:) with Windows key names. With <paramref name="zoom"/> (<see cref="WorkspaceSnapshot.ZoomHint"/>)
    /// the zoom line appears; null leaves it out. Windows needs no permission for Alt+scroll (WIN-INP-08). Like the Mac's since
    /// its 2026-09-24 interface pass, the tip shows no status message; the workspace keeps it (<see cref="WorkspaceSnapshot.Status"/>).
    /// </summary>
    public static string Text(WorkspaceSnapshot workspace, bool hasTextEditor, bool hasActiveEdit, ZoomHint? zoom = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        string mode = workspace.Mode == WorkspaceMode.Draw ? "Drawing" : "Using apps";
        string draw = WindowsShortcuts.Label(GlobalShortcutAction.ResumeDrawing, workspace.Preferences);
        string zoomKey = WindowsShortcuts.Label(GlobalShortcutAction.LiveZoom, workspace.Preferences);
        var lines = new List<string> { $"{mode}  ·  {draw}: draw" };
        if (workspace.Mode == WorkspaceMode.Draw && workspace.Tool == Tool.Highlighter)
        {
            lines.Add("Hold Shift for a straight highlight.");
        }
        bool zooming = zoom?.Active == true;
        if (zoom is { Active: true } active)
        {
            lines.Add($"Zoom {active.Magnification.ToString("0.0", CultureInfo.InvariantCulture)}×  ·  {zoomKey}: stop");
            lines.Add(active.ScrollStatus);
        }
        else if (zoom is not null)
        {
            lines.Add($"{zoomKey}: screen zoom  ·  Alt+scroll starts zoom");
        }
        if (hasTextEditor)
        {
            lines.Add("Escape finishes text.");
        }
        else if (hasActiveEdit)
        {
            lines.Add("Escape cancels the current edit.");
        }
        else if (workspace.Mode == WorkspaceMode.Draw && !zooming)
        {
            lines.Add("Escape: use apps");
        }
        lines.Add(Footer);
        return string.Join('\n', lines);
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
