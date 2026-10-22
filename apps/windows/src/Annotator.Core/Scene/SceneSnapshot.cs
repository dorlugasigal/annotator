namespace Annotator.Core.Scene;

/// <summary>
/// An immutable frame description: one display list per drawn element in stacking order (drawings, then pixel
/// effects, then covers), followed by the selection adornments. Unchanged elements keep the same list instances
/// from build to build, so a renderer can cache device resources per list. Safe to hand to another thread.
/// </summary>
public sealed class SceneSnapshot
{
    private readonly DisplayList[] elements;

    internal SceneSnapshot(DisplayList[] elements, DisplayList selection, SceneIssues issues)
    {
        this.elements = elements;
        Selection = selection;
        Issues = issues | selection.Issues;
    }

    public static SceneSnapshot Empty { get; } = new([], DisplayList.Empty, SceneIssues.None);

    /// <summary>
    /// A snapshot of lists from several sources, such as a live layer that combines the object being drawn, canvas
    /// adornments and covers drawn again. The lists are copied in order; empty lists are left out, as
    /// <see cref="SceneBuilder.Build"/> leaves them out.
    /// </summary>
    public static SceneSnapshot Create(ReadOnlySpan<DisplayList> elements, DisplayList? selection = null)
    {
        selection ??= DisplayList.Empty;
        var issues = SceneIssues.None;
        var count = 0;
        foreach (var list in elements)
        {
            ArgumentNullException.ThrowIfNull(list, nameof(elements));
            issues |= list.Issues;
            if (!list.IsEmpty)
            {
                count++;
            }
        }
        if (count == 0 && selection.IsEmpty && (issues | selection.Issues) == SceneIssues.None)
        {
            return Empty;
        }
        var lists = new DisplayList[count];
        count = 0;
        foreach (var list in elements)
        {
            if (!list.IsEmpty)
            {
                lists[count++] = list;
            }
        }
        return new SceneSnapshot(lists, selection, issues);
    }

    /// <summary>Element lists in drawing order. Elements that draw nothing have no list.</summary>
    public ReadOnlySpan<DisplayList> Elements => elements;

    /// <summary>Selection outlines and handles, drawn after every element.</summary>
    public DisplayList Selection { get; }

    /// <summary>Problems found while building this scene, including elements that ended up drawing nothing.</summary>
    public SceneIssues Issues { get; }

    internal bool SameLists(ReadOnlySpan<DisplayList> lists, DisplayList selection, SceneIssues issues)
    {
        if (!ReferenceEquals(Selection, selection) || lists.Length != elements.Length || Issues != (issues | selection.Issues))
        {
            return false;
        }
        for (var index = 0; index < lists.Length; index++)
        {
            if (!ReferenceEquals(lists[index], elements[index]))
            {
                return false;
            }
        }
        return true;
    }
}
