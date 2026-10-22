using Annotator.Platform.Capture;

namespace Annotator.Rendering.Effects;

/// <summary>
/// What an <see cref="EffectsPresenter"/> shows. Only <see cref="Live"/> shows filtered screen pixels; in every other
/// state each visible effect region is an opaque black rectangle (fail closed, WIN-CAP-03).
/// </summary>
public abstract record EffectsState
{
    private EffectsState()
    {
    }

    /// <summary>No region needs pixels; no capture runs.</summary>
    public sealed record Idle : EffectsState
    {
        public static readonly Idle Instance = new();
    }

    /// <summary>Capture is starting or the first effect frame is not drawn yet.</summary>
    public sealed record Waiting : EffectsState
    {
        public static readonly Waiting Instance = new();
    }

    /// <summary>Effect regions show the newest captured frame.</summary>
    public sealed record Live : EffectsState
    {
        public static readonly Live Instance = new();
    }

    /// <summary>Capture provides no pixels: <paramref name="Capture"/> says why (stalled, suspended or failed).</summary>
    public sealed record Paused(CaptureState Capture) : EffectsState;

    /// <summary>Drawing the effects failed; <see cref="EffectsPresenter.Retry"/> tries again.</summary>
    public sealed record Failed(Exception Error) : EffectsState;

    public sealed record Disposed : EffectsState
    {
        public static readonly Disposed Instance = new();
    }
}

public sealed class EffectsStateChangedEventArgs(EffectsState previous, EffectsState current) : EventArgs
{
    public EffectsState Previous { get; } = previous;

    public EffectsState Current { get; } = current;
}

/// <summary>Counters for measurements and tests.</summary>
public sealed class EffectsStatistics
{
    private long frames;
    private long skipped;
    private long repeated;
    private long ticks;

    /// <summary>Effect frames drawn and presented.</summary>
    public long Frames => Interlocked.Read(ref frames);

    /// <summary>Captured frames not drawn because the swap chain had no free buffer.</summary>
    public long Skipped => Interlocked.Read(ref skipped);

    /// <summary>Captured frames not drawn because they showed what was already on screen (nothing changed).</summary>
    public long Repeated => Interlocked.Read(ref repeated);

    /// <summary>CPU time of the last effect frame, from reading the capture to Present.</summary>
    public TimeSpan LastFrameTime => TimeSpan.FromTicks(Interlocked.Read(ref ticks));

    internal void Drawn(TimeSpan time)
    {
        Interlocked.Increment(ref frames);
        Interlocked.Exchange(ref ticks, time.Ticks);
    }

    internal void Skip() => Interlocked.Increment(ref skipped);

    internal void Repeat() => Interlocked.Increment(ref repeated);
}
