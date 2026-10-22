using Annotator.App.Overlay;
using Annotator.Platform.Displays;
using Annotator.Rendering.Sharing;
using Annotator.Workspace.Motion;

namespace Annotator.App.Motion;

/// <summary>
/// Another surface that plays the overlays' show and hide motion: the region sharing output (the Mac's
/// SharedWorkspaceTransition, which WorkspaceVisibilityAnimator feeds). <see cref="VisibilityAnimator"/> calls it on the
/// overlay thread in the same work items as its own changes, so both start in the same composition commit. A mirror
/// handles its own failures.
/// </summary>
internal interface IVisibilityMirror
{
    /// <summary>
    /// Transition <paramref name="serial"/> starts, or turns around when <see cref="VisibilityPlan.Reversing"/>:
    /// <paramref name="snapshots"/> are what the overlays play, in their order, from <paramref name="started"/> on the steady
    /// clock; particles draw their first frame at <paramref name="now"/>.
    /// </summary>
    void Play(ulong serial, VisibilityPlan plan, IReadOnlyList<SharedMotionSnapshot> snapshots, double started, double now);

    /// <summary>
    /// The toolbar's snapshot plays on the overlay of <paramref name="display"/>, resting at <paramref name="pixels"/> there,
    /// <paramref name="shift"/> seconds into the plan; <paramref name="blurred"/> and <paramref name="particles"/> say how.
    /// </summary>
    void PlayToolbar(ToolbarPlan toolbar, ToolbarImage image, string display, PixelRect pixels, bool blurred, bool particles, double started,
                     double shift, double now);

    /// <summary>The real toolbar plays its own motion (Fade, or no snapshot).</summary>
    void UseRealToolbar();

    /// <summary>Draws what the compositor cannot animate at <paramref name="now"/>; returns whether it still changes.</summary>
    bool Frame(double now, double started);

    /// <summary>The transition ended: <paramref name="completed"/> when its motion ran to its end, false when it was cancelled.</summary>
    void End(bool completed);
}
