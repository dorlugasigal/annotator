using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Workspace.Files;
using static Annotator.Workspace.Tests.TestWorkspace;

namespace Annotator.Workspace.Tests.Files;

/// <summary>
/// Open board, Save board and Export PNG in the workspace (Mac WorkspaceModel saveBoard, openBoard and exportPNG; spec 005),
/// with ports of SupportInfoTests' board error checks: an import error keeps the drawing, its mode and its history.
/// </summary>
public sealed class BoardWorkflowTests
{
    private const string OpenFailure = "The drawing could not be opened: Choose another Annotator board. Your current drawing has not changed. ";

    [Fact]
    public void ABrokenBoardKeepsTheDrawingAndALaterValidBoardOpens()
    {
        // SupportInfoTests.testBrokenBoardErrorStaysVisibleWithoutOpeningSettingsOrChangingWork (the model's part; the error
        // window's part is the app's ErrorWindow).
        using var folder = new TemporaryFolder();
        WorkspaceModel model = Drawing();
        var shape = new Element(Tool.Rectangle, new Rect(100, 100, 100, 80));
        AssertAccepted(model.Edit(Primary, document =>
        {
            document[0].Add(shape);
            document[0].Select([shape.Id]);
        }));
        CanvasDocument before = model.Document(Primary);
        string broken = folder.File("broken.annotator");
        File.WriteAllText(broken, "{\"schema\":1,\"elements\":[");

        Open(model, broken);
        Assert.Equal(OpenFailure + DecodingError.ReadCorruptMessage, model.Snapshot.ErrorMessage);
        Assert.Equal(model.Snapshot.ErrorMessage, model.Snapshot.Status);
        Assert.Equal(WorkspaceMode.Draw, model.Snapshot.Mode);
        Assert.Equal([shape], model.Elements(Primary));
        Assert.Equal([shape.Id], model.Document(Primary).Selection);
        Assert.Equal(before.Generation, model.Document(Primary).Generation);
        Assert.True(model.Document(Primary).CanUndo);

        Assert.True(model.DismissError(model.Snapshot.ErrorMessage!));
        Assert.Null(model.Snapshot.ErrorMessage);
        var replacement = new Element(Tool.Ellipse, new Rect(200, 200, 60, 60));
        string valid = folder.File("valid.annotator");
        BoardFiles.Save(valid, new CanvasDocument(ElementList.Create([replacement])));
        Open(model, valid);
        Assert.Null(model.Snapshot.ErrorMessage);
        Assert.Equal([replacement.Id], model.Elements(Primary).Select(element => element.Id));
        Assert.Equal(WorkspaceModel.BoardOpenedMessage, model.Snapshot.Status);
    }

    [Fact]
    public void AnUnsupportedFormatSaysWhichFormat()
    {
        using var folder = new TemporaryFolder();
        WorkspaceModel model = Drawing();
        string future = folder.File("future.annotator");
        File.WriteAllText(future, "{\"elements\":[],\"schemaVersion\":9}");
        Open(model, future);
        Assert.Equal(OpenFailure + "Drawing format 9 is not supported by this version.", model.Snapshot.ErrorMessage);
    }

    [Fact]
    public void AnOpenedBoardReplacesTheEditingTargetsBoardWithItsOwnHistory()
    {
        WorkspaceModel model = Drawing();
        AssertAccepted(model.Add(Primary, new Element(Tool.Rectangle, new Rect(10, 10, 50, 50))));
        var left = new Element(Tool.Ellipse, new Rect(5, 5, 20, 20));
        AssertAccepted(model.Add(Left, left));
        AssertAccepted(model.Add(Primary, new Element(Tool.Line, new Rect(0, 0, 10, 10))));
        var opened = new Element(Tool.Diamond, new Rect(40, 40, 80, 60));
        model.OpenBoard(new CanvasDocument(ElementList.Create([opened])));
        Assert.Equal([opened], model.Elements(Primary));
        Assert.Equal([left], model.Elements(Left));
        Assert.False(model.Snapshot.CanUndo);
        model.Undo();
        Assert.Equal([opened], model.Elements(Primary));
        Assert.Equal(WorkspaceMode.Draw, model.Snapshot.Mode);
    }

    [Fact]
    public void ABoardWithCoversIsNotReplaced()
    {
        // Mac openBoard(from:): "Remove existing protective covers explicitly before replacing this board."
        WorkspaceModel model = Drawing();
        var cover = new Element(Tool.Cover, new Rect(10, 10, 50, 50));
        AssertAccepted(model.Add(Primary, cover));
        model.OpenBoard(new CanvasDocument(ElementList.Create([new Element(Tool.Ellipse, new Rect(0, 0, 10, 10))])));
        Assert.Equal(OpenFailure + "The drawing could not be opened: Remove existing protective covers explicitly before replacing this board.",
            model.Snapshot.ErrorMessage);
        Assert.Equal([cover], model.Elements(Primary));
    }

    [Fact]
    public void OpeningABoardStartsDrawingFromUseAppsAndFromHiddenTools()
    {
        WorkspaceModel model = Create();
        model.ShowToolbar();
        var first = new Element(Tool.Rectangle, new Rect(0, 0, 10, 10));
        model.OpenBoard(new CanvasDocument(ElementList.Create([first])));
        Assert.Equal(WorkspaceMode.Draw, model.Snapshot.Mode);
        DisplayId target = model.Snapshot.ActiveDisplay!.Value;
        Assert.Equal([first], model.Elements(target));

        model.UseApps();
        model.HideTools();
        Assert.True(model.Snapshot.WorkspaceHidden);
        var second = new Element(Tool.Ellipse, new Rect(0, 0, 10, 10));
        model.OpenBoard(new CanvasDocument(ElementList.Create([second])));
        Assert.False(model.Snapshot.WorkspaceHidden);
        Assert.Equal(WorkspaceMode.Draw, model.Snapshot.Mode);
        Assert.Equal([second], model.Elements(target));
    }

    [Fact]
    public void SaveNeedsADrawingSessionAndCommitsTextFirst()
    {
        WorkspaceModel model = Create();
        Assert.Null(model.PrepareBoardSave());
        Assert.Equal(WorkspaceModel.NoSessionMessage, model.Snapshot.Status);
        Assert.False(model.PrepareExport());
        Assert.Null(model.ExportRequest());

        model.BeginDrawing();
        DisplaySnapshot display = model.Display(Primary);
        Assert.IsType<EditResult.Accepted>(model.BeginEdit(Primary, display.Version, EditKind.Text));
        CanvasDocument typing = display.Document;
        typing.Begin();
        var text = new Element(Tool.Text, new Rect(20, 20, 80, 30), text: "Hello");
        typing.Add(text);
        Assert.IsType<EditResult.Accepted>(model.UpdateEdit(Primary, display.Version, typing));
        BoardSaveRequest request = Assert.IsType<BoardSaveRequest>(model.PrepareBoardSave());
        Assert.Equal("Explanation.annotator", request.SuggestedName);
        Assert.Null(request.Message);
        Assert.Null(model.Display(Primary).ActiveEdit);
        Assert.Equal("Hello", Assert.Single(model.BoardToSave()!.Value.Elements.ToArray()).Text);
        model.BoardSaved();
        Assert.Equal(WorkspaceModel.BoardSavedMessage, model.Snapshot.Status);
    }

    [Fact]
    public void SavingABoardWithCoversAndImagesSaysThatTheImagesStayInIt()
    {
        WorkspaceModel model = Drawing();
        AssertAccepted(model.Add(Primary, new Element(Tool.Image, new Rect(0, 0, 40, 30)) { ImagePng = ValueList.Create(BoardFilesTests.Png) }));
        Assert.Null(model.PrepareBoardSave()!.Message);
        AssertAccepted(model.Add(Primary, new Element(Tool.Blur, new Rect(0, 0, 40, 30))));
        Assert.Equal("Editable boards retain original images. Share a flattened PNG for censored content.", model.PrepareBoardSave()!.Message);
    }

    [Fact]
    public void ExportUsesTheEditingTargetsCanvasScaleAndBoard()
    {
        // Mac exportPNG: the active canvas's bounds at its backing scale, over white or the Mac's ink for the boards.
        WorkspaceModel model = Drawing();
        var shape = new Element(Tool.Rectangle, new Rect(10, 10, 50, 50));
        AssertAccepted(model.Add(Primary, shape));
        Assert.True(model.PrepareExport());
        PngExport export = model.ExportRequest()!;
        Assert.Equal([shape], export.Elements.ToArray());
        Assert.Equal(new Size(640, 480), export.Size);
        Assert.Equal(2, export.Scale);
        Assert.Null(export.Background);
        model.SetBoard(Board.Whiteboard);
        Assert.Equal(Palette.White, model.ExportRequest()!.Background);
        model.SetBoard(Board.Blackboard);
        Assert.Equal(Palette.Ink, model.ExportRequest()!.Background);
        Assert.Equal("Explanation.png", PngExport.SuggestedName);
        model.PngExported();
        Assert.Equal("Annotations exported. This PNG does not contain desktop pixels.", model.Snapshot.Status);
    }

    [Fact]
    public void DismissingAnErrorNeverClearsANewerOne()
    {
        // SupportInfoTests.testErrorDismissalDoesNotClearANewerErrorAndWindowCloseAcknowledgesIt (the model's part).
        WorkspaceModel model = Drawing();
        model.ReportFailure(CanvasError.InvalidDocument("First error"));
        string first = model.Snapshot.ErrorMessage!;
        model.ReportFailure(CanvasError.InvalidDocument("Second error"));
        Assert.False(model.DismissError(first));
        Assert.Contains("Second error", model.Snapshot.ErrorMessage, StringComparison.Ordinal);
        Assert.True(model.DismissError(model.Snapshot.ErrorMessage!));
        Assert.Null(model.Snapshot.ErrorMessage);
        Assert.Equal(WorkspaceMode.Draw, model.Snapshot.Mode);
    }

    [Fact]
    public void FileAndImageFailuresAreReportedWithTheirMessages()
    {
        WorkspaceModel model = Drawing();
        model.ReportFailure(new RenderFailure(RenderFailureKind.InvalidSize));
        Assert.Equal("Export dimensions exceed the supported raster budget.", model.Snapshot.ErrorMessage);
        model.ReportFailure(new UnauthorizedAccessException("Access to the path is denied."));
        Assert.Equal("Access to the path is denied.", model.Snapshot.ErrorMessage);
    }

    /// <summary>What the app does after the Open dialog: read and check the file off the app thread, then open or report.</summary>
    private static void Open(WorkspaceModel model, string path)
    {
        CanvasDocument document;
        try
        {
            document = BoardFiles.Load(path, static _ => { });
        }
        catch (Exception error) when (error is IOException or CanvasError or DecodingError or RenderFailure)
        {
            model.ReportBoardOpenFailure(error);
            return;
        }
        model.OpenBoard(document);
    }
}
