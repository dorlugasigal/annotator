using System.Diagnostics;
using Annotator.App.Motion;
using Annotator.Platform.Displays;
using Annotator.Platform.Input;
using Annotator.Rendering.Presentation;
using Annotator.Workspace;
using Annotator.Workspace.Pointer;
using SystemDispatcherQueue = Windows.System.DispatcherQueue;

namespace Annotator.App.Overlay;

/// <summary>Counters for the live check and measurements.</summary>
internal sealed class PointerEffectsCounters
{
    /// <summary>Activity reports from the pointer watcher and the canvases' pen and touch input.</summary>
    public long Activity;

    /// <summary>Times the effects were shown on the overlays.</summary>
    public long Applies;

    public long Clicks;

    /// <summary>Times the pointer watcher started.</summary>
    public long WatcherStarts;

    /// <summary>Times the pointer watcher could not start or stopped working.</summary>
    public long WatcherFailures;

    /// <summary>The pointer position of the last show, in physical desktop pixels.</summary>
    public System.Drawing.Point LastPointer;

    public bool SpotlightShown;

    public bool HaloShown;

    /// <summary>The most layers that showed an effect at once: the overlays' and the shared region's.</summary>
    public int MostLayersShown;

    public PointerEffectsCounters Copy() => (PointerEffectsCounters)MemberwiseClone();
}

/// <summary>
/// Spotlight and Cursor halo on every overlay (Mac WorkspaceModel.updatePointerEffects, notePointerEvent and
/// SelectionOverlayView), on the overlay thread. While an effect is on and the workspace shows, a
/// <see cref="PointerWatcher"/> reports pointer activity; each display's <see cref="PointerEffectsLayer"/> then shows the
/// effects at the pointer, at most once per composition frame: the first move after a rest shows at once, later ones on the
/// next compositor clock tick. Fades, size changes and clicks take frames from a <see cref="FrameClock"/> only while they
/// move; the halo's Moving and Clicks visibility end with one timer. At rest there is no thread, timer or present
/// (WIN-SYS-07), and with every effect off no pointer watcher either.
/// </summary>
internal sealed class PointerEffectsHost : IDisposable
{
    private readonly SystemDispatcherQueue _queue;
    private readonly Action<OverlayEvent> _report;
    private readonly PointerEffects _state = new();
    private readonly FrameClock _clock;
    private readonly Windows.System.DispatcherQueueTimer _timer;
    private readonly Windows.System.DispatcherQueueHandler _onActivity;
    private readonly List<(PointerEffectsLayer Layer, PixelRect Bounds)> _layers = [];
    private PointerWatcher? _watcher;
    private bool _watcherFailed;
    private System.Drawing.Point _pointer;
    private System.Drawing.Point? _penPointer;
    private bool _appliedThisFrame;
    private bool _pending;
    private bool _clickShown;
    private bool _shownAny;
    private bool _ticking;
    private bool _released = true;
    private bool _disposed;

    public PointerEffectsHost(SystemDispatcherQueue queue, Action<OverlayEvent> report)
    {
        _queue = queue;
        _report = report;
        _clock = new FrameClock(queue, OnFrame);
        _timer = queue.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => Refresh();
        _onActivity = OnActivity;
    }

    public PointerEffectsCounters Counters { get; } = new();

    /// <summary>Whether the pointer watcher runs now (an effect is on and the workspace shows).</summary>
    public bool Watching => _watcher is not null;

    /// <summary>A canvas's layer joins; it shows the effects from now on.</summary>
    public void Attach(PointerEffectsLayer layer, PixelRect displayBounds)
    {
        _layers.Add((layer, displayBounds));
        if (_shownAny)
        {
            Apply(Now());
        }
    }

    /// <summary>A canvas is going away with its layer.</summary>
    public void Detach(PointerEffectsLayer layer) => _layers.RemoveAll(entry => entry.Layer == layer);

    /// <summary>Each published workspace frame (Mac property observers): effects on or off, settings, hide and show.</summary>
    public void Update(WorkspaceSnapshot snapshot)
    {
        if (_disposed)
        {
            return;
        }
        bool reduces = SystemMotion.ReducesMotion();
        _state.Update(snapshot, reduces);
        if (_state.Watching)
        {
            StartWatching();
        }
        else
        {
            StopWatching();
        }
        Refresh();
    }

    /// <summary>
    /// Pen and touch on a canvas in Draw (raw mouse input does not carry them): a move or press at
    /// <paramref name="desktop"/> pixels, which the effects follow until the mouse moves again.
    /// </summary>
    public void NoteCanvasPointer(System.Drawing.Point desktop, bool pressed)
    {
        if (_disposed || !_state.Watching)
        {
            return;
        }
        Counters.Activity++;
        _penPointer = desktop;
        double now = Now();
        _state.NoteMove(now);
        if (pressed)
        {
            Counters.Clicks++;
            _state.NoteClick(now, new Annotator.Core.Point(desktop.X, desktop.Y), SystemMotion.ReducesMotion());
        }
        Show(now);
    }

    /// <summary>New devices: each layer draws its halo again on the next show.</summary>
    public void DevicesChanged()
    {
        if (!_disposed && _shownAny)
        {
            Apply(Now());
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _timer.Stop();
        _clock.Dispose();
        StopWatching();
        _layers.Clear();
    }

    private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private void StartWatching()
    {
        if (_watcher is not null || _watcherFailed)
        {
            return;
        }
        try
        {
            _watcher = PointerWatcher.Start(() => _queue.TryEnqueue(_onActivity), error => _queue.TryEnqueue(() =>
            {
                if (_disposed)
                {
                    return;
                }
                // The effects stay at the last position and follow pen and touch on the canvases.
                _watcherFailed = true;
                Counters.WatcherFailures++;
                _report(new OverlayEvent.Failure("Follow the pointer for Spotlight and Cursor halo", error));
            }));
            Counters.WatcherStarts++;
        }
        catch (InvalidOperationException error)
        {
            _watcherFailed = true;
            Counters.WatcherFailures++;
            _report(new OverlayEvent.Failure("Follow the pointer for Spotlight and Cursor halo", error));
        }
    }

    private void StopWatching()
    {
        _watcherFailed = false;
        _watcher?.Dispose();
        _watcher = null;
    }

    /// <summary>
    /// The pointer watcher has news. If nothing was shown in this composition frame it is shown now; otherwise the next frame
    /// takes it, and until then the watcher stays quiet, so a fast mouse costs at most one call per frame here.
    /// </summary>
    private void OnActivity()
    {
        if (_disposed || _watcher is null)
        {
            return;
        }
        if (_appliedThisFrame)
        {
            _pending = true;
            StartTicking();
            return;
        }
        double now = Now();
        if (TakeActivity(now))
        {
            Show(now);
        }
    }

    /// <summary>Reads the watcher's activity into the effects' state; false when there was none.</summary>
    private bool TakeActivity(double now)
    {
        if (_watcher is not { } watcher)
        {
            return false;
        }
        PointerActivity activity = watcher.Take();
        if (activity.IsEmpty)
        {
            return false;
        }
        Counters.Activity++;
        if (activity.Moved)
        {
            _penPointer = null;
            _state.NoteMove(now);
        }
        if (activity.Click is { } click)
        {
            Counters.Clicks += activity.Clicks;
            _penPointer = null;
            double clicked = activity.ClickTimestamp / (double)Stopwatch.Frequency;
            _state.NoteClick(Math.Min(now, clicked), new Annotator.Core.Point(click.X, click.Y), SystemMotion.ReducesMotion());
        }
        return true;
    }

    /// <summary>Shows activity now if nothing was shown in this composition frame, else on the next frame.</summary>
    private void Show(double now)
    {
        if (!_appliedThisFrame)
        {
            Apply(now);
            _appliedThisFrame = true;
        }
        else
        {
            _pending = true;
        }
        StartTicking();
        ScheduleTimer(now);
    }

    /// <summary>A setting or a timer: show the effects as they are now and plan the next change.</summary>
    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }
        double now = Now();
        Apply(now);
        if (_state.NeedsFrames(now))
        {
            StartTicking();
        }
        ScheduleTimer(now);
        ReleaseWhenIdle(now);
    }

    /// <summary>With every effect off and nothing fading, the layers give back their surfaces, once.</summary>
    private void ReleaseWhenIdle(double now)
    {
        if (_released || _state.Watching || _state.NeedsFrames(now) || _shownAny)
        {
            return;
        }
        _released = true;
        foreach ((PointerEffectsLayer layer, _) in _layers)
        {
            layer.ReleaseIdleResources();
        }
    }

    private void OnFrame(double now)
    {
        if (_disposed)
        {
            return;
        }
        _appliedThisFrame = false;
        bool animating = _state.NeedsFrames(now);
        if (animating)
        {
            _state.Advance(now);
        }
        // An animated click that just ended shows the resting halo once more; a static emphasis ends by the timer.
        bool clickEnded = _clickShown && _state.ClickProgress(now) is null;
        if (_pending || animating || clickEnded)
        {
            if (_pending)
            {
                _pending = false;
                TakeActivity(now);
                ScheduleTimer(now);
            }
            Apply(now);
            _appliedThisFrame = true;
            return;
        }
        // A frame with nothing to show: the pointer rests and nothing moves, so the clock stops.
        _ticking = false;
        _clock.Stop();
        ReleaseWhenIdle(now);
    }

    private void StartTicking()
    {
        if (!_ticking)
        {
            _ticking = true;
            _clock.Start();
        }
    }

    private void ScheduleTimer(double now)
    {
        _timer.Stop();
        if (_state.NextChange(now) is { } next)
        {
            _timer.Interval = TimeSpan.FromSeconds(Math.Max(0.001, next - now + 0.001));
            _timer.Start();
        }
    }

    /// <summary>Shows Spotlight and the halo on every display at the pointer, or hides them.</summary>
    private void Apply(double now)
    {
        Counters.Applies++;
        if (_penPointer is { } pen)
        {
            _pointer = pen;
        }
        else if (PointerWatcher.CursorPosition() is { } cursor)
        {
            _pointer = cursor;
        }
        Counters.LastPointer = _pointer;
        bool spotlight = _state.SpotlightVisible;
        bool halo = _state.HaloVisible(now);
        double? progress = halo ? _state.ClickProgress(now) : null;
        System.Drawing.Point click = _state.ClickLocation is { } location
            ? new System.Drawing.Point((int)location.X, (int)location.Y) : _pointer;
        bool reduces = progress is not null && SystemMotion.ReducesMotion();
        double radius = _state.SpotlightRadius;
        double intensity = _state.Spotlight.Intensity;
        Annotator.Core.CursorHighlightSettings settings = _state.Settings;
        if (spotlight || halo)
        {
            Counters.MostLayersShown = Math.Max(Counters.MostLayersShown, _layers.Count);
        }
        foreach ((PointerEffectsLayer layer, PixelRect bounds) in _layers)
        {
            double x = _pointer.X - bounds.Left, y = _pointer.Y - bounds.Top;
            if (spotlight)
            {
                layer.ShowSpotlight(x, y, radius, intensity);
            }
            else
            {
                layer.HideSpotlight();
            }
            if (halo)
            {
                Exception? before = layer.Failure;
                layer.ShowHalo(settings, x, y, progress, click.X - bounds.Left, click.Y - bounds.Top, reduces);
                if (layer.Failure is { } failure && before is null)
                {
                    _report(new OverlayEvent.Failure("Show the cursor halo", failure));
                }
            }
            else
            {
                layer.HideHalo();
            }
        }
        _clickShown = progress is not null;
        _shownAny = spotlight || halo;
        _released &= !_shownAny;
        Counters.SpotlightShown = spotlight;
        Counters.HaloShown = halo;
    }
}
