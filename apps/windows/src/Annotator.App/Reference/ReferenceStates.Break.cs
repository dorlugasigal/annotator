using Annotator.Core;
using Annotator.Workspace.Breaks;

namespace Annotator.App.Reference;

/// <summary>The break screen's states (reference/ux-reference/macos/break-timer, specification 014).</summary>
internal static partial class ReferenceStates
{
    /// <summary>
    /// UXReferenceCaptureTests.test10BreakTimer: the countdown in each state at 480 by 300 points, each color set and the
    /// plain background without a title at 320 by 200. Sizes follow the shorter side, as on a full screen.
    /// </summary>
    private static ReferenceScenario? Break(string state)
    {
        var unlocked = new BreakTimerSettings { LockWhenTouched = false };
        const string Palette = "palette-";
        if (state.StartsWith(Palette, StringComparison.Ordinal))
        {
            return BreakPalette.FromRawValue(state[Palette.Length..]) is { } palette
                ? BreakState(Face("10:00", 1, settings: new BreakTimerSettings { Palette = palette }), small: true)
                : null;
        }
        return state switch
        {
            "arming" => BreakState(Face("10:00", 1, lockState: BreakLockState.Arming) with { ArmingSeconds = 3 }),
            "armed" => BreakState(Face("7:42", 0.77)),
            "controls" => BreakState(Face("7:42", 0.77, lockState: BreakLockState.Off, controls: true, settings: unlocked)),
            "paused" => BreakState(Face("7:42", 0.77, BreakPhase.Paused, BreakLockState.Off, controls: true, settings: unlocked)),
            "last-minute" => BreakState(Face("0:42", 0.07, BreakPhase.LastMinute)),
            "time-up" => BreakState(Face("+1:23", 1, BreakPhase.TimeUp, BreakLockState.Off, settings: unlocked)),
            "lock-failed" => BreakState(Face("7:42", 0.77, lockState: BreakLockState.Off, controls: true, notice: BreakCopy.LockNotConfirmed,
                settings: unlocked)),
            "plain-no-title" => BreakState(Face("25:00", 1, settings: new BreakTimerSettings
            {
                Background = BreakBackground.Plain,
                Title = string.Empty,
                ShowEndTime = false,
            }), small: true),
            _ => null,
        };
    }

    private static ReferenceScenario BreakState(BreakFace face, bool small = false) => new(ReferenceSurface.BreakTimer)
    {
        Break = face,
        Width = small ? 320 : 480,
        Height = small ? 200 : 300,
        // test10BreakTimer's settle: the ring and the controls finish arriving.
        Settle = TimeSpan.FromSeconds(1.2),
    };

    /// <summary>Mac BreakFaceState.preview(settings) with the capture's changes; the host adds the end time.</summary>
    private static BreakFace Face(string label, double fraction, BreakPhase phase = BreakPhase.Running, BreakLockState lockState = BreakLockState.Armed,
                                  bool controls = false, string? notice = null, BreakTimerSettings? settings = null) =>
        BreakFace.Preview(settings ?? new BreakTimerSettings(), endTime: null) with
        {
            Label = label,
            Fraction = fraction,
            Phase = phase,
            Lock = lockState,
            ControlsVisible = controls,
            Notice = notice,
        };
}
