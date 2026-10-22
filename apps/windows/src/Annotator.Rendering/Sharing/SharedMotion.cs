using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Annotator.Core;
using Annotator.Core.Scene;
using Annotator.Platform;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Motion;
using Annotator.Workspace.Motion;
using Windows.UI.Composition;

namespace Annotator.Rendering.Sharing;

/// <summary>
/// One snapshot of a show or hide on the overlays, as the shared region can copy it. <paramref name="Pixels"/> is where it
/// rests in its display's pixels; <paramref name="Lists"/> draw it, or <paramref name="Color"/> fills it (a board).
/// <paramref name="Protection"/> marks a cover or pixel effect, which the shared region never copies: it keeps showing the
/// live one. <paramref name="Particles"/> and <paramref name="Blurred"/> say how the overlay plays it (Dissolve's particles,
/// Soft focus's blur), so the copy plays the same way.
/// </summary>
public sealed record SharedMotionSnapshot(SnapshotPlan Plan, PixelRect Pixels, DisplayList[]? Lists, RGBA? Color, bool Protection,
                                          bool Particles, bool Blurred);

/// <summary>
/// Where the shared region is, in its display's pixels: the display (the motion's display identifier), the region as the
/// output shows it (its privacy crop) and the area each cover and pixel effect there can paint.
/// </summary>
public sealed record SharedMotionArea(string Display, PixelRect Region, ImmutableArray<PixelRect> Protection)
{
    /// <summary>What every copy keeps: the region, without the protected areas.</summary>
    public StageClip Clip => new(Region, Protection);

    public bool Equals(SharedMotionArea? other) =>
        other is not null && Display == other.Display && Region == other.Region
        && (Protection.IsDefaultOrEmpty ? other.Protection.IsDefaultOrEmpty : !other.Protection.IsDefault && Protection.SequenceEqual(other.Protection));

    public override int GetHashCode() => HashCode.Combine(Display, Region, Protection.IsDefault ? 0 : Protection.Length);
}

/// <summary>Why the shared region could not copy the motion (Mac RenderFailure); sharing stops with the message.</summary>
public sealed class SharedMotionFailure(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Mac RenderFailure.invalidSize.</summary>
    public const string BudgetMessage = "Export dimensions exceed the supported raster budget.";

    /// <summary>Mac RenderFailure.imageUnavailable.</summary>
    public const string RenderMessage = "The drawing could not be rendered.";
}

/// <summary>
/// The show and hide motion in the shared region's output (the Mac's SharedWorkspaceTransition, fed by
/// WorkspaceVisibilityAnimator): copies of the overlays' snapshots that rest in the region, each cut to the region and
/// without what covers and pixel effects hide, play the overlays' plan with the same compositor animations, light trails,
/// sparks, particles and blur, started in the same work item as the overlays' so they run together. <see cref="Root"/> is
/// in the display's pixels; the owner maps it onto the output and clips it to the region. Covers and effects never move
/// in the output: they keep showing live below the copies, and the owner draws them again above. After the motion the
/// copies hold their last pose until the owner hands off to the live scene; a hand-off or cancel releases every copy,
/// surface and swap chain, so nothing runs between transitions. Use it on the thread of its compositor.
/// </summary>
public sealed class SharedMotion : IDisposable
{
    /// <summary>Mac remainingSharedPixels: the copies of drawings and boards share 16 million pixels.</summary>
    public const long SnapshotBudget = 16_000_000;

    /// <summary>Mac SharedWorkspaceTransition.maximumImagePixels: everything one transition copies, the toolbar included.</summary>
    public const long ImageBudget = 20_000_000;

    /// <summary>Mac SharedWorkspaceTransition's frame pump: the output draws its particles every 33 ms.</summary>
    public const double ParticleInterval = 0.033;

    private const string ToolbarBurst = "\u0000toolbar";

    private readonly Compositor _compositor;
    private readonly ContainerVisual _root;
    private readonly ISystemColorResolver _colors;
    private readonly double _scale;
    private readonly MotionAnimations _animations;
    private readonly Dictionary<string, StageImage> _images = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ParticleSource> _particles = new(StringComparer.Ordinal);
    private readonly List<(StageImage Image, LayerMotion Motion)> _blurs = [];
    private GraphicsDevices? _devices;
    private VisibilityStage? _stage;
    private SharedMotionArea? _area;
    private StageImage? _toolbar;
    private long _copied;
    private long _added;
    private double _particlesDrawn = double.NegativeInfinity;
    private bool _particlesMoving;
    private bool _disposed;

    /// <param name="root">The container the copies go into, in the display's pixels.</param>
    /// <param name="scale">Pixels per point of the shared display.</param>
    public SharedMotion(Compositor compositor, ContainerVisual root, GraphicsDevices? devices, ISystemColorResolver colors, double scale)
    {
        _compositor = compositor ?? throw new ArgumentNullException(nameof(compositor));
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _colors = colors ?? throw new ArgumentNullException(nameof(colors));
        _devices = devices;
        _scale = scale;
        _animations = new MotionAnimations(compositor);
    }

    public ContainerVisual Root => _root;

    /// <summary>A show or hide plays here, or holds its last pose for the hand-off (Mac workspaceTransition != nil).</summary>
    public bool IsActive => _stage is not null;

    /// <summary>The motion ended and the copies hold their last pose until <see cref="HandOff"/> (Mac isFinishing).</summary>
    public bool IsFinishing { get; private set; }

    /// <summary>A copy of the toolbar's snapshot plays or holds.</summary>
    public bool HasToolbar => _toolbar is not null;

    /// <summary>The real toolbar plays its own motion (Fade, or no snapshot), so the output keeps showing it live.</summary>
    public bool RealToolbar { get; private set; }

    /// <summary>The region and protection of the motion that plays, or null.</summary>
    public SharedMotionArea? Area => _area;

    /// <summary>Copies on the stage now (particle bursts draw their own).</summary>
    public int ImageCount => _stage?.ImageCount ?? 0;

    /// <summary>Particle bursts on the stage now.</summary>
    public int BurstCount => _stage?.Bursts?.Count ?? 0;

    /// <summary>Transitions that played here since this object was made, and the copies they made (live checks, measurements).</summary>
    public (int Transitions, int Copies, int ParticleFrames) Statistics => (_transitions, _copies, _particleFrames);

    private int _transitions;
    private int _copies;
    private int _particleFrames;

    /// <summary>Continues on other devices, or none: whatever plays is dropped (the owner covers the output until its next frame).</summary>
    public void ReplaceDevices(GraphicsDevices? devices)
    {
        if (!ReferenceEquals(devices, _devices))
        {
            Release();
            _devices = devices;
        }
    }

    /// <summary>
    /// Plays <paramref name="plan"/> (Mac beginWorkspaceAnimation, add and prepare): every snapshot of the shared display
    /// that rests in the region, except covers and pixel effects, gets a copy cut by <see cref="SharedMotionArea.Clip"/> that
    /// plays its plan from <paramref name="started"/> (seconds on the steady clock, the overlays' start). A reversal over the
    /// same area keeps the copies it has and turns them around. Particles draw their first frame at
    /// <paramref name="now"/>.
    /// </summary>
    /// <exception cref="SharedMotionFailure">A copy is over the budget or could not be drawn; nothing plays.</exception>
    public void Play(VisibilityPlan plan, SharedMotionArea area, IReadOnlyList<SharedMotionSnapshot> snapshots, double started, double now)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(snapshots);
        bool fresh = !plan.Reversing || _stage is null || !area.Equals(_area);
        if (fresh)
        {
            Release();
        }
        try
        {
            if (_devices is null)
            {
                throw new InvalidOperationException("There are no graphics devices to copy the motion.");
            }
            _area = area;
            IsFinishing = false;
            RealToolbar = false;
            if (_stage is null)
            {
                _stage = new VisibilityStage(_compositor, _root, _devices, _colors, _scale, _animations);
                _copied = 0;
                _added = 0;
                _transitions++;
            }
            VisibilityStage stage = _stage;
            StageClip clip = area.Clip;
            var keep = new HashSet<string>(StringComparer.Ordinal);
            var bursts = new List<(string Key, ParticleSource Source, SnapshotPlan Plan)>();
            _blurs.Clear();
            foreach (SharedMotionSnapshot snapshot in snapshots)
            {
                SnapshotPlan item = snapshot.Plan;
                string key = item.Snapshot.Key;
                // Mac snapshots(): covers and effects stay live in the output; other displays and what misses the region,
                // or lies wholly under protection there, have no copy.
                if (snapshot.Protection || item.Snapshot.Display.Id != area.Display || clip.Hides(snapshot.Pixels))
                {
                    continue;
                }
                keep.Add(key);
                if (snapshot.Particles && item.Particles is not null)
                {
                    if (!_particles.TryGetValue(key, out ParticleSource? source))
                    {
                        Charge(snapshot.Pixels, copy: true);
                        ParticleBursts particles = stage.UseBursts(area.Region);
                        source = snapshot.Color is { } color
                            ? particles.Color(color, snapshot.Pixels, clip)
                            : stage.ParticleLists(particles, Lists(snapshot), snapshot.Pixels, clip);
                        _particles[key] = source;
                        _copies++;
                    }
                    bursts.Add((key, source, item));
                    continue;
                }
                if (!_images.TryGetValue(key, out StageImage? image))
                {
                    Charge(snapshot.Pixels, copy: true);
                    image = snapshot.Color is { } color
                        ? stage.AddColor(color, snapshot.Pixels, clip)
                        : snapshot.Blurred
                            ? stage.AddBlurredLists(Lists(snapshot), snapshot.Pixels, Padding, clip)
                            : stage.AddLists(Lists(snapshot), snapshot.Pixels, clip);
                    _images[key] = image;
                    _copies++;
                }
                Play(stage, image, item);
                if (snapshot.Blurred && item.Motion.Blur is not null)
                {
                    _blurs.Add((image, item.Motion));
                }
            }
            foreach (string key in _images.Keys.Where(key => !keep.Contains(key)).ToList())
            {
                stage.Remove(_images[key]);
                _images.Remove(key);
            }
            foreach (string key in _particles.Keys.Where(key => !keep.Contains(key) && key != ToolbarBurst).ToList())
            {
                _particles[key].Dispose();
                _particles.Remove(key);
            }
            if (_particles.ContainsKey(ToolbarBurst))
            {
                keep.Add(ToolbarBurst);
            }
            stage.Bursts?.Retain(keep);
            foreach ((string key, ParticleSource source, SnapshotPlan item) in bursts)
            {
                stage.Bursts!.Set(key, source, item.Particles!, item.Motion, started);
            }
            PrepareBursts(now);
            UpdateBlurs(started, started);
        }
        catch (SharedMotionFailure)
        {
            Release();
            throw;
        }
        catch (Exception error) when (error is NativeCallException or InvalidOperationException or ArgumentException or OutOfMemoryException
                                          or COMException)
        {
            Release();
            throw new SharedMotionFailure(SharedMotionFailure.RenderMessage, error);
        }
    }

    /// <summary>
    /// The toolbar's snapshot plays as on the overlays (VisibilityAnimator.PlayToolbar), <paramref name="shift"/> seconds into
    /// the plan: a copy of its premultiplied BGRA rows (<paramref name="width"/> by <paramref name="height"/>, resting at
    /// <paramref name="pixels"/>) cut like the other copies. Nothing plays when the toolbar is outside the region.
    /// </summary>
    /// <param name="blurred">Soft focus blurs the toolbar's snapshot.</param>
    /// <param name="particles">Dissolve's particles draw the toolbar on the overlays.</param>
    /// <exception cref="SharedMotionFailure">The copy is over the budget or could not be drawn; nothing plays.</exception>
    public void PlayToolbar(ToolbarPlan toolbar, ReadOnlySpan<byte> bgra, int width, int height, PixelRect pixels, bool blurred, bool particles,
                            double started, double shift, double now)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(toolbar);
        if (_stage is not { } stage || _area is not { } area)
        {
            return;
        }
        StageClip clip = area.Clip;
        if (clip.Hides(pixels))
        {
            return;
        }
        try
        {
            if (_toolbar is null)
            {
                Charge(pixels, copy: false);
                _toolbar = blurred
                    ? stage.AddBlurredBitmap(bgra, width, height, width * 4, pixels, Padding, clip)
                    : stage.AddBitmap(bgra, width, height, width * 4, pixels, clip);
                _copies++;
            }
            RealToolbar = false;
            StageImage image = _toolbar;
            stage.ClearExtras(image);
            switch (toolbar.Kind)
            {
                case ToolbarSnapshotKind.Gather:
                case ToolbarSnapshotKind.Pose:
                    stage.Animate(image, toolbar.SnapshotMotion!, shift);
                    if (toolbar.Pulses.Count > 0)
                    {
                        stage.Pulse(image, toolbar.Pulses, shift);
                    }
                    if (blurred && toolbar.SnapshotMotion!.Blur is not null)
                    {
                        _blurs.Add((image, toolbar.SnapshotMotion));
                        UpdateBlurs(now, started);
                    }
                    break;
                case ToolbarSnapshotKind.Poof:
                    MagicEffects.PlayPoof(stage, image, toolbar.Poof!, shift);
                    break;
                case ToolbarSnapshotKind.Particles when particles:
                    if (!_particles.TryGetValue(ToolbarBurst, out ParticleSource? source))
                    {
                        source = stage.UseBursts(area.Region).Bitmap(bgra, width * 4, image.Pixels, clip);
                        _particles[ToolbarBurst] = source;
                    }
                    stage.Bursts!.Set(ToolbarBurst, source, toolbar.Particles!, null, started);
                    image.Image.IsVisible = false;
                    PrepareBursts(now);
                    break;
                default:
                    stage.Animate(image, toolbar.RealMotion, shift);
                    break;
            }
        }
        catch (SharedMotionFailure)
        {
            Release();
            throw;
        }
        catch (Exception error) when (error is NativeCallException or InvalidOperationException or ArgumentException or OutOfMemoryException
                                          or COMException)
        {
            Release();
            throw new SharedMotionFailure(SharedMotionFailure.RenderMessage, error);
        }
    }

    /// <summary>
    /// The real toolbar plays its own motion (Fade, or its snapshot failed), so the output shows it live instead of a copy
    /// (the Mac mirrors the same motion with a copy of the toolbar).
    /// </summary>
    public void UseRealToolbar()
    {
        if (_stage is not { } stage)
        {
            return;
        }
        RealToolbar = true;
        if (_toolbar is { } toolbar)
        {
            stage.Remove(toolbar);
            _toolbar = null;
        }
        if (_particles.Remove(ToolbarBurst, out ParticleSource? source))
        {
            stage.Bursts?.Retain(_particles.Keys.ToHashSet(StringComparer.Ordinal));
            source.Dispose();
        }
    }

    /// <summary>
    /// Draws what the compositor cannot animate at <paramref name="now"/>: Soft focus's blur (every frame) and the particles
    /// (every <see cref="ParticleInterval"/>, as the Mac's frame pump), with the plan started at <paramref name="started"/>.
    /// Returns whether something still changes.
    /// </summary>
    /// <exception cref="SharedMotionFailure">Drawing failed; the copies are released.</exception>
    public bool Frame(double now, double started)
    {
        if (_stage is not { } stage || _disposed)
        {
            return false;
        }
        try
        {
            var moving = UpdateBlurs(now, started);
            if (_particlesMoving && stage.Bursts is { Count: > 0 } bursts)
            {
                if (now - _particlesDrawn >= ParticleInterval)
                {
                    _particlesMoving = bursts.Render(now);
                    _particlesDrawn = now;
                    _particleFrames++;
                }
                moving |= _particlesMoving;
            }
            return moving;
        }
        catch (Exception error) when (error is NativeCallException or InvalidOperationException)
        {
            Release();
            throw new SharedMotionFailure(SharedMotionFailure.RenderMessage, error);
        }
    }

    /// <summary>The motion ended (Mac finishWorkspaceAnimation): the copies hold their last pose until <see cref="HandOff"/>.</summary>
    public void Finish()
    {
        if (_stage is not null)
        {
            IsFinishing = true;
        }
    }

    /// <summary>
    /// The output shows the scene after the motion (Mac completingWorkspaceTransition): the copies go. With
    /// <paramref name="keepToolbar"/>, the toolbar's copy stays at rest until <see cref="ReleaseToolbar"/>, while the live
    /// toolbar starts showing again.
    /// </summary>
    public void HandOff(bool keepToolbar)
    {
        if (_stage is not { } stage)
        {
            return;
        }
        if (!keepToolbar || _toolbar is not { } toolbar)
        {
            Release();
            return;
        }
        foreach (StageImage image in _images.Values)
        {
            stage.Remove(image);
        }
        _images.Clear();
        _blurs.Clear();
        stage.ClearBursts();
        foreach (ParticleSource source in _particles.Values)
        {
            source.Dispose();
        }
        _particles.Clear();
        _particlesMoving = false;
        // Dissolve drew the toolbar with particles; its copy covers the hand-off as the bursts leave.
        toolbar.Image.IsVisible = true;
    }

    /// <summary>The live toolbar shows again: the toolbar's copy goes too.</summary>
    public void ReleaseToolbar()
    {
        if (_toolbar is not null)
        {
            Release();
        }
    }

    /// <summary>Stops at once (Mac cancel): every copy goes.</summary>
    public void Cancel() => Release();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        Release();
        _animations.Clear();
        _disposed = true;
    }

    /// <summary>
    /// Where <paramref name="protection"/> (the display's pixels) shows in the output's picture of
    /// <paramref name="region"/>, which fills <paramref name="fit"/> (the window's pixels): the insets, rounded outward to
    /// whole pixels, that clip a view of the region image at <paramref name="fit"/> to it; null when it misses the region.
    /// The output draws the covers and pixel effects there again above the motion's copies.
    /// </summary>
    public static (float Left, float Top, float Right, float Bottom)? LidInsets(PixelRect protection, PixelRect region, PixelRect fit)
    {
        PixelRect shown = StageClip.Intersect(protection, region);
        if (shown.IsEmpty || region.IsEmpty || fit.IsEmpty)
        {
            return null;
        }
        double x = fit.Width / (double)region.Width, y = fit.Height / (double)region.Height;
        var left = Math.Floor((shown.Left - region.Left) * x);
        var top = Math.Floor((shown.Top - region.Top) * y);
        var right = Math.Ceiling((shown.Right - region.Left) * x);
        var bottom = Math.Ceiling((shown.Bottom - region.Top) * y);
        return ((float)Math.Max(0, left), (float)Math.Max(0, top), (float)Math.Max(0, fit.Width - right), (float)Math.Max(0, fit.Height - bottom));
    }

    /// <summary>The copy of a snapshot or of the toolbar (<c>"toolbar"</c>), for tests.</summary>
    internal StageImage? Copy(string key) => key == "toolbar" ? _toolbar : _images.GetValueOrDefault(key);

    private int Padding => (int)Math.Ceiling(VisibilityPose.BlurPadding * _scale);

    private static DisplayList[] Lists(SharedMotionSnapshot snapshot) =>
        snapshot.Lists ?? throw new ArgumentException("A snapshot needs display lists or a color.", nameof(snapshot));

    /// <summary>Mac budgets: drawings and boards share <see cref="SnapshotBudget"/>; one transition copies at most <see cref="ImageBudget"/>.</summary>
    private void Charge(PixelRect pixels, bool copy)
    {
        long count = (long)pixels.Width * pixels.Height;
        if ((copy && _copied + count > SnapshotBudget) || _added + count > ImageBudget)
        {
            throw new SharedMotionFailure(SharedMotionFailure.BudgetMessage);
        }
        if (copy)
        {
            _copied += count;
        }
        _added += count;
    }

    private static void Play(VisibilityStage stage, StageImage image, SnapshotPlan item)
    {
        stage.ClearExtras(image);
        stage.Animate(image, item.Motion);
        if (item.Trail is { } trail)
        {
            MagicEffects.PlayTrails(stage, image, trail, item.Snapshot.Display, item.Motion, 0);
            MagicEffects.PlaySparks(stage, image, trail, item.Snapshot.Display, item.Motion, 0);
        }
    }

    private void PrepareBursts(double now)
    {
        if (_stage is { } stage)
        {
            _particlesMoving = stage.PrepareBursts(now);
            _particlesDrawn = now;
        }
    }

    /// <summary>Redraws each soft-focus copy with its blur at <paramref name="now"/>; returns whether a blur still changes.</summary>
    private bool UpdateBlurs(double now, double started)
    {
        if (_stage is not { } stage)
        {
            return false;
        }
        var moving = false;
        var time = now - started;
        foreach ((StageImage image, LayerMotion motion) in _blurs)
        {
            var local = motion.EffectiveDuration > 0 ? (time - motion.Started) / motion.EffectiveDuration : 1;
            stage.SetBlur(image, motion.Blur!.At(local) * _scale);
            moving |= time < motion.EndTime;
        }
        return moving;
    }

    private void Release()
    {
        _blurs.Clear();
        foreach (ParticleSource source in _particles.Values)
        {
            source.Dispose();
        }
        _particles.Clear();
        _images.Clear();
        _toolbar = null;
        _stage?.Dispose();
        _stage = null;
        _area = null;
        IsFinishing = false;
        RealToolbar = false;
        _particlesMoving = false;
        _particlesDrawn = double.NegativeInfinity;
    }
}
