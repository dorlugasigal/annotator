using System.Collections.Immutable;
using Annotator.Core;

namespace Annotator.Workspace;

public sealed partial class WorkspaceModel
{
    /// <summary>Selects every object on every display (Ctrl+A); the selection becomes workspace-wide with two or more displays.</summary>
    public void SelectAll()
    {
        using ChangeScope scope = Change();
        FinishStyleEditCore();
        if (_active is null)
        {
            return;
        }
        List<Canvas> targets = [.. _canvases.Values];
        foreach (Canvas canvas in targets)
        {
            CommitTextIfNeeded(canvas);
            CancelGesture(canvas);
            Guid[] ids = canvas.Document.Elements.Select(element => element.Id).ToArray();
            Mutate(canvas, (ref CanvasDocument document) => document.Select(ids));
        }
        _workspaceWide = targets.Count > 1;
        _tool = Tool.Select;
        Synchronize();
    }

    /// <summary>Undo on the editing target; a coordinated step undoes every display it changed, or none (spec 002).</summary>
    public void Undo()
    {
        using ChangeScope scope = Change();
        FinishZoomGestureCore();
        FinishStyleEditCore();
        if (_active is not { } active)
        {
            return;
        }
        if (active.Edit is not null)
        {
            CancelGesture(active);
            Synchronize();
            return;
        }
        if (ApplyWorkspaceHistory(redo: false))
        {
            _undoSerial++;
            return;
        }
        if (active.Document.CanUndo || active.Document.IsEditing)
        {
            if (active.Document.CanUndo)
            {
                _undoSerial++;
            }
            Mutate(active, (ref CanvasDocument document) => document.Undo());
        }
        Synchronize();
    }

    public void Redo()
    {
        using ChangeScope scope = Change();
        if (_active?.Edit is not null)
        {
            _status = "Finish or cancel the current edit before redoing.";
            return;
        }
        FinishStyleEditCore();
        if (ApplyWorkspaceHistory(redo: true))
        {
            return;
        }
        if (_active is { } active && active.Document.CanRedo)
        {
            Mutate(active, (ref CanvasDocument document) => document.Redo());
        }
        Synchronize();
    }

    /// <summary>Clears every display as one coordinated Undo step ("Clear all drawings — undoable").</summary>
    public void ClearCanvas()
    {
        using ChangeScope scope = Change();
        PerformAcrossDisplays((ref CanvasDocument document) => document.ClearAll());
    }

    /// <summary>
    /// Mac clearDrawingsKeepingProtection, "Clear drawings (keep effects)" in Focus and cleanup: removes every unlocked
    /// drawing that is not an effect or a cover, on every display, as one coordinated Undo step.
    /// </summary>
    public void ClearDrawingsKeepingProtection()
    {
        using ChangeScope scope = Change();
        PerformAcrossDisplays((ref CanvasDocument document) => document.ClearAnnotations());
    }

    /// <summary>
    /// Mac clearProtection, "Remove all censors" in Focus and cleanup: removes every effect and cover, locked or not, on every
    /// display, as one coordinated Undo step.
    /// </summary>
    public void ClearProtection()
    {
        using ChangeScope scope = Change();
        PerformAcrossDisplays((ref CanvasDocument document) => document.ClearProtection());
    }

    /// <summary>Deletes the selection, across displays when it is workspace-wide. Locked selections stay.</summary>
    public void DeleteSelected()
    {
        using ChangeScope scope = Change();
        if (IsLocked(SelectedElements()))
        {
            _status = "The selection is locked. Unlock it from Selection actions before deleting.";
            return;
        }
        Perform((ref CanvasDocument document) => document.DeleteSelection(includingProtection: true));
    }

    /// <summary>Backspace or Delete: cancels the current edit, else deletes the selection, else clears every display.</summary>
    public void DeleteCurrent()
    {
        using ChangeScope scope = Change();
        if (_active is not { } active)
        {
            return;
        }
        if (active.Edit is not null)
        {
            CancelGesture(active);
            Synchronize();
            return;
        }
        if (SelectedElements().Count > 0)
        {
            DeleteSelected();
        }
        else
        {
            ClearCanvas();
        }
    }

    /// <summary>
    /// Keyboard nudge (arrow keys move 1 point, Shift+arrow 10): moves the selection, on every display when
    /// it is workspace-wide, as one Undo step. Locked selections stay.
    /// </summary>
    public void TranslateSelection(Point offset)
    {
        using ChangeScope scope = Change();
        List<Element> selected = SelectedElements();
        if (selected.Count == 0)
        {
            return;
        }
        if (IsLocked(selected))
        {
            _status = "The selection is locked. Unlock it before moving.";
            return;
        }
        Perform((ref CanvasDocument document) => document.EditSelection((ref Element element) => element.Translate(offset)));
    }

    private void Perform(DocumentChange action)
    {
        if (_workspaceWide)
        {
            PerformAcrossDisplays(action);
            return;
        }
        FinishZoomGestureCore();
        FinishStyleEditCore();
        if (_active is not { } active)
        {
            _status = "Open a drawing session first.";
            return;
        }
        CommitTextIfNeeded(active);
        CancelGesture(active);
        try
        {
            Mutate(active, action);
            Synchronize();
        }
        catch (Exception error) when (IsReportable(error))
        {
            Report(error);
        }
    }

    private void PerformAcrossDisplays(DocumentChange mutation)
    {
        FinishZoomGestureCore();
        FinishStyleEditCore();
        if (_active is null)
        {
            return;
        }
        List<Canvas> targets = [.. _canvases.Values];
        foreach (Canvas canvas in targets)
        {
            CommitTextIfNeeded(canvas);
            CancelGesture(canvas);
        }
        try
        {
            var documents = new CanvasDocument[targets.Count];
            for (int index = 0; index < targets.Count; index++)
            {
                CanvasDocument document = targets[index].Document;
                document.Begin();
                mutation(ref document);
                documents[index] = document;
            }
            bool changed = false;
            for (int index = 0; index < targets.Count; index++)
            {
                changed |= targets[index].Document.Elements != documents[index].Elements
                    || targets[index].Document.Presentation != documents[index].Presentation;
            }
            if (!changed)
            {
                Synchronize();
                return;
            }
            CommitWorkspaceDocuments(documents, targets);
            Synchronize();
        }
        catch (Exception error) when (IsReportable(error))
        {
            Report(error);
        }
    }

    /// <summary>
    /// Mac commitWorkspaceDocuments: commits pending documents, with a shared operation when there are several,
    /// and assigns them only if every one keeps its Undo step.
    /// </summary>
    private void CommitWorkspaceDocuments(CanvasDocument[] documents, IReadOnlyList<Canvas> targets)
    {
        Guid? operation = targets.Count > 1 ? Guid.NewGuid() : null;
        for (int index = 0; index < documents.Length; index++)
        {
            documents[index].Commit(operation);
            if (!documents[index].CanUndo || (operation is not null && documents[index].UndoOperationId != operation))
            {
                throw new WorkspaceFailure(HistoryBudgetMessage);
            }
        }
        if (operation is { } id)
        {
            _workspaceHistory[id] = new HistoryEntry([.. documents.Select(document => document.Identity)], _workspaceWide);
        }
        for (int index = 0; index < documents.Length; index++)
        {
            targets[index].Document = documents[index];
            targets[index].Version = FreshVersion();
        }
    }

    /// <summary>Mac applyWorkspaceHistory: true when the step belongs to the workspace (applied or refused).</summary>
    private bool ApplyWorkspaceHistory(bool redo)
    {
        if (_active is not { } active)
        {
            return false;
        }
        Guid? operation = redo ? active.Document.RedoOperationId : active.Document.UndoOperationId;
        if (operation is not { } id || !_workspaceHistory.TryGetValue(id, out HistoryEntry? entry))
        {
            return false;
        }
        List<Canvas> targets = _canvases.Values.Where(canvas => entry.Documents.Contains(canvas.Document.Identity)).ToList();
        bool ready = targets.Count == entry.Documents.Count && targets.All(canvas =>
            canvas.Edit is null && !canvas.Document.IsEditing &&
            (redo ? canvas.Document.RedoOperationId : canvas.Document.UndoOperationId) == id);
        if (!ready)
        {
            Report(new WorkspaceFailure(HistoryChangedMessage));
            return true;
        }
        foreach (Canvas canvas in targets)
        {
            Mutate(canvas, redo ? (ref CanvasDocument document) => document.Redo() : (ref CanvasDocument document) => document.Undo());
        }
        _workspaceWide = entry.GlobalSelection;
        if (entry.GlobalSelection)
        {
            _tool = Tool.Select;
        }
        Synchronize();
        return true;
    }

    /// <summary>Mac useLocalSelection: other displays drop their selection and a workspace-wide selection ends.</summary>
    private void UseLocalSelection(Canvas session)
    {
        if (!_workspaceWide && _styleSessions.Count == 0 &&
            !_canvases.Values.Any(canvas => canvas != session && !canvas.Document.Selection.IsEmpty))
        {
            return;
        }
        FinishStyleEditCore();
        foreach (Canvas other in _canvases.Values)
        {
            if (other != session && !other.Document.Selection.IsEmpty)
            {
                Mutate(other, (ref CanvasDocument document) => document.Select(ImmutableHashSet<Guid>.Empty));
            }
        }
        _workspaceWide = false;
        Synchronize();
    }
}
