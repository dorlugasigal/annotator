using Annotator.App.Shell;
using Annotator.Platform.Capture;
using Annotator.Rendering.Effects;
using Annotator.Workspace.Capture;

namespace Annotator.App.Overlay;

/// <summary>
/// What the person reads when live effects fail (Mac DisplayCapture.CaptureFailure and reportCaptureFailure, with the
/// Windows terms of specification 013). Effect regions stay opaque in every case (WIN-CAP-03).
/// </summary>
internal static class EffectsMessages
{
    /// <summary>Why the effect layer cannot show pixels, or null while it shows them or waits for capture.</summary>
    public static string? Failure(EffectsState state) => state switch
    {
        EffectsState.Failed => "Annotator could not draw the effect regions, so they stay covered. Select an effect tool to try again.",
        EffectsState.Paused { Capture: CaptureState.Failed failed } => Capture(failed.Failure),
        _ => null,
    };

    /// <summary>The operation and error behind <see cref="Failure"/>, for the log only.</summary>
    public static string? Detail(EffectsState state) => state switch
    {
        EffectsState.Failed failed => failed.Error.ToString(),
        EffectsState.Paused { Capture: CaptureState.Failed failed } =>
            $"{failed.Failure.Kind}: {failed.Failure.Operation}{(failed.Failure.Error is { } error ? $" ({error})" : string.Empty)}",
        _ => null,
    };

    /// <summary>
    /// A capture failure in words. A display that changed or disconnected is not reported: the overlays are built again for
    /// the new displays, and their regions wait for capture meanwhile. Without screen access the words say what Windows
    /// refused, where to allow it and the next step (WIN-CAP-04).
    /// </summary>
    public static string? Capture(CaptureFailure failure) => failure.Kind switch
    {
        CaptureFailureKind.DisplayChanged or CaptureFailureKind.DisplayUnavailable => null,
        CaptureFailureKind.Unsupported => "Screen capture is not available on this version of Windows, so effect regions stay covered.",
        CaptureFailureKind.AccessNeeded => ScreenAccessText.NotAsked(ScreenAccessFeature.Effects),
        CaptureFailureKind.BorderRequired or CaptureFailureKind.AccessDenied => ScreenAccessText.Refused(Refusal(failure.Kind), ScreenAccessFeature.Effects),
        CaptureFailureKind.NoFrames => "Screen capture did not provide a usable image.",
        CaptureFailureKind.ExclusionFailed =>
            "Annotator could not keep its own windows out of screen capture, so effect regions stay covered. Select an effect tool to try again.",
        CaptureFailureKind.StreamClosed => "Screen capture stopped. Select an effect tool to try again.",
        _ => "Screen capture could not start, so effect regions stay covered. Select an effect tool to try again.",
    };

    /// <summary>What Windows refuses now, read again, or what the failure says when Windows reports no refusal.</summary>
    public static ScreenAccess Refusal(CaptureFailureKind kind)
    {
        ScreenAccess access = WindowsScreenAccess.Map(CaptureAccess.Check());
        return access.IsRefused ? access : kind == CaptureFailureKind.BorderRequired ? ScreenAccess.BorderOff : ScreenAccess.ScreenOff;
    }
}
