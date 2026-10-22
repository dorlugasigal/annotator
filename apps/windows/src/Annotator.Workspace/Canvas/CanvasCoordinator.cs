using Annotator.Core;

namespace Annotator.Workspace.Canvas;

/// <summary>
/// Connects the canvases of all displays that live on one thread (the overlay thread), so a drag can show the objects it
/// moves on the display under the pointer before the drop (Mac previewDisplayDrag). Not thread-safe; use it on that
/// thread only. Canvases register when created with it and leave when disposed.
/// </summary>
public sealed class CanvasCoordinator
{
    private readonly Dictionary<DisplayId, CanvasController> _canvases = [];

    /// <summary>The canvases that take part, by display.</summary>
    public IReadOnlyDictionary<DisplayId, CanvasController> Canvases => _canvases;

    internal void Register(CanvasController canvas)
    {
        if (!_canvases.TryAdd(canvas.Display, canvas))
        {
            throw new InvalidOperationException($"Display {canvas.Display} already has a canvas.");
        }
    }

    internal void Unregister(CanvasController canvas)
    {
        if (_canvases.TryGetValue(canvas.Display, out CanvasController? registered) && registered == canvas)
        {
            _canvases.Remove(canvas.Display);
        }
    }

    internal CanvasController? Find(DisplayId display) => _canvases.GetValueOrDefault(display);

    /// <summary>Shows <paramref name="elements"/> on <paramref name="display"/>'s canvas as a drag preview; empty clears it.</summary>
    internal void ShowDragPreview(DisplayId display, ElementList elements)
    {
        if (Find(display) is { } canvas)
        {
            canvas.SetDragPreview(elements);
        }
    }

    /// <summary>Removes every drag preview except on <paramref name="keep"/> (Mac clearDisplayDragPreviews).</summary>
    internal void ClearDragPreviews(DisplayId? keep = null)
    {
        foreach (CanvasController canvas in _canvases.Values)
        {
            if (canvas.Display != keep)
            {
                canvas.SetDragPreview(ElementList.Empty);
            }
        }
    }
}
