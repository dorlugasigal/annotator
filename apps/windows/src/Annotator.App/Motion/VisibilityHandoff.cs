using System.Runtime.InteropServices;
using Annotator.App.Overlay;
using Annotator.App.Toolbar;
using Annotator.Platform;
using Annotator.Workspace;
using Microsoft.UI.Dispatching;
using Windows.Graphics;
using Style = Annotator.Core.VisibilityAnimationStyle;

namespace Annotator.App.Motion;

/// <summary>
/// The app thread's part of showing and hiding with motion: it snapshots the toolbar for the overlay's motion and hands
/// the toolbar window over to it (cloaked while its snapshot moves), plays the real toolbar's own fade when there is no
/// snapshot, and completes each transition in the workspace when the overlay's motion ends. A safety deadline completes a
/// transition whose overlay never reports, so drawings and tools can never stay half shown.
/// </summary>
internal sealed class VisibilityHandoff
{
    /// <summary>The longest any style runs (Magic's show is about 1.3 s), with room to spare.</summary>
    private static readonly TimeSpan SafetyDeadline = TimeSpan.FromSeconds(3);

    private readonly WorkspaceModel _model;
    private readonly ToolbarWindow _toolbar;
    private readonly Func<bool> _overlaysReady;
    private readonly Action _postFrame;
    private readonly DispatcherQueue _queue;
    private DispatcherQueueTimer? _safety;
    private ulong _serial;
    private ulong _chain;
    private ToolbarImage? _image;
    private bool _capture;
    private bool _active;
    /// <summary>The snapshot the toolbar showed last.</summary>
    private WorkspaceSnapshot? _view;
    /// <summary>While a hide moves the toolbar's snapshot: the snapshot from before it, which the toolbar keeps showing.</summary>
    private WorkspaceSnapshot? _frozen;

    public VisibilityHandoff(WorkspaceModel model, ToolbarWindow toolbar, Func<bool> overlaysReady, Action postFrame)
    {
        _model = model;
        _toolbar = toolbar;
        _overlaysReady = overlaysReady;
        _postFrame = postFrame;
        _queue = DispatcherQueue.GetForCurrentThread();
    }

    /// <summary>What the overlay's motion needs of the toolbar now.</summary>
    public ToolbarState Toolbar => new(_toolbar.Frame, _image);

    /// <summary>
    /// The snapshot the toolbar shows for <paramref name="snapshot"/>, after <see cref="BeforeApply"/>. While a hide moves
    /// the toolbar's snapshot, the toolbar keeps the look it had (Draw, not Use apps): the Mac takes that snapshot before
    /// its toolbar redraws, so the toolbar leaves as the person last saw it. A fade shows the real toolbar's new look.
    /// </summary>
    public WorkspaceSnapshot ToolbarView(WorkspaceSnapshot snapshot)
    {
        if (_frozen is { } frozen && snapshot.Visibility is { Showing: false })
        {
            return frozen;
        }
        _frozen = null;
        _view = snapshot;
        return snapshot;
    }

    /// <summary>Runs before the toolbar shows <paramref name="snapshot"/>.</summary>
    public void BeforeApply(WorkspaceSnapshot snapshot)
    {
        VisibilityTransition? transition = snapshot.Visibility;
        if (transition is null || transition.Serial == _serial)
        {
            return;
        }
        var fresh = !_active;
        _serial = transition.Serial;
        _active = true;
        StartSafety(transition.Serial);
        Guard(() => _toolbar.SetMotionInputBlocked(true));
        if (!_overlaysReady())
        {
            // No overlay yet (just after startup): show or hide at once.
            _queue.TryEnqueue(() => _model.CompleteVisibilityTransition(transition.Serial));
            return;
        }
        if (!fresh)
        {
            // A reversal: the overlay keeps the snapshot it has.
            return;
        }
        _chain = transition.Serial;
        _image = null;
        if (transition.Style is Style.Fade or Style.None)
        {
            if (transition.Showing)
            {
                Guard(_toolbar.PrepareFadeIn);
            }
            return;
        }
        if (transition.Showing)
        {
            // The toolbar shows cloaked: it renders for its snapshot while the overlay's motion is what people see.
            Guard(() => _toolbar.SetCloaked(true));
        }
        else if (_view is { Visibility: null, ToolbarVisible: true, WorkspaceHidden: false } view)
        {
            _frozen = view;
        }
        _capture = true;
    }

    /// <summary>Runs after the toolbar showed <paramref name="snapshot"/>.</summary>
    public void AfterApply(WorkspaceSnapshot snapshot)
    {
        if (snapshot.Visibility is null)
        {
            if (_active)
            {
                // Completed or cancelled: the toolbar is shown or hidden as the workspace says; put it back at rest.
                _active = false;
                _serial = 0;
                _capture = false;
                _image = null;
                StopSafety();
                Guard(() => _toolbar.SetCloaked(false));
                Guard(_toolbar.ResetMotion);
                Guard(() => _toolbar.SetMotionInputBlocked(false));
            }
            return;
        }
        if (_capture)
        {
            _capture = false;
            _ = CaptureAsync(_chain);
        }
    }

    /// <summary>Handles the overlay's reports about the motion; returns false for other reports.</summary>
    public bool Handle(OverlayEvent report)
    {
        switch (report)
        {
            case OverlayEvent.VisibilityStarted started:
                StartupLog.Note("visibility-started", started.Serial.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return true;
            case OverlayEvent.ToolbarCovered covered:
                if (_active && covered.Serial == _serial)
                {
                    Guard(() => _toolbar.SetCloaked(true));
                }
                return true;
            case OverlayEvent.ToolbarFallback fallback:
                if (_active && fallback.Serial == _serial)
                {
                    if (_frozen is not null)
                    {
                        // The real toolbar moves instead of its snapshot, so it shows its new look, as the Mac's does.
                        _frozen = null;
                        Guard(() => _toolbar.Apply(ToolbarView(_model.Snapshot)));
                    }
                    Guard(() => _toolbar.PlayMotion(fallback.Motion, fallback.Delay));
                    Guard(() => _toolbar.SetCloaked(false));
                }
                return true;
            case OverlayEvent.VisibilityFinished finished:
                StartupLog.Note("visibility-finished", finished.Serial.ToString(System.Globalization.CultureInfo.InvariantCulture));
                _model.CompleteVisibilityTransition(finished.Serial);
                return true;
            case OverlayEvent.Status status:
                _model.ShowStatus(status.Message);
                return true;
            default:
                return false;
        }
    }

    private async Task CaptureAsync(ulong chain)
    {
        ToolbarImage image = await _toolbar.CaptureAsync(chain);
        if (!_active || chain != _chain)
        {
            return;
        }
        _image = image;
        StartupLog.Note("toolbar-snapshot", image.Pixels is null ? "failed" : $"{image.Width}x{image.Height}");
        _postFrame();
    }

    /// <summary>
    /// Runs one step of the toolbar's motion. A failure is shown on the status line (after the current change) and the
    /// toolbar simply shows or hides without that step; it never stops the app.
    /// </summary>
    private void Guard(Action step)
    {
        try
        {
            step();
        }
        catch (Exception error) when (error is COMException or ArgumentException or InvalidOperationException or NativeCallException)
        {
            StartupLog.Note("toolbar-motion", error.Message);
            var message = "Visibility animation unavailable: " + error.Message;
            _queue.TryEnqueue(() => _model.ShowStatus(message));
        }
    }

    private void StartSafety(ulong serial)
    {
        StopSafety();
        _safety = _queue.CreateTimer();
        _safety.Interval = SafetyDeadline;
        _safety.IsRepeating = false;
        _safety.Tick += (timer, _) =>
        {
            timer.Stop();
            if (_active && _serial == serial)
            {
                StartupLog.Note("visibility-safety", serial.ToString(System.Globalization.CultureInfo.InvariantCulture));
                _model.CompleteVisibilityTransition(serial);
            }
        };
        _safety.Start();
    }

    private void StopSafety()
    {
        _safety?.Stop();
        _safety = null;
    }
}
