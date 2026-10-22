using Annotator.App.Pickers;
using Annotator.App.Shell;
using Annotator.App.Toolbar;
using Annotator.Platform.Windowing;
using Annotator.Workspace;
using Annotator.Workspace.Toolbar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Annotator.App.Reference;

/// <summary>
/// Development only (UX reference capture): the full properties panel on its own, as the Mac capture renders
/// <c>ContextualInspector(model:).padding(16)</c> at a fixed width on the window background. Its buttons change the model
/// like the toolbar's; color and arrowhead choices do not open.
/// </summary>
internal sealed class PropertiesReferenceWindow : PopoverWindow, IPropertiesHost
{
    private const double Padding = 16;
    private readonly WorkspaceModel _model;
    private readonly List<Action<WorkspaceSnapshot>> _updates = [];
    private readonly SolidColorBrush _ink = new(PropertiesInk.Color());

    public PropertiesReferenceWindow(WindowRegistry registry, WorkspaceModel model, double width)
        : base(registry, "Properties")
    {
        _model = model;
        WorkspaceSnapshot snapshot = model.Snapshot;
        InspectorPlan plan = InspectorPlan.For(snapshot);
        var panel = new PropertiesPanel(this, plan, compact: false, width - Padding * 2, _updates);
        var root = new Grid { Width = width, Background = AppResources.Brush("AnnotatorWindowBackgroundBrush") };
        panel.Root.Margin = new Thickness(Padding);
        if (plan.Body == InspectorBody.Cover)
        {
            // The capture centers ContextualInspector in its frame; the effect controls are narrower than the frame.
            panel.Root.HorizontalAlignment = HorizontalAlignment.Center;
        }
        root.Children.Add(panel.Root);
        Surface = root;
        foreach (Action<WorkspaceSnapshot> update in _updates)
        {
            update(snapshot);
        }
        model.Published += Apply;
        Closed += (_, _) => model.Published -= Apply;
    }

    public WorkspaceModel Model => _model;

    public Brush Ink => _ink;

    public void Interacted()
    {
    }

    public void OpenColorPicker(ColorPickerRequest request, FrameworkElement anchor)
    {
    }

    public void OpenArrowHeads(bool start, FrameworkElement anchor, Action closed) => closed();

    public void BeginTyping(TextBox field) => field.Focus(FocusState.Pointer);

    public void EndTyping()
    {
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
