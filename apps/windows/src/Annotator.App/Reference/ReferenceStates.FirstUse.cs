using Annotator.Core.Tutorial;
using Annotator.Workspace;
using Annotator.Workspace.FirstUse;
using Annotator.Workspace.Shortcuts;

namespace Annotator.App.Reference;

/// <summary>The tutorial and first-use states of reference/ux-reference/macos (spec 005, WIN-UI-04).</summary>
internal static partial class ReferenceStates
{
    /// <summary>
    /// UXReferenceCaptureTests' tutorial cards with the default shortcuts working: welcome, coach-&lt;step&gt; for every step
    /// with nothing done, coach-arrow-curved (the arrow's first goal done), coach-shape-done (the step's check) and finish.
    /// </summary>
    private static ReferenceScenario? Tutorial(string state) =>
        state is "welcome" or "finish" or "coach-arrow-curved" or "coach-shape-done" ||
        (state.StartsWith("coach-", StringComparison.Ordinal) &&
         TutorialSteps.All.Any(step => "coach-" + step.RawValue == state))
            ? TutorialCard(state, ShortcutsWorking)
            : null;

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

    /// <summary>
    /// The fixed notes of the Mac's What's changed capture (ReleaseNotesCatalog.Entry.uxReference in
    /// WhatsChangedTests.swift), so the reference doesn't change with every release.
    /// </summary>
    internal static Annotator.Workspace.Settings.ReleaseNotesEntry WhatsChangedNotes { get; } = new(
        "0.2.0", 200, "2026-10-24",
        [
            ("Added", ["After an update, What's changed shows the new version's notes once."]),
            ("Changed", ["Annotator checks for updates once a day. You can turn this off in Settings."]),
            ("Fixed", ["The update window waits until you finish drawing or sharing a region."]),
        ],
        ["Effects do not follow moving windows and are not secure redaction."]);

    private static ReferenceScenario TutorialCard(string state, Action<WorkspaceModel> shortcuts) => new(ReferenceSurface.Tutorial)
    {
        TutorialState = state,
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
}
