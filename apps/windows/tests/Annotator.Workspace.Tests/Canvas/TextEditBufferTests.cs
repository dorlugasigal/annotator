using Annotator.Workspace.Canvas;

namespace Annotator.Workspace.Tests.Canvas;

/// <summary>The text editor's buffer and boundaries: its Undo steps, input-method edits and the word and cluster rules.</summary>
public sealed class TextEditBufferTests
{
    [Fact]
    public void TypingIsOneUndoStepUntilTheCaretMoves()
    {
        var buffer = new TextEditBuffer(string.Empty);
        foreach (char character in "abc")
        {
            buffer.Insert(character.ToString());
        }
        buffer.Select(TextSelection.At(1));
        buffer.Insert("X");
        Assert.Equal("aXbc", buffer.Text);
        Assert.Equal(new TextChange(1, 2, 1), buffer.Undo());
        Assert.Equal("abc", buffer.Text);
        Assert.Equal(TextSelection.At(1), buffer.Selection);
        Assert.Equal(new TextChange(0, 3, 0), buffer.Undo());
        Assert.Equal(string.Empty, buffer.Text);
        Assert.False(buffer.CanUndo);
        Assert.Equal(new TextChange(0, 0, 3), buffer.Redo());
        Assert.Equal(TextSelection.At(3), buffer.Selection);
    }

    [Fact]
    public void DeletingInOneDirectionIsOneStepAndTypingStartsAnother()
    {
        var buffer = new TextEditBuffer("abcdef");
        buffer.Replace(5, 6, string.Empty, TextEditGroup.DeleteBackward);
        buffer.Replace(4, 5, string.Empty, TextEditGroup.DeleteBackward);
        buffer.Insert("Z");
        Assert.Equal("abcdZ", buffer.Text);
        buffer.Undo();
        Assert.Equal("abcd", buffer.Text);
        buffer.Undo();
        Assert.Equal("abcdef", buffer.Text);
        Assert.False(buffer.CanUndo);
    }

    [Fact]
    public void AnInputMethodsEditMovesTheSelectionWithTheText()
    {
        var buffer = new TextEditBuffer("hello world", new TextSelection(8, 10));
        TextChange change = buffer.ReplaceFromInputMethod(0, 5, "HI");
        Assert.Equal(new TextChange(0, 5, 2), change);
        Assert.Equal(new TextSelection(5, 7), buffer.Selection);
        buffer.ReplaceFromInputMethod(5, 6, "--");
        Assert.Equal("HI wo--ld", buffer.Text);
        Assert.Equal(TextSelection.At(7), buffer.Selection);
        // Outside a composition it is typing: one Undo step with the typing around it.
        buffer.Undo();
        Assert.Equal("hello world", buffer.Text);
    }

    [Fact]
    public void CancellingACompositionLeavesNoTextAndNoUndoStep()
    {
        var buffer = new TextEditBuffer("ab");
        buffer.StartComposition();
        buffer.ReplaceFromInputMethod(2, 2, "\u304b");
        buffer.UpdateComposition(2, 3);
        (string Text, TextSelection Selection)? before = buffer.BeforeComposition;
        Assert.Equal(("ab", TextSelection.At(2)), before);
        // The input method finishes it first, then the editor puts the text back.
        buffer.EndComposition();
        Assert.True(buffer.CanUndo);
        Assert.Equal(new TextChange(2, 3, 2), buffer.CancelComposition(before));
        Assert.Equal("ab", buffer.Text);
        Assert.Equal(TextSelection.At(2), buffer.Selection);
        Assert.False(buffer.CanUndo);
        Assert.Null(buffer.Composition);
    }

    [Fact]
    public void CompositionRunsStayInsideTheComposition()
    {
        var buffer = new TextEditBuffer("ab");
        buffer.StartComposition();
        buffer.ReplaceFromInputMethod(2, 2, "xyz");
        buffer.UpdateComposition(2, 5);
        buffer.SetCompositionRuns([new CompositionRun(0, 3, CompositionStyle.Default), new CompositionRun(4, 9, CompositionStyle.Default)]);
        Assert.Equal([new CompositionRun(2, 3, CompositionStyle.Default), new CompositionRun(4, 5, CompositionStyle.Default)], buffer.Composition!.Runs);
    }

    [Fact]
    public void TheUndoHistoryKeepsTheLastHundredSteps()
    {
        var buffer = new TextEditBuffer(string.Empty);
        for (int index = 0; index < TextEditBuffer.UndoLimit + 20; index++)
        {
            buffer.Insert("x", TextEditGroup.None);
        }
        int steps = 0;
        while (buffer.Undo() is not null)
        {
            steps++;
        }
        Assert.Equal(TextEditBuffer.UndoLimit, steps);
        Assert.Equal(new string('x', 20), buffer.Text);
    }

    [Theory]
    [InlineData("abc", "abc", null)]
    [InlineData("abc", "abXc", "2,2,3")]
    [InlineData("aaa", "aa", "2,3,2")]
    [InlineData("hello", "help", "3,5,4")]
    public void DifferenceIsTheSmallestChange(string before, string after, string? expected)
    {
        TextChange? change = TextEditBuffer.Difference(before, after);
        Assert.Equal(expected, change is { } value ? $"{value.Start},{value.OldEnd},{value.NewEnd}" : null);
    }

    [Fact]
    public void ClustersKeepEmojiAndAccentsWhole()
    {
        string text = "e\u0301\U0001F468\u200D\U0001F469\u200D\U0001F467x\r\ny";
        Assert.Equal(2, TextBoundaries.NextCluster(text, 0));
        Assert.Equal(10, TextBoundaries.NextCluster(text, 2));
        Assert.Equal(13, TextBoundaries.NextCluster(text, 11));
        Assert.Equal(2, TextBoundaries.PreviousCluster(text, 10));
        Assert.Equal(0, TextBoundaries.PreviousCluster(text, 2));
        Assert.Equal(11, TextBoundaries.PreviousCluster(text, 13));
    }

    [Fact]
    public void WordsFollowWindowsCtrlArrows()
    {
        const string text = "Hello, big  world\nnext";
        Assert.Equal(5, TextBoundaries.NextWord(text, 0));
        Assert.Equal(7, TextBoundaries.NextWord(text, 5));
        Assert.Equal(12, TextBoundaries.NextWord(text, 7));
        Assert.Equal(17, TextBoundaries.NextWord(text, 12));
        Assert.Equal(18, TextBoundaries.NextWord(text, 17));
        Assert.Equal(12, TextBoundaries.PreviousWord(text, 17));
        Assert.Equal(7, TextBoundaries.PreviousWord(text, 12));
        Assert.Equal(17, TextBoundaries.PreviousWord(text, 18));
        Assert.Equal((7, 10), TextBoundaries.WordAt(text, 8));
        Assert.Equal((10, 12), TextBoundaries.WordAt(text, 11));
        Assert.Equal((18, 22), TextBoundaries.ParagraphAt(text, 20));
    }

    [Theory]
    [InlineData("\u05e9\u05dc\u05d5\u05dd", true)]
    [InlineData("12 \u05e9\u05dc\u05d5\u05dd", true)]
    [InlineData("Hello \u05e9\u05dc\u05d5\u05dd", false)]
    [InlineData("\u0645\u0631\u062d\u0628\u0627", true)]
    [InlineData("", false)]
    public void TheBaseDirectionIsTheFirstStrongCharacter(string text, bool rightToLeft) =>
        Assert.Equal(rightToLeft, TextBoundaries.IsRightToLeft(text));
}
