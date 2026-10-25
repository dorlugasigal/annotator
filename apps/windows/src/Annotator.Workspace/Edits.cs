using System.Collections.Immutable;
using Annotator.Core;

namespace Annotator.Workspace;

/// <summary>A display's document after a canvas edit, and the version the edit started from.</summary>
/// <param name="Document">
/// The canvas's copy of the display's document with the edit committed (not editing). It must descend from
/// the snapshot document of <paramref name="BaseVersion"/>: same identity, history extended by the edit.
/// </param>
public sealed record DisplayEdit(DisplayId Display, DocumentVersion BaseVersion, CanvasDocument Document);

/// <summary>
/// A finished canvas edit, built on the overlay thread from snapshot documents and committed there, so the
/// canvas's copy equals what the workspace stores. One edit is one Undo step on its display; an edit of
/// several displays (moving objects to another display) is one coordinated Undo step and needs
/// <see cref="OperationId"/> as the undo operation of every document (Mac commitWorkspaceDocuments).
/// </summary>
public sealed record EditSubmission
{
    public EditSubmission(DisplayEdit edit)
        : this([edit], null)
    {
    }

    public EditSubmission(ImmutableArray<DisplayEdit> edits, Guid? operationId)
    {
        if (edits.IsDefaultOrEmpty)
        {
            throw new ArgumentException("An edit names at least one display.", nameof(edits));
        }
        Edits = edits;
        OperationId = operationId;
    }

    /// <summary>The changed displays. The first one becomes the editing target.</summary>
    public ImmutableArray<DisplayEdit> Edits { get; }

    /// <summary>The shared undo operation of a multi-display edit; null for one display.</summary>
    public Guid? OperationId { get; }

    /// <summary>The tool to select afterwards, for example Select after finishing a clicked arrow.</summary>
    public Tool? ToolAfter { get; init; }

    /// <summary>
    /// True when a click on this canvas returns to local editing: other displays drop their selection and a
    /// workspace-wide selection ends (Mac useLocalSelection).
    /// </summary>
    public bool UsesLocalSelection { get; init; }

    /// <summary>
    /// The display whose canvas sent the edit; null means the first display. A refused edit ends that canvas's
    /// reported edit and counts in its <see cref="DisplaySnapshot.RefusedEdits"/>, so the canvas shows the
    /// workspace's document again. The other displays of a multi-display edit must have no edit in progress.
    /// </summary>
    public DisplayId? Origin { get; init; }
}

/// <summary>Why the workspace refused a canvas edit. The canvas then shows the latest snapshot again.</summary>
public enum EditRejection
{
    /// <summary>The display is not part of the workspace (it was removed or never prepared).</summary>
    UnknownDisplay,

    /// <summary>The display changed or the edit was cancelled after the edit started.</summary>
    StaleVersion,

    /// <summary>The document does not descend from the display's document.</summary>
    DocumentMismatch,

    /// <summary>The document still has an open transaction; commit it on the canvas first.</summary>
    StillEditing,

    /// <summary>Canvases accept input only in Draw with the workspace shown.</summary>
    NotDrawing,

    /// <summary>A style change across displays is open (for example the color palette); finish it first.</summary>
    StyleEditOpen,

    /// <summary>A multi-display edit lacks its shared undo operation, names a display twice, or would lose Undo.</summary>
    InvalidHistory,
}

/// <summary>The typed outcome of a canvas edit message.</summary>
public abstract record EditResult
{
    private EditResult()
    {
    }

    /// <summary>The workspace took the edit; each display named is now at the returned version.</summary>
    public sealed record Accepted(ImmutableArray<DisplayVersion> Versions) : EditResult;

    /// <summary>The workspace left every display unchanged. <paramref name="Message"/> is user-facing when not null.</summary>
    public sealed record Rejected(EditRejection Reason, string? Message) : EditResult;
}

public readonly record struct DisplayVersion(DisplayId Display, DocumentVersion Version);

/// <summary>Commands a canvas sends to the workspace, mostly from its keys (Mac DrawingCanvas.keyDown).</summary>
public enum CanvasCommandKind
{
    Undo,
    Redo,

    /// <summary>Select every object on every display (Ctrl+A).</summary>
    SelectAll,

    /// <summary>Delete the selection, or clear every display when nothing is selected (Backspace or Delete).</summary>
    DeleteCurrent,

    /// <summary>Escape with nothing else to finish on the canvas: commit or cancel the reported edit, else release input.</summary>
    Escape,

    /// <summary>Alt+F4 or a close request: release drawing input like Use apps (WIN-OVR-12).</summary>
    ReleaseInput,

    /// <summary>The on-screen tip's ✕: turns tips off, as Show tips on screen in Settings does (Mac setShortcutHints(false)).</summary>
    TurnOffTips,

    /// <summary>An unmodified ink color key (r, g, b, y, o, p, c, w, k).</summary>
    InkKey,

    /// <summary>Choose a tool from its key.</summary>
    ChooseTool,

    /// <summary>An arrow key moved the selection by <see cref="CanvasCommand.Offset"/> (1 point, or 10 with Shift).</summary>
    Nudge,

    /// <summary>Copies the selected objects (Ctrl+C). Not handled when nothing is selected.</summary>
    Copy,

    /// <summary>Copies the selected objects, then deletes them (Ctrl+X). A selection with a locked object is not cut.</summary>
    Cut,

    /// <summary>Pastes copied objects, or an image when the clipboard holds no objects (Ctrl+V).</summary>
    Paste,

    /// <summary>Copies the selection 18 points down and right (Ctrl+D).</summary>
    Duplicate,

    /// <summary>Ctrl+G.</summary>
    Group,

    /// <summary>Ctrl+Shift+G.</summary>
    Ungroup,

    BringToFront,

    SendToBack,

    Lock,

    Unlock,

    /// <summary>The context menu's Delete selected: deletes the selection; a locked selection stays.</summary>
    DeleteSelected,

    /// <summary>
    /// Ctrl+W or Ctrl+B: shows <see cref="CanvasCommand.Board"/> on the editing target, or the desktop again when
    /// that board is already shown.
    /// </summary>
    ToggleBoard,

    /// <summary>A toolbar button key such as Smart draw or Clear all: <see cref="CanvasCommand.Action"/>.</summary>
    ShortcutAction,

    /// <summary>Shows <see cref="CanvasCommand.Message"/> on the status line.</summary>
    Status,

    /// <summary>Shows <see cref="CanvasCommand.Message"/> as an error, as the Mac's report(error).</summary>
    Report,

    /// <summary>
    /// The canvas edits text or a shape label in <see cref="CanvasCommand.Style"/>; the toolbar shows that style
    /// until the text edit ends (Mac editingShapeLabelStyle). For a shape label, <see cref="CanvasCommand.Label"/> says whether
    /// it follows its shape's stroke and what that stroke's paint is.
    /// </summary>
    TextStyle,
}

/// <summary>
/// A shape label that a canvas edits (Mac editingShapeLabelID): whether it follows its shape's stroke paint (Mac
/// editingLabelFollowsStroke), and that stroke's color and gradient, which Match outline gives it again (Mac
/// matchLabelToStroke).
/// </summary>
public readonly record struct LabelEdit(bool FollowsStroke, RGBA StrokeColor, ColorGradient? StrokeGradient)
{
    /// <summary>
    /// The style a label in <paramref name="style"/> shows once it follows the stroke (Core ShapeLabel.ResolvedStyle): the
    /// stroke's color, with its opacity, and gradient, and the label's own font, size, weight, alignment and object opacity.
    /// </summary>
    public Style Following(Style style)
    {
        style.Color = StrokeColor;
        style.StrokeGradient = StrokeGradient;
        return style;
    }
}

/// <summary>
/// A command from the canvas of <see cref="Display"/>. <see cref="Key"/>, <see cref="Tool"/>, <see cref="Offset"/>,
/// <see cref="Board"/>, <see cref="Action"/>, <see cref="Message"/>, <see cref="Style"/> and <see cref="Label"/> apply to their
/// kinds.
/// </summary>
public sealed record CanvasCommand(CanvasCommandKind Kind, DisplayId Display)
{
    public string? Key { get; init; }

    public Tool? Tool { get; init; }

    public Point? Offset { get; init; }

    public Board? Board { get; init; }

    public ShortcutAction? Action { get; init; }

    public string? Message { get; init; }

    public Style? Style { get; init; }

    /// <summary>The shape label that <see cref="CanvasCommandKind.TextStyle"/> reports, or null for a text object.</summary>
    public LabelEdit? Label { get; init; }
}

/// <summary>
/// How a canvas on another thread talks to the workspace. Implementations post every call to the workspace
/// thread in call order and never block; results arrive with the next snapshots.
/// </summary>
public interface IWorkspaceChannel
{
    /// <summary>A gesture or text edit started on a canvas from <paramref name="baseVersion"/>.</summary>
    void BeginEdit(DisplayId display, DocumentVersion baseVersion, EditKind kind);

    /// <summary>
    /// The document a text edit or clicked path would leave if it ended now (its transaction may still be open), so
    /// an interruption can keep it. A text edit's update also shows its selection on the toolbar.
    /// </summary>
    void UpdateEdit(DisplayId display, DocumentVersion baseVersion, CanvasDocument document);

    /// <summary>The canvas cancelled its edit; the display keeps its document.</summary>
    void CancelEdit(DisplayId display, DocumentVersion baseVersion);

    void Submit(EditSubmission submission);

    void Send(CanvasCommand command);
}
