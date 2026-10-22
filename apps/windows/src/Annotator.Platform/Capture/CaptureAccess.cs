using System.Runtime.InteropServices;
using Annotator.Platform.Startup;
using Windows.Graphics.Capture;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace Annotator.Platform.Capture;

/// <summary>Windows' answer to one of Annotator's capture questions (AppCapabilityAccessStatus), plus the cases Annotator adds.</summary>
public enum CaptureAccessStatus
{
    /// <summary>An unpackaged process: Windows asks nothing of it (Gate 0 S-1).</summary>
    NotNeeded,

    Allowed,

    /// <summary>The person said No, or turned it off in Windows Settings.</summary>
    DeniedByUser,

    /// <summary>Windows or the organization does not allow it.</summary>
    DeniedBySystem,

    /// <summary>The package manifest lacks the capability.</summary>
    NotDeclaredByApp,

    /// <summary>Windows still needs the person's answer.</summary>
    UserPromptRequired,

    /// <summary>No answer came within the wait; Windows' question may still be open.</summary>
    NoAnswer,

    /// <summary>The request or the check itself failed.</summary>
    Failed,
}

/// <summary>
/// Windows' answers to its two capture questions for a packaged Annotator (research R-5 and R-6). <see cref="Screen"/> is
/// "Let Annotator take screenshots or record the screen?" (graphicsCaptureProgrammatic, Settings &gt; Privacy &amp; security
/// &gt; Screenshots and screen recording): every capture item Annotator makes from a window or display needs it.
/// <see cref="Border"/> is "Let Annotator turn off the screenshot border when taking screenshots or recording the screen?"
/// (graphicsCaptureWithoutBorder, Screenshot borders): without it Windows draws the yellow border although
/// IsBorderRequired is false.
/// </summary>
public readonly record struct CaptureConsent(CaptureAccessStatus Screen, CaptureAccessStatus Border)
{
    /// <summary>An unpackaged process, which Windows asks nothing.</summary>
    public static CaptureConsent NotNeeded { get; } = new(CaptureAccessStatus.NotNeeded, CaptureAccessStatus.NotNeeded);

    /// <summary>Capture may start: no consent is needed, or the person allowed both.</summary>
    public bool Granted => Screen == CaptureAccessStatus.NotNeeded
        || (Screen == CaptureAccessStatus.Allowed && Border == CaptureAccessStatus.Allowed);

    /// <summary>
    /// Why capture must not start, or null when it may: a question Windows has not asked yet is
    /// <see cref="CaptureFailureKind.AccessNeeded"/>, a refused or unknown screen answer <see cref="CaptureFailureKind.AccessDenied"/>,
    /// and a refused border answer <see cref="CaptureFailureKind.BorderRequired"/>.
    /// </summary>
    public CaptureFailure? Failure
    {
        get
        {
            if (Granted)
            {
                return null;
            }
            string operation = $"Screen capture consent (screenshots {Screen}, border {Border})";
            CaptureFailureKind kind = Screen switch
            {
                CaptureAccessStatus.UserPromptRequired => CaptureFailureKind.AccessNeeded,
                CaptureAccessStatus.Allowed when Border == CaptureAccessStatus.UserPromptRequired => CaptureFailureKind.AccessNeeded,
                CaptureAccessStatus.Allowed => CaptureFailureKind.BorderRequired,
                _ => CaptureFailureKind.AccessDenied,
            };
            return new CaptureFailure(kind, operation);
        }
    }
}

/// <summary>
/// Consent for Annotator's screen capture in a packaged app (WIN-CAP-04, research R-5 and R-6). Windows shows its questions
/// only while an Annotator window is the foreground window, so capture never asks: it reads the answers
/// (<see cref="Check"/>) and starts nothing without both (<see cref="Require"/>), failing closed at once. The app asks
/// (<see cref="RequestAsync"/>) from a window in front when the person starts a pixel feature: zoom, the lens, an effect,
/// region sharing or Pick from screen. An unpackaged process needs neither question (S-1) and never calls Windows here.
/// </summary>
public static class CaptureAccess
{
    private const string ScreenCapability = "graphicsCaptureProgrammatic";
    private const string BorderCapability = "graphicsCaptureWithoutBorder";

    /// <summary>The Windows Settings page of the screen question: Privacy &amp; security &gt; Screenshots and screen recording.</summary>
    public const string ScreenSettingsUri = "ms-settings:privacy-graphicscaptureprogrammatic";

    /// <summary>The Windows Settings page of the border question: Privacy &amp; security &gt; Screenshot borders.</summary>
    public const string BorderSettingsUri = "ms-settings:privacy-graphicscapturewithoutborder";

    /// <summary>How long a request waits for the person to answer one of Windows' questions.</summary>
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Windows' current answers, without asking the person (AppCapability.CheckAccess). Cheap enough to call before each
    /// capture starts. An unpackaged process needs no consent and never calls Windows here.
    /// </summary>
    public static CaptureConsent Check() =>
        StartupTaskService.HasPackageIdentity ? new CaptureConsent(CheckOne(ScreenCapability), CheckOne(BorderCapability)) : CaptureConsent.NotNeeded;

    /// <summary>
    /// Starts nothing without consent, and never waits for it: throws a <see cref="CaptureStartException"/> with
    /// <see cref="CaptureConsent.Failure"/> unless Windows allowed both kinds of capture or the process needs no consent.
    /// </summary>
    internal static void Require()
    {
        if (Check().Failure is { } failure)
        {
            throw new CaptureStartException(failure);
        }
    }

    /// <summary>
    /// Asks Windows the questions it has not asked yet, screen first, then border (GraphicsCaptureAccess.RequestAccessAsync).
    /// Call it while an Annotator window is the foreground window, such as right after the person clicked a button in it:
    /// otherwise Windows shows no question. No thread waits: each answer is awaited for at most <paramref name="perQuestion"/>,
    /// after which it reads <see cref="CaptureAccessStatus.NoAnswer"/> and Windows' question stays open.
    /// </summary>
    public static async Task<CaptureConsent> RequestAsync(TimeSpan perQuestion)
    {
        if (!StartupTaskService.HasPackageIdentity)
        {
            return CaptureConsent.NotNeeded;
        }
        CaptureConsent now = Check();
        CaptureAccessStatus screen = Unanswered(now.Screen) ? await AskAsync(GraphicsCaptureAccessKind.Programmatic, perQuestion) : now.Screen;
        if (screen != CaptureAccessStatus.Allowed)
        {
            return new CaptureConsent(screen, now.Border);
        }
        CaptureAccessStatus border = Unanswered(now.Border) ? await AskAsync(GraphicsCaptureAccessKind.Borderless, perQuestion) : now.Border;
        return new CaptureConsent(screen, border);
    }

    /// <summary>The words support info uses for the answers (the Mac's "Screen access allowed").</summary>
    public static string Describe(CaptureConsent consent) =>
        consent.Screen == CaptureAccessStatus.NotNeeded ? Describe(CaptureAccessStatus.NotNeeded)
        : consent.Granted ? Describe(CaptureAccessStatus.Allowed)
        : consent.Screen != CaptureAccessStatus.Allowed ? $"{Describe(consent.Screen)} (screenshots and screen recording)"
        : $"{Describe(consent.Border)} (screenshot border)";

    /// <summary>The words support info uses for one answer.</summary>
    public static string Describe(CaptureAccessStatus status) => status switch
    {
        CaptureAccessStatus.NotNeeded => "not needed",
        CaptureAccessStatus.Allowed => "allowed",
        CaptureAccessStatus.DeniedByUser => "turned off in Windows Settings",
        CaptureAccessStatus.DeniedBySystem => "not allowed by Windows",
        CaptureAccessStatus.NotDeclaredByApp => "not declared by the package",
        CaptureAccessStatus.UserPromptRequired => "not asked yet",
        _ => "unknown",
    };

    /// <summary>The typed status of an AppCapabilityAccessStatus value.</summary>
    public static CaptureAccessStatus Map(AppCapabilityAccessStatus status) => status switch
    {
        AppCapabilityAccessStatus.Allowed => CaptureAccessStatus.Allowed,
        AppCapabilityAccessStatus.DeniedByUser => CaptureAccessStatus.DeniedByUser,
        AppCapabilityAccessStatus.DeniedBySystem => CaptureAccessStatus.DeniedBySystem,
        AppCapabilityAccessStatus.NotDeclaredByApp => CaptureAccessStatus.NotDeclaredByApp,
        AppCapabilityAccessStatus.UserPromptRequired => CaptureAccessStatus.UserPromptRequired,
        _ => CaptureAccessStatus.Failed,
    };

    /// <summary>A question to ask: Windows has not asked it, or could not tell.</summary>
    private static bool Unanswered(CaptureAccessStatus status) => status is CaptureAccessStatus.UserPromptRequired or CaptureAccessStatus.Failed;

    private static CaptureAccessStatus CheckOne(string capability)
    {
        try
        {
            return Map(AppCapability.Create(capability).CheckAccess());
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return CaptureAccessStatus.Failed;
        }
    }

    private static async Task<CaptureAccessStatus> AskAsync(GraphicsCaptureAccessKind kind, TimeSpan timeout)
    {
        Task<AppCapabilityAccessStatus> request;
        try
        {
            request = GraphicsCaptureAccess.RequestAccessAsync(kind).AsTask();
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            return CaptureAccessStatus.Failed;
        }
        using (var limit = new CancellationTokenSource())
        {
            Task first = await Task.WhenAny(request, Task.Delay(timeout, limit.Token));
            limit.Cancel();
            if (first != request)
            {
                // Windows' question stays open; a later answer reaches Check. The request's own failure is observed here.
                _ = request.ContinueWith(static task => task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                return CaptureAccessStatus.NoAnswer;
            }
        }
        try
        {
            return Map(await request);
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            return CaptureAccessStatus.Failed;
        }
    }
}
