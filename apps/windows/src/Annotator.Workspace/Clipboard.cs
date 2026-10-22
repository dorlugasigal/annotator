using Annotator.Core;

namespace Annotator.Workspace;

/// <summary>
/// The system clipboard as the workspace uses it (WIN-CORE-07). The platform owns the data formats: the registered
/// <see cref="AnnotationClipboard.TypeName"/> format with the UTF-8 board payload, and PNG and bitmap images from
/// other apps. Methods run on the workspace thread and may throw; the workspace reports the errors.
/// </summary>
public interface IWorkspaceClipboard
{
    /// <summary>Increases whenever the clipboard's contents change (on Windows, the clipboard sequence number).</summary>
    ulong ChangeCount { get; }

    /// <summary>The copied objects' payload, or null when the clipboard holds none.</summary>
    byte[]? ReadAnnotations();

    /// <summary>Replaces the clipboard's contents with copied objects; false when the clipboard refused them.</summary>
    bool WriteAnnotations(byte[] payload);

    /// <summary>
    /// The clipboard's image, decoded and encoded again as PNG within the Mac's limits, or null when it holds no
    /// image. Throws with a message for the person when an image is there but cannot be used.
    /// </summary>
    ClipboardImage? ReadImage();
}

/// <summary>A clipboard image as PNG data with its size in pixels.</summary>
public sealed record ClipboardImage(ValueList<byte> Png, int Width, int Height);

/// <summary>A clipboard in memory, for tests and for runs without the system clipboard.</summary>
public sealed class MemoryClipboard : IWorkspaceClipboard
{
    private byte[]? _annotations;
    private ClipboardImage? _image;

    public ulong ChangeCount { get; private set; }

    public byte[]? Annotations => _annotations;

    public byte[]? ReadAnnotations() => _annotations;

    public bool WriteAnnotations(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        _annotations = payload;
        _image = null;
        ChangeCount++;
        return true;
    }

    public ClipboardImage? ReadImage() => _image;

    /// <summary>Puts an image on the clipboard, as another app would.</summary>
    public void SetImage(ClipboardImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        _image = image;
        _annotations = null;
        ChangeCount++;
    }

    /// <summary>Empties the clipboard, as copying text in another app would.</summary>
    public void Clear()
    {
        _annotations = null;
        _image = null;
        ChangeCount++;
    }
}

/// <summary>Where pasted objects land (Mac pasteOrigin, spec 005).</summary>
public static class PastePlacement
{
    /// <summary>
    /// Places objects whose bounds are <paramref name="bounds"/> on a canvas of <paramref name="canvas"/> size: at the
    /// pointer, centered, when the pointer is on the canvas, away from the copied objects (8 points) and not where
    /// the previous paste of the same clipboard put them; otherwise 18 points down and right of the previous paste
    /// (or of the copied objects). The result stays on the canvas.
    /// </summary>
    /// <returns>The offset to apply, the new top-left corner, and the pointer when it is on the canvas.</returns>
    public static (Point Offset, Point Target, Point? Pointer) Origin(Rect bounds, Rect canvas, Point? pointer, Point? previousOrigin,
                                                                    Point? previousPointer)
    {
        Point? onCanvas = pointer is { } local && canvas.Contains(local) ? local : null;
        Point target;
        if (onCanvas is { } at && !bounds.Contains(at, 8) && previousPointer != at)
        {
            target = at - new Point(bounds.Width / 2, bounds.Height / 2);
        }
        else
        {
            target = (previousOrigin ?? bounds.Origin) + new Point(18, 18);
        }
        target = new Point(Math.Max(0, Math.Min(target.X, canvas.Width - bounds.Width)),
                           Math.Max(0, Math.Min(target.Y, canvas.Height - bounds.Height)));
        return (target - bounds.Origin, target, onCanvas);
    }

    /// <summary>
    /// Mac insertImage placement: at most 600 by 450 points, keeping the aspect ratio, with the top-left corner at the
    /// pointer, kept on the canvas.
    /// </summary>
    public static Rect Image(int width, int height, Rect canvas, Point pointer)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        double scale = Math.Min(1, Math.Min(600.0 / width, 450.0 / height));
        double x = Math.Max(0, Math.Min(pointer.X, canvas.Width - width * scale));
        double y = Math.Max(0, Math.Min(pointer.Y, canvas.Height - height * scale));
        return new Rect(x, y, width * scale, height * scale);
    }
}
