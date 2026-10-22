using Annotator.App.Library;
using Annotator.App.Shell;
using Annotator.Platform.Windowing;
using Annotator.Workspace;
using Annotator.Workspace.Library;

namespace Annotator.App.Reference;

/// <summary>
/// Development only (UX reference capture): the Tools window's content on its own, as the Mac capture renders
/// <c>ToolLibraryPanel(model:section:dismiss:maximumHeight:width:)</c> at its ideal height on the window background, with an
/// in-memory My shapes library. Its controls change the model like the window's; nothing closes it.
/// </summary>
internal sealed class ToolsReferenceWindow : PopoverWindow
{
    private readonly ToolsPanel _panel;

    public ToolsReferenceWindow(WindowRegistry registry, WorkspaceModel model, ShapeLibrary library, ToolLibrarySection section,
                                double width, double maximumHeight)
        : base(registry, "Tools")
    {
        _panel = new ToolsPanel(model, library, thumbnails: null, new LibraryState { Section = section }, static () => { },
            maximumHeight, width);
        _panel.Root.Width = width;
        Surface = _panel.Root;
        model.Published += _panel.Apply;
        Closed += (_, _) =>
        {
            model.Published -= _panel.Apply;
            _panel.Detach();
        };
    }

    protected override void FocusFirst()
    {
    }
}
