using Annotator.Core;

namespace Annotator.Workspace.Motion;

/// <summary>A display as the visibility motion sees it: its size and work area in points (y down) and its desktop frame.</summary>
public sealed record MotionDisplay(string Id, Rect DesktopFrame, Rect WorkArea, double Scale)
{
    /// <summary>The display's size in points.</summary>
    public double Width => DesktopFrame.Width / Scale;

    public double Height => DesktopFrame.Height / Scale;

    /// <summary>A rectangle in points from the display's top-left corner, in the Mac's y-up space of this display.</summary>
    public MotionRect ToMotion(Rect local) => new(local.X, Height - local.Y - local.Height, local.Width, local.Height);

    public MotionPoint ToMotion(Point local) => new(local.X, Height - local.Y);

    /// <summary>A rectangle in desktop units, in this display's y-up points (it may lie outside the display).</summary>
    public MotionRect DesktopToMotion(Rect desktop) =>
        ToMotion(new Rect((desktop.X - DesktopFrame.X) / Scale, (desktop.Y - DesktopFrame.Y) / Scale, desktop.Width / Scale, desktop.Height / Scale));

    /// <summary>A y-up rectangle of this display back in points from its top-left corner.</summary>
    public Rect FromMotion(MotionRect rect) => new(rect.X, Height - rect.MaxY, rect.Width, rect.Height);

    public Point FromMotion(MotionPoint point) => new(point.X, Height - point.Y);

    /// <summary>The whole display, y-up.</summary>
    public MotionRect Screen => new(0, 0, Width, Height);

    /// <summary>The work area (the Mac's visible frame), y-up.</summary>
    public MotionRect VisibleFrame => ToMotion(WorkArea);
}

/// <summary>
/// One snapshot that takes part in a show or hide: an element (cropped to what it paints) or a display's board. Frames are
/// in points from the display's top-left corner.
/// </summary>
public sealed record MotionSnapshot(string Key, Guid? ElementId, MotionDisplay Display, Rect Frame, Point Center, long Pixels);

/// <summary>The toolbar as the visibility motion sees it.</summary>
public sealed record MotionToolbar(Rect DesktopFrame, MotionDisplay Display, ToolbarOrientation Orientation, ToolbarDockEdge DockEdge);

/// <summary>One request to show or hide (Mac <c>WorkspaceVisibilityAnimator.transition</c>).</summary>
public sealed record MotionRequest
{
    public required bool Showing { get; init; }

    /// <summary>The resolved style; Reduce Motion has already turned it into Fade.</summary>
    public required VisibilityAnimationStyle Style { get; init; }

    /// <summary>Seconds on the caller's steady clock.</summary>
    public required double Now { get; init; }

    /// <summary>The snapshots in drawing order: per display its board, then its elements in stacking order.</summary>
    public required IReadOnlyList<MotionSnapshot> Snapshots { get; init; }

    public MotionToolbar? Toolbar { get; init; }

    /// <summary>What a reversal must match: documents, generations, boards and display geometry.</summary>
    public required object SceneVersion { get; init; }

    /// <summary>Toolbar snapshot pixels, counted against the shared budget.</summary>
    public long ToolbarPixels { get; init; }
}

/// <summary>Dissolve particles of one snapshot or the toolbar.</summary>
public sealed record ParticlePlan(int Limit, double SweepAngle, double From, double To, double Started, double Duration)
{
    public double EndTime => Started + Duration;

    public double ProgressAt(double time) => ParticleField.Progress(From, To, Duration > 0 ? (time - Started) / Duration : 1);
}

/// <summary>Magic's toolbar point and spark burst.</summary>
public sealed record PoofPlan(double From, double To, double Started, double Duration)
{
    public double EndTime => Started + Duration;

    public double ProgressAt(double time) => MagicToolbarPoof.Progress(From, To, Duration > 0 ? (time - Started) / Duration : 1);

    public LayerState ImageStateAt(double time)
    {
        var pose = MagicToolbarPoof.PoseAt(ProgressAt(time));
        return new LayerState(MotionTransform.MakeScale(pose.Scale), pose.ImageOpacity);
    }
}

/// <summary>How one snapshot moves: its own style (boards fade for some styles), delay, timeline and extra effects.</summary>
public sealed record SnapshotPlan(
    MotionSnapshot Snapshot,
    VisibilityAnimationStyle ImageStyle,
    double Delay,
    LayerMotion Motion,
    MotionRect Envelope,
    MagicTrailGeometry? Trail,
    ParticlePlan? Particles,
    bool Blur);

/// <summary>What the toolbar snapshot does.</summary>
public enum ToolbarSnapshotKind
{
    /// <summary>The style plays on the real toolbar (Fade).</summary>
    None,

    /// <summary>Gather and Corners: it shrinks to its center, pulsing as gathered shapes land.</summary>
    Gather,

    /// <summary>Pop, Drop and Soft focus.</summary>
    Pose,

    /// <summary>Magic: a blue point and a spark burst.</summary>
    Poof,

    /// <summary>Dissolve: particles.</summary>
    Particles,
}

/// <summary>The toolbar's part of a show or hide.</summary>
public sealed record ToolbarPlan(
    ToolbarSnapshotKind Kind,
    double Delay,
    double Duration,
    ToolbarDockEdge Edge,
    LayerMotion RealMotion,
    LayerMotion? SnapshotMotion,
    PoofPlan? Poof,
    ParticlePlan? Particles,
    IReadOnlyList<double> Pulses,
    MotionRect Envelope,
    MotionRect Frame);

/// <summary>A planned show or hide. Times are seconds from <see cref="Started"/> on the caller's clock.</summary>
public sealed record VisibilityPlan(
    bool Showing,
    VisibilityAnimationStyle Style,
    bool Reversing,
    double Started,
    double TotalDuration,
    IReadOnlyList<SnapshotPlan> Snapshots,
    ToolbarPlan? Toolbar,
    IReadOnlyList<string> Messages)
{
    /// <summary>Seconds from the start until the completion deadline, as the Mac's finishByDeadline waits (20 ms margin).</summary>
    public double Deadline => TotalDuration + 0.02;
}

/// <summary>
/// Port of the planning in the Mac's <c>WorkspaceVisibilityAnimator</c>: snapshot ordering by distance, stagger, delays,
/// per-style timelines, envelopes, Corners routes and Dissolve particle budgets. A request in the opposite direction
/// while a plan still runs over the same scene reverses it from the current presentation state, with no new stagger.
/// It keeps the departure order and corner routes between a hide and its show, as the Mac animator does.
/// </summary>
public sealed class VisibilityChoreographer
{
    /// <summary>Transition images of every display share this pixel budget.</summary>
    public const long PixelBudget = 40_000_000;

    /// <summary>Larger boards show and hide at once.</summary>
    public const int ObjectLimit = 64;

    /// <summary>The toolbar snapshot's own limit.</summary>
    public const long ToolbarPixelLimit = 4_000_000;

    public const string LargeBoardMessage = "This board has more than 64 shapes. It will show and hide without animation.";
    public const string MissingCornerMessage = "A shape could not animate because its corner is unavailable.";

    /// <summary>The Mac's status for a snapshot over the budget (RenderFailure.invalidSize).</summary>
    public const string BudgetMessage = "Visibility animation unavailable: Export dimensions exceed the supported raster budget.";

    public const string ToolbarFallbackMessage = "The toolbar animation could not start. Using a fade instead.";

    private readonly Func<IReadOnlyList<string>, IReadOnlyList<string>>? _shuffled;
    private readonly CornerRoutes _cornerRoutes = new();
    private Dictionary<string, int> _objectOrder = new(StringComparer.Ordinal);
    private Active? _active;

    /// <param name="shuffled">Test hook replacing the distance order, as the Mac animator's <c>shuffled</c>.</param>
    public VisibilityChoreographer(Func<IReadOnlyList<string>, IReadOnlyList<string>>? shuffled = null)
    {
        _shuffled = shuffled;
    }

    /// <summary>The plan that runs, or null.</summary>
    public VisibilityPlan? Current => _active?.Plan;

    /// <summary>Each shape's start delay in the last plan (Mac transitionObjectDelays).</summary>
    public IReadOnlyDictionary<Guid, double> ObjectDelays { get; private set; } = new Dictionary<Guid, double>();

    /// <summary>Each shape's corner in the last Corners plan (Mac transitionCornerTargets), y-up points of its display.</summary>
    public IReadOnlyDictionary<Guid, MotionPoint> CornerTargets { get; private set; } = new Dictionary<Guid, MotionPoint>();

    /// <summary>Whether a request would turn the running plan around from its current state (same scene, not finished).</summary>
    public bool WouldReverse(MotionRequest request) =>
        _active is { } active && Equals(active.Version, request.SceneVersion);

    /// <summary>The running plan finished or was cancelled; the next request starts fresh.</summary>
    public void Clear() => _active = null;

    /// <summary>
    /// Plans a show or hide. With a running plan over the same scene, the snapshots it already has are reused and every
    /// layer turns around from its state at <see cref="MotionRequest.Now"/>.
    /// </summary>
    public VisibilityPlan Plan(MotionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var messages = new List<string>();
        var showing = request.Showing;
        var style = request.Style;
        var reversing = _active is { } running && Equals(running.Version, request.SceneVersion);
        IReadOnlyList<MotionSnapshot> snapshots;
        LayerState?[] imageStates = [];
        Dictionary<string, double> particleStates = new(StringComparer.Ordinal);
        LayerState? toolbarState = null;
        double? poofProgress = null;
        var now = request.Now;
        if (reversing)
        {
            var previous = _active!.Plan;
            snapshots = [.. previous.Snapshots.Select(plan => plan.Snapshot)];
            imageStates = [.. previous.Snapshots.Select(plan => (LayerState?)plan.Motion.StateAt(now - previous.Started))];
            foreach (var plan in previous.Snapshots)
            {
                if (plan.Particles is { } particles)
                {
                    particleStates[plan.Snapshot.Key] = particles.ProgressAt(now - previous.Started);
                }
            }
            if (previous.Toolbar is { } toolbar)
            {
                var local = now - previous.Started;
                poofProgress = toolbar.Poof?.ProgressAt(local);
                toolbarState = toolbar.Poof?.ImageStateAt(local) ?? toolbar.SnapshotMotion?.StateAt(local) ?? toolbar.RealMotion.StateAt(local);
                if (toolbar.Particles is { } particles)
                {
                    particleStates[ToolbarKey] = particles.ProgressAt(local);
                }
            }
            // Reversals keep the snapshots they have; the budget was checked when they were made.
        }
        else
        {
            snapshots = WithinBudget(request.Snapshots, messages);
        }
        _active = null;

        var elements = snapshots.Where(snapshot => snapshot.ElementId is not null).ToList();
        var keys = elements.Select(snapshot => snapshot.Key).ToList();
        if (style == VisibilityAnimationStyle.Corners)
        {
            _cornerRoutes.Update(elements.Select(snapshot => new CornerRoutes.Item(snapshot.Key, snapshot.Display.Id,
                snapshot.Display.VisibleFrame, snapshot.Display.ToMotion(snapshot.Center))));
        }
        var toolbarFrames = new Dictionary<string, MotionRect>(StringComparer.Ordinal);
        MotionRect ToolbarIn(MotionDisplay display)
        {
            if (!toolbarFrames.TryGetValue(display.Id, out var frame))
            {
                frame = request.Toolbar is { } toolbar ? display.DesktopToMotion(toolbar.DesktopFrame) : new MotionRect(0, display.Height, 0, 0);
                toolbarFrames[display.Id] = frame;
            }
            return frame;
        }
        if (!reversing && (!showing || !SameKeys(keys, _objectOrder.Keys)))
        {
            var order = _shuffled?.Invoke(keys) ?? ToolbarSlideMotion.DistanceOrder(elements.Select(snapshot =>
            {
                var frame = snapshot.Display.ToMotion(snapshot.Frame);
                var target = style == VisibilityAnimationStyle.Corners
                    ? _cornerRoutes.Destinations.TryGetValue(snapshot.Key, out var route) ? route.Point : frame.Center
                    : ToolbarIn(snapshot.Display).Center;
                return (snapshot.Key, frame.Center, target);
            }));
            _objectOrder = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var rank = 0; rank < order.Count; rank++)
            {
                _objectOrder[order[rank]] = rank;
            }
        }
        var timing = new VisibilityMotionTiming(style, showing, snapshots.Count > 0, keys.Count);
        var duration = timing.Duration;
        var toolbarDelay = reversing ? 0 : timing.ToolbarDelay;
        var plans = new List<SnapshotPlan>(snapshots.Count);
        var delays = new Dictionary<Guid, double>();
        var corners = new Dictionary<Guid, MotionPoint>();
        var gatherArrivals = new List<double>();
        var particleLimit = Math.Max(1, ParticleField.DrawingLimit / Math.Max(1, snapshots.Count));
        for (var index = 0; index < snapshots.Count; index++)
        {
            var snapshot = snapshots[index];
            var display = snapshot.Display;
            var frame = display.ToMotion(snapshot.Frame);
            var delay = reversing ? 0
                : snapshot.ElementId is null ? (showing ? 0 : timing.DrawingsEndDelay)
                : timing.DrawingDelay(_objectOrder.TryGetValue(snapshot.Key, out var rank) ? rank : 0);
            if (snapshot.ElementId is { } id)
            {
                delays[id] = delay;
            }
            var imageStyle = (style is VisibilityAnimationStyle.Magic or VisibilityAnimationStyle.Corners || VisibilityPose.UsesPose(style))
                && snapshot.ElementId is null ? VisibilityAnimationStyle.Fade : style;
            ulong? poseSeed = VisibilityPose.UsesPose(imageStyle) ? MagicFlight.Seed(snapshot.Key) : null;
            ulong? magicSeed = imageStyle == VisibilityAnimationStyle.Magic
                ? MagicFlight.Seed(snapshot.Key) ^ (showing ? 0x9e3779b97f4a7c15UL : 0)
                : null;
            var target = ToolbarSlideMotion.AnnotationTarget(frame, ToolbarIn(display), display.Screen);
            double curveSign = 1;
            if (imageStyle == VisibilityAnimationStyle.Corners)
            {
                if (!_cornerRoutes.Destinations.TryGetValue(snapshot.Key, out var route))
                {
                    messages.Add(MissingCornerMessage);
                    continue;
                }
                target = route.Point;
                curveSign = showing ? -route.BendSign : route.BendSign;
                if (snapshot.ElementId is { } cornered)
                {
                    corners[cornered] = target;
                }
            }
            var travels = imageStyle is VisibilityAnimationStyle.Slide or VisibilityAnimationStyle.Quick or VisibilityAnimationStyle.Magic
                or VisibilityAnimationStyle.Corners;
            var travel = travels ? frame.Union(new MotionRect(target.X - 12, target.Y - 12, 24, 24)) : VisibilityPose.Travel(imageStyle, frame);
            var initial = index < imageStates.Length ? imageStates[index] : null;
            if ((imageStyle is VisibilityAnimationStyle.Magic or VisibilityAnimationStyle.Corners) && initial is { } current)
            {
                travel = travel.Union(frame.Offset(current.Transform.Tx, current.Transform.Ty));
            }
            var padding = imageStyle == VisibilityAnimationStyle.Magic
                ? MagicFlight.MaximumBend + Math.Max(frame.Width, frame.Height) * 0.18 + 24
                : style == VisibilityAnimationStyle.Dissolve ? ParticleField.Padding : VisibilityPose.Padding(imageStyle, frame.Width, frame.Height);
            var envelope = travel.Inset(-padding, -padding);
            MotionTransform? hidden = imageStyle is VisibilityAnimationStyle.Slide or VisibilityAnimationStyle.Quick
                or VisibilityAnimationStyle.Dissolve or VisibilityAnimationStyle.Magic or VisibilityAnimationStyle.Corners
                ? ToolbarSlideMotion.AnnotationTransform(frame, target)
                : null;
            var motion = new LayerMotion(new LayerMotionOptions
            {
                Style = imageStyle,
                Appearing = showing,
                Duration = duration,
                Initial = initial,
                Started = delay,
                HiddenTransform = hidden,
                Annotation = true,
                MagicSeed = magicSeed,
                CurveSign = curveSign,
                PoseSeed = poseSeed,
                Width = frame.Width,
                Height = frame.Height,
            });
            if (imageStyle == VisibilityAnimationStyle.Slide && snapshot.ElementId is not null)
            {
                gatherArrivals.Add(motion.EndTime);
            }
            MagicTrailGeometry? trail = null;
            if (magicSeed is { } seed)
            {
                trail = MagicTrailGeometry.Create(frame.Center, motion.FlightTransforms, seed);
            }
            ParticlePlan? particles = null;
            if (style == VisibilityAnimationStyle.Dissolve)
            {
                var toward = ToolbarIn(display).Center;
                double dx = toward.X - frame.MidX, dy = toward.Y - frame.MidY;
                var sweep = double.Hypot(dx, dy) > 1 ? Math.Atan2(dy, dx) : -Math.PI / 2;
                var from = particleStates.TryGetValue(snapshot.Key, out var progress) ? progress : showing ? 1 : 0;
                double to = showing ? 0 : 1;
                var resumed = particleStates.ContainsKey(snapshot.Key);
                particles = new ParticlePlan(particleLimit, sweep, from, to, delay, ParticleField.Duration(duration, from, to, resumed));
            }
            plans.Add(new SnapshotPlan(snapshot, imageStyle, delay, motion, envelope, trail, particles, imageStyle == VisibilityAnimationStyle.Focus));
        }

        ToolbarPlan? toolbarPlan = null;
        if (request.Toolbar is { } bar)
        {
            var display = bar.Display;
            var frame = display.DesktopToMotion(bar.DesktopFrame);
            var edge = ToolbarSlideMotion.ResolvedEdge(frame, display.Screen, bar.Orientation, bar.DockEdge);
            var toolbarDuration = timing.ToolbarDuration;
            var real = new LayerMotion(new LayerMotionOptions
            {
                Style = style,
                Appearing = showing,
                Duration = toolbarDuration,
                Initial = toolbarState,
                Started = toolbarDelay,
                Edge = edge,
                Width = frame.Width,
                Height = frame.Height,
            });
            var kind = style switch
            {
                VisibilityAnimationStyle.Dissolve => ToolbarSnapshotKind.Particles,
                VisibilityAnimationStyle.Magic => ToolbarSnapshotKind.Poof,
                VisibilityAnimationStyle.Slide or VisibilityAnimationStyle.Corners => ToolbarSnapshotKind.Gather,
                _ when VisibilityPose.UsesPose(style) => ToolbarSnapshotKind.Pose,
                _ => ToolbarSnapshotKind.None,
            };
            if (kind != ToolbarSnapshotKind.None && !reversing
                && (plans.Sum(plan => plan.Snapshot.Pixels) + request.ToolbarPixels > PixelBudget || request.ToolbarPixels > ToolbarPixelLimit))
            {
                kind = ToolbarSnapshotKind.None;
                messages.Add(ToolbarFallbackMessage);
            }
            LayerMotion? snapshotMotion = null;
            PoofPlan? poof = null;
            ParticlePlan? particles = null;
            IReadOnlyList<double> pulses = [];
            switch (kind)
            {
                case ToolbarSnapshotKind.Particles:
                {
                    var from = particleStates.TryGetValue(ToolbarKey, out var progress) ? progress : showing ? 1 : 0;
                    double to = showing ? 0 : 1;
                    var resumed = particleStates.ContainsKey(ToolbarKey);
                    particles = new ParticlePlan(ParticleField.ToolbarLimit, -Math.PI / 2, from, to, toolbarDelay,
                                                 ParticleField.Duration(duration, from, to, resumed));
                    break;
                }
                case ToolbarSnapshotKind.Poof:
                {
                    var from = poofProgress ?? (showing ? 1 : 0);
                    double to = showing ? 0 : 1;
                    poof = new PoofPlan(from, to, toolbarDelay, MagicToolbarPoof.Duration(toolbarDuration, from, to, poofProgress is not null));
                    break;
                }
                case ToolbarSnapshotKind.Pose:
                    snapshotMotion = new LayerMotion(new LayerMotionOptions
                    {
                        Style = style,
                        Appearing = showing,
                        Duration = toolbarDuration,
                        Initial = toolbarState,
                        Started = toolbarDelay,
                        PoseSeed = 0,
                        Width = frame.Width,
                        Height = frame.Height,
                    });
                    break;
                case ToolbarSnapshotKind.Gather:
                    snapshotMotion = new LayerMotion(new LayerMotionOptions
                    {
                        Style = VisibilityAnimationStyle.Slide,
                        Appearing = showing,
                        Duration = toolbarDuration,
                        Initial = toolbarState,
                        Started = toolbarDelay,
                        HiddenTransform = ToolbarSlideMotion.AnnotationTransform(frame, frame.Center),
                        Width = frame.Width,
                        Height = frame.Height,
                    });
                    if (style == VisibilityAnimationStyle.Slide && !showing && !reversing)
                    {
                        // The toolbar gives a small pulse as gathered shapes land in it.
                        pulses = ToolbarSlideMotion.PulseTimes(gatherArrivals);
                    }
                    break;
            }
            toolbarPlan = new ToolbarPlan(kind, toolbarDelay, toolbarDuration, edge, real, snapshotMotion, poof, particles, pulses,
                                          frame.Inset(-ParticleField.Padding, -ParticleField.Padding), frame);
        }

        double total;
        if (reversing)
        {
            var ends = new List<double>();
            foreach (var plan in plans)
            {
                ends.Add(plan.Motion.EndTime);
                if (plan.Particles is { } particles)
                {
                    ends.Add(particles.EndTime);
                }
            }
            if (toolbarPlan is { } toolbar)
            {
                ends.Add(toolbar.RealMotion.EndTime);
                if (toolbar.SnapshotMotion is { } snapshotMotion)
                {
                    ends.Add(snapshotMotion.EndTime);
                }
                if (toolbar.Poof is { } poof)
                {
                    ends.Add(poof.EndTime);
                }
                if (toolbar.Particles is { } particles)
                {
                    ends.Add(particles.EndTime);
                }
            }
            total = Math.Max(0, ends.Count > 0 ? ends.Max() : Math.Max(snapshots.Count == 0 ? 0 : duration, timing.ToolbarDuration));
        }
        else
        {
            total = timing.TotalDuration;
        }
        ObjectDelays = delays;
        CornerTargets = corners;
        var result = new VisibilityPlan(showing, style, reversing, now, total, plans, toolbarPlan, messages);
        _active = new Active(result, request.SceneVersion);
        return result;
    }

    /// <summary>The key of the toolbar among particle bursts.</summary>
    public const string ToolbarKey = "toolbar";

    private static bool SameKeys(List<string> keys, IEnumerable<string> known)
    {
        var set = new HashSet<string>(known, StringComparer.Ordinal);
        return set.SetEquals(keys);
    }

    /// <summary>
    /// The Mac's snapshot budget: 40 million pixels across every display. A display whose snapshot does not fit loses its
    /// remaining snapshots and says why, without blocking the hide or show.
    /// </summary>
    private static List<MotionSnapshot> WithinBudget(IReadOnlyList<MotionSnapshot> snapshots, List<string> messages)
    {
        var result = new List<MotionSnapshot>(snapshots.Count);
        var remaining = PixelBudget;
        string? failedDisplay = null;
        foreach (var snapshot in snapshots)
        {
            if (snapshot.Display.Id == failedDisplay)
            {
                continue;
            }
            if (snapshot.Pixels > remaining)
            {
                failedDisplay = snapshot.Display.Id;
                messages.Add(BudgetMessage);
                continue;
            }
            remaining -= snapshot.Pixels;
            result.Add(snapshot);
        }
        return result;
    }

    private sealed record Active(VisibilityPlan Plan, object Version);
}
