using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Workspace.Canvas;
using static Annotator.Workspace.Tests.Canvas.CanvasHarness;

namespace Annotator.Workspace.Tests.Canvas;

/// <summary>
/// The text editor's own behavior (spec 013 S-7, WIN-INP-09): typing, editing keys, Undo, copy and paste inside the
/// editor, the composition rules for Escape, pointer selection, and what it draws. Keys and characters go to the editor
/// first, as the app routes them.
/// </summary>
public sealed class TextEditorTests
{
    private static (CanvasHarness Harness, EditorHost Host) Start(params Element[] elements)
    {
        var host = new EditorHost();
        var h = new CanvasHarness([MainDisplay], textEditor: host);
        h.SetDocument(Main, elements);
        return (h, host);
    }

    private static CanvasTextEditor Open(CanvasHarness h, EditorHost host, Point point)
    {
        h.Choose(Tool.Text);
        h.Down(Main, point);
        h.Up(Main, point);
        return host.Editor ?? throw new InvalidOperationException("No editor opened.");
    }

    private static bool Press(CanvasHarness h, EditorHost host, int key, KeyModifiers modifiers = KeyModifiers.None)
    {
        h.Sync();
        bool handled = host.Editor is { } editor ? editor.OnKey(new KeyEvent(key, modifiers)) : h[Main].OnKey(new KeyEvent(key, modifiers));
        h.Pump();
        return handled;
    }

    private static void Type(CanvasHarness h, EditorHost host, string text)
    {
        h.Sync();
        foreach (char character in text)
        {
            Assert.True(host.Editor!.OnCharacter(character));
        }
        h.Pump();
    }

    private static void Click(CanvasHarness h, EditorHost host, Point point, int count = 1, KeyModifiers modifiers = KeyModifiers.None)
    {
        for (int index = 0; index < count; index++)
        {
            Pointer(h, host, PointerPhase.Down, point, modifiers, time: 10_000 + index * 100);
            Pointer(h, host, PointerPhase.Up, point, modifiers, time: 10_000 + index * 100 + 10);
        }
    }

    private static void Pointer(CanvasHarness h, EditorHost host, PointerPhase phase, Point point, KeyModifiers modifiers = KeyModifiers.None,
                                long time = 20_000)
    {
        h.Sync();
        PointerSample[] samples = [new PointerSample(point, 1, time)];
        var e = new PointerEvent
        {
            Phase = phase,
            PointerId = 1,
            Buttons = phase is PointerPhase.Down or PointerPhase.Move ? PointerButtons.Primary : PointerButtons.None,
            ChangedButton = phase is PointerPhase.Down or PointerPhase.Up ? PointerButtons.Primary : PointerButtons.None,
            Modifiers = modifiers,
            Samples = samples,
        };
        if (host.Editor?.OnPointer(e) != true)
        {
            h[Main].OnPointer(e);
        }
        h.Pump();
    }

    [Fact]
    public void TypingGoesToTheCanvasAndEscapeCommitsOneUndoStep()
    {
        (CanvasHarness h, EditorHost host) = Start();
        using (h)
        {
            CanvasTextEditor editor = Open(h, host, new Point(100, 100));
            Type(h, host, "Hi\u0001\r");
            Assert.Equal("Hi", editor.Text);
            Assert.Equal("Hi", h[Main].State.Text?.Text);
            // Two characters 14 points wide, plus 2, at least 32 (NativeTextFont.contentSize).
            Assert.Equal(new Rect(100, 100, 32, Math.Ceiling(28 * 1.2) + 2), h[Main].State.Text?.Frame);
            Assert.False(h.Document(Main).CanUndo);
            // The canvas hides the object being typed and its selection; its document holds both, as the Mac's does.
            Assert.DoesNotContain(h[Main].State.Elements, element => element.Id == editor.Session.ElementId);
            CanvasDocument edited = h[Main].Document!.Value;
            Assert.Equal(new Rect(100, 100, 32, 42), Assert.Single(edited.Elements).Frame);
            Assert.Equal([editor.Session.ElementId], edited.Selection);

            Assert.True(Press(h, host, VirtualKeys.Escape));
            Assert.Null(host.Editor);
            Assert.Null(h[Main].State.Text);
            Assert.False(h[Main].HasTextEditor);
            Assert.Equal("Hi", Assert.Single(h.Elements(Main)).Text);
            Assert.True(Press(h, host, VirtualKeys.Escape));
            Assert.Equal(WorkspaceMode.UseApps, h.Model.Snapshot.Mode);
            h.Model.Undo();
            Assert.Empty(h.Elements(Main));
        }
    }

    [Fact]
    public void EnterTypesALineBreakAndCtrlEnterCommits()
    {
        (CanvasHarness h, EditorHost host) = Start();
        using (h)
        {
            CanvasTextEditor editor = Open(h, host, new Point(100, 100));
            Type(h, host, "One");
            Assert.True(Press(h, host, VirtualKeys.Enter));
            Assert.True(Press(h, host, VirtualKeys.Tab));
            Type(h, host, "Two");
            Assert.Equal("One\n\tTwo", editor.Text);
            Assert.True(Press(h, host, VirtualKeys.Enter, KeyModifiers.Ctrl));
            Assert.Equal("One\n\tTwo", Assert.Single(h.Elements(Main)).Text);
            Assert.Null(host.Editor);
        }
    }

    [Fact]
    public void AssistiveTechnologyCanSetTheWholeText()
    {
        (CanvasHarness h, EditorHost host) = Start();
        using (h)
        {
            CanvasTextEditor editor = Open(h, host, new Point(100, 100));
            Type(h, host, "Draft");
            host.ToldInputMethod.Clear();

            editor.ReplaceAll("Final note");
            h.Pump();

            Assert.Equal("Final note", editor.Text);
            Assert.Equal(TextSelection.At(10), editor.Buffer.Selection);
            Assert.Equal("Final note", h[Main].State.Text?.Text);
            Assert.Equal(new TextChange(0, 5, 10), Assert.Single(host.ToldInputMethod));
            Assert.True(Press(h, host, VirtualKeys.Z, KeyModifiers.Ctrl));
            Assert.Equal("Draft", editor.Text);
            Assert.True(Press(h, host, VirtualKeys.Escape));
            Assert.Equal("Draft", Assert.Single(h.Elements(Main)).Text);
        }
    }

    [Fact]
    public void ToolKeysAndCanvasShortcutsStayInsideTheEditor()
    {
        (CanvasHarness h, EditorHost host) = Start();
        using (h)
        {
            CanvasTextEditor editor = Open(h, host, new Point(100, 100));
            // "p" chooses the Pen and Ctrl+W shows the whiteboard on the canvas; in the editor they type or do nothing.
            Assert.True(Press(h, host, 'P'));
            Type(h, host, "p");
            Assert.True(Press(h, host, VirtualKeys.W, KeyModifiers.Ctrl));
            Assert.True(Press(h, host, VirtualKeys.Delete));
            Assert.Equal(Tool.Text, h.Model.Snapshot.Tool);
            Assert.Equal(Board.Desktop, h.Model.Display(Main).Board);
            Assert.Equal("p", editor.Text);
            Assert.True(h[Main].HasTextEditor);
            // Alt keys belong to Windows (system menus, Alt codes).
            Assert.False(editor.OnKey(new KeyEvent('X', KeyModifiers.Alt)));
        }
    }

    [Fact]
    public void SurrogatePairsArriveInTwoHalvesAndClustersDeleteTogether()
    {
        (CanvasHarness h, EditorHost host) = Start();
        using (h)
        {
            CanvasTextEditor editor = Open(h, host, new Point(100, 100));
            Type(h, host, "a\U0001F44D\U0001F3FDb");
            Assert.Equal("a\U0001F44D\U0001F3FDb", editor.Text);
            Assert.True(Press(h, host, VirtualKeys.Left));
            Assert.True(Press(h, host, VirtualKeys.Back));
            Assert.Equal("ab", editor.Text);
            Assert.Equal(TextSelection.At(1), editor.Buffer.Selection);
        }
    }

    [Fact]
    public void UndoInsideTheEditorKeepsTheDrawingsHistory()
    {
        (CanvasHarness h, EditorHost host) = Start();
        using (h)
        {
            CanvasTextEditor editor = Open(h, host, new Point(100, 100));
            Type(h, host, "abc");
            Assert.True(Press(h, host, VirtualKeys.Back));
            Assert.True(Press(h, host, VirtualKeys.Back));
            Assert.Equal("a", editor.Text);
            Assert.True(Press(h, host, VirtualKeys.Z, KeyModifiers.Ctrl));
            Assert.Equal("abc", editor.Text);
            Assert.True(Press(h, host, VirtualKeys.Z, KeyModifiers.Ctrl));
            Assert.Equal(string.Empty, editor.Text);
            Assert.True(Press(h, host, VirtualKeys.Y, KeyModifiers.Ctrl));
            Assert.Equal("abc", editor.Text);
            Assert.True(Press(h, host, VirtualKeys.Z, KeyModifiers.Ctrl | KeyModifiers.Shift));
            Assert.Equal("a", editor.Text);
            Assert.True(h[Main].HasTextEditor);
            Assert.False(h.Document(Main).CanUndo);
            Assert.Equal("a", h[Main].State.Text?.Text);
            Assert.Equal([new TextChange(0, 0, 1), new TextChange(1, 1, 2), new TextChange(2, 2, 3), new TextChange(2, 3, 2), new TextChange(1, 2, 1),
                          new TextChange(1, 1, 3), new TextChange(0, 3, 0), new TextChange(0, 0, 3), new TextChange(1, 3, 1)], host.ToldInputMethod);
        }
    }

    [Fact]
    public void CopyCutAndPasteUseTheClipboardsPlainText()
    {
        (CanvasHarness h, EditorHost host) = Start();
        using (h)
        {
            CanvasTextEditor editor = Open(h, host, new Point(100, 100));
            Type(h, host, "Hello world");
            Assert.True(Press(h, host, VirtualKeys.Left, KeyModifiers.Ctrl | KeyModifiers.Shift));
            Assert.Equal(new TextSelection(11, 6), editor.Buffer.Selection);
            Assert.True(Press(h, host, VirtualKeys.C, KeyModifiers.Ctrl));
            Assert.Equal("world", host.Clipboard.Text);
            Assert.True(Press(h, host, VirtualKeys.X, KeyModifiers.Ctrl));
            Assert.Equal("Hello ", editor.Text);
            Assert.True(Press(h, host, VirtualKeys.Home));
            Assert.True(Press(h, host, VirtualKeys.V, KeyModifiers.Ctrl));
            Assert.Equal("worldHello ", editor.Text);
            Assert.Equal(TextSelection.At(5), editor.Buffer.Selection);
            // A copy of an object stays the canvas's: the editor's Ctrl+C never commits.
            Assert.True(h[Main].HasTextEditor);

            host.Clipboard.Failure = "The clipboard is in use by another app. Try again.";
            Assert.True(Press(h, host, VirtualKeys.V, KeyModifiers.Ctrl));
            Assert.Equal("worldHello ", editor.Text);
            Assert.Equal(["The clipboard is in use by another app. Try again."], host.Failures);
        }
    }

    [Fact]
    public void ArrowsHomeEndAndShiftMoveTheCaretAndSelection()
    {
        (CanvasHarness h, EditorHost host) = Start();
        using (h)
        {
            CanvasTextEditor editor = Open(h, host, new Point(100, 100));
            Type(h, host, "ab");
            Assert.True(Press(h, host, VirtualKeys.Enter));
            Type(h, host, "cdef");
            Assert.Equal("ab\ncdef", editor.Text);
            Assert.Equal(TextSelection.At(7), editor.Buffer.Selection);
            Assert.True(Press(h, host, VirtualKeys.Up));
            Assert.Equal(TextSelection.At(2), editor.Buffer.Selection);
            Assert.True(Press(h, host, VirtualKeys.Down));
            // The column the caret came from: line 2, fourth character.
            Assert.Equal(TextSelection.At(7), editor.Buffer.Selection);
            Assert.True(Press(h, host, VirtualKeys.Home));
            Assert.Equal(TextSelection.At(3), editor.Buffer.Selection);
            Assert.True(Press(h, host, VirtualKeys.End, KeyModifiers.Shift));
            Assert.Equal(new TextSelection(3, 7), editor.Buffer.Selection);
            Assert.True(Press(h, host, VirtualKeys.Left));
            Assert.Equal(TextSelection.At(3), editor.Buffer.Selection);
            Assert.True(Press(h, host, VirtualKeys.Home, KeyModifiers.Ctrl | KeyModifiers.Shift));
            Assert.Equal(new TextSelection(3, 0), editor.Buffer.Selection);
            Assert.True(Press(h, host, VirtualKeys.A, KeyModifiers.Ctrl));
            Assert.Equal(new TextSelection(0, 7), editor.Buffer.Selection);
            // Up collapses the selection and moves its caret, on the second line, up a line.
            Assert.True(Press(h, host, VirtualKeys.Up));
            Assert.Equal(TextSelection.At(2), editor.Buffer.Selection);
            Assert.True(host.SelectionsTold > 0);
        }
    }

    [Fact]
    public void RightToLeftTextSwapsTheArrows()
    {
        (CanvasHarness h, EditorHost host) = Start();
        using (h)
        {
            CanvasTextEditor editor = Open(h, host, new Point(100, 100));
            Type(h, host, "\u05e9\u05dc\u05d5\u05dd");
            Assert.True(Press(h, host, VirtualKeys.Right));
            Assert.Equal(TextSelection.At(3), editor.Buffer.Selection);
            Assert.True(Press(h, host, VirtualKeys.Left));
            Assert.Equal(TextSelection.At(4), editor.Buffer.Selection);
        }
    }

    [Fact]
    public void ClicksInsideTheEditorPlaceTheCaretAndSelectWordsWhileOutsideCommits()
    {
        (CanvasHarness h, EditorHost host) = Start();
        using (h)
        {
            CanvasTextEditor editor = Open(h, host, new Point(100, 100));
            Type(h, host, "Hello world");
            // Characters are 14 points wide: the seventh boundary is 98 points into the frame.
            Click(h, host, new Point(198, 110));
            Assert.Equal(TextSelection.At(7), editor.Buffer.Selection);
            Click(h, host, new Point(170, 110), modifiers: KeyModifiers.Shift);
            Assert.Equal(new TextSelection(7, 5), editor.Buffer.Selection);
            Click(h, host, new Point(198, 110), count: 2);
            Assert.Equal(new TextSelection(6, 11), editor.Buffer.Selection);
            Click(h, host, new Point(198, 110), count: 3);
            Assert.Equal(new TextSelection(0, 11), editor.Buffer.Selection);
            Pointer(h, host, PointerPhase.Down, new Point(120, 110), time: 40_000);
            Pointer(h, host, PointerPhase.Move, new Point(190, 110), time: 40_050);
            Pointer(h, host, PointerPhase.Up, new Point(190, 110), time: 40_060);
            Assert.Equal(new TextSelection(1, 6), editor.Buffer.Selection);
            Assert.True(h[Main].HasTextEditor);

            // A click elsewhere with the Text tool commits and starts new text there (Mac mouseDown).
            h.Choose(Tool.Select);
            Click(h, host, new Point(600, 600));
            Assert.Null(host.Editor);
            Assert.Equal("Hello world", Assert.Single(h.Elements(Main)).Text);
        }
    }

    [Fact]
    public void EscapeDuringACompositionCancelsItAndALaterEscapeCommits()
    {
        // WIN-INP-09: "The first Escape during IME composition cancels the composition; later presses follow the 005 and 009 text rules."
        (CanvasHarness h, EditorHost host) = Start();
        using (h)
        {
            CanvasTextEditor editor = Open(h, host, new Point(100, 100));
            Type(h, host, "A");
            h.Sync();
            editor.CompositionStarted();
            editor.ReplaceFromInputMethod(1, 1, "\u304b\u306a");
            editor.CompositionUpdated(1, 3);
            editor.SelectFromInputMethod(TextSelection.At(3), interim: false);
            h.Pump();
            Assert.Equal("A\u304b\u306a", h[Main].State.Text?.Text);
            Assert.Empty(host.ToldInputMethod.Skip(1));

            // The input method finishes its composition when the editor asks; Escape then puts the text back.
            host.FinishComposition = finishing => finishing.CompositionEnded();
            Assert.True(Press(h, host, VirtualKeys.Escape));
            Assert.Equal(1, host.Finishes);
            Assert.Equal("A", editor.Text);
            Assert.Equal("A", h[Main].State.Text?.Text);
            Assert.Null(editor.Buffer.Composition);
            Assert.True(h[Main].HasTextEditor);
            Assert.Equal(new TextChange(1, 3, 1), host.ToldInputMethod[^1]);

            Assert.True(Press(h, host, VirtualKeys.Escape));
            Assert.Null(host.Editor);
            Assert.Equal("A", Assert.Single(h.Elements(Main)).Text);
        }
    }

    [Fact]
    public void ACompositionIsOneUndoStepInsideTheEditor()
    {
        (CanvasHarness h, EditorHost host) = Start();
        using (h)
        {
            CanvasTextEditor editor = Open(h, host, new Point(100, 100));
            Type(h, host, "ab");
            h.Sync();
            editor.CompositionStarted();
            editor.ReplaceFromInputMethod(2, 2, "\u304b");
            editor.CompositionUpdated(2, 3);
            editor.ReplaceFromInputMethod(2, 3, "\u304b\u306a");
            editor.CompositionUpdated(2, 4);
            editor.ReplaceFromInputMethod(2, 4, "\u4eee\u540d");
            editor.CompositionEnded();
            h.Pump();
            Assert.Equal("ab\u4eee\u540d", editor.Text);
            // Typing continues the composition's step, as it continued the typing before it.
            Assert.True(Press(h, host, VirtualKeys.Z, KeyModifiers.Ctrl));
            Assert.Equal(string.Empty, editor.Text);
            Assert.True(Press(h, host, VirtualKeys.Y, KeyModifiers.Ctrl));
            Assert.Equal("ab\u4eee\u540d", editor.Text);
        }
    }

    [Fact]
    public void AnEditWhileComposingFinishesTheCompositionFirst()
    {
        (CanvasHarness h, EditorHost host) = Start();
        using (h)
        {
            CanvasTextEditor editor = Open(h, host, new Point(100, 100));
            h.Sync();
            editor.CompositionStarted();
            editor.ReplaceFromInputMethod(0, 0, "\ud55c");
            editor.CompositionUpdated(0, 1);
            editor.SelectFromInputMethod(new TextSelection(0, 1), interim: true);
            h.Pump();
            Assert.True(editor.Buffer.InterimCharacter);
            Assert.Null(editor.Caret);
            // No input method answers here, so the editor ends the composition itself and keeps its text.
            Type(h, host, "!");
            Assert.Equal(1, host.Finishes);
            Assert.Equal("\ud55c!", editor.Text);
            Assert.NotNull(editor.Caret);
        }
    }

    [Fact]
    public void TheSelectionDrawsBelowTheTextAndCompositionLinesAbove()
    {
        (CanvasHarness h, EditorHost host) = Start();
        using (h)
        {
            CanvasTextEditor editor = Open(h, host, new Point(100, 100));
            Type(h, host, "Hello");
            Assert.Same(TextEditorDecorations.None, editor.Decorations);
            Click(h, host, new Point(114, 110));
            Click(h, host, new Point(156, 110), modifiers: KeyModifiers.Shift);
            TextEditorDecorations selection = editor.Decorations;
            DisplayItem fill = Assert.Single(selection.Under.Items.ToArray());
            Assert.Equal(DisplayOp.FillRect, fill.Op);
            Assert.Equal(new Rect(114, 100, 42, 33.6), fill.Rect);
            Assert.True(selection.Over.IsEmpty);
            Assert.Same(selection, editor.Decorations);

            h.Sync();
            editor.CompositionStarted();
            editor.ReplaceFromInputMethod(5, 5, "xyz");
            editor.CompositionUpdated(5, 8);
            editor.SelectFromInputMethod(TextSelection.At(8), interim: false);
            editor.CompositionStyled([new CompositionRun(5, 7, new CompositionStyle(CompositionLine.Solid, BoldLine: true)),
                                      new CompositionRun(7, 8, new CompositionStyle(CompositionLine.Dot, Background: new RGBA(1, 1, 0)))]);
            h.Pump();
            TextEditorDecorations composing = editor.Decorations;
            Assert.Equal([DisplayOp.FillRect], composing.Under.Items.ToArray().Select(item => item.Op));
            Assert.Equal([DisplayOp.StrokePath, DisplayOp.StrokePath], composing.Over.Items.ToArray().Select(item => item.Op));
            Assert.Equal(2, composing.Over.Items[0].Stroke!.Width);
        }
    }

    [Fact]
    public void TheCaretFollowsARotatedLabel()
    {
        var shape = new Element(Tool.Rectangle, new Rect(100, 100, 200, 120)) { Rotation = Math.PI / 2 };
        (CanvasHarness h, EditorHost host) = Start(shape);
        using (h)
        {
            h.Choose(Tool.Select);
            Click(h, host, new Point(200, 160), count: 2);
            CanvasTextEditor editor = host.Editor!;
            Assert.True(editor.Session.IsLabel);
            Type(h, host, "Hi");
            TextCaretPlacement caret = Assert.IsType<TextCaretPlacement>(editor.Caret);
            Rect frame = editor.Session.Frame;
            Point top = new Point(frame.X + frame.Width / 2 + 14, frame.Y).Rotated(frame.Center, Math.PI / 2);
            Assert.Equal(top.X, caret.Top.X, 6);
            Assert.Equal(top.Y, caret.Top.Y, 6);
            Assert.Equal(Math.PI / 2, caret.Rotation);
            Assert.True(editor.Contains(frame.Center));
            // The shape keeps a gap for the label as it is typed (Mac visibleElements).
            Assert.Equal("Hi", h[Main].State.Elements[0].Label?.Text);
            Assert.Equal([shape.Id], h[Main].State.HiddenLabelIds);
        }
    }

    [Fact]
    public void TheSummaryCountsTheTextBeingTypedOnceAndSelectedLikeTheMac()
    {
        // Mac refreshAccessibilitySummary reads session.document, which the Mac synchronizes when its editor opens: the new
        // text is in it, selected, as soon as the editor shows; a label's shape is selected while its label is edited.
        var shape = new Element(Tool.Rectangle, new Rect(400, 100, 200, 120));
        (CanvasHarness h, EditorHost host) = Start(shape);
        using (h)
        {
            Assert.Equal((1, 0), CanvasAccessibility.Counts(h[Main]));
            Open(h, host, new Point(100, 300));
            Assert.Equal((2, 1), CanvasAccessibility.Counts(h[Main]));
            Type(h, host, "Hi");
            Assert.Equal((2, 1), CanvasAccessibility.Counts(h[Main]));
            Assert.True(Press(h, host, VirtualKeys.Escape));
            Assert.Equal(2, CanvasAccessibility.Counts(h[Main]).Objects);

            // Blank new text leaves nothing behind.
            Open(h, host, new Point(100, 500));
            Assert.Equal((3, 1), CanvasAccessibility.Counts(h[Main]));
            Assert.True(Press(h, host, VirtualKeys.Escape));
            Assert.Equal(2, CanvasAccessibility.Counts(h[Main]).Objects);

            h.Choose(Tool.Text);
            Click(h, host, shape.Frame.Center);
            Assert.True(host.Editor?.Session.IsLabel);
            Assert.Equal((2, 1), CanvasAccessibility.Counts(h[Main]));
        }
    }

    [Fact]
    public void TheLiveTextIsDrawnExactlyLikeTheCommittedText()
    {
        // WIN-INP-09: the committed text matches the edited text within 0.5 points; here they are the same display list.
        var gradient = new ColorGradient([new RGBA(1, 0, 0), new RGBA(0, 0, 1)]);
        var shapeStyle = Style.Defaults(Tool.Rectangle);
        shapeStyle.StrokeGradient = gradient;
        var shape = new Element(Tool.Rectangle, new Rect(400, 100, 200, 120), style: shapeStyle) { Rotation = 0.3 };
        var cover = new Element(Tool.Cover, new Rect(0, 0, 50, 50));
        (CanvasHarness h, EditorHost host) = Start(shape, cover);
        using (h)
        {
            var scene = new CanvasScene(new FixedTextMeasurer());
            foreach (bool label in new[] { false, true })
            {
                if (label)
                {
                    h.Choose(Tool.Text);
                    Click(h, host, shape.Frame.Center);
                }
                else
                {
                    Open(h, host, new Point(100, 300));
                }
                Type(h, host, "Live text");
                scene.Update(h[Main].State, new Rect(0, 0, 1600, 1200), editor: host.Editor!.Decorations);
                DisplayItem live = scene.Live.Elements.ToArray().SelectMany(list => list.Items.ToArray()).Single(item => item.Op == DisplayOp.DrawText);
                // The caret and the selection are laid out on the baseline the live text is drawn on.
                CanvasTextEditor editor = host.Editor!;
                Assert.Equal(live.Text!.Baseline, editor.Session.Frame.Y + editor.Layout.Baseline, 9);
                // Covers come after the live text, so nothing typed shows through a cover.
                Assert.Equal(cover.Id, scene.Live.Elements[^1].ElementId);
                Assert.True(Press(h, host, VirtualKeys.Escape));
                scene.Update(h[Main].State, new Rect(0, 0, 1600, 1200));
                Assert.True(scene.Live.Elements.IsEmpty);
                DisplayItem committed = scene.Scene.Elements.ToArray().SelectMany(list => list.Items.ToArray())
                    .Single(item => item.Op == DisplayOp.DrawText && item.Text!.Text == "Live text");
                Assert.Equal(committed.Text, live.Text);
                if (label)
                {
                    Assert.Equal(PaintKind.LinearGradient, live.Text!.Paint.Kind);
                }
                h.SetDocument(Main, shape, cover);
            }
        }
    }
}
