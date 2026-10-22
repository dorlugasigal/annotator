using Annotator.Platform.Capture;
using Annotator.Platform.Windowing;
using Annotator.Workspace.Capture;
using Windows.Graphics;

namespace Annotator.App.Shell;

/// <summary>
/// Screen access for a packaged Annotator (WIN-CAP-04; the Mac's DisplayCapture.hasPermission and requestPermission): Windows'
/// answers to its two capture questions, and the explanation that asks them. One request runs at a time; a newer request takes
/// over the answer of one that is open. Windows' answer is read again from Windows once a request ends, so what Annotator
/// shows is what Windows keeps. A development build is not packaged and gets no host (it needs no access).
/// </summary>
internal sealed class WindowsScreenAccess(WindowRegistry registry, Func<RectInt32?> anchor) : IScreenAccess
{
    private ScreenAccessWindow? _window;
    private Action<ScreenAccessAnswer>? _answered;
    private bool _asking;

    public ScreenAccess Check() => Map(CaptureAccess.Check());

    public void Ask(bool explain, Action<ScreenAccessAnswer> answered)
    {
        ArgumentNullException.ThrowIfNull(answered);
        _answered = answered;
        if (_asking)
        {
            // Windows asks already; its answer goes to this request.
            if (explain)
            {
                _window?.Present();
            }
            return;
        }
        if (explain)
        {
            (_window ??= new ScreenAccessWindow(registry, anchor, AskWindows, NotNow)).Present();
            return;
        }
        _ = AskWindowsAsync();
    }

    /// <summary>Closes the explanation for good (Quit).</summary>
    public void Shutdown()
    {
        _window?.Shutdown();
        _window = null;
    }

    /// <summary>
    /// Windows' answers as the workspace's access: the screen question first, then the border question. A question Windows has
    /// not asked, or one that got no answer, is still to ask; a refusal by the person names its page; anything else (a policy,
    /// a package without the capability, a failed check) is blocked.
    /// </summary>
    public static ScreenAccess Map(CaptureConsent consent) => consent.Screen switch
    {
        CaptureAccessStatus.NotNeeded => ScreenAccess.NotNeeded,
        CaptureAccessStatus.Allowed => consent.Border switch
        {
            CaptureAccessStatus.Allowed => ScreenAccess.Allowed,
            CaptureAccessStatus.UserPromptRequired or CaptureAccessStatus.NoAnswer => ScreenAccess.NotAsked,
            CaptureAccessStatus.DeniedByUser => ScreenAccess.BorderOff,
            _ => ScreenAccess.Blocked,
        },
        CaptureAccessStatus.UserPromptRequired or CaptureAccessStatus.NoAnswer => ScreenAccess.NotAsked,
        CaptureAccessStatus.DeniedByUser => ScreenAccess.ScreenOff,
        _ => ScreenAccess.Blocked,
    };

    private void AskWindows()
    {
        if (!_asking)
        {
            _window?.ShowAsking();
            _ = AskWindowsAsync();
        }
    }

    private void NotNow()
    {
        _window?.Hide();
        Finish(new ScreenAccessAnswer(Check(), ScreenAccessOutcome.NotNow));
    }

    /// <summary>
    /// Asks Windows' open questions on this thread without blocking it; each answer is awaited for at most
    /// <see cref="CaptureAccess.AnswerTimeout"/>. The explanation stays in front meanwhile, as Windows' question belongs to it.
    /// </summary>
    private async Task AskWindowsAsync()
    {
        _asking = true;
        CaptureConsent asked;
        try
        {
            asked = await CaptureAccess.RequestAsync(CaptureAccess.AnswerTimeout);
        }
        catch (Exception error)
        {
            // Not an answer from Windows: the person sees the error, and the waiting feature fails as unanswered.
            Native.CallbackErrors.Report(error);
            asked = new CaptureConsent(CaptureAccessStatus.Failed, CaptureAccessStatus.Failed);
        }
        finally
        {
            _asking = false;
        }
        _window?.Hide();
        CaptureConsent kept = CaptureAccess.Check();
        StartupLog.Note("screen-access", $"asked: {CaptureAccess.Describe(asked)} ({asked.Screen}, {asked.Border}); kept: {CaptureAccess.Describe(kept)}");
        bool unanswered = asked.Screen is CaptureAccessStatus.NoAnswer or CaptureAccessStatus.Failed
            || asked.Border is CaptureAccessStatus.NoAnswer or CaptureAccessStatus.Failed;
        ScreenAccess access = Map(kept);
        Finish(new ScreenAccessAnswer(access, unanswered && !access.IsGranted ? ScreenAccessOutcome.NoAnswer : ScreenAccessOutcome.Answered));
    }

    private void Finish(ScreenAccessAnswer answer)
    {
        Action<ScreenAccessAnswer>? answered = _answered;
        _answered = null;
        answered?.Invoke(answer);
    }
}
