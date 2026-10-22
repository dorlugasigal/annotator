using Annotator.Core;
using Annotator.Workspace;
using Microsoft.UI.Dispatching;

namespace Annotator.App.Overlay;

/// <summary>
/// The overlay thread's way to the workspace: every call is queued to the app thread in call order and never
/// blocks the overlay thread. Rejections that carry a message are already reported by the workspace.
/// </summary>
internal sealed class WorkspaceChannel(DispatcherQueue appQueue, WorkspaceModel model) : IWorkspaceChannel
{
    public void BeginEdit(DisplayId display, DocumentVersion baseVersion, EditKind kind) =>
        Post(() => model.BeginEdit(display, baseVersion, kind));

    public void UpdateEdit(DisplayId display, DocumentVersion baseVersion, CanvasDocument document) =>
        Post(() => model.UpdateEdit(display, baseVersion, document));

    public void CancelEdit(DisplayId display, DocumentVersion baseVersion) =>
        Post(() => model.CancelEdit(display, baseVersion));

    public void Submit(EditSubmission submission) => Post(() => model.Submit(submission));

    public void Send(CanvasCommand command) => Post(() => model.Perform(command));

    private void Post(Action work)
    {
        // A refused enqueue means the app thread is shutting down; the workspace no longer exists to receive it.
        appQueue.TryEnqueue(() => work());
    }
}
