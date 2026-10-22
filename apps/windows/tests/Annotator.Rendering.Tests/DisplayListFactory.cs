using System.Runtime.CompilerServices;
using Annotator.Core.Scene;

namespace Annotator.Rendering.Tests;

/// <summary>
/// Builds display lists and snapshots from hand-written items through Annotator.Core's internal constructors, so
/// every display operation can be tested on its own. Test-only.
/// </summary>
internal static class DisplayListFactory
{
    public static DisplayList Create(Guid elementId, DisplayItem[] items) => NewList(elementId, items, SceneIssues.None);

    public static SceneSnapshot Scene(DisplayList[] lists, DisplayList selection) => NewScene(lists, selection, SceneIssues.None);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern DisplayList NewList(Guid elementId, DisplayItem[] items, SceneIssues issues);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern SceneSnapshot NewScene(DisplayList[] elements, DisplayList selection, SceneIssues issues);
}
