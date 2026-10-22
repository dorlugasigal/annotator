namespace Annotator.Core.Scene;

/// <summary>
/// Builds a display list item by item, for drawings that are not elements (canvas adornments such as the marquee) or
/// that wrap an element's list (a transition drawn in an opacity layer). Reusable: <see cref="ToList"/> starts a new
/// list. Not thread-safe.
/// </summary>
public sealed class DisplayListBuilder
{
    private readonly DisplayListWriter writer = new();
    private readonly Stack<DisplayOp> open = new();

    /// <summary>Items added since the last <see cref="ToList"/>.</summary>
    public int Count => writer.Count;

    public DisplayListBuilder Add(in DisplayItem item)
    {
        switch (item.Op)
        {
            case DisplayOp.PushTransform or DisplayOp.PushLayer or DisplayOp.PushClip:
                open.Push(item.Op);
                break;
            case DisplayOp.PopTransform or DisplayOp.PopLayer or DisplayOp.PopClip:
                var expected = item.Op switch
                {
                    DisplayOp.PopTransform => DisplayOp.PushTransform,
                    DisplayOp.PopLayer => DisplayOp.PushLayer,
                    _ => DisplayOp.PushClip,
                };
                if (open.Count == 0 || open.Peek() != expected)
                {
                    throw new InvalidOperationException($"{item.Op} does not close the innermost open item.");
                }
                open.Pop();
                break;
        }
        writer.Add(item);
        return this;
    }

    /// <summary>Adds every item of <paramref name="list"/> and keeps its issues.</summary>
    public DisplayListBuilder AddRange(DisplayList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        foreach (ref readonly var item in list.Items)
        {
            Add(item);
        }
        writer.Issues |= list.Issues;
        return this;
    }

    /// <summary>The items as an immutable list for <paramref name="elementId"/> (empty for adornments), then starts again.</summary>
    /// <exception cref="InvalidOperationException">A transform, layer or clip is still open.</exception>
    public DisplayList ToList(Guid elementId = default)
    {
        if (open.Count != 0)
        {
            throw new InvalidOperationException($"{open.Count} transforms, layers or clips are still open.");
        }
        return writer.ToList(elementId);
    }

    /// <summary>Drops the items added since the last <see cref="ToList"/>.</summary>
    public void Clear()
    {
        open.Clear();
        writer.Reset();
    }
}
