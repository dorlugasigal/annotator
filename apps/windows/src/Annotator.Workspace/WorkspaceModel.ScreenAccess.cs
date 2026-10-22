using Annotator.Core;
using Annotator.Workspace.Capture;

namespace Annotator.Workspace;

/// <summary>
/// Screen access (WIN-CAP-04; the Mac's ensureCapturePermission, requestCapturePermission and the zoom start's permission
/// wait): a pixel feature starts only with access. While Windows has not asked yet, the feature waits and the host shows
/// Annotator's explanation, which asks Windows; the feature starts once Windows allows it. A refusal fails the feature closed
/// with the Windows Settings page that turns access on. A development build needs no access and never asks.
/// </summary>
public sealed partial class WorkspaceModel
{
    private readonly IScreenAccess? _screenAccessHost;
    private ScreenAccess _screenAccess;
    private ulong _screenAccessRequest;
    private PendingScreenAccess? _pendingScreenAccess;

    /// <summary>
    /// Settings &gt; Permissions as it shows and its Refresh status: reads Windows' answers again, as the person may have
    /// changed them in Windows Settings. Publishes only a change; a development build reads nothing.
    /// </summary>
    public ScreenAccess RefreshScreenAccess()
    {
        VerifyAccess();
        ScreenAccess access = _screenAccessHost?.Check() ?? ScreenAccess.NotNeeded;
        if (access != _screenAccess)
        {
            using ChangeScope scope = Change();
            _screenAccess = access;
        }
        return access;
    }

    /// <summary>
    /// Settings &gt; Permissions' Allow screen access (the Mac's requestCapturePermission): asks Windows at once, from Settings in
    /// front. Once allowed, a lens or zoom that failed starts again and failed effects try again (Mac retryLiveEffects). Windows
    /// does not ask again after a refusal; Settings then opens the Windows Settings page instead.
    /// </summary>
    public void RequestScreenAccess()
    {
        using ChangeScope scope = Change();
        ScreenAccess access = ReadScreenAccess();
        if (access.IsGranted)
        {
            _status = ScreenAccessText.Allowed;
            return;
        }
        if (access.IsRefused || _screenAccessHost is not { } host)
        {
            _status = ScreenAccessText.RefusedInSettings(access);
            return;
        }
        ulong request = ++_screenAccessRequest;
        _pendingScreenAccess = new PendingScreenAccess(request, null, RetryAfterScreenAccess, null);
        host.Ask(explain: false, answer => OnScreenAccessAnswered(request, answer));
    }

    /// <summary>
    /// For a pixel feature the host starts itself (Pick from screen) or region sharing: true when it may start now. Otherwise
    /// false, and either the host's explanation asks Windows, after which <paramref name="granted"/> runs once Windows allows
    /// it, or Windows refused, and <paramref name="notGranted"/> runs at once. <paramref name="notGranted"/> gets the words
    /// that say why, or null when the person chose Not now.
    /// </summary>
    public bool EnsureScreenAccess(ScreenAccessFeature feature, Action granted, Action<string?> notGranted)
    {
        ArgumentNullException.ThrowIfNull(granted);
        ArgumentNullException.ThrowIfNull(notGranted);
        using ChangeScope scope = Change();
        return Ensure(feature, granted, notGranted);
    }

    /// <summary>
    /// Mac ensureCapturePermission: true when pixels may be read now. While Windows has not asked yet, the explanation shows and
    /// <paramref name="granted"/> runs once Windows allows it (a newer request replaces this one); a refusal fails the feature.
    /// </summary>
    private bool Ensure(ScreenAccessFeature feature, Action granted, Action<string?>? notGranted = null)
    {
        ScreenAccess access = ReadScreenAccess();
        if (access.IsGranted)
        {
            return true;
        }
        if (access.IsRefused || _screenAccessHost is not { } host)
        {
            NotGranted(feature, ScreenAccessText.Refused(access, feature), notGranted);
            return false;
        }
        ulong request = ++_screenAccessRequest;
        _pendingScreenAccess = new PendingScreenAccess(request, feature, granted, notGranted);
        _status = feature == ScreenAccessFeature.ScreenZoom ? ScreenAccessText.ZoomWaiting : ScreenAccessText.Waiting;
        host.Ask(explain: true, answer => OnScreenAccessAnswered(request, answer));
        return false;
    }

    /// <summary>Windows' answers now; a host-less workspace (tests, reference states) needs none.</summary>
    private ScreenAccess ReadScreenAccess()
    {
        _screenAccess = _screenAccessHost?.Check() ?? ScreenAccess.NotNeeded;
        return _screenAccess;
    }

    /// <summary>
    /// The request ended: a granted feature starts, a declined one stops waiting, and a refused or unanswered one fails with
    /// its words. An answer to a request that a newer one replaced only updates the access.
    /// </summary>
    private void OnScreenAccessAnswered(ulong request, ScreenAccessAnswer answer)
    {
        using ChangeScope scope = Change();
        _screenAccess = answer.Access;
        if (_pendingScreenAccess is not { } pending || pending.Request != request)
        {
            return;
        }
        _pendingScreenAccess = null;
        if (answer.Access.IsGranted)
        {
            _status = ScreenAccessText.Allowed;
            pending.Granted();
            return;
        }
        if (pending.Feature is not { } feature)
        {
            // Settings' own request: its card shows the access and these words.
            _status = answer.Access.IsRefused ? ScreenAccessText.RefusedInSettings(answer.Access) : ScreenAccessText.Declined;
            return;
        }
        if (answer.Outcome == ScreenAccessOutcome.NotNow)
        {
            Declined(feature, pending.NotGranted);
            return;
        }
        NotGranted(feature, answer.Access.IsRefused ? ScreenAccessText.Refused(answer.Access, feature) : ScreenAccessText.NoAnswer(feature),
            pending.NotGranted);
    }

    /// <summary>
    /// The feature cannot read pixels: zoom and the lens fail as the Mac's reportCaptureFailure does (the toolbar's warning,
    /// Settings and its Refresh status), effects stay covered with the toolbar's warning, and a host feature gets the words.
    /// </summary>
    private void NotGranted(ScreenAccessFeature feature, string message, Action<string?>? notGranted)
    {
        switch (feature)
        {
            case ScreenAccessFeature.ScreenZoom:
                FailZoomForScreenAccess(PresentationMode.Screen, message);
                break;
            case ScreenAccessFeature.Lens:
                FailZoomForScreenAccess(PresentationMode.Lens, message);
                break;
            case ScreenAccessFeature.Effects:
                ReportCaptureFailure(message);
                break;
        }
        notGranted?.Invoke(message);
    }

    /// <summary>The person chose Not now: a pending zoom is cancelled (Mac cancelPendingZoom), and nothing else starts.</summary>
    private void Declined(ScreenAccessFeature feature, Action<string?>? notGranted)
    {
        if (feature == ScreenAccessFeature.ScreenZoom)
        {
            CancelPendingZoom();
        }
        else
        {
            _status = ScreenAccessText.Declined;
        }
        notGranted?.Invoke(null);
    }

    /// <summary>Settings' request was allowed: what failed for want of access starts again (Mac retryLiveEffects).</summary>
    private void RetryAfterScreenAccess()
    {
        if (!RetryLiveZoom() && _captureFailureMessage is not null)
        {
            RetryLiveEffects();
        }
    }

    /// <param name="Feature">The feature that waits, or null for Settings' own request.</param>
    /// <param name="NotGranted">The host feature's words, or null for the workspace's own features.</param>
    private sealed record PendingScreenAccess(ulong Request, ScreenAccessFeature? Feature, Action Granted, Action<string?>? NotGranted);
}
