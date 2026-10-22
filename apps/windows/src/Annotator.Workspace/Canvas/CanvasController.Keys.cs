using Annotator.Core;

namespace Annotator.Workspace.Canvas;

public sealed partial class CanvasController
{
    /// <summary>
    /// Takes a key press on the canvas (Mac DrawingCanvas.keyDown with the spec 013 keyboard mapping). Returns true when
    /// the canvas used the key; false lets the platform apply its default.
    /// </summary>
    public bool OnKey(in KeyEvent e)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_workspace is null || !_hasDocument || !IsDrawing)
        {
            return false;
        }
        KeyModifiers modifiers = e.Modifiers;
        if (e.VirtualKey == VirtualKeys.Escape)
        {
            if (e.IsRepeat)
            {
                return true;
            }
            Escape();
            return true;
        }
        string key = e.Text?.ToLowerInvariant() ?? string.Empty;
        if (modifiers == KeyModifiers.Ctrl && e.VirtualKey == VirtualKeys.L && ToolLibraryKeyIsFree())
        {
            Request(CanvasRequest.ToolLibrary);
            return true;
        }
        if (!HasActiveEdit && (modifiers & ~KeyModifiers.Shift) == KeyModifiers.None && NudgeDirection(e.VirtualKey) is { } direction
            && (Workspace.SelectionCount > 0 || !_document.Selection.IsEmpty))
        {
            double distance = (modifiers & KeyModifiers.Shift) != 0 ? 10 : 1;
            _channel.Send(new CanvasCommand(CanvasCommandKind.Nudge, Display) { Offset = direction * distance });
            return true;
        }
        if (modifiers == KeyModifiers.Ctrl && e.VirtualKey == VirtualKeys.W)
        {
            _channel.Send(new CanvasCommand(CanvasCommandKind.ToggleBoard, Display) { Board = Board.Whiteboard });
            return true;
        }
        if (modifiers == KeyModifiers.Ctrl && e.VirtualKey == VirtualKeys.B)
        {
            _channel.Send(new CanvasCommand(CanvasCommandKind.ToggleBoard, Display) { Board = Board.Blackboard });
            return true;
        }
        if (e.VirtualKey == VirtualKeys.Enter && _linearId is not null)
        {
            if (!e.IsRepeat)
            {
                FinishLinearPath();
            }
            return true;
        }
        if (e.VirtualKey == VirtualKeys.Enter && modifiers == KeyModifiers.Ctrl && _text is not null)
        {
            CommitText();
            return true;
        }
        if ((modifiers & KeyModifiers.Ctrl) != 0)
        {
            return CommandKey(e.VirtualKey, modifiers);
        }
        if (e.VirtualKey is VirtualKeys.Back or VirtualKeys.Delete)
        {
            if (HasActiveEdit)
            {
                CancelGesture();
            }
            else
            {
                Send(CanvasCommandKind.DeleteCurrent);
            }
            return true;
        }
        if ((modifiers & KeyModifiers.Alt) == 0 && key.Length > 0)
        {
            if (InkShortcut.Choices.ContainsKey(key))
            {
                _channel.Send(new CanvasCommand(CanvasCommandKind.InkKey, Display) { Key = key });
                return true;
            }
            bool shift = (modifiers & KeyModifiers.Shift) != 0;
            if ((LocalShortcut.Create(key, shift) ?? LocalShortcut.Create(key)) is { } pressed
                && Workspace.Preferences.CommandForPressed(pressed) is { } command)
            {
                if (command.Tool is { } tool)
                {
                    _channel.Send(new CanvasCommand(CanvasCommandKind.ChooseTool, Display) { Tool = tool });
                }
                else if (command.Action is { } action)
                {
                    _channel.Send(new CanvasCommand(CanvasCommandKind.ShortcutAction, Display) { Action = action });
                }
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The Escape order (spec 009 and WIN-A11Y-04): commit text, else cancel the gesture, clicked arrow or region choice;
    /// otherwise the workspace stops zoom or the lens once they exist, or releases drawing input.
    /// </summary>
    public void Escape()
    {
        if (_text is not null)
        {
            CommitText();
        }
        else if (HasActiveEdit)
        {
            CancelGesture();
        }
        else
        {
            Send(CanvasCommandKind.Escape);
        }
    }

    /// <summary>Ctrl shortcuts (Mac Command shortcuts with Ctrl, plus Windows' Ctrl+Y and Ctrl+, ).</summary>
    private bool CommandKey(int virtualKey, KeyModifiers modifiers)
    {
        bool shift = (modifiers & KeyModifiers.Shift) != 0;
        if ((modifiers & KeyModifiers.Alt) != 0)
        {
            return false;
        }
        switch (virtualKey)
        {
            case VirtualKeys.Z:
                Send(shift ? CanvasCommandKind.Redo : CanvasCommandKind.Undo);
                return true;
            case VirtualKeys.Y when !shift:
                Send(CanvasCommandKind.Redo);
                return true;
            case VirtualKeys.A when !shift:
                Send(CanvasCommandKind.SelectAll);
                return true;
            case VirtualKeys.D when !shift:
                Send(CanvasCommandKind.Duplicate);
                return true;
            case VirtualKeys.G:
                Send(shift ? CanvasCommandKind.Ungroup : CanvasCommandKind.Group);
                return true;
            case VirtualKeys.S when !shift:
                Request(CanvasRequest.SaveBoard);
                return true;
            case VirtualKeys.O when !shift:
                Request(CanvasRequest.OpenBoard);
                return true;
            case VirtualKeys.C when !shift:
                CommitText();
                Send(CanvasCommandKind.Copy);
                return true;
            case VirtualKeys.X when !shift:
                CommitText();
                Send(CanvasCommandKind.Cut);
                return true;
            case VirtualKeys.V when !shift:
                CommitText();
                CancelGesture();
                Send(CanvasCommandKind.Paste);
                return true;
            case VirtualKeys.OemComma when !shift:
                Request(CanvasRequest.Settings);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Mac toolLibraryShortcut: Ctrl+L opens the tool library unless a global shortcut uses those keys.</summary>
    private bool ToolLibraryKeyIsFree() => Shortcuts.WindowsShortcuts.ToolLibraryKeyIsFree(Workspace.Preferences);

    private static Point? NudgeDirection(int virtualKey) => virtualKey switch
    {
        VirtualKeys.Left => new Point(-1, 0),
        VirtualKeys.Right => new Point(1, 0),
        VirtualKeys.Down => new Point(0, 1),
        VirtualKeys.Up => new Point(0, -1),
        _ => null,
    };
}
