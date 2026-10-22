using System.Collections.Immutable;
using Annotator.Core;

namespace Annotator.Workspace.Canvas;

/// <summary>
/// A selection in the text editor, in UTF-16 offsets: <see cref="Anchor"/> stays where the selection started while
/// <see cref="Active"/>, the caret, moves.
/// </summary>
public readonly record struct TextSelection(int Anchor, int Active)
{
    public int Start => Math.Min(Anchor, Active);

    public int End => Math.Max(Anchor, Active);

    public int Length => End - Start;

    public bool IsEmpty => Anchor == Active;

    /// <summary>The caret is at the start of a selection that is not empty (TSF's TS_AE_START).</summary>
    public bool ActiveAtStart => Active < Anchor;

    public static TextSelection At(int index) => new(index, index);

    public static TextSelection Range(int start, int end, bool activeAtStart = false) => activeAtStart ? new(end, start) : new(start, end);
}

/// <summary>A change of the editor's text: [<see cref="Start"/>, <see cref="OldEnd"/>) became [<see cref="Start"/>, <see cref="NewEnd"/>).</summary>
public readonly record struct TextChange(int Start, int OldEnd, int NewEnd);

/// <summary>The underline an input method asks for under part of its composition (TSF's TF_DA_LINESTYLE).</summary>
public enum CompositionLine : byte
{
    None,
    Solid,
    Dot,
    Dash,
    Squiggle,
}

/// <summary>
/// How an input method wants one part of its composition drawn (TSF's TF_DISPLAYATTRIBUTE): the underline, its weight
/// and color, and a background. A null color means the text's own color.
/// </summary>
public readonly record struct CompositionStyle(CompositionLine Line, bool BoldLine = false, RGBA? LineColor = null, RGBA? Background = null)
{
    /// <summary>What the editor draws when an input method gives no attributes: a thin solid underline.</summary>
    public static CompositionStyle Default => new(CompositionLine.Solid);
}

/// <summary>One part of a composition, [<see cref="Start"/>, <see cref="End"/>), and how it is drawn.</summary>
public readonly record struct CompositionRun(int Start, int End, CompositionStyle Style);

/// <summary>The text an input method is still composing, [<see cref="Start"/>, <see cref="End"/>), and how its parts are drawn.</summary>
public sealed record TextComposition(int Start, int End, ImmutableArray<CompositionRun> Runs);

/// <summary>Which typing an edit continues: consecutive edits of one kind are one Undo step, as in Windows text fields.</summary>
public enum TextEditGroup
{
    /// <summary>An edit of its own (paste, cut, a dropped composition).</summary>
    None,
    Typing,
    DeleteBackward,
    DeleteForward,
}

/// <summary>
/// The text editor's content (spec 013 S-7, WIN-INP-09): the text, the selection, the input method's composition and the
/// editor's own Undo history. Edits the person makes (typing, deleting, pasting, Undo) return the change, so the Windows
/// half can tell the Text Services Framework; edits an input method makes come in through the <c>InputMethod</c> members
/// and are not reported back. A composition becomes one Undo step when it ends. Undo inside the editor never reaches the
/// drawing's history: committing the text is the drawing's Undo step.
/// </summary>
public sealed class TextEditBuffer
{
    /// <summary>Undo steps kept inside one editing session.</summary>
    public const int UndoLimit = 100;

    private readonly List<UndoStep> _undo = [];
    private readonly List<UndoStep> _redo = [];
    private TextEditGroup _group = TextEditGroup.None;
    private (string Text, TextSelection Selection)? _beforeComposition;
    private bool _compositionContinuesTyping;

    private sealed record UndoStep(string BeforeText, TextSelection BeforeSelection, TextEditGroup Group)
    {
        public string AfterText { get; set; } = string.Empty;

        public TextSelection AfterSelection { get; set; }
    }

    public TextEditBuffer(string text, TextSelection? selection = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
        Selection = Clamp(selection ?? TextSelection.At(text.Length), text.Length);
    }

    public string Text { get; private set; }

    public TextSelection Selection { get; private set; }

    /// <summary>
    /// The selected character is one a Korean input method is still building (TSF's fInterimChar): it is drawn as a box,
    /// and the caret is hidden.
    /// </summary>
    public bool InterimCharacter { get; private set; }

    /// <summary>The input method's composition, or null.</summary>
    public TextComposition? Composition { get; private set; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Replaces [<paramref name="start"/>, <paramref name="end"/>) with <paramref name="text"/> and puts the caret after it.</summary>
    public TextChange Replace(int start, int end, string text, TextEditGroup group)
    {
        ArgumentNullException.ThrowIfNull(text);
        CheckRange(start, end);
        UndoStep step = OpenStep(group);
        TextChange change = Apply(start, end, text);
        Selection = TextSelection.At(change.NewEnd);
        InterimCharacter = false;
        CloseStep(step);
        return change;
    }

    /// <summary>Types <paramref name="text"/> over the selection.</summary>
    public TextChange Insert(string text, TextEditGroup group = TextEditGroup.Typing) => Replace(Selection.Start, Selection.End, text, group);

    /// <summary>Moves the selection; any typing that follows starts a new Undo step. Returns false when nothing changed.</summary>
    public bool Select(TextSelection selection)
    {
        _group = TextEditGroup.None;
        selection = Clamp(selection, Text.Length);
        if (selection == Selection && !InterimCharacter)
        {
            return false;
        }
        Selection = selection;
        InterimCharacter = false;
        return true;
    }

    /// <summary>Undoes the last edit inside the editor; null when there is none or a composition is open.</summary>
    public TextChange? Undo()
    {
        if (_undo.Count == 0 || Composition is not null)
        {
            return null;
        }
        UndoStep step = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(step);
        _group = TextEditGroup.None;
        return Restore(step.BeforeText, step.BeforeSelection);
    }

    /// <summary>Redoes the last undone edit; null when there is none or a composition is open.</summary>
    public TextChange? Redo()
    {
        if (_redo.Count == 0 || Composition is not null)
        {
            return null;
        }
        UndoStep step = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(step);
        _group = TextEditGroup.None;
        return Restore(step.AfterText, step.AfterSelection);
    }

    /// <summary>
    /// An input method replaces [<paramref name="start"/>, <paramref name="end"/>) while it holds the store's write lock.
    /// A selection after the range moves with the text; a selection that touched it ends up after the new text. Outside a
    /// composition the edit is typing (one Undo step with the typing around it); inside one it waits for the end.
    /// </summary>
    public TextChange ReplaceFromInputMethod(int start, int end, ReadOnlySpan<char> text)
    {
        CheckRange(start, end);
        UndoStep? step = Composition is null && _beforeComposition is null ? OpenStep(TextEditGroup.Typing) : null;
        TextSelection before = Selection;
        TextChange change = Apply(start, end, text.ToString());
        int delta = change.NewEnd - change.OldEnd;
        Selection = before.Start >= end
            ? new TextSelection(before.Anchor + delta, before.Active + delta)
            : before.End <= start ? before : TextSelection.At(change.NewEnd);
        InterimCharacter = false;
        if (Composition is { } composition)
        {
            Composition = composition with
            {
                Start = Shift(composition.Start, change),
                End = Shift(composition.End, change),
                Runs = [],
            };
        }
        if (step is not null)
        {
            CloseStep(step);
        }
        return change;
    }

    /// <summary>An input method sets the selection; <paramref name="interim"/> marks a character it is still building.</summary>
    public void SelectFromInputMethod(TextSelection selection, bool interim)
    {
        Selection = Clamp(selection, Text.Length);
        InterimCharacter = interim && !Selection.IsEmpty;
        if (Composition is null && _beforeComposition is null)
        {
            _group = TextEditGroup.None;
        }
    }

    /// <summary>An input method started composing; the text and selection before it are what cancelling restores.</summary>
    public void StartComposition()
    {
        if (_beforeComposition is null)
        {
            _beforeComposition = (Text, Selection);
            _compositionContinuesTyping = _group == TextEditGroup.Typing;
        }
        _group = TextEditGroup.None;
        Composition = new TextComposition(Selection.Start, Selection.Start, []);
    }

    /// <summary>The composition now covers [<paramref name="start"/>, <paramref name="end"/>); its drawing comes with <see cref="SetCompositionRuns"/>.</summary>
    public void UpdateComposition(int start, int end)
    {
        start = Math.Clamp(start, 0, Text.Length);
        end = Math.Clamp(end, start, Text.Length);
        if (Composition is { } composition && composition.Start == start && composition.End == end)
        {
            return;
        }
        if (_beforeComposition is null)
        {
            _beforeComposition = (Text, Selection);
            _compositionContinuesTyping = false;
        }
        Composition = new TextComposition(start, end, []);
    }

    /// <summary>How the input method wants the parts of its composition drawn.</summary>
    public void SetCompositionRuns(ImmutableArray<CompositionRun> runs)
    {
        if (Composition is not { } composition)
        {
            return;
        }
        var clipped = ImmutableArray.CreateBuilder<CompositionRun>(runs.Length);
        foreach (CompositionRun run in runs)
        {
            int start = Math.Max(run.Start, composition.Start), end = Math.Min(run.End, composition.End);
            if (start < end)
            {
                clipped.Add(run with { Start = start, End = end });
            }
        }
        Composition = composition with { Runs = clipped.ToImmutable() };
    }

    /// <summary>The composition ended: what it left is one Undo step that typing next to it continues.</summary>
    public void EndComposition()
    {
        Composition = null;
        if (InterimCharacter)
        {
            // A character still being built is finished where it stands; the caret goes after it.
            Selection = TextSelection.At(Selection.End);
            InterimCharacter = false;
        }
        if (_beforeComposition is not { } before)
        {
            return;
        }
        _beforeComposition = null;
        if (before.Text == Text)
        {
            return;
        }
        UndoStep? last = _undo.Count > 0 ? _undo[^1] : null;
        if (last is { Group: TextEditGroup.Typing } && _compositionContinuesTyping && last.AfterText == before.Text)
        {
            last.AfterText = Text;
            last.AfterSelection = Selection;
        }
        else
        {
            Push(new UndoStep(before.Text, before.Selection, TextEditGroup.Typing) { AfterText = Text, AfterSelection = Selection });
        }
        _redo.Clear();
        _group = TextEditGroup.Typing;
    }

    /// <summary>
    /// Escape during a composition (WIN-INP-09): puts back the text and selection from before the composition (or
    /// <paramref name="before"/>, taken before the input method finished it) and forgets it, leaving no Undo step.
    /// Returns the change, or null when there was nothing to put back.
    /// </summary>
    public TextChange? CancelComposition((string Text, TextSelection Selection)? before = null)
    {
        (string Text, TextSelection Selection)? previous = before ?? _beforeComposition;
        Composition = null;
        _beforeComposition = null;
        InterimCharacter = false;
        _group = TextEditGroup.None;
        if (previous is not { } restored)
        {
            return null;
        }
        if (_undo.Count > 0 && _undo[^1].AfterText == Text)
        {
            UndoStep last = _undo[^1];
            if (last.BeforeText == restored.Text)
            {
                _undo.RemoveAt(_undo.Count - 1);
            }
            else
            {
                // The composition continued typing; that typing stays and the composition leaves it.
                last.AfterText = restored.Text;
                last.AfterSelection = restored.Selection;
            }
        }
        return Restore(restored.Text, restored.Selection);
    }

    /// <summary>Where the composition started from, or null when none is open; for cancelling after the input method finished it.</summary>
    public (string Text, TextSelection Selection)? BeforeComposition => _beforeComposition;

    private UndoStep OpenStep(TextEditGroup group)
    {
        if (group != TextEditGroup.None && group == _group && _undo.Count > 0 && _undo[^1].AfterText == Text && _undo[^1].AfterSelection == Selection)
        {
            return _undo[^1];
        }
        var step = new UndoStep(Text, Selection, group);
        Push(step);
        _redo.Clear();
        return step;
    }

    private void CloseStep(UndoStep step)
    {
        step.AfterText = Text;
        step.AfterSelection = Selection;
        _group = step.Group;
        if (step.BeforeText == step.AfterText && _undo.Count > 0 && ReferenceEquals(_undo[^1], step))
        {
            _undo.RemoveAt(_undo.Count - 1);
            _group = TextEditGroup.None;
        }
    }

    private void Push(UndoStep step)
    {
        if (_undo.Count == UndoLimit)
        {
            _undo.RemoveAt(0);
        }
        _undo.Add(step);
    }

    private TextChange Apply(int start, int end, string text)
    {
        Text = string.Concat(Text.AsSpan(0, start), text, Text.AsSpan(end));
        return new TextChange(start, end, start + text.Length);
    }

    private TextChange? Restore(string text, TextSelection selection)
    {
        TextChange? change = Difference(Text, text);
        Text = text;
        Selection = Clamp(selection, text.Length);
        InterimCharacter = false;
        return change ?? new TextChange(Selection.Start, Selection.Start, Selection.Start);
    }

    /// <summary>The smallest change that turns <paramref name="before"/> into <paramref name="after"/>, or null when they are equal.</summary>
    public static TextChange? Difference(string before, string after)
    {
        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            return null;
        }
        int prefix = 0;
        int shorter = Math.Min(before.Length, after.Length);
        while (prefix < shorter && before[prefix] == after[prefix])
        {
            prefix++;
        }
        int suffix = 0;
        while (suffix < shorter - prefix && before[^(suffix + 1)] == after[^(suffix + 1)])
        {
            suffix++;
        }
        return new TextChange(prefix, before.Length - suffix, after.Length - suffix);
    }

    private static int Shift(int index, TextChange change) =>
        index <= change.Start ? index : index >= change.OldEnd ? index + change.NewEnd - change.OldEnd : change.NewEnd;

    private static TextSelection Clamp(TextSelection selection, int length) =>
        new(Math.Clamp(selection.Anchor, 0, length), Math.Clamp(selection.Active, 0, length));

    private void CheckRange(int start, int end)
    {
        if (start < 0 || end < start || end > Text.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start), $"[{start}, {end}) is outside the text of length {Text.Length}.");
        }
    }
}
