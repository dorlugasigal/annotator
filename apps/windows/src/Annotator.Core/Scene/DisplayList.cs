using System.Text;

namespace Annotator.Core.Scene;

/// <summary>Problems found while building display lists. The Mac logs these; Windows surfaces them to the caller.</summary>
[Flags]
public enum SceneIssues : byte
{
    None = 0,

    /// <summary>A gradient failed validation, so the paint it would have filled was not drawn (as on the Mac).</summary>
    InvalidGradient = 1,

    /// <summary>An image element had no PNG data; its replacement outline was drawn.</summary>
    MissingImageData = 2,
}

/// <summary>
/// An immutable list of display operations in drawing order, built once per element and reused until the element
/// or the drawing options change. Lists and everything they reference are safe to read from another thread.
/// </summary>
public sealed class DisplayList
{
    private readonly DisplayItem[] items;

    internal DisplayList(Guid elementId, DisplayItem[] items, SceneIssues issues)
    {
        ElementId = elementId;
        this.items = items;
        Issues = issues;
    }

    public static DisplayList Empty { get; } = new(Guid.Empty, [], SceneIssues.None);

    /// <summary>The element drawn by this list; empty for the selection list.</summary>
    public Guid ElementId { get; }

    public ReadOnlySpan<DisplayItem> Items => items;

    public int Count => items.Length;

    public bool IsEmpty => items.Length == 0;

    public SceneIssues Issues { get; }

    /// <summary>Exact equality of every item, for tests and diagnostics.</summary>
    public bool ContentEquals(DisplayList? other)
    {
        if (other is null || ElementId != other.ElementId || Issues != other.Issues || items.Length != other.items.Length)
        {
            return false;
        }
        for (var index = 0; index < items.Length; index++)
        {
            if (!items[index].ContentEquals(other.items[index]))
            {
                return false;
            }
        }
        return true;
    }

    public override string ToString()
    {
        var text = new StringBuilder();
        var depth = 0;
        foreach (ref readonly var item in items.AsSpan())
        {
            if (item.Op is DisplayOp.PopTransform or DisplayOp.PopLayer or DisplayOp.PopClip)
            {
                depth--;
            }
            text.Append(' ', depth * 2).AppendLine(item.ToString());
            if (item.Op is DisplayOp.PushTransform or DisplayOp.PushLayer or DisplayOp.PushClip)
            {
                depth++;
            }
        }
        return text.ToString();
    }
}

/// <summary>A reusable buffer that collects items and copies them into an exactly sized list.</summary>
internal sealed class DisplayListWriter
{
    private DisplayItem[] buffer = new DisplayItem[32];
    private int count;

    public int Count => count;

    public SceneIssues Issues { get; set; }

    public void Add(in DisplayItem item)
    {
        if (count == buffer.Length)
        {
            Array.Resize(ref buffer, buffer.Length * 2);
        }
        buffer[count++] = item;
    }

    /// <summary>Whether any item after <paramref name="start"/> draws something (not only pushes and pops).</summary>
    public bool DrawsSince(int start)
    {
        for (var index = start; index < count; index++)
        {
            if (buffer[index].Op is DisplayOp.FillPath or DisplayOp.StrokePath or DisplayOp.FillRect
                or DisplayOp.StrokeSegments or DisplayOp.DrawText or DisplayOp.DrawImage)
            {
                return true;
            }
        }
        return false;
    }

    public void Truncate(int newCount) => count = newCount;

    public DisplayList ToList(Guid elementId)
    {
        var list = count == 0 && Issues == SceneIssues.None
            ? (elementId == Guid.Empty ? DisplayList.Empty : new DisplayList(elementId, [], SceneIssues.None))
            : new DisplayList(elementId, buffer.AsSpan(0, count).ToArray(), Issues);
        Reset();
        return list;
    }

    public void Reset()
    {
        Array.Clear(buffer, 0, count);
        count = 0;
        Issues = SceneIssues.None;
    }
}
