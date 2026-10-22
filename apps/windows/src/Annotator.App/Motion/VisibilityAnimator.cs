using System.Diagnostics;
using Annotator.App.Overlay;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Platform;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Platform.Overlays;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Motion;
using Annotator.Rendering.Sharing;
using Annotator.Workspace;
using Annotator.Workspace.Motion;
using Windows.UI.Composition;
using SystemDispatcherQueue = Windows.System.DispatcherQueue;
using SystemDispatcherQueueTimer = Windows.System.DispatcherQueueTimer;

namespace Annotator.App.Motion;

/// <summary>
/// The overlay thread's part of showing and hiding drawings and tools with motion (spec 009, WIN-MOT-02), the Windows
/// counterpart of the Mac's WorkspaceVisibilityAnimator: for each <see cref="VisibilityTransition"/> it snapshots every
/// visible object and the toolbar, plans the Mac's choreography with <see cref="VisibilityChoreographer"/> and plays it
/// with compositor animations on each overlay's <see cref="VisibilityStage"/>, above the hidden canvas. A bounded
/// deadline, not a compositor callback, ends each transition, so a locked or sleeping display cannot hold it; the app
/// thread then completes it in the workspace, and the next frame shows the live canvas and removes the snapshots in one
/// composition commit. Nothing runs between transitions.
/// </summary>
internal sealed class VisibilityAnimator : IDisposable
{
    /// <summary>How long a show waits for the toolbar's snapshot before it starts without it.</summary>
    private static readonly TimeSpan ToolbarWait = TimeSpan.FromMilliseconds(250);

    /// <summary>How long the toolbar snapshot stays after a show, until the real toolbar is surely on screen above it.</summary>
    private static readonly TimeSpan ToolbarLinger = TimeSpan.FromMilliseconds(60);

    /// <summary>Calibrated white 0.08, the Mac's blackboard, as the live canvas fills it.</summary>
    private static readonly RGBA Blackboard = new(0x1A / 255.0, 0x1A / 255.0, 0x1A / 255.0);

    private static readonly RGBA Whiteboard = new(1, 1, 1);

    private readonly SystemDispatcherQueue _queue;
    private readonly Action<OverlayEvent> _report;
    private readonly ITextMeasurer _measurer;
    private readonly Func<ISystemColorResolver> _colors;
    private readonly VisibilityChoreographer _choreographer = new();
    private readonly Dictionary<OverlayWindow, VisibilityStage> _stages = [];
    private readonly Dictionary<string, Placed> _images = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MotionDisplay> _displays = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (VisibilityStage Stage, ParticleSource Source)> _particles = new(StringComparer.Ordinal);
    private readonly List<(VisibilityStage Stage, string Key, ParticleSource Source, ParticlePlan Plan, LayerMotion? Motion)> _pendingBursts = [];
    private readonly List<(Placed Placed, LayerMotion Motion)> _blurs = [];
    private FrameClock? _clock;
    private MotionAnimations? _animations;
    private SceneBuilder? _builder;
    private Placed? _toolbar;
    private Placed? _lingering;
    private VisibilityTransition? _transition;
    private VisibilityPlan? _plan;
    private ulong _chain;
    private double _started;
    /// <summary>Whether <see cref="_started"/> is the compositor's start of the motion rather than the commit's.</summary>
    private bool _measured;
    /// <summary>Seconds after <see cref="_started"/> when the deadline ends the transition.</summary>
    private double _end;
    private bool _holding;
    private bool _toolbarSettled;
    private SystemDispatcherQueueTimer? _deadline;
    private SystemDispatcherQueueTimer? _wait;
    private SystemDispatcherQueueTimer? _linger;
    private OverlayFrame? _frame;
    private IReadOnlyDictionary<OverlayWindow, IOverlayCanvas>? _canvases;
    private GraphicsDevices? _devices;
    private bool _disposed;
    /// <summary>The transition whose motion ran to its deadline; it completes rather than being cancelled.</summary>
    private ulong _finished;

    public VisibilityAnimator(SystemDispatcherQueue queue, Action<OverlayEvent> report, ITextMeasurer measurer, Func<ISystemColorResolver> colors)
    {
        _queue = queue;
        _report = report;
        _measurer = measurer;
        _colors = colors;
    }

    /// <summary>The region sharing output, which plays the same motion (Mac SharedWorkspaceTransition), or null.</summary>
    public IVisibilityMirror? Mirror { get; set; }

    /// <summary>Snapshots on every overlay now, for tests and measurements.</summary>
    public int SnapshotCount => _stages.Values.Sum(stage => stage.ImageCount);

    /// <summary>The plan that plays, or null.</summary>
    public VisibilityPlan? Plan => _plan;

    /// <summary>Animations and keyframes created so far, for measurements.</summary>
    public (int Animations, int Keyframes) Created => _animations is { } animations ? (animations.Created, animations.Keyframes) : (0, 0);

    /// <summary>Seconds on the steady clock that compositor delays count from.</summary>
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>Called after the canvases presented <paramref name="frame"/>, in the same work item.</summary>
    public void Presented(OverlayFrame frame, IReadOnlyDictionary<OverlayWindow, IOverlayCanvas> canvases, GraphicsDevices? devices)
    {
        if (_disposed)
        {
            return;
        }
        _frame = frame;
        _canvases = canvases;
        if (!ReferenceEquals(devices, _devices))
        {
            _devices = devices;
            foreach (VisibilityStage stage in _stages.Values)
            {
                stage.ReplaceDevices(devices);
            }
        }
        VisibilityTransition? transition = frame.Workspace.Visibility;
        if (transition is null)
        {
            if (_transition is { } ended)
            {
                End(completedShow: ended.Showing && !frame.Workspace.WorkspaceHidden);
                Mirror?.End(completed: _finished == ended.Serial);
            }
            return;
        }
        if (_transition is { } current && current.Serial == transition.Serial)
        {
            if (_holding)
            {
                if (ToolbarImageReady(frame) is not null)
                {
                    Start(transition);
                }
            }
            else if (!_toolbarSettled)
            {
                AttachToolbar(transition);
            }
            return;
        }
        var fresh = _transition is null;
        if (fresh)
        {
            _chain = transition.Serial;
            ClearLingering();
        }
        _transition = transition;
        StopTimer(ref _wait);
        StopTimer(ref _deadline);
        if (_devices is null || canvases.Count == 0)
        {
            // Without devices there is nothing to draw the motion with: finish at once.
            _report(new OverlayEvent.VisibilityFinished(transition.Serial));
            return;
        }
        if (transition.Showing && fresh && NeedsToolbarSnapshot(transition.Style) && ToolbarImageReady(frame) is null)
        {
            // Everything is still hidden, so waiting for the toolbar's snapshot costs nothing visible.
            _holding = true;
            _wait = Timer(ToolbarWait, () =>
            {
                if (_holding && _transition?.Serial == transition.Serial)
                {
                    Start(transition);
                }
            });
            return;
        }
        Start(transition);
    }

    /// <summary>The overlays are about to be destroyed: release every visual under their roots.</summary>
    public void OverlaysChanging()
    {
        if (_transition is not null)
        {
            Mirror?.End(completed: false);
        }
        ClearAll();
        foreach (VisibilityStage stage in _stages.Values)
        {
            stage.Dispose();
        }
        _stages.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        OverlaysChanging();
        _clock?.Dispose();
        _animations?.Clear();
        _disposed = true;
    }

    private static bool NeedsToolbarSnapshot(VisibilityAnimationStyle style) =>
        style is not (VisibilityAnimationStyle.Fade or VisibilityAnimationStyle.None);

    private ToolbarImage? ToolbarImageReady(OverlayFrame frame) =>
        frame.Toolbar?.Image is { } image && image.Chain == _chain ? image : null;

    private void Start(VisibilityTransition transition)
    {
        _holding = false;
        StopTimer(ref _wait);
        OverlayFrame frame = _frame!;
        var now = Now;
        MotionRequest request;
        List<Source> sources;
        try
        {
            (request, sources) = Request(frame, transition, now);
        }
        catch (Exception error) when (error is CanvasError or ArgumentException or InvalidOperationException)
        {
            _report(new OverlayEvent.Failure("Prepare the show and hide motion", error));
            _report(new OverlayEvent.VisibilityFinished(transition.Serial));
            return;
        }
        var reversing = _choreographer.WouldReverse(request);
        if (!reversing)
        {
            ClearImages();
        }
        VisibilityPlan plan = _choreographer.Plan(request);
        _plan = plan;
        _toolbarSettled = false;
        foreach (var message in plan.Messages)
        {
            _report(new OverlayEvent.Status(message));
        }
        var keep = new HashSet<string>(StringComparer.Ordinal);
        _pendingBursts.Clear();
        _blurs.Clear();
        List<SharedMotionSnapshot>? mirrored = Mirror is null ? null : new(plan.Snapshots.Count);
        foreach (SnapshotPlan item in plan.Snapshots)
        {
            keep.Add(item.Snapshot.Key);
            if (item.Particles is { } particles && !_images.ContainsKey(item.Snapshot.Key)
                && Particles(item.Snapshot.Key, sources) is var (stage, particleSource))
            {
                _pendingBursts.Add((stage, item.Snapshot.Key, particleSource, particles, item.Motion));
                Mirrored(mirrored, item, sources, particles: true, blurred: false);
                continue;
            }
            if (!_images.TryGetValue(item.Snapshot.Key, out Placed? placed))
            {
                Source? source = sources.Find(candidate => candidate.Key == item.Snapshot.Key);
                if (source is null || Render(source, item.Blur) is not { } rendered)
                {
                    continue;
                }
                placed = rendered;
                _images[item.Snapshot.Key] = placed;
            }
            Play(placed, item, shift: 0);
            if (item.Blur && item.Motion.Blur is not null)
            {
                _blurs.Add((placed, item.Motion));
            }
            Mirrored(mirrored, item, sources, particles: false, blurred: placed.Blurred && item.Motion.Blur is not null);
        }
        foreach (var key in _images.Keys.Where(key => !keep.Contains(key)).ToList())
        {
            Placed stale = _images[key];
            stale.Stage.Remove(stale.Image);
            _images.Remove(key);
        }
        foreach (var key in _particles.Keys.Where(key => !keep.Contains(key) && key != ToolbarBurst).ToList())
        {
            _particles[key].Source.Dispose();
            _particles.Remove(key);
        }
        // Until the compositor reports that this work item's animations started, the motion counts from here.
        _started = Now;
        _measured = false;
        _end = plan.Deadline;
        StartBursts(keep);
        StartBlurs();
        // The shared region plays the same plan in this commit (Mac beginWorkspaceAnimation, add and prepare).
        if (mirrored is not null)
        {
            Mirror!.Play(transition.Serial, plan, mirrored, _started, Now);
        }
        AttachToolbar(transition);
        // The compositor starts the animations only when it takes this commit, which can be tens of milliseconds later
        // than now while it uploads new snapshots. A probe animation in the same commit says when that happened, so the
        // blur, the toolbar's late part and the deadline follow the compositor's timeline (the Mac's begin time).
        if (!Probe(transition.Serial))
        {
            Measured(transition.Serial, _started);
        }
        _deadline = Timer(TimeSpan.FromSeconds(_end), () => Finish(transition.Serial));
    }

    /// <summary>A probe of how long a commit takes to start, from its own one-millisecond animation.</summary>
    private static readonly TimeSpan ProbeDuration = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// Adds a one-millisecond animation to this work item's commit and calls <see cref="Measured"/> when the compositor
    /// finished it. Returns false when there is no stage to probe on.
    /// </summary>
    private bool Probe(ulong serial)
    {
        if (_stages.Values.FirstOrDefault(stage => stage.ImageCount > 0) is not { } stage)
        {
            return false;
        }
        Compositor compositor = stage.Compositor;
        CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        using (ScalarKeyFrameAnimation tick = compositor.CreateScalarKeyFrameAnimation())
        {
            tick.InsertKeyFrame(1, 1);
            tick.Duration = ProbeDuration;
            stage.Visual.Properties.InsertScalar(ProbeProperty, 0);
            stage.Visual.Properties.StartAnimation(ProbeProperty, tick);
        }
        batch.End();
        batch.Completed += (_, _) =>
        {
            batch.Dispose();
            if (!_disposed && !_measured && _transition?.Serial == serial)
            {
                Measured(serial, Math.Max(_started, Now - ProbeDuration.TotalSeconds));
            }
        };
        return true;
    }

    private const string ProbeProperty = "motionProbe";

    /// <summary>
    /// The motion started on the compositor at <paramref name="started"/>: the CPU-drawn parts (Soft focus blurs) and the
    /// deadline count from there, and the app hears when it began.
    /// </summary>
    private void Measured(ulong serial, double started)
    {
        _started = started;
        _measured = true;
        _report(new OverlayEvent.VisibilityStarted(serial, (long)(_started * Stopwatch.Frequency), _plan?.TotalDuration ?? 0));
        if (_blurs.Count > 0 && UpdateBlurs(Now))
        {
            _clock ??= new FrameClock(_queue, OnFrame);
            _clock.Start();
        }
        RearmDeadline(serial);
    }

    /// <summary>Restarts the deadline so that it fires <see cref="_end"/> seconds after <see cref="_started"/>.</summary>
    private void RearmDeadline(ulong serial)
    {
        if (_deadline is not { } deadline)
        {
            return;
        }
        deadline.Stop();
        deadline.Interval = TimeSpan.FromSeconds(Math.Max(0.001, _end - (Now - _started)));
        deadline.Start();
    }

    private void Finish(ulong serial)
    {
        if (_transition?.Serial == serial)
        {
            if (!_measured)
            {
                // The compositor never said (a locked or sleeping display): the motion counted from the commit.
                _measured = true;
                _report(new OverlayEvent.VisibilityStarted(serial, (long)(_started * Stopwatch.Frequency), _plan?.TotalDuration ?? 0));
            }
            _finished = serial;
            _report(new OverlayEvent.VisibilityFinished(serial));
        }
    }

    /// <summary>Seconds of the motion's timeline that have passed: none until the compositor has started it.</summary>
    private double Elapsed => _measured ? Math.Max(0, Now - _started) : 0;

    /// <summary>Puts the toolbar's part on stage once its snapshot is there, or hands it to the real toolbar.</summary>
    private void AttachToolbar(VisibilityTransition transition)
    {
        if (_plan is not { Toolbar: { } toolbar } plan || _toolbarSettled)
        {
            return;
        }
        var elapsed = Elapsed;
        if (toolbar.Kind == ToolbarSnapshotKind.None)
        {
            Fallback(transition, toolbar, elapsed, report: false);
            return;
        }
        if (_toolbar is null)
        {
            if (ToolbarImageReady(_frame!) is not { } image)
            {
                // Not taken yet. A show starts only with it or after waiting for it; a hide waits while the real toolbar
                // still shows, and the app posts a frame as soon as the snapshot is ready or failed.
                if (transition.Showing)
                {
                    Fallback(transition, toolbar, elapsed, report: true);
                }
                return;
            }
            // Soft focus blurs the toolbar too (Mac: the toolbar snapshot's VisibilityImageView enableBlur).
            var blur = toolbar.Kind == ToolbarSnapshotKind.Pose && toolbar.SnapshotMotion?.Blur is not null;
            if (image.Pixels is null || _frame!.Toolbar is not { } state || Place(state, image, blur) is not { } placed)
            {
                Fallback(transition, toolbar, elapsed, report: true);
                return;
            }
            _toolbar = placed;
        }
        _toolbarSettled = true;
        PlayToolbar(_toolbar, toolbar, elapsed);
        _report(new OverlayEvent.ToolbarCovered(transition.Serial));
        // A late toolbar can end after the plan's deadline; wait for it.
        var end = ToolbarEnd(toolbar) + Math.Max(0, elapsed - toolbar.Delay);
        if (end + 0.02 > _end)
        {
            _end = end + 0.02;
            RearmDeadline(transition.Serial);
        }
    }

    private void Fallback(VisibilityTransition transition, ToolbarPlan toolbar, double elapsed, bool report)
    {
        ReleaseToolbar();
        _toolbarSettled = true;
        if (report)
        {
            _report(new OverlayEvent.Status(VisibilityChoreographer.ToolbarFallbackMessage));
        }
        _report(new OverlayEvent.ToolbarFallback(transition.Serial, toolbar.RealMotion, Math.Max(0, toolbar.RealMotion.Started - elapsed)));
        Mirror?.UseRealToolbar();
    }

    private static double ToolbarEnd(ToolbarPlan toolbar) =>
        Math.Max(toolbar.RealMotion.EndTime, Math.Max(toolbar.SnapshotMotion?.EndTime ?? 0,
            Math.Max(toolbar.Poof?.EndTime ?? 0, toolbar.Particles?.EndTime ?? 0)));

    /// <summary>Plays one snapshot's plan on its stage.</summary>
    private void Play(Placed placed, SnapshotPlan item, double shift)
    {
        placed.Stage.ClearExtras(placed.Image);
        placed.Stage.Animate(placed.Image, item.Motion, shift);
        if (item.Trail is { } trail)
        {
            try
            {
                MagicEffects.PlayTrails(placed.Stage, placed.Image, trail, item.Snapshot.Display, item.Motion, shift);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // Mac: "Light trails could not start. Shapes will still move."
                _report(new OverlayEvent.Status("Light trails could not start. Shapes will still move."));
                StartupLog.Note("magic-trail", error.Message);
            }
            MagicEffects.PlaySparks(placed.Stage, placed.Image, trail, item.Snapshot.Display, item.Motion, shift);
        }
    }

    private void PlayToolbar(Placed placed, ToolbarPlan toolbar, double shift)
    {
        placed.Stage.ClearExtras(placed.Image);
        var particles = false;
        switch (toolbar.Kind)
        {
            case ToolbarSnapshotKind.Gather:
            case ToolbarSnapshotKind.Pose:
                placed.Stage.Animate(placed.Image, toolbar.SnapshotMotion!, shift);
                if (toolbar.Pulses.Count > 0)
                {
                    placed.Stage.Pulse(placed.Image, toolbar.Pulses, shift);
                }
                if (toolbar.SnapshotMotion!.Blur is not null && placed.Blurred)
                {
                    _blurs.Add((placed, toolbar.SnapshotMotion));
                    UpdateBlurs(_measured ? Now : _started);
                    if (_measured)
                    {
                        _clock ??= new FrameClock(_queue, OnFrame);
                        _clock.Start();
                    }
                }
                break;
            case ToolbarSnapshotKind.Poof:
                MagicEffects.PlayPoof(placed.Stage, placed.Image, toolbar.Poof!, shift);
                break;
            case ToolbarSnapshotKind.Particles:
                particles = ToolbarParticles(placed, toolbar.Particles!);
                if (!particles)
                {
                    placed.Stage.Animate(placed.Image, toolbar.RealMotion, shift);
                }
                break;
        }
        if (Mirror is { } mirror && _frame?.Toolbar?.Image is { Pixels: not null } image && placed.Display is { } display && placed.Rest is { } rest)
        {
            mirror.PlayToolbar(toolbar, image, display, rest, placed.Blurred && toolbar.SnapshotMotion?.Blur is not null, particles, _started, shift, Now);
        }
    }

    /// <summary>Ends the motion when the workspace completed or cancelled it: snapshots leave in this commit.</summary>
    private void End(bool completedShow)
    {
        StopTimer(ref _deadline);
        StopTimer(ref _wait);
        _choreographer.Clear();
        _transition = null;
        _plan = null;
        _holding = false;
        ClearImages();
        if (completedShow && _toolbar is { } toolbar)
        {
            // Dissolve drew the toolbar with particles; its snapshot covers the hand-off as the bursts leave.
            toolbar.Image.Image.IsVisible = true;
            // The real toolbar appears above it on the app thread; the snapshot goes once that is surely on screen.
            _toolbar = null;
            ClearLingering();
            _lingering = toolbar;
            _linger = Timer(ToolbarLinger, ClearLingering);
        }
        else
        {
            ReleaseToolbar();
        }
        _builder?.Reset();
    }

    private void ClearAll()
    {
        StopTimer(ref _deadline);
        StopTimer(ref _wait);
        _choreographer.Clear();
        _transition = null;
        _plan = null;
        _holding = false;
        ClearImages();
        ReleaseToolbar();
        ClearLingering();
    }

    private void ClearImages()
    {
        foreach (Placed placed in _images.Values)
        {
            placed.Stage.Remove(placed.Image);
        }
        _images.Clear();
        _blurs.Clear();
        _clock?.Stop();
        foreach (VisibilityStage stage in _stages.Values)
        {
            stage.ClearBursts();
        }
        foreach ((_, ParticleSource source) in _particles.Values)
        {
            source.Dispose();
        }
        _particles.Clear();
    }

    /// <summary>The key of the toolbar's burst among the snapshots' bursts.</summary>
    private const string ToolbarBurst = "\u0000toolbar";

    /// <summary>
    /// Dissolve: the snapshot's pixels as a particle texture on its overlay (Mac ParticleBurstView), reused on a reversal.
    /// Null when particles cannot start; the snapshot then plays its motion as an image, as the Mac falls back.
    /// </summary>
    private (VisibilityStage Stage, ParticleSource Source)? Particles(string key, List<Source> sources)
    {
        if (_particles.TryGetValue(key, out var existing))
        {
            return existing;
        }
        if (sources.Find(candidate => candidate.Key == key) is not { } source)
        {
            return null;
        }
        try
        {
            ParticleBursts bursts = source.Stage.UseBursts(Bounds(source.Stage));
            ParticleSource texture = source.Color is { } color
                ? bursts.Color(color, source.Pixels)
                : source.Stage.ParticleLists(bursts, source.Lists!, source.Pixels);
            _particles[key] = (source.Stage, texture);
            return (source.Stage, texture);
        }
        catch (Exception error) when (error is NativeCallException or InvalidOperationException or ArgumentException or OutOfMemoryException)
        {
            _report(new OverlayEvent.Status(DissolveFallbackMessage));
            StartupLog.Note("dissolve", error.Message);
            return null;
        }
    }

    /// <summary>Mac: "Dissolve could not start. Using the slide animation instead."</summary>
    private const string DissolveFallbackMessage = "Dissolve could not start. Using the slide animation instead.";

    /// <summary>Starts this transition's bursts at the motion's start and draws their first frame in this commit.</summary>
    private void StartBursts(HashSet<string> keep)
    {
        if (_toolbar is not null && _particles.ContainsKey(ToolbarBurst))
        {
            keep.Add(ToolbarBurst);
        }
        foreach (VisibilityStage stage in _stages.Values)
        {
            stage.Bursts?.Retain(keep);
        }
        foreach (var (stage, key, source, plan, motion) in _pendingBursts)
        {
            stage.Bursts!.Set(key, source, plan, motion, _started);
        }
        _pendingBursts.Clear();
        PrepareBursts();
    }

    /// <summary>Dissolve's toolbar: its snapshot dissolves into particles where it stands (Mac toolbar ParticleBurstView).</summary>
    private bool ToolbarParticles(Placed placed, ParticlePlan plan)
    {
        if (_frame?.Toolbar?.Image is not { Pixels: { } pixels } image)
        {
            return false;
        }
        try
        {
            if (!_particles.TryGetValue(ToolbarBurst, out var entry))
            {
                ParticleBursts bursts = placed.Stage.UseBursts(Bounds(placed.Stage));
                entry = (placed.Stage, bursts.Bitmap(pixels, image.Width * 4, placed.Image.Pixels));
                _particles[ToolbarBurst] = entry;
            }
            entry.Stage.Bursts!.Set(ToolbarBurst, entry.Source, plan, null, _started);
            placed.Image.Image.IsVisible = false;
            PrepareBursts();
            return true;
        }
        catch (Exception error) when (error is NativeCallException or InvalidOperationException or ArgumentException or OutOfMemoryException)
        {
            _report(new OverlayEvent.Status("The toolbar particle animation could not start. Using a fade instead."));
            StartupLog.Note("dissolve", error.Message);
            return false;
        }
    }

    /// <summary>Sizes each overlay's particle layer, draws the first frame and runs the frame clock while bursts move.</summary>
    private void PrepareBursts()
    {
        var moving = false;
        var now = Now;
        foreach (VisibilityStage stage in _stages.Values)
        {
            try
            {
                moving |= stage.PrepareBursts(now);
            }
            catch (Exception error) when (error is NativeCallException or InvalidOperationException)
            {
                _report(new OverlayEvent.Failure("Show the particles", error));
                stage.ClearBursts();
            }
        }
        if (moving)
        {
            _clock ??= new FrameClock(_queue, OnFrame);
            _clock.Start();
        }
    }

    /// <summary>Soft focus: draws each blur's first pose; the blurs move once the compositor started the motion.</summary>
    private void StartBlurs()
    {
        if (_blurs.Count > 0)
        {
            UpdateBlurs(_started);
        }
    }

    /// <summary>
    /// Redraws each soft-focus snapshot with its blur now (Mac: the layer's animated CIGaussianBlur radius, in points, which
    /// is the deviation). Composition animates the rest of the motion. Returns whether a blur still changes.
    /// </summary>
    private bool UpdateBlurs(double now)
    {
        var moving = false;
        var time = now - _started;
        foreach ((Placed placed, LayerMotion motion) in _blurs)
        {
            var local = motion.EffectiveDuration > 0 ? (time - motion.Started) / motion.EffectiveDuration : 1;
            try
            {
                placed.Stage.SetBlur(placed.Image, motion.Blur!.At(local) * placed.Stage.Scale);
            }
            catch (Exception error) when (error is NativeCallException or InvalidOperationException)
            {
                _report(new OverlayEvent.Failure("Blur the snapshots", error));
            }
            moving |= time < motion.EndTime;
        }
        return moving;
    }

    private void OnFrame(double now)
    {
        var moving = _blurs.Count > 0 && UpdateBlurs(now);
        moving |= Mirror?.Frame(now, _started) ?? false;
        foreach (VisibilityStage stage in _stages.Values)
        {
            try
            {
                moving |= stage.Bursts?.Render(now) ?? false;
            }
            catch (Exception error) when (error is NativeCallException or InvalidOperationException)
            {
                _report(new OverlayEvent.Failure("Show the particles", error));
                stage.ClearBursts();
            }
        }
        if (!moving)
        {
            _clock?.Stop();
        }
    }

    /// <summary>The size in pixels of the overlay <paramref name="stage"/> is on.</summary>
    private PixelRect Bounds(VisibilityStage stage)
    {
        foreach ((OverlayWindow overlay, VisibilityStage candidate) in _stages)
        {
            if (ReferenceEquals(candidate, stage))
            {
                return new PixelRect(0, 0, overlay.Display.Bounds.Width, overlay.Display.Bounds.Height);
            }
        }
        throw new InvalidOperationException("The stage has no overlay.");
    }

    private void ReleaseToolbar()
    {
        if (_toolbar is { } toolbar)
        {
            toolbar.Stage.Remove(toolbar.Image);
            _toolbar = null;
        }
    }

    private void ClearLingering()
    {
        StopTimer(ref _linger);
        if (_lingering is { } lingering)
        {
            lingering.Stage.Remove(lingering.Image);
            _lingering = null;
        }
    }

    /// <summary>The snapshots of every display (board, then objects in stacking order) and the toolbar.</summary>
    private (MotionRequest Request, List<Source> Sources) Request(OverlayFrame frame, VisibilityTransition transition, double now)
    {
        _builder ??= new SceneBuilder(_measurer);
        _displays.Clear();
        var snapshots = new List<MotionSnapshot>();
        var sources = new List<Source>();
        var version = new System.Text.StringBuilder();
        WorkspaceSnapshot workspace = frame.Workspace;
        var drawing = workspace.Mode == WorkspaceMode.Draw;
        foreach ((OverlayWindow overlay, _) in _canvases!)
        {
            if (workspace.Display(new DisplayId(overlay.Display.DeviceName)) is not { } display)
            {
                continue;
            }
            DisplayDescriptor descriptor = display.Display;
            var motionDisplay = new MotionDisplay(display.Id.Value, descriptor.DesktopFrame, descriptor.WorkArea, descriptor.Scale);
            _displays[display.Id.Value] = motionDisplay;
            CanvasDocument document = display.Document;
            version.Append(display.Id.Value).Append('|').Append(descriptor.DesktopFrame).Append('|').Append(descriptor.WorkArea).Append('|')
                .Append(descriptor.Scale).Append('|').Append(document.Identity).Append('|').Append(document.Generation).Append('|')
                .Append(display.Board).Append(';');
            if (document.Elements.IsEmpty && display.Board == Board.Desktop)
            {
                continue;
            }
            VisibilityStage stage = Stage(overlay, descriptor.Scale);
            Rect canvas = descriptor.Bounds;
            var scale = descriptor.Scale;
            var identity = document.Identity.ToString("D").ToUpperInvariant();
            if (display.Board != Board.Desktop)
            {
                PixelRect pixels = Pixels(canvas, scale);
                var key = $"{identity}/board";
                snapshots.Add(new MotionSnapshot(key, null, motionDisplay, Crop(pixels, scale), canvas.Center, (long)pixels.Width * pixels.Height));
                sources.Add(new Source(key, stage, pixels, null, display.Board == Board.Whiteboard ? Whiteboard : Blackboard));
            }
            ElementList ordered = ElementStacking.Ordered(document.Elements);
            foreach (ref readonly Element element in ordered)
            {
                var selected = drawing && document.Selection.Contains(element.Id);
                var padding = selected ? 32.0 : 0;
                Rect bounds = RectMath.Outset(_builder.DrawingBounds(element), padding, padding);
                if (RectMath.Intersection(bounds, canvas) is not { } clipped || RectMath.IsEmpty(clipped))
                {
                    continue;
                }
                PixelRect pixels = Pixels(clipped, scale);
                if (pixels.Width <= 0 || pixels.Height <= 0)
                {
                    continue;
                }
                var key = $"{identity}/{element.Id.ToString("D").ToUpperInvariant()}";
                snapshots.Add(new MotionSnapshot(key, element.Id, motionDisplay, Crop(pixels, scale), element.Frame.Center, (long)pixels.Width * pixels.Height));
                DisplayList[] lists = selected
                    ? [_builder.List(element), _builder.Selection(ElementList.Create([element]), new HashSet<Guid> { element.Id })]
                    : [_builder.List(element)];
                sources.Add(new Source(key, stage, pixels, lists, null, element.Tool.IsProtection));
            }
        }
        MotionToolbar? toolbar = null;
        long toolbarPixels = 0;
        if (frame.Toolbar is { } state && _displays.Count > 0)
        {
            var bounds = new Rect(state.Frame.X, state.Frame.Y, state.Frame.Width, state.Frame.Height);
            MotionDisplay home = Home(bounds);
            WorkspacePreferences preferences = workspace.Preferences;
            toolbar = new MotionToolbar(bounds, home, preferences.ToolbarOrientation ?? ToolbarOrientation.Horizontal,
                                        preferences.ToolbarDockEdge ?? ToolbarDockEdge.Automatic);
            toolbarPixels = (long)state.Frame.Width * state.Frame.Height;
        }
        var request = new MotionRequest
        {
            Showing = transition.Showing,
            Style = transition.Style,
            Now = now,
            Snapshots = snapshots,
            Toolbar = toolbar,
            SceneVersion = version.ToString(),
            ToolbarPixels = toolbarPixels,
        };
        return (request, sources);
    }

    /// <summary>The display whose desktop frame holds most of <paramref name="desktop"/> (its center, else the first).</summary>
    private MotionDisplay Home(Rect desktop)
    {
        var center = desktop.Center;
        foreach (MotionDisplay display in _displays.Values)
        {
            if (display.DesktopFrame.Contains(center))
            {
                return display;
            }
        }
        return _displays.Values.First();
    }

    private Placed? Render(Source source, bool blur = false)
    {
        try
        {
            StageImage image = source.Color is { } color
                ? source.Stage.AddColor(color, source.Pixels)
                : blur
                    ? source.Stage.AddBlurredLists(source.Lists!, source.Pixels, (int)Math.Ceiling(VisibilityPose.BlurPadding * source.Stage.Scale))
                    : source.Stage.AddLists(source.Lists!, source.Pixels);
            return new Placed(source.Stage, image, Blurred: blur && source.Color is null);
        }
        catch (Exception error) when (error is NativeCallException or InvalidOperationException or ArgumentException or OutOfMemoryException)
        {
            // Mac: a failed snapshot is reported and that object shows or hides without its own motion.
            _report(new OverlayEvent.Status("Visibility animation unavailable: " + error.Message));
            return null;
        }
    }

    /// <summary>The toolbar snapshot on the overlay of its display, at the toolbar's own pixels; Soft focus can blur it.</summary>
    private Placed? Place(ToolbarState state, ToolbarImage image, bool blur)
    {
        var center = new Point(state.Frame.X + state.Frame.Width / 2.0, state.Frame.Y + state.Frame.Height / 2.0);
        foreach ((OverlayWindow overlay, _) in _canvases!)
        {
            PixelRect bounds = overlay.Display.Bounds;
            if (center.X < bounds.Left || center.X >= bounds.Right || center.Y < bounds.Top || center.Y >= bounds.Bottom)
            {
                continue;
            }
            VisibilityStage stage = Stage(overlay, overlay.Display.Scale);
            var pixels = PixelRect.FromSize(state.Frame.X - bounds.Left, state.Frame.Y - bounds.Top, image.Width, image.Height);
            try
            {
                return blur
                    ? new Placed(stage, stage.AddBlurredBitmap(image.Pixels, image.Width, image.Height, image.Width * 4, pixels,
                                                               (int)Math.Ceiling(VisibilityPose.BlurPadding * stage.Scale)), Blurred: true)
                    {
                        Display = overlay.Display.DeviceName,
                        Rest = pixels,
                    }
                    : new Placed(stage, stage.AddBitmap(image.Pixels, image.Width, image.Height, image.Width * 4, pixels))
                    {
                        Display = overlay.Display.DeviceName,
                        Rest = pixels,
                    };
            }
            catch (Exception error) when (error is NativeCallException or InvalidOperationException or ArgumentException or OutOfMemoryException)
            {
                _report(new OverlayEvent.Failure("Show the toolbar's motion", error));
                return null;
            }
        }
        return null;
    }

    private VisibilityStage Stage(OverlayWindow overlay, double scale)
    {
        if (!_stages.TryGetValue(overlay, out VisibilityStage? stage))
        {
            Compositor compositor = overlay.Root.Compositor;
            _animations ??= new MotionAnimations(compositor);
            stage = new VisibilityStage(compositor, overlay.Root, _devices, _colors(), scale, _animations);
            _stages[overlay] = stage;
        }
        return stage;
    }

    /// <summary>A rectangle in points as whole device pixels: the origin rounds down and the far corner up.</summary>
    private static PixelRect Pixels(Rect points, double scale) => new(
        (int)Math.Floor(points.X * scale), (int)Math.Floor(points.Y * scale),
        (int)Math.Ceiling(points.MaxX * scale), (int)Math.Ceiling(points.MaxY * scale));

    private static Rect Crop(PixelRect pixels, double scale) =>
        new(pixels.Left / scale, pixels.Top / scale, pixels.Width / scale, pixels.Height / scale);

    private SystemDispatcherQueueTimer Timer(TimeSpan interval, Action tick)
    {
        SystemDispatcherQueueTimer timer = _queue.CreateTimer();
        timer.Interval = interval <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : interval;
        timer.IsRepeating = false;
        timer.Tick += (sender, _) =>
        {
            sender.Stop();
            if (!_disposed)
            {
                tick();
            }
        };
        timer.Start();
        return timer;
    }

    private static void StopTimer(ref SystemDispatcherQueueTimer? timer)
    {
        timer?.Stop();
        timer = null;
    }

    /// <summary>One snapshot to render: object lists in display points, or a board color; covers and pixel effects are protection.</summary>
    private sealed record Source(string Key, VisibilityStage Stage, PixelRect Pixels, DisplayList[]? Lists, RGBA? Color, bool Protection = false);

    /// <summary>
    /// The shared region's copy of <paramref name="item"/> as the overlays play it (Mac sharedImage), added to
    /// <paramref name="mirrored"/> when the region is shared.
    /// </summary>
    private static void Mirrored(List<SharedMotionSnapshot>? mirrored, SnapshotPlan item, List<Source> sources, bool particles, bool blurred)
    {
        if (mirrored is not null && sources.Find(candidate => candidate.Key == item.Snapshot.Key) is { } source)
        {
            mirrored.Add(new SharedMotionSnapshot(item, source.Pixels, source.Lists, source.Color, source.Protection, particles, blurred));
        }
    }

    /// <summary>
    /// A snapshot on its stage; <paramref name="Blurred"/> when Soft focus can blur it. The toolbar's snapshot also knows its
    /// overlay's display and where it rests there (<see cref="Display"/>, <see cref="Rest"/>), for the shared region.
    /// </summary>
    internal sealed record Placed(VisibilityStage Stage, StageImage Image, bool Blurred = false)
    {
        public string? Display { get; init; }

        public PixelRect? Rest { get; init; }
    }
}
