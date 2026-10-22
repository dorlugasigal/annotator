using Annotator.Core;
using static Annotator.Workspace.Tests.TestWorkspace;

namespace Annotator.Workspace.Tests;

/// <summary>The threading and edit contract between the workspace (app thread) and canvases (overlay thread).</summary>
public sealed class EditContractTests
{
    [Fact]
    public void AcceptedEditsAdvanceToThePredictedVersionSoCanvasesCanChainEdits()
    {
        WorkspaceModel model = Drawing();
        DisplaySnapshot start = model.Display(Primary);
        CanvasDocument first = start.Document;
        first.Add(new Element(Tool.Rectangle));
        CanvasDocument second = first;
        second.Add(new Element(Tool.Ellipse));

        var accepted = Assert.IsType<EditResult.Accepted>(model.Submit(new EditSubmission(new DisplayEdit(Primary, start.Version, first))));
        Assert.Equal(start.Version.Next, accepted.Versions.Single().Version);
        AssertAccepted(model.Submit(new EditSubmission(new DisplayEdit(Primary, start.Version.Next, second))));

        Assert.Equal(start.Version.Next.Next, model.Display(Primary).Version);
        Assert.Equal(2, model.Elements(Primary).Length);
        model.Undo();
        Assert.Single(model.Elements(Primary));
    }

    [Fact]
    public void AnEditBuiltOnARejectedEditIsNeverAccepted()
    {
        WorkspaceModel model = Drawing();
        DisplaySnapshot start = model.Display(Primary);
        CanvasDocument first = start.Document;
        first.Add(new Element(Tool.Rectangle));
        CanvasDocument second = first;
        second.Add(new Element(Tool.Ellipse));

        model.UseApps();
        model.BeginDrawing();
        AssertRejected(model.Submit(new EditSubmission(new DisplayEdit(Primary, start.Version, first))), EditRejection.StaleVersion);
        AssertRejected(model.Submit(new EditSubmission(new DisplayEdit(Primary, start.Version.Next, second))), EditRejection.StaleVersion);
        Assert.Empty(model.Elements(Primary));
    }

    [Fact]
    public void WorkspaceChangesInvalidateEditsStartedBeforeThem()
    {
        WorkspaceModel model = Drawing();
        AssertAccepted(model.Add(Primary, new Element(Tool.Rectangle)));
        DisplaySnapshot before = model.Display(Primary);
        AssertAccepted(model.BeginEdit(Primary, before.Version, EditKind.Gesture));
        model.Undo();
        Assert.NotEqual(before.Version, model.Display(Primary).Version);
        Assert.Single(model.Elements(Primary));

        CanvasDocument late = before.Document;
        late.Add(new Element(Tool.Ellipse));
        AssertRejected(model.Submit(new EditSubmission(new DisplayEdit(Primary, before.Version, late))), EditRejection.StaleVersion);
        Assert.Single(model.Elements(Primary));
    }

    [Fact]
    public void SubmissionsAreValidated()
    {
        WorkspaceModel model = Drawing();
        DisplaySnapshot display = model.Display(Primary);

        CanvasDocument open = display.Document;
        open.Begin();
        open.Add(new Element(Tool.Rectangle));
        AssertRejected(model.Submit(new EditSubmission(new DisplayEdit(Primary, display.Version, open))), EditRejection.StillEditing);

        var stranger = new CanvasDocument();
        AssertRejected(model.Submit(new EditSubmission(new DisplayEdit(Primary, display.Version, stranger))), EditRejection.DocumentMismatch);

        AssertRejected(model.Submit(new EditSubmission(new DisplayEdit(new DisplayId("missing"), display.Version, display.Document))),
            EditRejection.UnknownDisplay);

        DisplaySnapshot left = model.Display(Left);
        AssertRejected(model.Submit(new EditSubmission(
            [new DisplayEdit(Primary, display.Version, display.Document), new DisplayEdit(Left, left.Version, left.Document)], null)),
            EditRejection.InvalidHistory);
        AssertRejected(model.Submit(new EditSubmission(
            [new DisplayEdit(Primary, display.Version, display.Document), new DisplayEdit(Primary, display.Version, display.Document)], Guid.NewGuid())),
            EditRejection.InvalidHistory);

        model.HideTools();
        DisplaySnapshot hidden = model.Display(Primary);
        AssertRejected(model.Submit(new EditSubmission(new DisplayEdit(Primary, hidden.Version, hidden.Document))), EditRejection.NotDrawing);
        Assert.Throws<ArgumentException>(() => new EditSubmission([], null));
    }

    [Fact]
    public void StyleSessionsBlockCanvasEditsUntilFinished()
    {
        WorkspaceModel model = Drawing();
        var shape = new Element(Tool.Rectangle);
        AssertAccepted(model.Edit(Primary, document =>
        {
            document[0].Add(shape);
            document[0].Select([shape.Id]);
        }));
        model.BeginStyleEdit();
        Assert.True(model.Document(Primary).IsEditing);
        DisplaySnapshot during = model.Display(Primary);
        AssertRejected(model.BeginEdit(Primary, during.Version, EditKind.Gesture), EditRejection.StyleEditOpen);
        model.FinishStyleEdit();
        DisplaySnapshot after = model.Display(Primary);
        Assert.False(after.Document.IsEditing);
        AssertAccepted(model.BeginEdit(Primary, after.Version, EditKind.Gesture));
    }

    [Fact]
    public void TextInterruptedByUseAppsIsCommitted()
    {
        WorkspaceModel model = Drawing();
        DisplaySnapshot display = model.Display(Primary);
        AssertAccepted(model.BeginEdit(Primary, display.Version, EditKind.Text));
        CanvasDocument typing = display.Document;
        typing.Begin();
        var text = new Element(Tool.Text, new Rect(5, 5, 60, 30), text: "Note");
        typing.Add(text);
        AssertAccepted(model.UpdateEdit(Primary, display.Version, typing));
        model.UseApps();
        Assert.Equal([text], model.Elements(Primary));
        Assert.False(model.Document(Primary).IsEditing);
    }

    [Fact]
    public void ClickedPathsFinishOnToolChoiceAndCancelOtherwise()
    {
        WorkspaceModel model = Drawing();
        var arrow = new Element(Tool.Arrow, points: ValueList.Create<Point>([new(0, 0), new(40, 0)]));
        foreach (bool chooseTool in new[] { false, true })
        {
            DisplaySnapshot display = model.Display(Primary);
            AssertAccepted(model.BeginEdit(Primary, display.Version, EditKind.Path));
            CanvasDocument building = display.Document;
            building.Begin();
            building.Add(arrow with { Id = Guid.NewGuid() });
            AssertAccepted(model.UpdateEdit(Primary, display.Version, building));
            if (chooseTool)
            {
                model.Choose(Tool.Pen);
                Assert.Single(model.Elements(Primary));
            }
            else
            {
                model.UseApps();
                model.BeginDrawing();
                Assert.Empty(model.Elements(Primary));
            }
        }
    }

    [Fact]
    public void UpdatesNeedTheEditTheyBelongTo()
    {
        WorkspaceModel model = Drawing();
        DisplaySnapshot display = model.Display(Primary);
        AssertRejected(model.UpdateEdit(Primary, display.Version, display.Document), EditRejection.StaleVersion);
        AssertAccepted(model.BeginEdit(Primary, display.Version, EditKind.Text));
        AssertRejected(model.UpdateEdit(Primary, display.Version, new CanvasDocument()), EditRejection.DocumentMismatch);
        AssertRejected(model.CancelEdit(Primary, display.Version.Next), EditRejection.StaleVersion);
        AssertAccepted(model.CancelEdit(Primary, display.Version));
        Assert.Equal(display.Version, model.Display(Primary).Version);
    }

    [Fact]
    public void EveryChangePublishesAnImmutableSnapshot()
    {
        WorkspaceModel model = Create();
        var published = new List<WorkspaceSnapshot>();
        model.Published += published.Add;
        ulong revision = model.Snapshot.Revision;
        model.BeginDrawing();
        Assert.Single(published);
        Assert.Same(published[0], model.Snapshot);
        Assert.True(model.Snapshot.Revision > revision);

        WorkspaceSnapshot before = model.Snapshot;
        CanvasDocument copy = before.Display(Primary)!.Document;
        copy.Add(new Element(Tool.Rectangle));
        Assert.True(before.Display(Primary)!.Document.Elements.IsEmpty);
        Assert.True(model.Document(Primary).Elements.IsEmpty);

        AssertAccepted(model.Add(Primary, new Element(Tool.Ellipse)));
        Assert.True(before.Display(Primary)!.Document.Elements.IsEmpty);
        Assert.Single(model.Snapshot.Display(Primary)!.Document.Elements);
        Assert.Equal(2, published.Count);
    }

    [Fact]
    public void TheModelBelongsToItsThread()
    {
        WorkspaceModel model = Create();
        WorkspaceSnapshot? seen = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            seen = model.Snapshot;
            try
            {
                model.BeginDrawing();
            }
            catch (InvalidOperationException error)
            {
                failure = error;
            }
        });
        thread.Start();
        thread.Join();
        Assert.NotNull(seen);
        Assert.IsType<InvalidOperationException>(failure);
    }

    [Fact]
    public void VersionsChainInTheLowBitsAndFreshVersionsDiffer()
    {
        DocumentVersion fresh = DocumentVersion.Fresh(3);
        Assert.Equal(3UL << 32, fresh.Value);
        Assert.Equal((3UL << 32) + 1, fresh.Next.Value);
        Assert.NotEqual(DocumentVersion.Fresh(4), fresh.Next);
        Assert.Equal("3.1", fresh.Next.ToString());
    }
}
