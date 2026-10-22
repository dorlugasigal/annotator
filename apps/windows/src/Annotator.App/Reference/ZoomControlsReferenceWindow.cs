using Annotator.App.Shell;
using Annotator.App.Toolbar;
using Annotator.Platform.Windowing;
using Annotator.Workspace;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Annotator.App.Reference;

/// <summary>
/// Development only (UX reference capture): the live zoom level on its own, as the Mac capture renders
/// <c>LiveZoomControls(model:compact:).padding(12)</c> on the window background. Its buttons change the zoom like the
/// toolbar's.
/// </summary>
internal sealed class ZoomControlsReferenceWindow : PopoverWindow
{
    private const double Padding = 12;
    private readonly List<Action<WorkspaceSnapshot>> _updates = [];

    public ZoomControlsReferenceWindow(WindowRegistry registry, WorkspaceModel model, bool compact)
        : base(registry, "Live zoom level")
    {
        FrameworkElement level = ZoomControls.Level(model, compact, _updates, static () => { });
        level.Margin = new Thickness(Padding);
        var root = new Grid { Background = AppResources.Brush("AnnotatorWindowBackgroundBrush") };
        root.Children.Add(level);
        Surface = root;
        Apply(model.Snapshot);
        model.Published += Apply;
        Closed += (_, _) => model.Published -= Apply;
    }

    protected override void FocusFirst()
    {
    }

    private void Apply(WorkspaceSnapshot snapshot)
    {
        foreach (Action<WorkspaceSnapshot> update in _updates)
        {
            update(snapshot);
        }
    }
}
