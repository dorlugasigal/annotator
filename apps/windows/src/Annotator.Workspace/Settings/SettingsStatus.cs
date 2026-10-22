using Annotator.Workspace.Capture;

namespace Annotator.Workspace.Settings;

/// <summary>The status lines of Settings that follow the workspace (Mac CustomizationView.permissions).</summary>
public static class SettingsStatus
{
    /// <summary>
    /// The screen capture card's status (Mac permissionCard): "Not allowed" until Windows allows screen access, which a
    /// development build needs none of; then a failure says so until capture works again, and capture works once a frame
    /// arrived. Allowed and working stay separate (spec 005, WIN-CAP-04).
    /// </summary>
    public static string Capture(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return !snapshot.ScreenAccess.IsGranted ? SettingsCopy.CaptureNotAllowed
            : snapshot.CaptureFailureMessage is not null ? SettingsCopy.CaptureFailed
            : snapshot.CaptureVerified ? SettingsCopy.CaptureWorking
            : SettingsCopy.CaptureAllowed;
    }

    /// <summary>
    /// The screen capture card's button (Mac: Open Screen Recording settings once allowed, else Allow screen access, which asks
    /// or, once Windows will not ask again, opens Windows Settings).
    /// </summary>
    public static string CaptureAction(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.ScreenAccess.IsGranted ? SettingsCopy.OpenCaptureSettings : SettingsCopy.AllowScreenAccess;
    }

    /// <summary>
    /// The red line under the card (Mac captureFailureMessage): the capture failure, or, while Windows refuses screen access,
    /// where to allow it; empty otherwise.
    /// </summary>
    public static string CaptureProblem(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.CaptureFailureMessage
            ?? (snapshot.ScreenAccess.IsRefused ? ScreenAccessText.RefusedInSettings(snapshot.ScreenAccess) : string.Empty);
    }

    /// <summary>Mac permissionCard: a check mark for a status that needs nothing more, otherwise an information mark.</summary>
    public static bool IsSettled(string status) => status is SettingsCopy.CaptureWorking or SettingsCopy.InputStatus;
}
