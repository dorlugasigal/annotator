using Annotator.Workspace;
using Annotator.Workspace.FirstUse;
using Annotator.Workspace.Shortcuts;

namespace Annotator.App.Reference;

/// <summary>The tour and first-use states of reference/ux-reference/macos (spec 005, WIN-UI-04).</summary>
internal static partial class ReferenceStates
{
    /// <summary>
    /// UXReferenceCaptureTests' tour pages, each story at its end. Page 4's Mac states are the permissions' three states;
    /// on Windows nothing needs a permission (WIN-INP-08), so 4-setup and 4-setup-allowed show the shortcuts working after a
    /// normal launch, and 4-setup-not-allowed shows Show / hide taken by another app (WIN-INP-05).
    /// </summary>
    private static ReferenceScenario? Tour(string state) => state switch
    {
        "1-draw" => TourPage(IntroductionPage.Draw, ShortcutsWorking),
        "2-color" => TourPage(IntroductionPage.Color, ShortcutsWorking),
        "3-focus" => TourPage(IntroductionPage.Focus, ShortcutsWorking),
        "4-setup" or "4-setup-allowed" => TourPage(IntroductionPage.Setup, ShortcutsWorking),
        "4-setup-not-allowed" => TourPage(IntroductionPage.Setup, ShowHideTaken),
        _ => null,
    };

    /// <summary>The on-screen tip while drawing and the coffee reminder (UXReferenceCaptureTests.test06WindowsAndDialogs).</summary>
    private static ReferenceScenario? FirstUseDialogs(string state) => state switch
    {
        "canvas-hint" => new ReferenceScenario(ReferenceSurface.CanvasHint)
        {
            Setup = model =>
            {
                model.ShowToolbar();
                model.BeginDrawing();
            },
        },
        "coffee-reminder" => new ReferenceScenario(ReferenceSurface.CoffeeReminder) { Setup = static model => model.ShowToolbar() },
        _ => null,
    };

    private static ReferenceScenario TourPage(IntroductionPage page, Action<WorkspaceModel> shortcuts) => new(ReferenceSurface.Tour)
    {
        TourPage = page,
        Setup = model =>
        {
            model.ShowToolbar();
            shortcuts(model);
        },
    };

    /// <summary>What a normal launch registers: the four shortcuts work (GlobalShortcuts.Register).</summary>
    private static void ShortcutsWorking(WorkspaceModel model)
    {
        foreach (GlobalShortcutAction action in new[]
                 {
                     GlobalShortcutAction.ShowHide, GlobalShortcutAction.ResumeDrawing, GlobalShortcutAction.LiveZoom, GlobalShortcutAction.HideOnly,
                 })
        {
            model.SetGlobalShortcutState(new GlobalShortcutState(action, WindowsShortcuts.Label(action, model.Snapshot.Preferences),
                GlobalShortcutStatus.Registered, null));
        }
    }

    /// <summary>Another app holds Ctrl+2: Show / hide has no shortcut. The error it reports is dismissed, as after a look.</summary>
    private static void ShowHideTaken(WorkspaceModel model)
    {
        ShortcutsWorking(model);
        string label = WindowsShortcuts.Label(GlobalShortcutAction.ShowHide, model.Snapshot.Preferences);
        model.SetGlobalShortcutState(new GlobalShortcutState(GlobalShortcutAction.ShowHide, label, GlobalShortcutStatus.AlreadyUsed,
            $"{label} is used by another app."));
        model.DismissError();
    }
}
