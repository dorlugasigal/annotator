using System.Collections.Immutable;
using Annotator.Core;

namespace Annotator.Workspace;

public sealed partial class WorkspaceModel
{
    /// <summary>
    /// A canvas started an edit from <paramref name="baseVersion"/>. The display becomes the editing target and
    /// shows the edit as in progress (Undo cancels it, Redo waits).
    /// </summary>
    public EditResult BeginEdit(DisplayId display, DocumentVersion baseVersion, EditKind kind)
    {
        using ChangeScope scope = Change();
        if (!_canvases.TryGetValue(display, out Canvas? canvas))
        {
            return new EditResult.Rejected(EditRejection.UnknownDisplay, null);
        }
        if (canvas.Version != baseVersion)
        {
            return new EditResult.Rejected(EditRejection.StaleVersion, null);
        }
        if (_styleSessions.Count != 0)
        {
            return new EditResult.Rejected(EditRejection.StyleEditOpen, null);
        }
        if (!ActivateDrawingSession(canvas, clearsSelection: false))
        {
            return new EditResult.Rejected(EditRejection.NotDrawing, null);
        }
        if (canvas.Version != baseVersion)
        {
            return new EditResult.Rejected(EditRejection.StaleVersion, null);
        }
        UseLocalSelection(canvas);
        canvas.Edit = new PendingEdit(baseVersion, kind, null);
        return new EditResult.Accepted([new DisplayVersion(display, canvas.Version)]);
    }

    /// <summary>
    /// Stores the document a text edit or clicked path would leave if it ended now, so an interruption can keep it
    /// (Mac commitTextIfNeeded and finishLinearPath). Publishes a snapshot for text edits, whose selection and
    /// style the toolbar shows; path updates publish nothing.
    /// </summary>
    public EditResult UpdateEdit(DisplayId display, DocumentVersion baseVersion, CanvasDocument document)
    {
        VerifyAccess();
        if (!_canvases.TryGetValue(display, out Canvas? canvas))
        {
            return new EditResult.Rejected(EditRejection.UnknownDisplay, null);
        }
        if (canvas.Version != baseVersion || canvas.Edit is not { } edit || edit.Base != baseVersion)
        {
            return new EditResult.Rejected(EditRejection.StaleVersion, null);
        }
        if (document.Identity != canvas.Document.Identity)
        {
            return new EditResult.Rejected(EditRejection.DocumentMismatch, null);
        }
        if (edit.Kind == EditKind.Text)
        {
            using ChangeScope scope = Change();
            canvas.Edit = edit with { Interim = document };
            Synchronize();
        }
        else
        {
            canvas.Edit = edit with { Interim = document };
        }
        return new EditResult.Accepted([new DisplayVersion(display, canvas.Version)]);
    }

    /// <summary>The canvas cancelled its edit; the display keeps its document and version.</summary>
    public EditResult CancelEdit(DisplayId display, DocumentVersion baseVersion)
    {
        using ChangeScope scope = Change();
        if (!_canvases.TryGetValue(display, out Canvas? canvas))
        {
            return new EditResult.Rejected(EditRejection.UnknownDisplay, null);
        }
        if (canvas.Edit is not { } edit || edit.Base != baseVersion)
        {
            return new EditResult.Rejected(EditRejection.StaleVersion, null);
        }
        canvas.Edit = null;
        Synchronize();
        return new EditResult.Accepted([new DisplayVersion(display, canvas.Version)]);
    }

    /// <summary>
    /// Takes a finished canvas edit when every display named is still at the edit's base version, then moves
    /// each to <see cref="DocumentVersion.Next"/>. Otherwise nothing changes and the result says why.
    /// </summary>
    public EditResult Submit(EditSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        using ChangeScope scope = Change();
        if (Validate(submission) is { } refusal)
        {
            if (refusal.Message is { } message)
            {
                _errorMessage = message;
                _status = message;
            }
            Refuse(submission);
            return refusal;
        }
        Canvas primary = _canvases[submission.Edits[0].Display];
        if (_canvases.TryGetValue(OriginOf(submission), out Canvas? origin) && origin.Edit is { } reported
            && submission.Edits.Any(edit => edit.Display == OriginOf(submission) && edit.BaseVersion == reported.Base))
        {
            // This submission finishes the origin's reported edit, so making another display the editing target
            // must not cancel it.
            origin.Edit = null;
        }
        if (!ActivateDrawingSession(primary, clearsSelection: false))
        {
            Refuse(submission);
            return new EditResult.Rejected(EditRejection.NotDrawing, null);
        }
        if (Validate(submission) is { } stale)
        {
            Refuse(submission);
            return stale;
        }
        var versions = ImmutableArray.CreateBuilder<DisplayVersion>(submission.Edits.Length);
        foreach (DisplayEdit edit in submission.Edits)
        {
            Canvas canvas = _canvases[edit.Display];
            canvas.Document = edit.Document;
            canvas.Version = edit.BaseVersion.Next;
            canvas.Edit = null;
            versions.Add(new DisplayVersion(edit.Display, canvas.Version));
        }
        if (submission.OperationId is { } operation && submission.Edits.Length > 1)
        {
            _workspaceHistory[operation] = new HistoryEntry([.. submission.Edits.Select(edit => edit.Document.Identity)], _workspaceWide);
        }
        if (submission.UsesLocalSelection)
        {
            UseLocalSelection(primary);
        }
        if (submission.ToolAfter is { } tool)
        {
            _tool = tool;
        }
        Synchronize();
        return new EditResult.Accepted(versions.MoveToImmutable());
    }

    /// <summary>Runs a canvas key command (Mac DrawingCanvas.keyDown); key commands first make that display the editing target.</summary>
    public CanvasCommandResult Perform(CanvasCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        using ChangeScope scope = Change();
        if (!_canvases.TryGetValue(command.Display, out Canvas? canvas))
        {
            return CanvasCommandResult.UnknownDisplay;
        }
        switch (command.Kind)
        {
            case CanvasCommandKind.ReleaseInput:
                UseApps();
                return CanvasCommandResult.Done;
            case CanvasCommandKind.TurnOffTips:
                SetShortcutHints(false);
                return CanvasCommandResult.Done;
            case CanvasCommandKind.Status:
                if (string.IsNullOrWhiteSpace(command.Message))
                {
                    return CanvasCommandResult.NotHandled;
                }
                _status = command.Message;
                return CanvasCommandResult.Done;
            case CanvasCommandKind.Report:
                if (string.IsNullOrWhiteSpace(command.Message))
                {
                    return CanvasCommandResult.NotHandled;
                }
                _errorMessage = command.Message;
                _status = command.Message;
                return CanvasCommandResult.Done;
            case CanvasCommandKind.TextStyle:
                if (command.Style is not { } textStyle || canvas.Edit is not { Kind: EditKind.Text } textEdit)
                {
                    return CanvasCommandResult.NotHandled;
                }
                canvas.Edit = textEdit with { TextStyle = textStyle, Label = command.Label };
                Synchronize();
                return CanvasCommandResult.Done;
        }
        if (_mode == WorkspaceMode.Draw && !ActivateDrawingSession(canvas, clearsSelection: true))
        {
            return CanvasCommandResult.NotDrawing;
        }
        switch (command.Kind)
        {
            case CanvasCommandKind.Undo:
                Undo();
                break;
            case CanvasCommandKind.Redo:
                Redo();
                break;
            case CanvasCommandKind.SelectAll:
                SelectAll();
                break;
            case CanvasCommandKind.DeleteCurrent:
                DeleteCurrent();
                break;
            case CanvasCommandKind.Escape:
                if (canvas.Edit is { Kind: EditKind.Text })
                {
                    CommitTextIfNeeded(canvas);
                    Synchronize();
                }
                else if (canvas.Edit is not null)
                {
                    CancelGesture(canvas);
                    Synchronize();
                }
                else if (_screenZoomEnabled || _magnifierEnabled)
                {
                    // Mac keyDown: Escape stops zoom or the lens before it releases drawing input.
                    HandleZoomEscape();
                }
                else
                {
                    UseApps();
                }
                break;
            case CanvasCommandKind.InkKey:
                if (command.Key is null || !ApplyInkKey(command.Key))
                {
                    return CanvasCommandResult.NotHandled;
                }
                break;
            case CanvasCommandKind.ChooseTool:
                // An effect key that waits for screen access was handled: the explanation shows.
                if (command.Tool is not { } tool || Choose(tool) is not (ToolChoice.Chosen or ToolChoice.NeedsScreenAccess))
                {
                    return CanvasCommandResult.NotHandled;
                }
                break;
            case CanvasCommandKind.Nudge:
                if (command.Offset is not { } offset || canvas.Edit is not null || SelectedElements().Count == 0)
                {
                    return CanvasCommandResult.NotHandled;
                }
                TranslateSelection(offset);
                break;
            case CanvasCommandKind.Copy:
                if (!CopySelection(cut: false))
                {
                    return CanvasCommandResult.NotHandled;
                }
                break;
            case CanvasCommandKind.Cut:
                if (!CopySelection(cut: true))
                {
                    return CanvasCommandResult.NotHandled;
                }
                break;
            case CanvasCommandKind.Paste:
                if (_clipboard is null)
                {
                    return CanvasCommandResult.NotHandled;
                }
                Paste();
                break;
            case CanvasCommandKind.Duplicate:
                Perform((ref CanvasDocument document) => document.DuplicateSelection());
                break;
            case CanvasCommandKind.Group:
                Perform((ref CanvasDocument document) => document.GroupSelection());
                break;
            case CanvasCommandKind.Ungroup:
                Perform((ref CanvasDocument document) => document.UngroupSelection());
                break;
            case CanvasCommandKind.BringToFront:
                Perform((ref CanvasDocument document) => document.ArrangeSelection(toFront: true));
                break;
            case CanvasCommandKind.SendToBack:
                Perform((ref CanvasDocument document) => document.ArrangeSelection(toFront: false));
                break;
            case CanvasCommandKind.Lock:
                Perform((ref CanvasDocument document) => document.SetSelectionLocked(true));
                break;
            case CanvasCommandKind.Unlock:
                Perform((ref CanvasDocument document) => document.SetSelectionLocked(false));
                break;
            case CanvasCommandKind.DeleteSelected:
                DeleteSelected();
                break;
            case CanvasCommandKind.ToggleBoard:
                if (command.Board is not { } board)
                {
                    return CanvasCommandResult.NotHandled;
                }
                SetBoard(Board == board ? Board.Desktop : board);
                break;
            case CanvasCommandKind.ShortcutAction:
                if (command.Action is not { } action || !PerformShortcutAction(action))
                {
                    return CanvasCommandResult.NotHandled;
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command.Kind, "Unknown canvas command.");
        }
        return CanvasCommandResult.Done;
    }

    /// <summary>The editing target's board (Mac board).</summary>
    private Board Board => _active?.Board ?? Board.Desktop;

    /// <summary>Mac setBoard: shows a board behind the editing target's drawings, opening drawing first when needed.</summary>
    public void SetBoard(Board board)
    {
        using ChangeScope scope = Change();
        if (_active is null)
        {
            BeginDrawing();
        }
        if (_active is not { } active)
        {
            return;
        }
        active.Board = board;
    }

    /// <summary>Smart Draw on or off (the Pen preference and Settings switch); completed drawings stay unchanged.</summary>
    public void SetSmartDraw(bool enabled)
    {
        using ChangeScope scope = Change();
        if (_preferences.SmartDraw == enabled)
        {
            return;
        }
        _preferences.SmartDraw = enabled;
        PersistPreferences();
    }

    /// <summary>
    /// Mac performShortcutAction for the toolbar buttons: Properties, Board, Spotlight, Cursor halo, Smart draw, Share region
    /// (with region sharing attached), Clear all and Pointer lens. Returns false when the button cannot act, so its key does
    /// nothing.
    /// </summary>
    public bool PerformShortcutAction(ShortcutAction action)
    {
        using ChangeScope scope = Change();
        switch (action)
        {
            case ShortcutAction.Properties:
                CyclePropertiesMode();
                return true;
            case ShortcutAction.Board:
                SetBoard(Board.Next);
                _status = $"{Board.RawValue} background.";
                return true;
            case ShortcutAction.Spotlight:
                SetSpotlight(!_spotlight);
                _status = _spotlight ? "Spotlight is on." : "Spotlight is off.";
                return true;
            case ShortcutAction.CursorHalo:
                SetCursorHalo(!_cursorHalo);
                _status = _cursorHalo ? "Cursor halo is on." : "Cursor halo is off.";
                return true;
            case ShortcutAction.SmartDraw:
                SetSmartDraw(!_preferences.SmartDraw);
                _status = _preferences.SmartDraw ? "Smart draw is on." : "Smart draw is off.";
                return true;
            case ShortcutAction.ShareRegion:
                return OpenRegionSharing();
            case ShortcutAction.ClearAll:
                ClearCanvas();
                return true;
            case ShortcutAction.PointerLens:
                ToggleMagnifier();
                return true;
            case ShortcutAction.FocusMenu or ShortcutAction.ZoomMenu:
                // Menus open with a click and have no key.
                return false;
            default:
                return false;
        }
    }

    private static DisplayId OriginOf(EditSubmission submission) => submission.Origin ?? submission.Edits[0].Display;

    /// <summary>A refused edit ends the origin canvas's reported edit and tells that canvas through its snapshot.</summary>
    private void Refuse(EditSubmission submission)
    {
        if (_canvases.TryGetValue(OriginOf(submission), out Canvas? origin))
        {
            origin.Edit = null;
            origin.Refused++;
        }
    }

    private EditResult.Rejected? Validate(EditSubmission submission)
    {
        var seen = new HashSet<DisplayId>();
        foreach (DisplayEdit edit in submission.Edits)
        {
            if (!_canvases.TryGetValue(edit.Display, out Canvas? canvas))
            {
                return new EditResult.Rejected(EditRejection.UnknownDisplay, null);
            }
            if (!seen.Add(edit.Display))
            {
                return new EditResult.Rejected(EditRejection.InvalidHistory, null);
            }
            if (canvas.Version != edit.BaseVersion)
            {
                return new EditResult.Rejected(EditRejection.StaleVersion, null);
            }
            if (edit.Document.Identity != canvas.Document.Identity)
            {
                return new EditResult.Rejected(EditRejection.DocumentMismatch, null);
            }
            if (edit.Document.IsEditing)
            {
                return new EditResult.Rejected(EditRejection.StillEditing, null);
            }
        }
        if (submission.Edits.Length > 1)
        {
            DisplayId origin = OriginOf(submission);
            foreach (DisplayEdit edit in submission.Edits)
            {
                if (edit.Display != origin && _canvases[edit.Display].Edit is not null)
                {
                    return new EditResult.Rejected(EditRejection.StillEditing, OtherDisplayEditingMessage);
                }
            }
        }
        if (_styleSessions.Count != 0)
        {
            return new EditResult.Rejected(EditRejection.StyleEditOpen, null);
        }
        if (_mode != WorkspaceMode.Draw || _hidden)
        {
            return new EditResult.Rejected(EditRejection.NotDrawing, null);
        }
        if (submission.Edits.Length > 1)
        {
            if (submission.OperationId is not { } operation)
            {
                return new EditResult.Rejected(EditRejection.InvalidHistory, null);
            }
            foreach (DisplayEdit edit in submission.Edits)
            {
                if (!edit.Document.CanUndo || edit.Document.UndoOperationId != operation)
                {
                    return new EditResult.Rejected(EditRejection.InvalidHistory, HistoryBudgetMessage);
                }
            }
        }
        return null;
    }
}

/// <summary>The outcome of <see cref="WorkspaceModel.Perform"/>.</summary>
public enum CanvasCommandResult
{
    Done,

    /// <summary>The command did not apply (for example a key that is not an ink key); let the key go elsewhere.</summary>
    NotHandled,

    UnknownDisplay,

    NotDrawing,
}
