using System.Collections.Immutable;
using Annotator.Core;

namespace Annotator.Workspace.Canvas;

/// <summary>The commands of the canvas context menu (Mac DrawingCanvas.menu(for:)).</summary>
public enum CanvasMenuCommand
{
    FinishArrow,
    CancelEdit,
    Properties,
    Copy,
    Cut,
    Duplicate,
    Group,
    Ungroup,
    BringToFront,
    SendToBack,
    Lock,
    Unlock,
    SaveToMyShapes,
    DeleteSelected,
    Paste,
    Undo,
    Redo,
    MoreShapesAndTools,

    /// <summary>
    /// The canvas's VoiceOver action Focus drawing tools (Mac focusDrawingTools), a menu item on Windows (WIN-A11Y-02): the
    /// toolbar takes the keyboard. Only the menu opened with Shift+F10 or the Menu key offers it.
    /// </summary>
    FocusDrawingTools,
}

/// <summary>One context menu entry; a separator has no command.</summary>
public sealed record CanvasMenuItem(CanvasMenuCommand? Command, string Title, bool IsEnabled)
{
    public static CanvasMenuItem Separator { get; } = new(null, string.Empty, false);

    public bool IsSeparator => Command is null;
}

/// <summary>A context menu to show at <paramref name="Position"/> (canvas points).</summary>
public sealed record CanvasMenu(Point Position, ImmutableArray<CanvasMenuItem> Items);

public sealed partial class CanvasController
{
    /// <summary>
    /// Mac menu(for:): a right-click selects the object under the pointer unless it is already selected, then offers the
    /// selection's actions. During an edit the menu only finishes or cancels it.
    /// </summary>
    private void OpenContextMenu(Point point)
    {
        if (!CanStartEdit)
        {
            return;
        }
        if (HasActiveEdit)
        {
            ContextMenuRequested?.Invoke(new CanvasMenu(point, [.. EditItems()]));
            return;
        }
        FinishTransitions();
        Element? hit = ElementStacking.Frontmost(point, _document.Elements);
        bool selectedHere = hit is not { } target || !_document.Selection.Contains(target.Id);
        if (selectedHere)
        {
            BeginWork(report: null);
            _work.Select(point);
            SubmitWork();
            Publish(CanvasChangeKinds.Selection, null);
        }
        ContextMenuRequested?.Invoke(new CanvasMenu(point, [.. SelectionItems(selectedHere)]));
    }

    /// <summary>
    /// Shift+F10 or the Menu key (WIN-A11Y-02): the context menu for what is selected now, without selecting anything, at the
    /// selection's center or at <paramref name="fallback"/> (canvas points) when nothing is selected. It ends with the
    /// canvas's VoiceOver action, Focus drawing tools, which the Mac offers to VoiceOver only. Returns false when the canvas
    /// takes no edits now (a hidden workspace or a transition), so the key goes back to Windows.
    /// </summary>
    public bool OpenKeyboardContextMenu(Point fallback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanStartEdit)
        {
            return false;
        }
        List<CanvasMenuItem> items;
        Point at = fallback;
        if (HasActiveEdit)
        {
            items = EditItems();
        }
        else
        {
            FinishTransitions();
            items = SelectionItems(selectedHere: false);
            Rect? selected = null;
            foreach (ref readonly Element element in _document.Elements)
            {
                if (_document.Selection.Contains(element.Id))
                {
                    selected = selected is { } union ? union.Union(element.Frame) : element.Frame;
                }
            }
            if (selected is { } bounds)
            {
                at = bounds.Center;
            }
        }
        items.Add(CanvasMenuItem.Separator);
        items.Add(new CanvasMenuItem(CanvasMenuCommand.FocusDrawingTools, CanvasAccessibility.FocusToolsAction, true));
        ContextMenuRequested?.Invoke(new CanvasMenu(at, [.. items]));
        return true;
    }

    /// <summary>The menu during an edit: finish a clicked arrow, or cancel the edit.</summary>
    private List<CanvasMenuItem> EditItems()
    {
        var items = new List<CanvasMenuItem>();
        if (ConstructingArrow)
        {
            items.Add(new CanvasMenuItem(CanvasMenuCommand.FinishArrow, "Finish arrow", true));
        }
        items.Add(new CanvasMenuItem(CanvasMenuCommand.CancelEdit, "Cancel edit", true));
        return items;
    }

    /// <summary>
    /// The selection's actions, then Paste, Undo, Redo and More shapes and tools. <paramref name="selectedHere"/> is true when
    /// the menu's own press chose the selection, so Delete reads the new selection's lock state.
    /// </summary>
    private List<CanvasMenuItem> SelectionItems(bool selectedHere)
    {
        var items = new List<CanvasMenuItem>();
        CanvasDocument document = _document;
        var selected = new List<Element>();
        foreach (ref readonly Element element in document.Elements)
        {
            if (document.Selection.Contains(element.Id))
            {
                selected.Add(element);
            }
        }
        bool editable = selected.Count > 0 && !selected.Any(element => element.Locked);
        if (selected.Count > 0)
        {
            items.Add(new CanvasMenuItem(CanvasMenuCommand.Properties, "Properties", true));
            items.Add(new CanvasMenuItem(CanvasMenuCommand.Copy, "Copy", true));
            items.Add(new CanvasMenuItem(CanvasMenuCommand.Cut, "Cut", editable));
            items.Add(new CanvasMenuItem(CanvasMenuCommand.Duplicate, "Duplicate", editable));
            items.Add(new CanvasMenuItem(CanvasMenuCommand.Group, "Group", editable && selected.Count > 1));
            items.Add(new CanvasMenuItem(CanvasMenuCommand.Ungroup, "Ungroup", editable && selected.Any(element => element.GroupId is not null)));
            items.Add(CanvasMenuItem.Separator);
            items.Add(new CanvasMenuItem(CanvasMenuCommand.BringToFront, "Bring to front", editable));
            items.Add(new CanvasMenuItem(CanvasMenuCommand.SendToBack, "Send to back", editable));
            items.Add(new CanvasMenuItem(CanvasMenuCommand.Lock, "Lock", selected.Any(element => !element.Locked)));
            items.Add(new CanvasMenuItem(CanvasMenuCommand.Unlock, "Unlock", selected.Any(element => element.Locked)));
            items.Add(CanvasMenuItem.Separator);
            items.Add(new CanvasMenuItem(CanvasMenuCommand.SaveToMyShapes, "Save to My shapes...",
                                         selected.All(element => !element.Tool.IsProtection && element.Tool != Tool.Image)));
            bool locked = selectedHere ? selected.All(element => element.Locked) : Workspace.SelectionIsLocked;
            items.Add(new CanvasMenuItem(CanvasMenuCommand.DeleteSelected,
                                         selected.Any(element => element.Tool.IsProtection) ? "Delete selected censor region" : "Delete selected",
                                         !locked));
            items.Add(CanvasMenuItem.Separator);
        }
        items.Add(new CanvasMenuItem(CanvasMenuCommand.Paste, "Paste", true));
        items.Add(new CanvasMenuItem(CanvasMenuCommand.Undo, "Undo", Workspace.CanUndo || document.CanUndo));
        items.Add(new CanvasMenuItem(CanvasMenuCommand.Redo, "Redo", Workspace.CanRedo));
        items.Add(new CanvasMenuItem(CanvasMenuCommand.MoreShapesAndTools, "More shapes and tools", true));
        return items;
    }

    /// <summary>Runs the context menu entry the person chose.</summary>
    public void PerformMenuItem(CanvasMenuCommand command)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        switch (command)
        {
            case CanvasMenuCommand.FinishArrow:
                FinishLinearPath();
                break;
            case CanvasMenuCommand.CancelEdit:
                CancelGesture();
                break;
            case CanvasMenuCommand.Properties:
                Request(CanvasRequest.Properties);
                break;
            case CanvasMenuCommand.Copy:
                Send(CanvasCommandKind.Copy);
                break;
            case CanvasMenuCommand.Cut:
                Send(CanvasCommandKind.Cut);
                break;
            case CanvasMenuCommand.Duplicate:
                Send(CanvasCommandKind.Duplicate);
                break;
            case CanvasMenuCommand.Group:
                Send(CanvasCommandKind.Group);
                break;
            case CanvasMenuCommand.Ungroup:
                Send(CanvasCommandKind.Ungroup);
                break;
            case CanvasMenuCommand.BringToFront:
                Send(CanvasCommandKind.BringToFront);
                break;
            case CanvasMenuCommand.SendToBack:
                Send(CanvasCommandKind.SendToBack);
                break;
            case CanvasMenuCommand.Lock:
                Send(CanvasCommandKind.Lock);
                break;
            case CanvasMenuCommand.Unlock:
                Send(CanvasCommandKind.Unlock);
                break;
            case CanvasMenuCommand.SaveToMyShapes:
                Request(CanvasRequest.SaveToMyShapes);
                break;
            case CanvasMenuCommand.DeleteSelected:
                Send(CanvasCommandKind.DeleteSelected);
                break;
            case CanvasMenuCommand.Paste:
                CancelGesture();
                Send(CanvasCommandKind.Paste);
                break;
            case CanvasMenuCommand.Undo:
                Send(CanvasCommandKind.Undo);
                break;
            case CanvasMenuCommand.Redo:
                Send(CanvasCommandKind.Redo);
                break;
            case CanvasMenuCommand.MoreShapesAndTools:
                Request(CanvasRequest.ToolLibrary);
                break;
            case CanvasMenuCommand.FocusDrawingTools:
                FocusTools();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command, "Unknown menu command.");
        }
    }
}
