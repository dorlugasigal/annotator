using Annotator.Core;
using Annotator.Core.Geometry;

namespace Annotator.Workspace.Files;

/// <summary>Decodes an image the way the platform shows it; throws <see cref="RenderFailure"/> when it cannot (Mac RasterAsset.decode).</summary>
public delegate void ImageCheck(ReadOnlySpan<byte> encoded);

/// <summary>
/// Board files (<c>.annotator</c>, WIN-CORE-02, WIN-SYS-04) as the Mac's openBoard(from:) and saveBoard read and write them:
/// versioned <see cref="DrawingFile"/> JSON, at most 128 MiB to import, written atomically. Safe on any thread.
/// </summary>
public static class BoardFiles
{
    /// <summary>The Mac's safe import budget: a board file of at most 128 MiB.</summary>
    public const long MaximumBytes = 128L * 1_024 * 1_024;

    /// <summary>The name the Save dialog suggests (Mac nameFieldStringValue).</summary>
    public const string SuggestedName = "Explanation.annotator";

    /// <summary>The file name extension of boards, without the dot.</summary>
    public const string Extension = "annotator";

    /// <summary>
    /// Mac openBoard(from:) up to replacing anything: the file is at most 128 MiB, reads as a board of a supported format, and
    /// every pasted image in it decodes (<paramref name="checkImage"/>). Throws the reason otherwise: an IO error,
    /// <see cref="CanvasError"/>, <see cref="DecodingError"/> or <see cref="RenderFailure"/>.
    /// </summary>
    public static CanvasDocument Load(string path, ImageCheck checkImage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(checkImage);
        if (new FileInfo(path).Length > MaximumBytes)
        {
            throw CanvasError.InvalidDocument("The board exceeds the safe import budget.");
        }
        CanvasDocument document = DrawingFile.Read(File.ReadAllBytes(path));
        foreach (ref readonly Element element in document.Elements)
        {
            if (element.Tool != Tool.Image)
            {
                continue;
            }
            if (element.ImagePng is not { } png)
            {
                throw new RenderFailure(RenderFailureKind.ImageUnavailable);
            }
            checkImage(png.AsSpan());
        }
        return document;
    }

    /// <summary>
    /// Mac saveBoard's write: the document's objects in the oldest format that holds them, written atomically. Throws the
    /// reason when the board is too large (<see cref="CanvasError"/>) or the file cannot be written.
    /// </summary>
    public static void Save(string path, CanvasDocument document) => AtomicFile.Write(path, new DrawingFile(document).Data());
}

/// <summary>Image files for Insert image (Mac importScreenshot). Safe on any thread.</summary>
public static class ImageFiles
{
    /// <summary>The Mac's limit on an image file to insert: 128 MiB.</summary>
    public const long MaximumBytes = 128L * 1_024 * 1_024;

    /// <summary>
    /// The file's bytes when it is at most 128 MiB; otherwise throws <see cref="RenderFailure"/> with the Mac's raster budget
    /// message. IO errors are thrown as they are.
    /// </summary>
    public static byte[] Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (new FileInfo(path).Length > MaximumBytes)
        {
            throw new RenderFailure(RenderFailureKind.InvalidSize);
        }
        return File.ReadAllBytes(path);
    }
}
