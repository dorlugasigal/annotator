using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Workspace.Files;

namespace Annotator.Workspace.Tests.Files;

/// <summary>A folder under the temporary path for one test, deleted with everything in it afterwards.</summary>
internal sealed class TemporaryFolder : IDisposable
{
    public TemporaryFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "annotator-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        foreach (string file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
        {
            System.IO.File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(Path, recursive: true);
    }
}

/// <summary>
/// Board and image files (spec 005 "atomic saves", WIN-CORE-02, WIN-CORE-03, WIN-SYS-04): the Mac's openBoard(from:) checks,
/// the 128 MiB import budget, the oldest format that holds a board, and saves that never leave a half-written file.
/// </summary>
public sealed class BoardFilesTests
{
    internal static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13];

    [Fact]
    public void AtomicWritesReplaceTheFileAndLeaveNothingElse()
    {
        using var folder = new TemporaryFolder();
        string path = folder.File("Explanation.annotator");
        AtomicFile.Write(path, "first"u8);
        Assert.Equal("first"u8.ToArray(), File.ReadAllBytes(path));
        AtomicFile.Write(path, "second, longer"u8);
        Assert.Equal("second, longer"u8.ToArray(), File.ReadAllBytes(path));
        Assert.Equal([path], Directory.GetFiles(folder.Path));
    }

    [Fact]
    public void AFailedSaveKeepsTheEarlierFileAndLeavesNoTemporaryFile()
    {
        using var folder = new TemporaryFolder();
        string path = folder.File("Explanation.annotator");
        AtomicFile.Write(path, "earlier"u8);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        Assert.ThrowsAny<UnauthorizedAccessException>(() => AtomicFile.Write(path, "later"u8));
        Assert.Equal("earlier"u8.ToArray(), File.ReadAllBytes(path));
        Assert.Equal([path], Directory.GetFiles(folder.Path));
        Assert.ThrowsAny<IOException>(() => AtomicFile.Write(folder.File(Path.Combine("missing", "board.annotator")), "x"u8));
        Assert.Equal([path], Directory.GetFiles(folder.Path));
    }

    [Fact]
    public void BoardsSaveInTheOldestFormatAndReadBackEqual()
    {
        // WIN-CORE-02: a board written here decodes to an equal model; schema 1 without labels, 2 with labels, 3 with paint.
        using var folder = new TemporaryFolder();
        var box = new Element(Tool.Rectangle, new Rect(10, 20, 120, 80), style: Style.Defaults(Tool.Rectangle));
        var image = new Element(Tool.Image, new Rect(200, 40, 40, 30)) { ImagePng = ValueList.Create(Png) };
        var labelled = new Element(Tool.Ellipse, new Rect(300, 40, 90, 60)) { Label = new ShapeLabel("Orders") };
        string path = folder.File("board.annotator");

        BoardFiles.Save(path, new CanvasDocument(ElementList.Create([box, image])));
        Assert.Contains("\"schemaVersion\":1", File.ReadAllText(path), StringComparison.Ordinal);
        var checkedImages = new List<byte[]>();
        CanvasDocument read = BoardFiles.Load(path, encoded => checkedImages.Add(encoded.ToArray()));
        Assert.Equal([box, image], read.Elements.ToArray());
        Assert.Equal([Png], checkedImages);
        Assert.False(read.CanUndo);

        BoardFiles.Save(path, new CanvasDocument(ElementList.Create([labelled])));
        Assert.Contains("\"schemaVersion\":2", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal([labelled], BoardFiles.Load(path, static _ => { }).Elements.ToArray());
    }

    [Fact]
    public void BrokenUnsupportedAndOversizedBoardsAreRefusedWithTheMacMessages()
    {
        using var folder = new TemporaryFolder();
        string broken = folder.File("broken.annotator");
        File.WriteAllText(broken, "{\"schema\":1,\"elements\":[");
        var decoding = Assert.Throws<DecodingError>(() => BoardFiles.Load(broken, static _ => { }));
        Assert.Equal(DecodingError.ReadCorruptMessage, decoding.Message);

        string future = folder.File("future.annotator");
        File.WriteAllText(future, "{\"elements\":[],\"schemaVersion\":9}");
        Assert.Equal("Drawing format 9 is not supported by this version.",
            Assert.Throws<CanvasError>(() => BoardFiles.Load(future, static _ => { })).Message);

        string large = folder.File("large.annotator");
        using (FileStream stream = File.Create(large))
        {
            stream.SetLength(BoardFiles.MaximumBytes + 1);
        }
        Assert.Equal("The drawing could not be opened: The board exceeds the safe import budget.",
            Assert.Throws<CanvasError>(() => BoardFiles.Load(large, static _ => { })).Message);

        Assert.Throws<FileNotFoundException>(() => BoardFiles.Load(folder.File("missing.annotator"), static _ => { }));
    }

    [Fact]
    public void AnImageThatDoesNotDecodeRefusesTheBoard()
    {
        // Mac openBoard(from:) decodes every pasted image before anything changes (RasterAsset.decode).
        using var folder = new TemporaryFolder();
        string path = folder.File("images.annotator");
        BoardFiles.Save(path, new CanvasDocument(ElementList.Create([new Element(Tool.Image, new Rect(0, 0, 4, 3)) { ImagePng = ValueList.Create(Png) }])));
        var failure = Assert.Throws<RenderFailure>(() => BoardFiles.Load(path, static _ => throw new RenderFailure(RenderFailureKind.InvalidSize)));
        Assert.Equal("Export dimensions exceed the supported raster budget.", failure.Message);
    }

    [Fact]
    public void ImageFilesOverTheBudgetUseTheRasterMessage()
    {
        // Mac importScreenshot: a file over 128 MiB throws RenderFailure.invalidSize before it is read.
        using var folder = new TemporaryFolder();
        string small = folder.File("small.png");
        File.WriteAllBytes(small, Png);
        Assert.Equal(Png, ImageFiles.Read(small));
        string large = folder.File("large.png");
        using (FileStream stream = File.Create(large))
        {
            stream.SetLength(ImageFiles.MaximumBytes + 1);
        }
        Assert.Equal(RenderFailureKind.InvalidSize, Assert.Throws<RenderFailure>(() => ImageFiles.Read(large)).Kind);
    }
}
