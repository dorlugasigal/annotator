using Annotator.App.Shell;
using Annotator.Platform.Windowing;
using Annotator.Workspace;
using Microsoft.UI.Xaml;

namespace Annotator.App.Reference;

/// <summary>
/// The keycaps states of UXReferenceCaptureTests.test06WindowsAndDialogs with the default profile: dialogs/keycaps-* show
/// the keycaps of one global shortcut's default with 8 points of padding; Hide only shows the Windows default,
/// Ctrl+Alt+Shift+2 (D-5). The Toolbar &amp; Shortcuts states show the real Settings window (ReferenceStates.Settings).
/// </summary>
internal static class ShortcutReferenceStates
{
    public static ReferenceScenario? Keycaps(string state) => Action(state) is null ? null : new ReferenceScenario(ReferenceSurface.Keycaps);

    public static GlobalShortcutAction? Action(string state) => state switch
    {
        "keycaps-show-hide" => GlobalShortcutAction.ShowHide,
        "keycaps-resume-drawing" => GlobalShortcutAction.ResumeDrawing,
        "keycaps-hide-only" => GlobalShortcutAction.HideOnly,
        "keycaps-live-zoom" => GlobalShortcutAction.LiveZoom,
        _ => null,
    };
}

/// <summary>
/// Development only (UX reference capture): the keycaps of one global shortcut on their own, at a fixed size, without
/// activation. Nothing closes it.
/// </summary>
internal sealed class ShortcutsReferenceWindow : PopoverWindow
{
    public ShortcutsReferenceWindow(WindowRegistry registry, string title, FrameworkElement surface)
        : base(registry, title)
    {
        Surface = surface;
    }

    protected override void FocusFirst()
    {
    }
}