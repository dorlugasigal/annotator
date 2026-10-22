using Annotator.Core;

namespace Annotator.Workspace;

/// <summary>Moving objects from one display's document to another's (spec 002, ADR 0006).</summary>
public static class DisplayTransfer
{
    /// <summary>
    /// The translation that keeps <paramref name="desktopAnchor"/> (for example the drop point) at the same
    /// screen position when objects move from <paramref name="from"/> to <paramref name="to"/>. Sizes stay in
    /// points (WIN-OVR-05); between displays of one scale every point keeps its screen position.
    /// </summary>
    public static Point Offset(DisplayDescriptor from, DisplayDescriptor to, Point desktopAnchor)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        return to.ToLocal(desktopAnchor) - from.ToLocal(desktopAnchor);
    }

    /// <summary>
    /// Mac movedElements: the unlocked objects of <paramref name="ids"/> translated by <paramref name="offset"/>.
    /// Groups and attachments between moved objects stay; attachments to objects left behind are removed.
    /// </summary>
    public static ElementList MovedElements(in CanvasDocument source, IReadOnlySet<Guid> ids, Point offset)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var moved = new List<Element>();
        foreach (ref readonly Element original in source.Elements)
        {
            if (!ids.Contains(original.Id) || original.Locked)
            {
                continue;
            }
            Element element = original;
            element.Translate(offset);
            if (element.StartAnchor is { } start && !ids.Contains(start.ElementId))
            {
                element.StartAnchor = null;
            }
            if (element.EndAnchor is { } end && !ids.Contains(end.ElementId))
            {
                element.EndAnchor = null;
            }
            moved.Add(element);
        }
        return ElementList.Create(moved.ToArray());
    }
}
