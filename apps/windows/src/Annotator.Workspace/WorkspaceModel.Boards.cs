using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Workspace.Files;

namespace Annotator.Workspace;

/// <summary>What Save board... needs before its dialog (Mac saveBoard): the suggested name and the dialog's note, or null.</summary>
public sealed record BoardSaveRequest(string SuggestedName, string? Message);

/// <summary>
/// What Export PNG... renders (Mac exportPNG): the editing target's objects on a canvas of <paramref name="Size"/> points at
/// <paramref name="Scale"/> pixels per point, over the board's color, or transparent over the desktop board. Covers and
/// effect regions are drawn opaque, so the PNG holds annotations only and never desktop pixels.
/// </summary>
public sealed record PngExport(ElementList Elements, Size Size, double Scale, RGBA? Background)
{
    /// <summary>The name the Save dialog suggests (Mac nameFieldStringValue).</summary>
    public const string SuggestedName = "Explanation.png";
}

public sealed partial class WorkspaceModel
{
    /// <summary>The Mac's status when a board action finds no drawing session.</summary>
    public const string NoSessionMessage = "Open a drawing session first.";

    /// <summary>The Mac's Save dialog note for a board with covers or effects and pasted images.</summary>
    public const string RetainedImagesMessage = "Editable boards retain original images. Share a flattened PNG for censored content.";

    public const string BoardSavedMessage =
        "Drawing layers and pasted images saved locally. No ambient desktop capture was added.";

    public const string BoardOpenedMessage = "Board opened. Check cover positions before sharing.";

    public const string PngExportedMessage = "Annotations exported. This PNG does not contain desktop pixels.";

    private const string BoardOpenFailure = "Choose another Annotator board. Your current drawing has not changed. ";

    /// <summary>
    /// Mac saveBoard before its dialog: the editing target's text is committed; without a drawing session the status says so
    /// and there is nothing to save (null). The note warns that a board with covers keeps the pasted images under them.
    /// </summary>
    public BoardSaveRequest? PrepareBoardSave()
    {
        using ChangeScope scope = Change();
        if (!CommitTextForFileAction())
        {
            return null;
        }
        CanvasDocument document = _active!.Document;
        bool images = false;
        foreach (ref readonly Element element in document.Elements)
        {
            images |= element.Tool == Tool.Image;
        }
        return new BoardSaveRequest(BoardFiles.SuggestedName, document.HasProtection && images ? RetainedImagesMessage : null);
    }

    /// <summary>
    /// The editing target's document as Save board... writes it once its dialog closed (Mac: active.document after runModal),
    /// or null with the status <see cref="NoSessionMessage"/> when there is no drawing session any more.
    /// </summary>
    public CanvasDocument? BoardToSave()
    {
        using ChangeScope scope = Change();
        if (_active is not { } active)
        {
            _status = NoSessionMessage;
            return null;
        }
        return active.Document;
    }

    /// <summary>The board was written (Mac saveBoard's status).</summary>
    public void BoardSaved()
    {
        using ChangeScope scope = Change();
        RecordUsage(Metrics.UsageMetric.BoardSave);
        _status = BoardSavedMessage;
    }

    /// <summary>
    /// Mac openBoard(from:) once the file was read and checked (<see cref="BoardFiles.Load"/>): enters Draw, then replaces the
    /// editing target's board with <paramref name="document"/>, which starts with its own Undo history. A board with covers or
    /// effects is never replaced: the person removes them first. Failures keep the current board and are reported as the
    /// Mac reports them.
    /// </summary>
    public void OpenBoard(CanvasDocument document)
    {
        using ChangeScope scope = Change();
        try
        {
            BeginDrawing(recordsUsage: false);
            if (_active is not { } active)
            {
                return;
            }
            if (active.Document.HasProtection)
            {
                throw CanvasError.InvalidDocument("Remove existing protective covers explicitly before replacing this board.");
            }
            active.Edit = null;
            active.Document = document;
            active.Version = FreshVersion();
            RecordUsage(Metrics.UsageMetric.BoardOpen);
            Synchronize();
            _status = BoardOpenedMessage;
        }
        catch (Exception error) when (IsReportable(error))
        {
            ReportBoardOpenFailure(error);
        }
    }

    /// <summary>
    /// A board could not be opened (Mac openBoard(from:)'s catch): the message says to choose another board, that the current
    /// drawing is unchanged, and why.
    /// </summary>
    public void ReportBoardOpenFailure(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        using ChangeScope scope = Change();
        Report(CanvasError.InvalidDocument(BoardOpenFailure + Describe(error)));
    }

    /// <summary>
    /// Mac exportPNG before its dialog: the editing target's text is committed; false, with the status
    /// <see cref="NoSessionMessage"/>, when there is no drawing session.
    /// </summary>
    public bool PrepareExport()
    {
        using ChangeScope scope = Change();
        return CommitTextForFileAction();
    }

    /// <summary>
    /// What Export PNG... renders once its dialog closed (Mac exportPNG): every object of the editing target on its display, in
    /// points at the display's scale, over white for the whiteboard and the Mac's ink for the blackboard. Null, with the
    /// status <see cref="NoSessionMessage"/>, when there is no drawing session any more.
    /// </summary>
    public PngExport? ExportRequest()
    {
        using ChangeScope scope = Change();
        if (_active is not { } active)
        {
            _status = NoSessionMessage;
            return null;
        }
        RGBA? background = active.Board switch
        {
            Board.Whiteboard => Palette.White,
            Board.Blackboard => Palette.Ink,
            _ => null,
        };
        Rect bounds = active.Display.Bounds;
        return new PngExport(active.Document.Elements, new Size(bounds.Width, bounds.Height), active.Display.Scale, background);
    }

    /// <summary>The PNG was written (Mac exportPNG's status).</summary>
    public void PngExported()
    {
        using ChangeScope scope = Change();
        RecordUsage(Metrics.UsageMetric.PngExport);
        _status = PngExportedMessage;
    }

    /// <summary>
    /// Mac report(error) for work the host did outside the model, such as reading an image file to insert or writing a board:
    /// the error window and the status show its message.
    /// </summary>
    public void ReportFailure(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        using ChangeScope scope = Change();
        Report(error);
    }

    /// <summary>
    /// Dismisses the error that the error window shows (Mac WorkspaceErrorController.dismiss): only while it is still the
    /// current one, so a newer error is never cleared unseen. Returns false when a newer error replaced it.
    /// </summary>
    public bool DismissError(string displayed)
    {
        ArgumentNullException.ThrowIfNull(displayed);
        VerifyAccess();
        if (!string.Equals(_errorMessage, displayed, StringComparison.Ordinal))
        {
            return false;
        }
        DismissError();
        return true;
    }

    /// <summary>Mac active?.canvas.commitTextIfNeeded() with the guard that follows it; false sets the status.</summary>
    private bool CommitTextForFileAction()
    {
        if (_active is { } current && current.Edit is { Kind: EditKind.Text })
        {
            CommitTextIfNeeded(current);
            Synchronize();
        }
        if (_active is null)
        {
            _status = NoSessionMessage;
            return false;
        }
        return true;
    }
}
