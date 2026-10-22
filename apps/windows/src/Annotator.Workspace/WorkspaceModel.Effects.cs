using Annotator.Core;

namespace Annotator.Workspace;

/// <summary>
/// Live effects (spec 003, WIN-CAP-01, WIN-CAP-03, WIN-OVR-09): the effect choice, what the host reports about capture,
/// and the Mac's suspension after system transitions. The host captures and draws; the workspace keeps what the toolbar
/// and the status line show.
/// </summary>
public sealed partial class WorkspaceModel
{
    /// <summary>The Mac's status after a system transition (suspendPresentation).</summary>
    public const string CapturePausedMessage =
        "Capture paused after a system transition. Existing effect regions are covered opaquely; select an effect tool to resume.";

    /// <summary>The toolbar's warning while capture failed (Mac ToolbarView, captureFailureMessage set).</summary>
    public const string CaptureUnavailableWarning = "Live pixels are unavailable. Details are in Settings.";

    /// <summary>The toolbar's warning while effect regions wait for capture (Mac ToolbarView, censorFallbackActive).</summary>
    public const string CaptureCoveredWarning = "Pixel effects are covered solidly until capture is ready.";

    private string? _captureFailureMessage;
    private bool _captureVerified;
    private ulong _effectsRetry;

    /// <summary>Mac preferredEffectTool: the last effect the person chose, else Blur.</summary>
    public static Tool PreferredEffect(in WorkspacePreferences preferences) =>
        preferences.LastCoverMode is { IsPixelEffect: true } last ? last : Tool.Blur;

    /// <summary>
    /// Mac setCoverMode, the effect picker: a selection of covers and effects changes to <paramref name="value"/> as one
    /// Undo step, unless it is locked; otherwise <paramref name="value"/> becomes the tool.
    /// </summary>
    public void SetCoverMode(Tool value)
    {
        using ChangeScope scope = Change();
        if (!value.IsPixelEffect)
        {
            Report(CanvasError.InvalidDocument("Choose Blur, Pixelate or Distort. Use Rectangle for a solid block."));
            return;
        }
        // Mac setCoverMode: an effect needs screen access first; the mode changes once Windows allows it.
        if (!Ensure(Capture.ScreenAccessFeature.Effects, () => SetCoverMode(value)))
        {
            return;
        }
        List<Element> selected = SelectedElements();
        if (selected.Count == 0 || !selected.All(element => element.Tool.IsProtection))
        {
            Choose(value);
            return;
        }
        if (IsLocked(selected))
        {
            Report(CanvasError.InvalidDocument("Unlock this cover before changing its mode."));
            return;
        }
        Perform((ref CanvasDocument document) => document.EditSelection((ref Element element) => element.Tool = value));
        _preferences.LastCoverMode = value;
        PersistPreferences();
        Synchronize();
    }

    /// <summary>
    /// What the host shows on <paramref name="display"/>: whether it has effect regions (including one being drawn) and
    /// whether they show live pixels (Mac <c>effectsReady</c>). The active display's state decides the toolbar's warning.
    /// </summary>
    public void SetEffectsState(DisplayId display, bool regions, bool ready)
    {
        VerifyAccess();
        if (!_canvases.TryGetValue(display, out Canvas? canvas) || (canvas.EffectRegions == regions && canvas.EffectsReady == ready))
        {
            return;
        }
        using ChangeScope scope = Change();
        canvas.EffectRegions = regions;
        canvas.EffectsReady = ready;
    }

    /// <summary>Mac noteCaptureFrame: capture delivers pixels again, so an earlier failure is no longer shown.</summary>
    public void NoteCaptureFrame()
    {
        VerifyAccess();
        if (_captureFailureMessage is null && _captureVerified)
        {
            return;
        }
        using ChangeScope scope = Change();
        _captureFailureMessage = null;
        _captureVerified = true;
    }

    /// <summary>
    /// Mac reportCaptureFailure: capture or drawing the effects failed, so effect regions stay opaque. The toolbar warns
    /// (<see cref="CaptureUnavailableWarning"/>) and the status line says what happened and what to do.
    /// </summary>
    public void ReportCaptureFailure(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        VerifyAccess();
        if (_captureFailureMessage == message && _status == message)
        {
            return;
        }
        using ChangeScope scope = Change();
        _captureFailureMessage = message;
        _captureVerified = false;
        _status = message;
    }

    /// <summary>Mac retryLiveEffects: the host ends a capture pause and tries failed effects again.</summary>
    public void RetryLiveEffects()
    {
        using ChangeScope scope = Change();
        _effectsRetry++;
    }

    /// <summary>
    /// Mac suspendPresentation after a lock, sleep, sign-out, console or Remote Desktop change, or a virtual desktop switch
    /// (WIN-OVR-09, WIN-OVR-06): region sharing stops with its safety message, a running show or hide stops, a pending zoom
    /// start is cancelled (not for a virtual desktop switch, <paramref name="cancelPendingRequests"/> false, as the Mac keeps it
    /// on a Space change), a zoom gesture ends, text being edited is kept, zoom stops at once, drawing input is released and the
    /// status says that capture is paused. The host pauses capture, so effect regions stay opaque until an effect tool is chosen.
    /// </summary>
    public void SuspendPresentation(bool cancelPendingRequests = true)
    {
        using ChangeScope scope = Change();
        _sharing?.StopForSafety(Sharing.RegionSharingText.SystemPaused);
        CancelVisibilityTransition();
        if (cancelPendingRequests)
        {
            ZoomStart.Cancel();
        }
        FinishZoomGestureCore();
        foreach (Canvas canvas in _canvases.Values)
        {
            CommitTextIfNeeded(canvas);
        }
        foreach (Canvas canvas in _canvases.Values)
        {
            if (canvas.Document.Presentation.Mode != PresentationMode.Off)
            {
                Mutate(canvas, (ref CanvasDocument document) => document.StopPresentationWithoutHistory());
                canvas.PresentationCut++;
            }
        }
        UseApps();
        if (!ZoomStart.IsWaiting)
        {
            _status = CapturePausedMessage;
        }
    }
}
