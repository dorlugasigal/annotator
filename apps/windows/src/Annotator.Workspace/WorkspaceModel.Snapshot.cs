using System.Collections.Immutable;
using Annotator.Core;

namespace Annotator.Workspace;

public sealed partial class WorkspaceModel
{
    /// <summary>The editing target first, then, for a workspace-wide selection, the other displays from left to right.</summary>
    private List<Canvas> SelectionSessions()
    {
        if (_active is not { } active)
        {
            return [];
        }
        if (!_workspaceWide)
        {
            return [active];
        }
        return [active, .. _canvases.Values.Where(canvas => canvas != active).OrderBy(canvas => canvas.Display.DesktopFrame.X)];
    }

    private List<Element> SelectedElements()
    {
        var selected = new List<Element>();
        foreach (Canvas canvas in SelectionSessions())
        {
            CanvasDocument document = ShownDocument(canvas);
            ImmutableHashSet<Guid> selection = document.Selection;
            if (selection.IsEmpty)
            {
                continue;
            }
            foreach (ref readonly Element element in document.Elements)
            {
                if (selection.Contains(element.Id))
                {
                    selected.Add(element);
                }
            }
        }
        return selected;
    }

    /// <summary>
    /// The document whose selection the toolbar shows: the last document a text edit reported, or the display's
    /// document (Mac: the session document during text editing).
    /// </summary>
    private static CanvasDocument ShownDocument(Canvas canvas) =>
        canvas.Edit is { Kind: EditKind.Text, Interim: { } interim } ? interim : canvas.Document;

    /// <summary>True while the editing target's canvas edits text or a shape label.</summary>
    private bool EditsText => _active?.Edit is { Kind: EditKind.Text };

    private static List<Element> Editable(List<Element> selected) => selected.Where(element => !element.Locked).ToList();

    private static bool IsLocked(List<Element> selected) => selected.Count > 0 && selected.All(element => element.Locked);

    private Tool EffectiveTool(List<Element> selected)
    {
        if (EditsText)
        {
            return Tool.Text;
        }
        if (selected.Count == 0)
        {
            return _tool;
        }
        List<Element> editable = Editable(selected);
        List<Element> candidates = editable.Count == 0 ? selected : editable;
        Tool first = candidates[0].Tool;
        return candidates.All(element => element.Tool == first) ? first : Tool.Select;
    }

    private bool CanClearCanvas() =>
        _active is not null && _canvases.Values.Any(canvas =>
            !canvas.Document.Elements.IsEmpty || canvas.Edit is not null || canvas.Document.Presentation.Mode != PresentationMode.Off);

    /// <summary>The Mac's synchronize(): drops history entries no document holds and shows the selection's or tool's style.</summary>
    private void Synchronize()
    {
        SynchronizeZoom();
        if (_workspaceHistory.Count > 0)
        {
            var operations = new HashSet<Guid>();
            foreach (Canvas canvas in _canvases.Values)
            {
                operations.UnionWith(canvas.Document.HistoryOperationIds);
            }
            foreach (Guid operation in _workspaceHistory.Keys.ToArray())
            {
                if (!operations.Contains(operation))
                {
                    _workspaceHistory.Remove(operation);
                }
            }
        }
        List<Element> selected = SelectedElements();
        List<Element> editable = Editable(selected);
        if (_active?.Edit is { Kind: EditKind.Text } text)
        {
            _style = text.TextStyle ?? (editable.Count > 0 ? editable[0].Style : StyleFor(Tool.Text));
        }
        else if (editable.Count > 0)
        {
            _style = editable[0].Style;
        }
        else if (selected.Count > 0)
        {
            _style = selected[0].Style;
        }
        else
        {
            _style = StyleFor(_tool);
        }
    }

    /// <summary>Port of the Mac toolbarItems: a profile's item list wins only while it matches its tool list.</summary>
    private ValueList<ShortcutCommand> ToolbarItems()
    {
        IEnumerable<Tool> configured = _preferences.ToolbarTools is { } names
            ? names.Select(ToolExtensions.FromRawValue).Where(tool => tool is not null).Select(tool => tool!.Value)
            : WorkspacePreferences.DefaultToolbar;
        List<Tool> legacy = NormalizedTools(configured);
        var seen = new HashSet<ShortcutCommand>();
        var items = new List<ShortcutCommand>();
        foreach (string id in _preferences.ToolbarItems ?? ValueList<string>.Empty)
        {
            if (ShortcutCommand.FromId(id) is { } command && seen.Add(command))
            {
                items.Add(command);
            }
        }
        List<Tool> tools = items.Where(item => item.Tool is not null).Select(item => item.Tool!.Value).ToList();
        if (!items.Contains(ShortcutCommand.ForTool(Tool.Select)) || !NormalizedTools(tools).SequenceEqual(legacy))
        {
            return legacy.Select(ShortcutCommand.ForTool).ToValueList();
        }
        return items.ToValueList();
    }

    private static List<Tool> NormalizedTools(IEnumerable<Tool> tools)
    {
        var seen = new HashSet<Tool>();
        return tools.Select(tool => tool.IsProtection ? Tool.Cover : tool).Where(seen.Add).ToList();
    }

    private WorkspaceSnapshot BuildSnapshot(ulong revision)
    {
        Canvas? active = _active;
        List<Element> selected = SelectedElements();
        List<Element> editable = Editable(selected);
        bool locked = IsLocked(selected);
        bool canClear = CanClearCanvas();
        Tool effective = EffectiveTool(selected);
        string deleteLabel = "Delete";
        if (active is not null)
        {
            deleteLabel = active.Edit is not null ? "Cancel current drawing"
                : selected.Count > 0 ? (locked ? "Unlock selection to delete" : "Delete selection")
                : active.Document.Presentation.Mode != PresentationMode.Off ? "Clear canvas and stop zoom"
                : !active.Document.Elements.IsEmpty ? "Clear canvas"
                : "Delete";
        }
        var displays = ImmutableArray.CreateBuilder<DisplaySnapshot>(_canvases.Count);
        foreach (Canvas canvas in _canvases.Values.OrderBy(canvas => canvas.Display.DesktopFrame.X).ThenBy(canvas => canvas.Display.DesktopFrame.Y))
        {
            displays.Add(new DisplaySnapshot(canvas.Display, canvas.Document, canvas.Version, canvas.TakesInput, canvas == active,
                                             canvas.Edit?.Kind, canvas.Board, canvas.Refused, canvas.Edit?.TextStyle, canvas.PresentationCut,
                                             canvas.Edit?.Label));
        }
        LabelEdit? editedLabel = active?.Edit is { Kind: EditKind.Text, Label: { } label } ? label : null;
        return new WorkspaceSnapshot
        {
            Revision = revision,
            Mode = _mode,
            WorkspaceHidden = _hidden,
            ToolbarVisible = _toolbarVisible,
            ToolbarCollapsed = _toolbarCollapsed,
            Visibility = _transition,
            Tool = _tool,
            EffectiveTool = effective,
            Style = _style,
            EditingShapeLabel = editedLabel is not null,
            EditingLabelFollowsStroke = editedLabel is { FollowsStroke: true },
            Displays = displays.MoveToImmutable(),
            ActiveDisplay = active?.Display.Id,
            SelectionIsWorkspaceWide = _workspaceWide,
            SelectionCount = selected.Count,
            SelectionIsLocked = locked,
            SelectionSupportsFill = selected.Count == 0 ? effective.SupportsFill : editable.Any(element => element.Tool.SupportsFill),
            CanUndo = active is not null && (active.Document.CanUndo || active.Document.IsEditing || active.Edit is not null),
            CanRedo = active is not null && active.Document.CanRedo && !active.Document.IsEditing && active.Edit is null,
            UndoSerial = _undoSerial,
            CanClearCanvas = canClear,
            CanDeleteCurrent = active is not null && (active.Edit is not null || (selected.Count > 0 ? editable.Count > 0 : canClear)),
            DeleteActionLabel = deleteLabel,
            DeleteActionSymbol = active?.Edit is not null ? "xmark.circle" : "trash",
            ToolbarItems = ToolbarItems(),
            Status = _status,
            ErrorMessage = _errorMessage,
            FocusRequest = _focus,
            GlobalShortcuts = _shortcuts,
            Preferences = _preferences,
            Board = Board,
            TextEditingAvailable = _textEditing,
            CaptureFailureMessage = _captureFailureMessage,
            CaptureVerified = _captureVerified,
            ScreenAccess = _screenAccess,
            CensorFallbackActive = active is { EffectRegions: true, EffectsReady: false },
            EffectsRetry = _effectsRetry,
            Spotlight = _spotlight,
            CursorHalo = _cursorHalo,
            PointerEffectsSerial = _pointerEffectsSerial,
            ZoomStart = _zoomStartState,
            ScreenZoomEnabled = _screenZoomEnabled,
            MagnifierEnabled = _magnifierEnabled,
            Magnification = Magnification,
            ZoomGesturesAvailable = _zoomGesturesAvailable,
            ZoomScrollStatus = ZoomScrollStatus,
            ZoomEntrance = _zoomEntrance,
            ZoomScrollSerial = _zoomScrollSerial,
            AnimatesScreenZoom = !_reducesMotion(),
            ZoomShortcutLabel = ZoomShortcutLabel,
            RegionSharing = _sharing?.Snapshot ?? Sharing.RegionSharingSnapshot.Off,
        };
    }

    /// <summary>Test access to a display's live document.</summary>
    internal CanvasDocument DocumentOf(DisplayId display) => _canvases[display].Document;

    /// <summary>Replaces a display's document, for tests that need special history limits (Mac tests assign documents).</summary>
    internal void ReplaceDocumentForTesting(DisplayId display, CanvasDocument document)
    {
        using ChangeScope scope = Change();
        Canvas canvas = _canvases[display];
        canvas.Document = document;
        canvas.Version = FreshVersion();
        Synchronize();
    }
}
