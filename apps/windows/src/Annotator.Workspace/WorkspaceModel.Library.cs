using Annotator.Core;

namespace Annotator.Workspace;

/// <summary>What Save selection in My shapes saves: the editing target's document, or why there is nothing to save yet.</summary>
/// <param name="Document">The document whose selected drawings become the shape, or null.</param>
/// <param name="Problem">The Mac's message when the selection cannot be saved now, or null.</param>
public sealed record ShapeSaveSource(CanvasDocument? Document, string? Problem);

public sealed partial class WorkspaceModel
{
    /// <summary>
    /// Whether a toolbar button's feature is built on Windows (every Mac button is now); the toolbar and the Tools window show
    /// a button that is not as unavailable, and its key does nothing (<see cref="PerformShortcutAction"/>).
    /// </summary>
    public static bool IsAvailable(ShortcutAction action) =>
        action is ShortcutAction.Properties or ShortcutAction.Board or ShortcutAction.SmartDraw or ShortcutAction.ClearAll
            or ShortcutAction.Spotlight or ShortcutAction.CursorHalo or ShortcutAction.PointerLens or ShortcutAction.ShareRegion;

    /// <summary>
    /// Mac MyShapesLibrary.saveSelection's checks: a selection on one display, a drawing canvas and no unfinished edit. The
    /// document's selected drawings are what <see cref="Library.ShapeLibrary.SaveAsync"/> saves.
    /// </summary>
    public ShapeSaveSource ShapeToSave()
    {
        VerifyAccess();
        if (SelectionSessions().Count(canvas => !canvas.Document.Selection.IsEmpty) > 1)
        {
            return new ShapeSaveSource(null, "Select drawings on one display before saving a reusable shape.");
        }
        if (_active is not { } active)
        {
            return new ShapeSaveSource(null, "Open a drawing canvas first.");
        }
        if (active.Edit is not null)
        {
            return new ShapeSaveSource(null, ShapeLibraryError.UnfinishedEdit.Message);
        }
        return new ShapeSaveSource(active.Document, null);
    }

    /// <summary>
    /// Mac insertSavedShape: adds fresh, editable copies of a saved shape at the center of the editing target's canvas as one
    /// Undo step, selects them, enters Draw and chooses Select. Throws <see cref="ShapeLibraryError"/> or
    /// <see cref="CanvasError"/> with the Mac's message when there is no canvas, an edit is unfinished, the canvas cannot keep
    /// its Undo history, or the drawing tools are hidden.
    /// </summary>
    public void InsertSavedShape(SavedShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        using ChangeScope scope = Change();
        if (_active is not { } active)
        {
            throw ShapeLibraryError.InvalidFile("Open a drawing canvas first.");
        }
        if (active.Edit is not null)
        {
            throw ShapeLibraryError.UnfinishedEdit;
        }
        // Mac: the center of what screen zoom shows, else of the canvas.
        CanvasDocument updated = shape.Adding(active.Document, VisibleCenter(active));
        UseLocalSelection(active);
        // Mac activateCanvasForEditing.
        if (_hidden || _transition is not null)
        {
            throw CanvasError.InvalidDocument("Show the drawing tools before inserting a saved shape.");
        }
        _mode = WorkspaceMode.Draw;
        foreach (Canvas canvas in _canvases.Values)
        {
            SetDrawing(canvas, true, focus: canvas == active);
        }
        active.Document = updated;
        active.Version = FreshVersion();
        RecordUsage(Metrics.UsageMetric.ReusableShape);
        _tool = Tool.Select;
        Synchronize();
        _status = $"Inserted {shape.Name} at the canvas center. Drag it to move it; Undo removes this copy.";
    }
}
