using Annotator.Core;

namespace Annotator.Workspace.Tests;

/// <summary>Builds workspaces like the Mac tests: display 101 at the origin with scale 2, display 202 to its left with scale 1.</summary>
internal static class TestWorkspace
{
    public static readonly DisplayId Primary = new("101");
    public static readonly DisplayId Left = new("202");

    public static DisplayDescriptor PrimaryDisplay { get; } =
        new(Primary, new Rect(0, 0, 1280, 960), new Rect(0, 48, 1280, 912), 2);

    public static DisplayDescriptor LeftDisplay { get; } =
        new(Left, new Rect(-800, 120, 800, 600), new Rect(-800, 120, 800, 560), 1);

    public static WorkspaceModel Create(Func<IReadOnlyList<DisplayDescriptor>>? displays = null, Func<Point?>? pointer = null,
                                        IWorkspacePreferencesStore? store = null)
    {
        DisplayDescriptor[] defaults = [PrimaryDisplay, LeftDisplay];
        return new WorkspaceModel(new WorkspaceOptions
        {
            Displays = displays ?? (() => defaults),
            Pointer = pointer ?? (() => new Point(10, 10)),
            PreferencesStore = store ?? new MemoryPreferencesStore(),
        });
    }

    /// <summary>A workspace in Draw on both displays (Mac MultiDisplayEditingTests.workspace()).</summary>
    public static WorkspaceModel Drawing()
    {
        WorkspaceModel model = Create();
        model.BeginDrawing();
        return model;
    }

    public static DisplaySnapshot Display(this WorkspaceModel model, DisplayId id) =>
        model.Snapshot.Display(id) ?? throw new InvalidOperationException($"Display {id} has no canvas.");

    public static CanvasDocument Document(this WorkspaceModel model, DisplayId id) => model.Display(id).Document;

    public static Element[] Elements(this WorkspaceModel model, DisplayId id) => [.. model.Document(id).Elements];

    /// <summary>Commits a canvas edit the way a canvas does: change a snapshot copy, then submit it.</summary>
    public static EditResult Edit(this WorkspaceModel model, DisplayId id, Action<CanvasDocument[]> change, bool localSelection = false)
    {
        DisplaySnapshot display = model.Display(id);
        CanvasDocument[] document = [display.Document];
        change(document);
        return model.Submit(new EditSubmission(new DisplayEdit(id, display.Version, document[0])) { UsesLocalSelection = localSelection });
    }

    /// <summary>Adds objects through a canvas edit and returns the result.</summary>
    public static EditResult Add(this WorkspaceModel model, DisplayId id, params Element[] elements) =>
        model.Edit(id, document =>
        {
            foreach (Element element in elements)
            {
                document[0].Add(element);
            }
        });

    public static void AssertAccepted(EditResult result) =>
        Assert.True(result is EditResult.Accepted, $"Expected an accepted edit, got {result}.");

    public static EditResult.Rejected AssertRejected(EditResult result, EditRejection reason)
    {
        var rejected = Assert.IsType<EditResult.Rejected>(result);
        Assert.Equal(reason, rejected.Reason);
        return rejected;
    }
}
