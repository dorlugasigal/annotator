using Annotator.Core;

namespace Annotator.Workspace;

public sealed partial class WorkspaceModel
{
    private const string NoImageMessage =
        "Copy an image or image file, then choose Paste image. A web link alone does not contain image pixels.";

    /// <summary>
    /// Copies the selected objects (Mac copySelection), on every display for a workspace-wide selection. Objects of
    /// other displays move into the editing target's points, keeping their places relative to its top-left corner.
    /// Returns false when there is nothing to copy, so the key can go elsewhere. A cut that includes a locked object
    /// changes nothing.
    /// </summary>
    public bool CopySelection(bool cut = false)
    {
        using ChangeScope scope = Change();
        if (_clipboard is null || _mode != WorkspaceMode.Draw || _hidden || _active is not { } active)
        {
            return false;
        }
        FinishStyleEditCore();
        CommitTextIfNeeded(active);
        var copied = new List<Element>();
        foreach (Canvas session in SelectionSessions())
        {
            Point shift = active.Display.ToLocal(new Point(session.Display.DesktopFrame.X, session.Display.DesktopFrame.Y));
            var selection = session.Document.Selection;
            foreach (ref readonly Element original in session.Document.Elements)
            {
                if (!selection.Contains(original.Id))
                {
                    continue;
                }
                Element element = original;
                if (session != active)
                {
                    element.Translate(shift);
                }
                copied.Add(element);
            }
        }
        if (copied.Count == 0)
        {
            return false;
        }
        if (cut && copied.Any(element => element.Locked))
        {
            _status = "Some selected objects are locked. Unlock them before cutting, or use Copy.";
            return true;
        }
        try
        {
            byte[] data = new DrawingFile(new CanvasDocument(ElementList.Create([.. copied]))).Data();
            if (!_clipboard.WriteAnnotations(data))
            {
                throw CanvasError.InvalidDocument("The objects could not be copied. Try Copy again.");
            }
            _pasteCascade = null;
            if (cut)
            {
                DeleteSelected();
            }
            string noun = copied.Count == 1 ? "1 object" : $"{copied.Count} objects";
            _status = cut ? $"Cut {noun}. Press Ctrl+V to paste." : $"Copied {noun}. Press Ctrl+V to paste.";
        }
        catch (Exception error) when (IsReportable(error))
        {
            Report(error);
        }
        return true;
    }

    /// <summary>
    /// Pastes copied objects onto the editing target as one Undo step (Mac paste): new identities, unlocked, selected,
    /// placed by <see cref="PastePlacement.Origin"/>. Without copied objects, pastes the clipboard's image.
    /// </summary>
    public void Paste()
    {
        using ChangeScope scope = Change();
        if (_clipboard is null)
        {
            return;
        }
        try
        {
            if (_clipboard.ReadAnnotations() is not { } data)
            {
                PasteImage();
                return;
            }
            ElementList copied = DrawingFile.Read(data).Elements;
            if (copied.IsEmpty)
            {
                return;
            }
            Canvas active = PrepareCanvasForInsertion();
            CommitTextIfNeeded(active);
            CancelGesture(active);
            var corners = new Point[copied.Count * 2];
            for (int index = 0; index < copied.Count; index++)
            {
                corners[index * 2] = copied[index].Frame.Origin;
                corners[index * 2 + 1] = new Point(copied[index].Frame.MaxX, copied[index].Frame.MaxY);
            }
            Rect bounds = Rect.Enclosing(corners);
            PasteCascade? previous = _pasteCascade is { } cascade && cascade.ChangeCount == _clipboard.ChangeCount
                && cascade.Document == active.Document.Identity ? cascade : null;
            (Point offset, Point target, Point? pointer) = PastePlacement.Origin(bounds, active.Display.Bounds, LocalPointer(active),
                previous?.Origin, previous?.Pointer);
            Mutate(active, (ref CanvasDocument document) => document.InsertCopies(copied, offset));
            _pasteCascade = new PasteCascade(_clipboard.ChangeCount, active.Document.Identity, target, pointer);
            _tool = Tool.Select;
            Synchronize();
            _status = copied.Count == 1 ? "Pasted 1 object." : $"Pasted {copied.Count} objects.";
        }
        catch (Exception error) when (IsReportable(error))
        {
            Report(error);
        }
    }

    /// <summary>Pastes the clipboard's image as an editable image object (Mac pasteImage).</summary>
    public void PasteImage()
    {
        using ChangeScope scope = Change();
        if (_clipboard is null)
        {
            return;
        }
        try
        {
            InsertImage(_clipboard.ReadImage() ?? throw CanvasError.InvalidDocument(NoImageMessage));
        }
        catch (Exception error) when (IsReportable(error))
        {
            Report(error);
        }
    }

    /// <summary>
    /// Mac insertImage: adds a PNG image under the pointer (at most 600 by 450 points), selects it and chooses Select,
    /// as one Undo step. Throws when the image cannot be added; <see cref="PasteImage"/> reports that.
    /// </summary>
    public void InsertImage(ClipboardImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        using ChangeScope scope = Change();
        Canvas active = PrepareCanvasForInsertion();
        Rect frame = PastePlacement.Image(image.Width, image.Height, active.Display.Bounds, LocalPointer(active) ?? Point.Zero);
        var element = new Element(Tool.Image, frame, style: StyleFor(Tool.Image)) { ImagePng = image.Png };
        Mutate(active, (ref CanvasDocument document) =>
        {
            document.Add(element);
            document.Select([element.Id]);
        });
        _tool = Tool.Select;
        Synchronize();
        _status = "Image pasted as an editable layer. Resize, rotate, group or delete it like other objects.";
        RecordUsage(Metrics.UsageMetric.ImageInsertion);
    }

    /// <summary>Shows hidden tools and opens drawing when needed, then returns the editing target (Mac paste and insertImage).</summary>
    private Canvas PrepareCanvasForInsertion()
    {
        if (_active is { } current)
        {
            UseLocalSelection(current);
        }
        if (_hidden)
        {
            // Mac paste while hidden: revealWorkspaceSurfaces() and showToolbar() at once, without motion.
            RevealImmediately();
        }
        if (_active is null || _mode != WorkspaceMode.Draw)
        {
            BeginDrawing(recordsUsage: false);
        }
        return _active ?? throw CanvasError.InvalidDocument("No canvas is available.");
    }

    /// <summary>The pointer in a display's points, or null when the pointer position is unknown.</summary>
    private Point? LocalPointer(Canvas canvas) => _pointer() is { } desktop ? canvas.Display.ToLocal(desktop) : null;

    /// <summary>The previous paste of one clipboard content on one document (Mac PasteCascade).</summary>
    private sealed record PasteCascade(ulong ChangeCount, Guid Document, Point Origin, Point? Pointer);
}
