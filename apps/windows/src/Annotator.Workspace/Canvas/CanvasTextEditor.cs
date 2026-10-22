using System.Collections.Immutable;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;

namespace Annotator.Workspace.Canvas;

/// <summary>Where the text editor's caret goes: its top in canvas points, its height, and the text's rotation around that top.</summary>
/// <param name="Top">The top of the caret line, in canvas points with the text's rotation applied.</param>
/// <param name="Height">The height of the caret's line, in points.</param>
/// <param name="Rotation">The text's rotation in radians: draw the caret turned by it around <paramref name="Top"/>.</param>
/// <param name="Color">The Mac's insertion point color: the text color without the text's opacity.</param>
public readonly record struct TextCaretPlacement(Point Top, double Height, double Rotation, RGBA Color);

/// <summary>
/// The platform-neutral half of the canvas text editor (spec 013 S-7, WIN-INP-09; the Mac's NSTextView on the canvas):
/// typing and editing keys, caret movement, selection with the pointer, Undo, copy and paste inside the editor, and what
/// it draws around the live text. The Windows half connects it to the Text Services Framework (input methods, the emoji
/// panel, voice typing and the touch keyboard), draws the blinking caret and gives the system clipboard and UI
/// Automation. Use it on the canvas thread; it lives for one editing session.
/// </summary>
public sealed class CanvasTextEditor
{
    /// <summary>The Mac's accent at 30%: over white this is the Mac's selectedTextBackgroundColor, #B3D7FF.</summary>
    private static readonly Paint SelectionPaint = Paint.System(SystemColor.Accent, 0.3);

    private readonly CanvasController _canvas;
    private readonly ITextEditingLayout _layout;
    private readonly ITextClipboard _clipboard;
    private readonly double _doubleClickTime;
    private readonly Size _doubleClickSize;
    private readonly List<Rect> _rects = [];
    private readonly DisplayListBuilder _lists = new();
    private CanvasTextSession _session;
    private TextEditorDecorations? _decorations;
    private double? _desiredX;
    private char _highSurrogate;
    private uint? _dragPointer;
    private (int Start, int End) _dragAnchor;
    private int _dragUnit;
    private bool _hasClick;
    private long _lastClickTime;
    private Point _lastClickPoint;
    private int _clicks;
    private bool _ended;

    /// <param name="doubleClickTime">The longest time between the clicks of a double-click, in milliseconds.</param>
    /// <param name="doubleClickSize">The area, in points, the next click of a double-click must hit.</param>
    public CanvasTextEditor(CanvasController canvas, CanvasTextSession session, ITextEditingLayout layout, ITextClipboard clipboard,
                            double doubleClickTime = 500, Size? doubleClickSize = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(clipboard);
        _canvas = canvas;
        _session = session;
        _layout = layout;
        _clipboard = clipboard;
        _doubleClickTime = doubleClickTime;
        _doubleClickSize = doubleClickSize ?? new Size(4, 4);
        // Like the Mac's text view after it gets its string: the caret after the last character.
        Buffer = new TextEditBuffer(session.Text);
    }

    /// <summary>The person changed the text by typing, deleting, pasting or Undo (not an input method): tell the input method.</summary>
    public event Action<TextChange>? TextEdited;

    /// <summary>The person moved the selection: tell the input method.</summary>
    public event Action? SelectionMoved;

    /// <summary>The input method's composition must end now, keeping its text (before an edit, a commit or a cancel).</summary>
    public event Action? FinishCompositionRequested;

    /// <summary>The text, selection, caret or composition changed: draw the editor again and update what reads it.</summary>
    public event Action? Changed;

    /// <summary>Copy or paste failed; the message is for the person.</summary>
    public event Action<string>? Failed;

    public TextEditBuffer Buffer { get; }

    public CanvasTextSession Session => _session;

    public string Text => Buffer.Text;

    /// <summary>The editor shows its caret only while its window has the keyboard focus.</summary>
    public bool HasFocus { get; private set; } = true;

    /// <summary>The session ended (committed or cancelled); the editor takes no more input.</summary>
    public bool IsEnded => _ended;

    /// <summary>The layout the renderer draws the live text with, on the baseline the scene draws it on.</summary>
    public TextLayoutRequest Layout => new(Buffer.Text, TextFont.Resolve(_session.Style), _session.Style.Alignment,
                                           new Size(_session.Frame.Width, _session.Frame.Height),
                                           _session.FirstBaseline(Buffer.Text) - _session.Frame.Y);

    /// <summary>What to draw below and above the live text; the same instance until something changes.</summary>
    public TextEditorDecorations Decorations => _decorations ??= BuildDecorations();

    /// <summary>The caret, or null while it is hidden (a selection, a character an input method is building, or no focus).</summary>
    public TextCaretPlacement? Caret
    {
        get
        {
            if (_ended || !HasFocus || !Buffer.Selection.IsEmpty || Buffer.InterimCharacter)
            {
                return null;
            }
            TextCaret caret = _layout.Caret(Layout, Buffer.Selection.Active);
            Point top = ToCanvas(new Point(caret.X, caret.Top));
            return new TextCaretPlacement(top, caret.Bottom - caret.Top, _session.Rotation, _session.Style.Color);
        }
    }

    /// <summary>The canvas controller changed the session: a new frame, style or text from the toolbar.</summary>
    public void Update(CanvasTextSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_ended)
        {
            return;
        }
        _session = session;
        Invalidate();
    }

    /// <summary>The editor's window got or lost the keyboard focus.</summary>
    public void SetFocus(bool focused)
    {
        if (HasFocus != focused)
        {
            HasFocus = focused;
            Changed?.Invoke();
        }
    }

    /// <summary>The session ended; nothing changes the canvas any more.</summary>
    public void End()
    {
        _ended = true;
        _dragPointer = null;
        _decorations = TextEditorDecorations.None;
    }

    /// <summary>
    /// A key while the editor is open. Returns true for every key it uses, which is every key but the system's Alt keys,
    /// so no key reaches the canvas's tool shortcuts while typing (the Mac's text view takes them all). Escape cancels a
    /// composition, then commits; Ctrl+Enter commits (spec 009, WIN-INP-09).
    /// </summary>
    public bool OnKey(in KeyEvent e)
    {
        if (_ended)
        {
            return false;
        }
        KeyModifiers modifiers = e.Modifiers;
        bool ctrl = (modifiers & KeyModifiers.Ctrl) != 0;
        bool shift = (modifiers & KeyModifiers.Shift) != 0;
        bool alt = (modifiers & KeyModifiers.Alt) != 0;
        if (alt && !ctrl)
        {
            return false;
        }
        switch (e.VirtualKey)
        {
            case VirtualKeys.Escape:
                if (!e.IsRepeat && !CancelComposition())
                {
                    Commit();
                }
                return true;
            case VirtualKeys.Enter when ctrl && !alt:
                if (!e.IsRepeat)
                {
                    Commit();
                }
                return true;
            case VirtualKeys.Enter when !ctrl:
                Type("\n");
                return true;
            case VirtualKeys.Tab when !ctrl:
                if (!shift)
                {
                    Type("\t");
                }
                return true;
            case VirtualKeys.Back:
                DeleteBackward(ctrl && !alt);
                return true;
            case VirtualKeys.Delete when shift && !ctrl:
                Copy(cut: true);
                return true;
            case VirtualKeys.Delete:
                DeleteForward(ctrl && !alt);
                return true;
            case VirtualKeys.Insert when ctrl && !shift:
                Copy(cut: false);
                return true;
            case VirtualKeys.Insert when shift && !ctrl:
                Paste();
                return true;
            case VirtualKeys.Left or VirtualKeys.Right:
                MoveHorizontally(e.VirtualKey == VirtualKeys.Right, ctrl, shift);
                return true;
            case VirtualKeys.Up or VirtualKeys.Down:
                MoveVertically(e.VirtualKey == VirtualKeys.Down, shift);
                return true;
            case VirtualKeys.Home or VirtualKeys.End:
                MoveToEdge(e.VirtualKey == VirtualKeys.End, ctrl, shift);
                return true;
        }
        if (ctrl && !alt)
        {
            switch (e.VirtualKey)
            {
                case VirtualKeys.A:
                    Select(new TextSelection(0, Buffer.Text.Length));
                    break;
                case VirtualKeys.C:
                    Copy(cut: false);
                    break;
                case VirtualKeys.X:
                    Copy(cut: true);
                    break;
                case VirtualKeys.V:
                    Paste();
                    break;
                case VirtualKeys.Z:
                    UndoOrRedo(redo: shift);
                    break;
                case VirtualKeys.Y when !shift:
                    UndoOrRedo(redo: true);
                    break;
            }
        }
        // Characters arrive separately (OnCharacter); every other key stays inside the editor.
        return true;
    }

    /// <summary>
    /// A character the keyboard layout produced (WM_CHAR), one UTF-16 unit at a time. Control characters come with their
    /// keys and are ignored here; a surrogate pair is typed once both halves arrived.
    /// </summary>
    public bool OnCharacter(char character)
    {
        if (_ended)
        {
            return false;
        }
        if (char.IsHighSurrogate(character))
        {
            _highSurrogate = character;
            return true;
        }
        if (char.IsLowSurrogate(character))
        {
            if (_highSurrogate != '\0')
            {
                Type(string.Concat(_highSurrogate.ToString(), character.ToString()));
            }
            _highSurrogate = '\0';
            return true;
        }
        _highSurrogate = '\0';
        if (character < ' ' || character == '\u007F')
        {
            return true;
        }
        Type(character.ToString());
        return true;
    }

    /// <summary>
    /// A pointer event on the canvas, in canvas points. Returns true when the editor took it: a press inside the text places
    /// the caret, a double-click selects a word, a triple-click a paragraph, a drag extends the selection, and Shift extends
    /// it from the caret. A press outside belongs to the canvas, which commits the text.
    /// </summary>
    public bool OnPointer(in PointerEvent e)
    {
        if (_ended || e.Samples.IsEmpty)
        {
            return false;
        }
        Point point = e.Position;
        switch (e.Phase)
        {
            case PointerPhase.Down when e.ChangedButton == PointerButtons.Secondary:
                return _dragPointer is null && Contains(point);
            case PointerPhase.Down when e.ChangedButton is PointerButtons.Primary or PointerButtons.None:
                if (_dragPointer is not null || !Contains(point))
                {
                    return false;
                }
                PointerDown(e, point);
                return true;
            case PointerPhase.Move when _dragPointer == e.PointerId:
                DragTo(point);
                return true;
            case PointerPhase.Up or PointerPhase.Cancel when _dragPointer == e.PointerId:
                _dragPointer = null;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Whether <paramref name="point"/> (canvas points) is inside the editor's frame.</summary>
    public bool Contains(Point point) => _session.Frame.Contains(Local(point) + _session.Frame.Origin);

    /// <summary>The caret offset nearest to <paramref name="point"/> in canvas points.</summary>
    public int HitTest(Point point) => Math.Clamp(_layout.HitTest(Layout, Local(point)), 0, Buffer.Text.Length);

    /// <summary>
    /// The canvas area [<paramref name="start"/>, <paramref name="end"/>) covers, as an upright rectangle around the rotated
    /// text: the caret's line for an empty range. For input methods placing their candidate windows.
    /// </summary>
    public Rect Bounds(int start, int end)
    {
        TextLayoutRequest request = Layout;
        start = Math.Clamp(start, 0, Buffer.Text.Length);
        end = Math.Clamp(end, start, Buffer.Text.Length);
        Rect local;
        if (start == end)
        {
            TextCaret caret = _layout.Caret(request, start);
            local = new Rect(caret.X, caret.Top, 1, caret.Bottom - caret.Top);
        }
        else
        {
            _rects.Clear();
            _layout.Range(request, start, end, _rects);
            local = _rects.Count == 0 ? new Rect(0, 0, 1, _session.Frame.Height) : _rects[0];
            foreach (Rect rect in _rects)
            {
                local = local.Union(rect);
            }
        }
        return CanvasBounds(local);
    }

    /// <summary>The editor's frame as an upright rectangle around the rotated text, in canvas points.</summary>
    public Rect FrameBounds => CanvasBounds(new Rect(0, 0, _session.Frame.Width, _session.Frame.Height));

    /// <summary>Escape or Ctrl+Enter: the composition ends with its text, then the canvas commits (Mac commitTextIfNeeded).</summary>
    public void Commit()
    {
        if (_ended)
        {
            return;
        }
        FinishComposition();
        if (!_ended)
        {
            _canvas.CommitText();
        }
    }

    /// <summary>An input method replaced [<paramref name="start"/>, <paramref name="end"/>) under its write lock.</summary>
    public TextChange ReplaceFromInputMethod(int start, int end, ReadOnlySpan<char> text)
    {
        TextChange change = Buffer.ReplaceFromInputMethod(start, end, text);
        _desiredX = null;
        PushText();
        Invalidate();
        return change;
    }

    /// <summary>
    /// Assistive technology set the whole text (UI Automation's ValuePattern.SetValue): one Undo step, with the caret after
    /// the new text.
    /// </summary>
    public void ReplaceAll(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_ended)
        {
            return;
        }
        FinishComposition();
        if (_ended || text == Buffer.Text)
        {
            return;
        }
        AfterEdit(Buffer.Replace(0, Buffer.Text.Length, text, TextEditGroup.None));
    }

    /// <summary>An input method moved the selection.</summary>
    public void SelectFromInputMethod(TextSelection selection, bool interim)
    {
        Buffer.SelectFromInputMethod(selection, interim);
        _desiredX = null;
        Invalidate();
    }

    public void CompositionStarted()
    {
        Buffer.StartComposition();
        Invalidate();
    }

    public void CompositionUpdated(int start, int end)
    {
        Buffer.UpdateComposition(start, end);
        Invalidate();
    }

    public void CompositionStyled(ImmutableArray<CompositionRun> runs)
    {
        Buffer.SetCompositionRuns(runs);
        Invalidate();
    }

    public void CompositionEnded()
    {
        Buffer.EndComposition();
        Invalidate();
    }

    private void PointerDown(in PointerEvent e, Point point)
    {
        FinishComposition();
        PointerSample sample = e.Current;
        bool again = _hasClick && sample.Timestamp >= _lastClickTime && sample.Timestamp - _lastClickTime <= _doubleClickTime
            && Math.Abs(point.X - _lastClickPoint.X) <= _doubleClickSize.Width / 2
            && Math.Abs(point.Y - _lastClickPoint.Y) <= _doubleClickSize.Height / 2;
        _clicks = again ? _clicks + 1 : 1;
        _hasClick = true;
        _lastClickTime = sample.Timestamp;
        _lastClickPoint = point;
        _dragPointer = e.PointerId;
        int index = HitTest(point);
        _dragUnit = Math.Min(3, _clicks);
        if (_dragUnit == 1 && (e.Modifiers & KeyModifiers.Shift) != 0)
        {
            _dragAnchor = (Buffer.Selection.Anchor, Buffer.Selection.Anchor);
            Select(new TextSelection(Buffer.Selection.Anchor, index));
            return;
        }
        _dragAnchor = Unit(index);
        Select(new TextSelection(_dragAnchor.Start, _dragAnchor.End));
    }

    private void DragTo(Point point)
    {
        (int start, int end) = Unit(HitTest(point));
        Select(start < _dragAnchor.Start
            ? new TextSelection(_dragAnchor.End, start)
            : new TextSelection(_dragAnchor.Start, Math.Max(end, _dragAnchor.End)));
    }

    private (int Start, int End) Unit(int index) => _dragUnit switch
    {
        2 => TextBoundaries.WordAt(Buffer.Text, index),
        3 => TextBoundaries.ParagraphAt(Buffer.Text, index),
        _ => (index, index),
    };

    private void Type(string text, TextEditGroup group = TextEditGroup.Typing)
    {
        FinishComposition();
        if (_ended)
        {
            return;
        }
        AfterEdit(Buffer.Insert(text, group));
    }

    private void DeleteBackward(bool word)
    {
        FinishComposition();
        TextSelection selection = Buffer.Selection;
        if (!selection.IsEmpty)
        {
            AfterEdit(Buffer.Replace(selection.Start, selection.End, string.Empty, TextEditGroup.None));
            return;
        }
        int end = selection.Active;
        int start = word ? TextBoundaries.PreviousWord(Buffer.Text, end) : TextBoundaries.PreviousCluster(Buffer.Text, end);
        if (start < end)
        {
            AfterEdit(Buffer.Replace(start, end, string.Empty, TextEditGroup.DeleteBackward));
        }
    }

    private void DeleteForward(bool word)
    {
        FinishComposition();
        TextSelection selection = Buffer.Selection;
        if (!selection.IsEmpty)
        {
            AfterEdit(Buffer.Replace(selection.Start, selection.End, string.Empty, TextEditGroup.None));
            return;
        }
        int start = selection.Active;
        int end = word ? TextBoundaries.NextWord(Buffer.Text, start) : TextBoundaries.NextCluster(Buffer.Text, start);
        if (start < end)
        {
            AfterEdit(Buffer.Replace(start, end, string.Empty, TextEditGroup.DeleteForward));
        }
    }

    private void UndoOrRedo(bool redo)
    {
        FinishComposition();
        if ((redo ? Buffer.Redo() : Buffer.Undo()) is { } change)
        {
            AfterEdit(change);
        }
    }

    private void Copy(bool cut)
    {
        TextSelection selection = Buffer.Selection;
        if (selection.IsEmpty)
        {
            return;
        }
        try
        {
            _clipboard.SetText(Buffer.Text[selection.Start..selection.End]);
        }
        catch (InvalidOperationException error)
        {
            Failed?.Invoke(error.Message);
            return;
        }
        if (cut)
        {
            FinishComposition();
            AfterEdit(Buffer.Replace(selection.Start, selection.End, string.Empty, TextEditGroup.None));
        }
    }

    private void Paste()
    {
        string? text;
        try
        {
            text = _clipboard.GetText();
        }
        catch (InvalidOperationException error)
        {
            Failed?.Invoke(error.Message);
            return;
        }
        if (!string.IsNullOrEmpty(text))
        {
            Type(text, TextEditGroup.None);
        }
    }

    private void MoveHorizontally(bool right, bool word, bool extend)
    {
        TextSelection selection = Buffer.Selection;
        string text = Buffer.Text;
        // Right-to-left text runs from right to left, so the arrows move the other way through it.
        bool forward = right != TextBoundaries.IsRightToLeft(text);
        int target;
        if (!extend && !selection.IsEmpty && !word)
        {
            target = forward ? selection.End : selection.Start;
        }
        else if (forward)
        {
            target = word ? TextBoundaries.NextWord(text, selection.Active) : TextBoundaries.NextCluster(text, selection.Active);
        }
        else
        {
            target = word ? TextBoundaries.PreviousWord(text, selection.Active) : TextBoundaries.PreviousCluster(text, selection.Active);
        }
        _desiredX = null;
        Select(extend ? selection with { Active = target } : TextSelection.At(target));
    }

    private void MoveVertically(bool down, bool extend)
    {
        TextSelection selection = Buffer.Selection;
        TextLayoutRequest request = Layout;
        int from = selection.Active;
        TextLine line = _layout.Line(request, from);
        TextLine last = _layout.Line(request, Buffer.Text.Length);
        TextLine first = _layout.Line(request, 0);
        double x = _desiredX ?? _layout.Caret(request, from).X;
        int target;
        if (down && line.Start == last.Start)
        {
            target = Buffer.Text.Length;
        }
        else if (!down && line.Start == first.Start)
        {
            target = 0;
        }
        else
        {
            double y = down ? line.Bottom + (line.Bottom - line.Top) / 2 : line.Top - (line.Bottom - line.Top) / 2;
            target = Math.Clamp(_layout.HitTest(request, new Point(x, y)), 0, Buffer.Text.Length);
        }
        Select(extend ? selection with { Active = target } : TextSelection.At(target));
        _desiredX = x;
    }

    private void MoveToEdge(bool end, bool document, bool extend)
    {
        TextSelection selection = Buffer.Selection;
        int target;
        if (document)
        {
            target = end ? Buffer.Text.Length : 0;
        }
        else
        {
            TextLine line = _layout.Line(Layout, selection.Active);
            target = end ? line.End : line.Start;
        }
        _desiredX = null;
        Select(extend ? selection with { Active = target } : TextSelection.At(target));
    }

    private void Select(TextSelection selection)
    {
        if (Buffer.Selection == selection && !Buffer.InterimCharacter)
        {
            return;
        }
        FinishComposition();
        if (Buffer.Select(selection))
        {
            SelectionMoved?.Invoke();
            Invalidate();
        }
    }

    /// <summary>Escape while composing: the composition goes, and the text before it comes back. False without a composition.</summary>
    private bool CancelComposition()
    {
        if (Buffer.Composition is null)
        {
            return false;
        }
        (string Text, TextSelection Selection)? before = Buffer.BeforeComposition;
        FinishCompositionRequested?.Invoke();
        if (Buffer.Composition is not null)
        {
            Buffer.EndComposition();
        }
        if (Buffer.CancelComposition(before) is { } change)
        {
            TextEdited?.Invoke(change);
            PushText();
        }
        Invalidate();
        return true;
    }

    private void FinishComposition()
    {
        if (Buffer.Composition is null)
        {
            return;
        }
        FinishCompositionRequested?.Invoke();
        if (Buffer.Composition is not null)
        {
            // No input method answered (none is connected): the composition's text stays as it is.
            Buffer.EndComposition();
            Invalidate();
        }
    }

    private void AfterEdit(TextChange change)
    {
        _desiredX = null;
        TextEdited?.Invoke(change);
        PushText();
        Invalidate();
    }

    /// <summary>Hands the text to the canvas, which sizes the frame and tells the workspace (Mac textDidChange).</summary>
    private void PushText()
    {
        if (!_ended)
        {
            _canvas.SetText(Buffer.Text);
        }
    }

    private void Invalidate()
    {
        if (_ended)
        {
            return;
        }
        _decorations = null;
        Changed?.Invoke();
    }

    /// <summary>A canvas point in the frame's own space: points from its top-left corner before rotation.</summary>
    private Point Local(Point point)
    {
        Rect frame = _session.Frame;
        Point upright = _session.Rotation == 0 ? point : point.Rotated(frame.Center, -_session.Rotation);
        return new Point(upright.X - frame.X, upright.Y - frame.Y);
    }

    private Point ToCanvas(Point local)
    {
        Rect frame = _session.Frame;
        var point = new Point(frame.X + local.X, frame.Y + local.Y);
        return _session.Rotation == 0 ? point : point.Rotated(frame.Center, _session.Rotation);
    }

    private Rect CanvasBounds(Rect local)
    {
        Rect frame = _session.Frame;
        Rect canvas = local.Translated(frame.Origin);
        return _session.Rotation == 0 ? canvas : AffineTransform.RotationAbout(frame.Center, _session.Rotation).TransformBounds(canvas);
    }

    private TextEditorDecorations BuildDecorations()
    {
        if (_ended)
        {
            return TextEditorDecorations.None;
        }
        TextLayoutRequest request = Layout;
        Rect frame = _session.Frame;
        Style style = _session.Style;
        RGBA textColor = style.Color with { Alpha = style.Color.Alpha * style.Opacity };
        TextComposition? composition = Buffer.Composition;
        ImmutableArray<CompositionRun> runs = composition is null ? []
            : !composition.Runs.IsDefaultOrEmpty ? composition.Runs
            : composition.End > composition.Start ? [new CompositionRun(composition.Start, composition.End, CompositionStyle.Default)] : [];

        BeginRotation();
        foreach (CompositionRun run in runs)
        {
            if (run.Style.Background is { } background)
            {
                foreach (Rect rect in RangeRects(request, run.Start, run.End))
                {
                    _lists.Add(DisplayItem.FillRect(rect.Translated(frame.Origin), Paint.Solid(background)));
                }
            }
        }
        TextSelection selection = Buffer.Selection;
        if (!selection.IsEmpty)
        {
            foreach (Rect rect in RangeRects(request, selection.Start, selection.End))
            {
                _lists.Add(DisplayItem.FillRect(rect.Translated(frame.Origin), SelectionPaint));
            }
        }
        DisplayList under = EndRotation(_session.ElementId);

        BeginRotation();
        foreach (CompositionRun run in runs)
        {
            if (run.Style.Line == CompositionLine.None)
            {
                continue;
            }
            double thickness = run.Style.BoldLine ? 2 : 1;
            Paint paint = Paint.Solid(run.Style.LineColor ?? textColor);
            // Clauses are drawn apart, as input methods expect, so each one's line ends a point early.
            double inset = runs.Length > 1 ? 1 : 0;
            foreach (Rect rect in RangeRects(request, run.Start, run.End))
            {
                double y = frame.Y + rect.MaxY - thickness / 2 - 1;
                double left = frame.X + rect.X + inset, right = frame.X + rect.MaxX - inset;
                if (right <= left)
                {
                    continue;
                }
                _lists.Add(DisplayItem.StrokePath(Underline(run.Style.Line, left, right, y, thickness), UnderlineStroke(run.Style.Line, thickness), paint));
            }
        }
        DisplayList over = EndRotation(_session.ElementId);
        return under.IsEmpty && over.IsEmpty ? TextEditorDecorations.None : new TextEditorDecorations(under, over);
    }

    private void BeginRotation()
    {
        _lists.Clear();
        if (_session.Rotation != 0)
        {
            _lists.Add(DisplayItem.PushTransform(AffineTransform.RotationAbout(_session.Frame.Center, _session.Rotation)));
        }
    }

    private DisplayList EndRotation(Guid id)
    {
        int empty = _session.Rotation != 0 ? 1 : 0;
        if (_lists.Count == empty)
        {
            _lists.Clear();
            return DisplayList.Empty;
        }
        if (empty == 1)
        {
            _lists.Add(DisplayItem.PopTransform());
        }
        return _lists.ToList(id);
    }

    private List<Rect> RangeRects(in TextLayoutRequest request, int start, int end)
    {
        _rects.Clear();
        start = Math.Clamp(start, 0, Buffer.Text.Length);
        end = Math.Clamp(end, start, Buffer.Text.Length);
        if (start < end)
        {
            _layout.Range(request, start, end, _rects);
        }
        return _rects;
    }

    private static PathData Underline(CompositionLine line, double left, double right, double y, double thickness)
    {
        if (line != CompositionLine.Squiggle)
        {
            return new PathBuilder(2, 2).MoveTo(new Point(left, y)).LineTo(new Point(right, y)).ToPathData();
        }
        // A wavy line two thicknesses high, one wave every four points.
        int waves = Math.Max(1, (int)Math.Round((right - left) / 2));
        var path = new PathBuilder(waves + 1, waves + 1).MoveTo(new Point(left, y));
        for (int step = 1; step <= waves; step++)
        {
            double x = left + (right - left) * step / waves;
            path.LineTo(new Point(x, y + (step % 2 == 0 ? 0 : thickness * 1.5)));
        }
        return path.ToPathData();
    }

    private static StrokeStyle UnderlineStroke(CompositionLine line, double thickness) => line switch
    {
        CompositionLine.Dot => new StrokeStyle(thickness, LineCap.Butt, LineJoin.Miter, 10, [thickness, thickness]),
        CompositionLine.Dash => new StrokeStyle(thickness, LineCap.Butt, LineJoin.Miter, 10, [thickness * 4, thickness * 2]),
        _ => new StrokeStyle(thickness),
    };
}
