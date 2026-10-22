using Annotator.Core;
using Annotator.Workspace.Library;

namespace Annotator.Workspace.Tests.Library;

/// <summary>
/// My shapes in the workspace: the Mac ShapeLibraryModel's operations and messages, MyShapesLibrary.saveSelection's checks
/// and WorkspaceModel.insertSavedShape (spec 005 "My shapes", WIN-CORE-03).
/// </summary>
public sealed class MyShapesTests
{
    private static readonly Element Box = new(Tool.Rectangle, new Rect(100, 80, 120, 60), style: Style.Defaults(Tool.Rectangle));

    [Fact]
    public async Task AnInMemoryLibrarySavesRenamesAndDeletes()
    {
        ShapeLibrary library = ShapeLibrary.InMemory();
        int changes = 0;
        library.Changed += () => changes++;
        Assert.False(library.IsLoaded);
        await library.ReloadAsync();
        Assert.True(library.IsLoaded);
        Assert.Empty(library.Shapes);
        Assert.Equal("Login box", (await library.SaveAsync("  Login box \n", Selected(Box)))?.Name);
        SavedShape saved = Assert.Single(library.Shapes);
        Assert.Equal("Login box", saved.Name);
        await library.RenameAsync(saved, "Sign-in box");
        Assert.Equal("Sign-in box", Assert.Single(library.Shapes).Name);
        Assert.Equal(saved.Id, library.Shapes[0].Id);
        await library.DeleteAsync(library.Shapes[0]);
        Assert.Empty(library.Shapes);
        Assert.Null(library.ErrorMessage);
        Assert.False(library.IsBusy);
        Assert.Equal(8, changes);
    }

    [Fact]
    public async Task FailuresUseTheMacMessages()
    {
        ShapeLibrary library = ShapeLibrary.InMemory();
        await library.ReloadAsync();
        Assert.Null(await library.SaveAsync("   ", Selected(Box)));
        Assert.Equal("Shape not saved. Enter a name with 1 to 120 characters and no line breaks or control characters.", library.ErrorMessage);
        Assert.Null(await library.SaveAsync(new string('a', 121), Selected(Box)));
        Assert.Equal("Shape not saved. Enter a name with 1 to 120 characters and no line breaks or control characters.", library.ErrorMessage);
        Assert.Null(await library.SaveAsync("Box", CanvasDocument.Empty()));
        Assert.Equal("Shape not saved. Select one or more drawings to save.", library.ErrorMessage);
        var cover = new Element(Tool.Cover, new Rect(0, 0, 40, 40), style: Style.Defaults(Tool.Cover));
        Assert.Null(await library.SaveAsync("Cover", Selected(cover)));
        Assert.Equal("Shape not saved. My shapes saves drawings and text only. Deselect covers, pixel effects and images, then try again.",
            library.ErrorMessage);
        Assert.NotNull(await library.SaveAsync("Box", Selected(Box)));
        Assert.Null(library.ErrorMessage);
        await library.RenameAsync(library.Shapes[0], "a\u0007b");
        Assert.Equal("Shape not renamed. Enter a name with 1 to 120 characters and no line breaks or control characters.", library.ErrorMessage);
        SavedShape gone = library.Shapes[0];
        await library.DeleteAsync(gone);
        await library.DeleteAsync(gone);
        Assert.Equal("Shape not deleted. My shapes could not read a saved shape. This shape is no longer saved. Reload My shapes.", library.ErrorMessage);
    }

    [Fact]
    public async Task AFolderLibraryReadsNothingUntilUsedAndKeepsOneFilePerShape()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "scratch-my-shapes", Guid.NewGuid().ToString("N"), "My Shapes");
        try
        {
            ShapeLibrary library = ShapeLibrary.InFolder(folder);
            Assert.Equal(folder, library.Directory);
            Assert.False(Directory.Exists(folder));
            await library.ReloadAsync();
            Assert.True(library.IsLoaded);
            Assert.False(Directory.Exists(folder));
            Assert.NotNull(await library.SaveAsync("Box", Selected(Box)));
            string file = Assert.Single(Directory.GetFiles(folder));
            Assert.Equal($"{library.Shapes[0].Id.ToString().ToUpperInvariant()}.json", Path.GetFileName(file));
            ShapeLibrary reopened = ShapeLibrary.InFolder(folder);
            await reopened.ReloadAsync();
            Assert.Equal("Box", Assert.Single(reopened.Shapes).Name);
            await File.WriteAllTextAsync(file, "{", TestContext.Current.CancellationToken);
            await reopened.ReloadAsync();
            Assert.False(reopened.IsLoaded);
            Assert.Equal($"My shapes could not load. {DecodingError.ReadCorruptMessage}", reopened.ErrorMessage);
            Assert.Single(Directory.GetFiles(folder));
        }
        finally
        {
            string scratch = Path.Combine(AppContext.BaseDirectory, "scratch-my-shapes");
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    [Fact]
    public async Task OneOperationRunsAtATime()
    {
        ShapeLibrary library = ShapeLibrary.InMemory();
        Task<SavedShape?>? attempt = null;
        // The first change is the reload starting; a save asked for then is ignored, as on the Mac.
        library.Changed += () => attempt ??= library.SaveAsync("Box", Selected(Box));
        await library.ReloadAsync();
        Assert.Null(await attempt!);
        Assert.False(library.IsBusy);
        Assert.Empty(library.Shapes);
    }

    [Fact]
    public void SavingNeedsADrawingCanvasWithoutAnUnfinishedEdit()
    {
        WorkspaceModel off = TestWorkspace.Create();
        Assert.Equal(new ShapeSaveSource(null, "Open a drawing canvas first."), off.ShapeToSave());

        WorkspaceModel model = TestWorkspace.Drawing();
        TestWorkspace.AssertAccepted(model.Add(TestWorkspace.Primary, Box));
        DisplaySnapshot display = model.Display(TestWorkspace.Primary);
        TestWorkspace.AssertAccepted(model.BeginEdit(TestWorkspace.Primary, display.Version, EditKind.Gesture));
        Assert.Equal(ShapeLibraryError.UnfinishedEdit.Message, model.ShapeToSave().Problem);
        TestWorkspace.AssertAccepted(model.CancelEdit(TestWorkspace.Primary, display.Version));
        ShapeSaveSource source = model.ShapeToSave();
        Assert.Null(source.Problem);
        Assert.Equal(model.Document(TestWorkspace.Primary).Identity, source.Document!.Value.Identity);
    }

    [Fact]
    public void SavingRefusesASelectionOnSeveralDisplays()
    {
        WorkspaceModel model = TestWorkspace.Drawing();
        TestWorkspace.AssertAccepted(model.Add(TestWorkspace.Primary, Box));
        TestWorkspace.AssertAccepted(model.Add(TestWorkspace.Left, new Element(Tool.Ellipse, new Rect(10, 10, 50, 50), style: Style.Defaults(Tool.Ellipse))));
        model.SelectAll();
        Assert.Equal("Select drawings on one display before saving a reusable shape.", model.ShapeToSave().Problem);
    }

    [Fact]
    public void InsertingAddsSelectedCopiesAtTheCanvasCenterAsOneUndoStep()
    {
        WorkspaceModel model = TestWorkspace.Drawing();
        model.Choose(Tool.Pen);
        SavedShape shape = new("Box", Selected(Box));
        model.UseApps();
        model.InsertSavedShape(shape);

        WorkspaceSnapshot snapshot = model.Snapshot;
        Assert.Equal(WorkspaceMode.Draw, snapshot.Mode);
        Assert.Equal(Tool.Select, snapshot.Tool);
        Element copy = Assert.Single(model.Elements(TestWorkspace.Primary));
        Assert.NotEqual(Box.Id, copy.Id);
        Assert.Equal(TestWorkspace.PrimaryDisplay.Bounds.Center, copy.Frame.Center);
        Assert.Equal([copy.Id], model.Document(TestWorkspace.Primary).Selection);
        Assert.Equal("Inserted Box at the canvas center. Drag it to move it; Undo removes this copy.", snapshot.Status);
        Assert.Equal(TestWorkspace.Primary, snapshot.FocusRequest?.Display);
        model.Undo();
        Assert.Empty(model.Elements(TestWorkspace.Primary));
    }

    [Fact]
    public void InsertingNeedsShownToolsAndACanvas()
    {
        SavedShape shape = new("Box", Selected(Box));
        WorkspaceModel off = TestWorkspace.Create();
        ShapeLibraryError missing = Assert.Throws<ShapeLibraryError>(() => off.InsertSavedShape(shape));
        Assert.Equal("My shapes could not read a saved shape. Open a drawing canvas first.", missing.Message);

        WorkspaceModel model = TestWorkspace.Drawing();
        model.HideTools();
        CanvasError hidden = Assert.Throws<CanvasError>(() => model.InsertSavedShape(shape));
        Assert.Equal(CanvasError.InvalidDocument("Show the drawing tools before inserting a saved shape."), hidden);
        Assert.Empty(model.Elements(TestWorkspace.Primary));
    }

    [Fact]
    public void InsertingWaitsForAnUnfinishedEdit()
    {
        WorkspaceModel model = TestWorkspace.Drawing();
        DisplaySnapshot display = model.Display(TestWorkspace.Primary);
        TestWorkspace.AssertAccepted(model.BeginEdit(TestWorkspace.Primary, display.Version, EditKind.Gesture));
        Assert.Equal(ShapeLibraryError.UnfinishedEdit, Assert.Throws<ShapeLibraryError>(() => model.InsertSavedShape(new SavedShape("Box", Selected(Box)))));
    }

    [Fact]
    public void OnlyBuiltToolbarButtonsAreAvailable()
    {
        Assert.Equal([ShortcutAction.Properties, ShortcutAction.Board, ShortcutAction.Spotlight, ShortcutAction.CursorHalo, ShortcutAction.PointerLens,
                      ShortcutAction.SmartDraw, ShortcutAction.ShareRegion, ShortcutAction.ClearAll],
            ShortcutAction.AllCases.Where(WorkspaceModel.IsAvailable));
    }

    private static CanvasDocument Selected(Element element)
    {
        CanvasDocument document = CanvasDocument.Empty();
        document.Add(element);
        document.Select([element.Id]);
        return document;
    }
}
