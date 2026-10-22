using Annotator.App.Shell;
using Annotator.Platform.Windowing;
using Annotator.Workspace.Capture;

namespace Annotator.App.Reference;

/// <summary>
/// Development only (UX reference capture): the screen access explanation's content on its own, 460 points wide on the window
/// background, before Windows asks or while it asks (WIN-CAP-04). Windows only, so no macOS capture exists. Its buttons do
/// nothing here.
/// </summary>
internal sealed class ScreenAccessReferenceWindow : PopoverWindow
{
    public ScreenAccessReferenceWindow(WindowRegistry registry, bool asking)
        : base(registry, ScreenAccessText.WindowTitle)
    {
        var content = new ScreenAccessContent(static () => { }, static () => { }) { Asking = asking };
        content.Root.Height = content.ContentHeight();
        Surface = content.Root;
    }

    protected override void FocusFirst()
    {
    }
}

/// <summary>Development only: screen access that Windows reports as <paramref name="access"/> and never asks.</summary>
internal sealed class FixedScreenAccess(ScreenAccess access) : IScreenAccess
{
    public ScreenAccess Check() => access;

    public void Ask(bool explain, Action<ScreenAccessAnswer> answered)
    {
    }
}
