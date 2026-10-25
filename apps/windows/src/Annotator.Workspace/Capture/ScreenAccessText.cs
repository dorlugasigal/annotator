namespace Annotator.Workspace.Capture;

/// <summary>
/// The words of screen access: the Mac's permission flow (WorkspaceModel.requestCapturePermission, reportCaptureFailure,
/// ZoomStartController, the Screen Recording cards of CustomizationView) with the Windows terms
/// of specification 013, where Screen Recording is Windows' Screenshots and screen recording. Windows asks two questions
/// with Yes and No (checked on build 26200: "Let Annotator take screenshots or record the screen?" and "Let Annotator turn
/// off the screenshot border when taking screenshots or recording the screen?"), so the explanation says so.
/// </summary>
public static class ScreenAccessText
{
    /// <summary>The explanation's title bar, as the error window's.</summary>
    public const string WindowTitle = "Annotator";

    /// <summary>The Mac card's "Screen Recording", as Windows names its page.</summary>
    public const string Title = "Screenshots and screen recording";

    /// <summary>The Mac card's detail (CustomizationView).</summary>
    public const string Detail = "For zoom, live effects and region sharing. No video is recorded.";

    /// <summary>Windows only: what happens after Allow screen access.</summary>
    public const string Ask = "Select Allow screen access, then choose Yes for both questions from Windows. The first lets Annotator see the screen. The second turns off the yellow screenshot border.";

    /// <summary>Windows only: the explanation while Windows asks.</summary>
    public const string Asking = "Answer the questions from Windows to continue.";

    /// <summary>The Mac card's button.</summary>
    public const string Allow = "Allow screen access";

    public const string NotNow = "Not now";

    /// <summary>The Mac's status while zoom waits for the permission (ZoomStartController), with the Windows term.</summary>
    public const string ZoomWaiting = "Zoom is waiting for screen recording permission.";

    /// <summary>The Mac toolbar's pending zoom detail, with the Windows term.</summary>
    public const string ZoomWaitingDetail = "Waiting for screen recording permission";

    /// <summary>Windows only: the status while the explanation shows for another feature.</summary>
    public const string Waiting = "Annotator is waiting for screen recording permission.";

    /// <summary>The Mac's status once the permission is on (requestCapturePermission).</summary>
    public const string Allowed = "Screenshots and screen recording is on. Try screen zoom or the live effect again.";

    /// <summary>Windows only: the person closed the explanation without asking Windows.</summary>
    public const string Declined = "Screen access isn't on yet. Zoom, live effects and region sharing need it.";

    /// <summary>What a feature needs, as the first sentence of its messages.</summary>
    public static string Needs(ScreenAccessFeature feature) => feature switch
    {
        ScreenAccessFeature.Effects => "Effect regions need screen access, so they stay covered.",
        ScreenAccessFeature.Lens => "The pointer lens needs screen access.",
        ScreenAccessFeature.ScreenZoom => "Live zoom needs screen access.",
        ScreenAccessFeature.RegionSharing => "Region sharing needs screen access.",
        _ => "Pick from screen needs screen access.",
    };

    /// <summary>
    /// Mac reportCaptureFailure's "Screen Recording is off for this copy of Annotator. Allow it in System Settings." for a
    /// refusal: what is off, the Windows Settings page that turns it on, and the next step.
    /// </summary>
    public static string Refused(ScreenAccess access, ScreenAccessFeature feature) => $"{Needs(feature)} {Reason(access, Then(feature))}";

    /// <summary>Settings &gt; Permissions: the refusal, the Windows Settings page and Refresh status.</summary>
    public static string RefusedInSettings(ScreenAccess access) => Reason(access, "select Refresh status");

    /// <summary>Windows gave no answer in time.</summary>
    public static string NoAnswer(ScreenAccessFeature feature) => $"{Needs(feature)} Windows got no answer to its question. {Again(feature)}";

    /// <summary>Capture found that Windows has not asked yet, for regions or a view that need pixels without a new request.</summary>
    public static string NotAsked(ScreenAccessFeature feature) => feature switch
    {
        ScreenAccessFeature.Effects => $"{Needs(feature)} Select an effect tool to allow it.",
        ScreenAccessFeature.RegionSharing => $"{Needs(feature)} Start sharing again to allow it.",
        _ => $"{Needs(feature)} Try again to allow it.",
    };

    private static string Reason(ScreenAccess access, string then) => access switch
    {
        ScreenAccess.BorderOff =>
            $"Annotator isn't allowed to turn off the yellow screenshot border. Allow it in Windows Settings > Privacy & security > Screenshot borders, then {then}.",
        ScreenAccess.Blocked =>
            $"Windows or your organization doesn't let Annotator take screenshots or record the screen. Check Windows Settings > Privacy & security > Screenshots and screen recording or ask your administrator, then {then}.",
        _ =>
            $"Screenshots and screen recording is off for Annotator. Allow it in Windows Settings > Privacy & security > Screenshots and screen recording, then {then}.",
    };

    private static string Then(ScreenAccessFeature feature) => feature switch
    {
        ScreenAccessFeature.Effects => "select an effect tool to try again",
        ScreenAccessFeature.RegionSharing => "start sharing again",
        _ => "try again",
    };

    private static string Again(ScreenAccessFeature feature) => feature switch
    {
        ScreenAccessFeature.Effects => "Select an effect tool to try again.",
        ScreenAccessFeature.RegionSharing => "Start sharing again.",
        _ => "Try again.",
    };
}
