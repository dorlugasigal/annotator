using Annotator.Core;
using Annotator.Workspace.Canvas;
using static Annotator.Workspace.Tests.Canvas.CanvasHarness;

namespace Annotator.Workspace.Tests.Canvas;

/// <summary>
/// Until the Windows text editor (spike S-7) exists, the app runs without one: text must say it is not ready instead of
/// opening an editor that nothing can be typed into.
/// </summary>
public sealed class TextAvailabilityTests
{
    [Fact]
    public void TheTextToolSaysItIsNotReadyAndKeepsTheCurrentTool()
    {
        using var h = new CanvasHarness([MainDisplay], textEditing: false);
        h.Choose(Tool.Pen);
        Assert.False(h.Model.Snapshot.TextEditingAvailable);
        Assert.Equal(ToolChoice.Unavailable, h.Model.Choose(Tool.Text));
        Assert.Equal(Tool.Pen, h.Model.Snapshot.Tool);
        Assert.Equal(WorkspaceModel.TextUnavailableMessage, h.Model.Snapshot.Status);
    }

    [Fact]
    public void DoubleClickingAShapeOrTheCanvasOpensNoEditor()
    {
        using var h = new CanvasHarness([MainDisplay], textEditing: false);
        var shape = new Element(Tool.Rectangle, new Rect(100, 100, 80, 60), style: Style.Defaults(Tool.Rectangle));
        h.SetDocument(Main, shape);
        h.Choose(Tool.Select);
        foreach (Point point in new[] { new Point(100, 130), new Point(600, 600) })
        {
            h.Click(Main, point, count: 2);
            Assert.False(h[Main].HasTextEditor);
            Assert.Null(h[Main].State.Text);
            Assert.Equal(WorkspaceModel.TextUnavailableMessage, h.Model.Snapshot.Status);
        }
        Assert.Equal(0, h.Editor.Begun);
        Element only = Assert.Single(h.Elements(Main));
        Assert.Equal(shape.Id, only.Id);
        Assert.Equal(shape.Frame, only.Frame);
        Assert.Null(only.Label);
        Assert.Null(h.Model.Display(Main).ActiveEdit);
    }

    [Fact]
    public void WithAnEditorTextStaysAvailable()
    {
        using CanvasHarness h = Single();
        Assert.True(h.Model.Snapshot.TextEditingAvailable);
        Assert.Equal(ToolChoice.Chosen, h.Model.Choose(Tool.Text));
    }
}
