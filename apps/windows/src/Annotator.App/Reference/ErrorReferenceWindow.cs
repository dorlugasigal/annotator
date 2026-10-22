using Annotator.App.Shell;
using Annotator.Platform.Windowing;

namespace Annotator.App.Reference;

/// <summary>
/// Development only (UX reference capture): the error window's content on its own, 460 by 160 points on the window
/// background, as the Mac capture renders WorkspaceErrorController's content view with a message. OK does nothing here.
/// </summary>
internal sealed class ErrorReferenceWindow : PopoverWindow
{
    public ErrorReferenceWindow(WindowRegistry registry, string message)
        : base(registry, "Annotator")
    {
        var content = new ErrorContent(static () => { }) { Message = message };
        content.Root.Height = content.ContentHeight();
        Surface = content.Root;
    }

    protected override void FocusFirst()
    {
    }
}
