using System.Collections.Immutable;
using Annotator.Core;

namespace Annotator.Workspace.Toolbar;

/// <summary>
/// Mac StyleValueControl.Target: the display, selection, selection scope and tool that a typed value or a slider drag
/// edits. A value control keeps one while a draft or a drag lasts and discards the draft when the target changes, so
/// a value typed for one object or tool never reaches another.
/// </summary>
public sealed class StyleEditTarget
{
    private readonly DisplayId? _active;
    private readonly bool _workspaceWide;
    private readonly Tool _tool;
    private readonly ImmutableArray<(DisplayId Display, ImmutableHashSet<Guid> Selection)> _selections;

    private StyleEditTarget(DisplayId? active, bool workspaceWide, Tool tool,
                            ImmutableArray<(DisplayId Display, ImmutableHashSet<Guid> Selection)> selections)
    {
        _active = active;
        _workspaceWide = workspaceWide;
        _tool = tool;
        _selections = selections;
    }

    public static StyleEditTarget Of(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ImmutableArray<(DisplayId Display, ImmutableHashSet<Guid> Selection)>.Builder selections =
            ImmutableArray.CreateBuilder<(DisplayId Display, ImmutableHashSet<Guid> Selection)>();
        foreach (DisplaySnapshot display in snapshot.Displays)
        {
            ImmutableHashSet<Guid> selection = display.Document.Selection;
            if (!selection.IsEmpty)
            {
                selections.Add((display.Id, selection));
            }
        }
        return new StyleEditTarget(snapshot.ActiveDisplay, snapshot.SelectionIsWorkspaceWide, snapshot.EffectiveTool,
            selections.ToImmutable());
    }

    /// <summary>True when <paramref name="snapshot"/> edits the same display, selection and tool.</summary>
    public bool Matches(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.ActiveDisplay != _active || snapshot.SelectionIsWorkspaceWide != _workspaceWide ||
            snapshot.EffectiveTool != _tool)
        {
            return false;
        }
        int index = 0;
        foreach (DisplaySnapshot display in snapshot.Displays)
        {
            ImmutableHashSet<Guid> selection = display.Document.Selection;
            if (selection.IsEmpty)
            {
                continue;
            }
            if (index == _selections.Length || _selections[index].Display != display.Id ||
                (!ReferenceEquals(_selections[index].Selection, selection) && !_selections[index].Selection.SetEquals(selection)))
            {
                return false;
            }
            index++;
        }
        return index == _selections.Length;
    }
}
