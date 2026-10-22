using System.Diagnostics;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Platform.Threading;

namespace Annotator.Platform.Capture;

/// <summary>
/// One display's capture source, shared by every lease of that display (WIN-PERF-05). It keeps the newest frame on
/// the GPU (no CPU copy, WIN-CAP-01) in a fixed set of slots: a frame that a consumer still holds is retired when a newer
/// one arrives and released when the consumer disposes it.
/// </summary>
/// <remarks>
/// <para>With a frame comparer, each frame is compared with the one before it on the GPU, only in the tiles that the
/// leases' areas reach (<see cref="ICaptureLease.SetInterest"/>), and nothing waits for the result: it is read later, in
/// frame order, when the GPU has set the service's completion event or the next frame arrives. Each result sets the content
/// version of every lease (<see cref="CaptureFrame.ContentVersion"/>). Until its result is known, a frame has its own version
/// for a lease whose areas changed in the frame before (it is drawn at once, so moving content keeps the display's rate),
/// and the lease's last version otherwise (it is not drawn; if the result then finds a change, the lease hears of the frame
/// again). A source whose frames change nothing in any lease's areas asks for fewer of them (<see cref="WantsFrame"/>).</para>
/// <para>A paused source keeps its pixel source but takes no frames and holds none, so a lease that comes back reuses the
/// capture (<see cref="CaptureServiceOptions.Linger"/>).</para>
/// <para>Frames, results, state changes and health checks arrive on different threads; consumer calls are serialized per
/// source.</para>
/// </remarks>
internal sealed class CaptureSource : IPixelSink, ICaptureFrameOwner
{
    private const int SlotCount = 4;
    private const int ResultLimit = 32;
    private const int SharedBit = 31;

    private readonly CaptureService service;
    private readonly Lock stateGate = new();
    private readonly Lock notifyGate = new();
    private readonly FrameSlot[] slots = new FrameSlot[SlotCount];
    private CaptureService.Lease[] leases = [];
    private CaptureState state;
    private IPixelSource? pixels;
    private GraphicsDevices? devices;
    private nint completion;
    private bool accepting;
    private bool paused;
    private int newest = -1;
    private long sequence;
    private long lastFrame;
    private long started;
    private int unchangedInARow;
    private long lastRequest;

    // Lease bits and the version of the leases' areas, under stateGate.
    private uint usedBits;
    private int sharedBitLeases;
    private long interestVersion;

    // Comparisons, under notifyGate; resolvedThrough and the leases' versions also under stateGate.
    private readonly Result[] results = new Result[ResultLimit];
    private readonly List<CaptureService.Lease> told = [];
    private int resultHead;
    private int resultCount;
    private long resolvedThrough;
    private ICaptureChangeTracker? tracker;
    private uint[] tiles = [];
    private bool tilesEmpty = true;
    private bool trackerHasTiles;
    private long builtInterest = -1;

    private long framesReceived;
    private long framesDropped;
    private long framesUnchanged;
    private long framesCompared;
    private long framesUncompared;
    private long renotified;
    private long resultsRead;
    private long resultDelayTicks;
    private long resultDelayMax;

    public CaptureSource(CaptureService service, DisplayInfo display, CaptureState initial)
    {
        this.service = service;
        Display = display;
        state = initial;
    }

    public DisplayInfo Display { get; }

    /// <summary>Whether a pixel source runs, is starting or is paused. Guarded by the service's lock.</summary>
    public bool IsRunning { get; set; }

    /// <summary>Whether the source is paused after its last lease (capture thread). Guarded by the service's lock.</summary>
    public bool IsPaused { get; set; }

    /// <summary>When a paused source ends (Stopwatch ticks). Guarded by the service's lock.</summary>
    public long LingerUntil { get; set; }

    public int LeaseCount => Volatile.Read(ref leases).Length;

    public CaptureState State
    {
        get
        {
            using (NonPumpingLock.Enter(stateGate))
            {
                return state;
            }
        }
    }

    /// <summary>
    /// Frames that arrived, frames dropped (no free slot or result place, a stale size, a stopped or paused source) and frames
    /// that changed nothing in any lease's areas, for measurements.
    /// </summary>
    public (long Received, long Dropped, long Unchanged) Counters =>
        (Interlocked.Read(ref framesReceived), Interlocked.Read(ref framesDropped), Interlocked.Read(ref framesUnchanged));

    /// <summary>Comparisons started, frames counted as changed without one, and leases told of a frame again, for measurements.</summary>
    public (long Compared, long Uncompared, long Renotified) Comparisons =>
        (Interlocked.Read(ref framesCompared), Interlocked.Read(ref framesUncompared), Interlocked.Read(ref renotified));

    /// <summary>
    /// GPU comparisons read, and the total and longest time from a frame's arrival to reading its comparison (Stopwatch
    /// ticks), for measurements.
    /// </summary>
    public (long Read, long DelayTicks, long MaxDelayTicks) ResultDelays =>
        (Interlocked.Read(ref resultsRead), Interlocked.Read(ref resultDelayTicks), Interlocked.Read(ref resultDelayMax));

    /// <summary>Whether comparisons wait to be read (any thread; a hint for the pacer).</summary>
    public bool HasPendingResults => Volatile.Read(ref resultCount) > 0;

    /// <summary>
    /// Pacer thread: whether to ask for a frame on this tick. A source whose last <paramref name="stillFrames"/> frames or
    /// more changed nothing, without <paramref name="input"/>, asks at most once per <paramref name="stillPeriod"/> (Stopwatch
    /// ticks); input (a moved pointer, keys or buttons) or a changed frame makes it ask on every tick again.
    /// </summary>
    public bool WantsFrame(long now, bool input, int stillFrames, long stillPeriod)
    {
        if (input)
        {
            Volatile.Write(ref unchangedInARow, 0);
        }
        else if (Volatile.Read(ref unchangedInARow) >= stillFrames && now - lastRequest < stillPeriod)
        {
            return false;
        }
        lastRequest = now;
        return true;
    }

    public bool Matches(DisplayInfo display) => display.DeviceName == Display.DeviceName && display.Bounds == Display.Bounds;

    /// <summary>
    /// Adds a lease with a bit of its own (one shared bit after 31 leases) and the whole display as its area. It starts out
    /// changing, so its first frame is drawn at once. Call under the service's lock.
    /// </summary>
    public void AddLease(CaptureService.Lease lease)
    {
        using (NonPumpingLock.Enter(stateGate))
        {
            var free = ~usedBits & ~(1u << SharedBit);
            if (free != 0)
            {
                lease.Bit = System.Numerics.BitOperations.TrailingZeroCount(free);
                usedBits |= 1u << lease.Bit;
            }
            else
            {
                lease.Bit = SharedBit;
                sharedBitLeases++;
            }
            lease.Version = sequence;
            lease.Hot = true;
            lease.LastProvisional = 0;
            lease.CountedFrom = long.MaxValue;
            interestVersion++;
            Volatile.Write(ref leases, [.. leases, lease]);
        }
    }

    /// <summary>Removes a lease; false when it was not there. Call under the service's lock.</summary>
    public bool RemoveLease(CaptureService.Lease lease)
    {
        using (NonPumpingLock.Enter(stateGate))
        {
            var index = Array.IndexOf(leases, lease);
            if (index < 0)
            {
                return false;
            }
            if (lease.Bit == SharedBit)
            {
                sharedBitLeases--;
            }
            else
            {
                usedBits &= ~(1u << lease.Bit);
            }
            interestVersion++;
            Volatile.Write(ref leases, [.. leases[..index], .. leases[(index + 1)..]]);
            return true;
        }
    }

    /// <summary>Replaces a lease's areas (any thread); the next comparison uses them.</summary>
    public void SetInterest(CaptureService.Lease lease, ReadOnlySpan<PixelRect> areas)
    {
        using (NonPumpingLock.Enter(stateGate))
        {
            if (Array.IndexOf(leases, lease) < 0)
            {
                return;
            }
            if (lease.Interest.Length < areas.Length)
            {
                lease.Interest = new PixelRect[Math.Max(areas.Length, lease.Interest.Length * 2)];
            }
            areas.CopyTo(lease.Interest);
            lease.InterestCount = areas.Length;
            lease.WholeDisplay = false;
            lease.CountedFrom = long.MaxValue;
            interestVersion++;
        }
    }

    /// <summary>Starts accepting frames for <paramref name="bundle"/> (capture thread, before the pixel source starts).</summary>
    public void Prepare(GraphicsDevices bundle, nint completed)
    {
        using (NonPumpingLock.Enter(stateGate))
        {
            devices = bundle;
            completion = completed;
            accepting = true;
            paused = false;
            started = Stopwatch.GetTimestamp();
            lastFrame = started;
        }
        Transition(CaptureState.Starting.Instance);
    }

    /// <summary>Keeps the started pixel source (capture thread).</summary>
    public void Attach(IPixelSource source)
    {
        using (NonPumpingLock.Enter(stateGate))
        {
            Volatile.Write(ref pixels, source);
        }
    }

    /// <summary>
    /// Stops accepting frames and hands back the pixel source to dispose (capture thread). Frames that consumers still
    /// hold stay valid until they are disposed; comparisons that were not read are dropped.
    /// </summary>
    public IPixelSource? Detach()
    {
        using (NonPumpingLock.Enter(notifyGate))
        {
            IPixelSource? source;
            using (NonPumpingLock.Enter(stateGate))
            {
                source = pixels;
                Volatile.Write(ref pixels, null);
                accepting = false;
                paused = false;
                devices = null;
            }
            LetFramesGo();
            return source;
        }
    }

    /// <summary>
    /// Pauses after the last lease (capture thread): the pixel source stays, but frames are no longer taken and the stored
    /// ones go, so a capture that starts again hands out only frames taken after it.
    /// </summary>
    public void Pause()
    {
        using (NonPumpingLock.Enter(notifyGate))
        {
            using (NonPumpingLock.Enter(stateGate))
            {
                accepting = false;
                paused = true;
            }
            LetFramesGo();
        }
        Transition(CaptureState.Stopped.Instance);
    }

    /// <summary>Takes frames again after <see cref="Pause"/> (capture thread, with the exclusions applied again first).</summary>
    public void Resume()
    {
        using (NonPumpingLock.Enter(stateGate))
        {
            if (!paused || pixels is null)
            {
                return;
            }
            paused = false;
            accepting = true;
            started = Stopwatch.GetTimestamp();
            lastFrame = started;
            unchangedInARow = 0;
        }
        Transition(CaptureState.Starting.Instance);
    }

    /// <summary>Asks the pixel source for a frame (pacer thread).</summary>
    public void Invalidate() => Volatile.Read(ref pixels)?.Invalidate();

    /// <summary>Applies a new exclude list (capture thread). Throws <see cref="NativeCallException"/>.</summary>
    public void ApplyExclusions(ReadOnlySpan<nint> windows) => Volatile.Read(ref pixels)?.ApplyExclusions(windows);

    /// <summary>Moves to <see cref="CaptureState.Failed"/> and stops accepting frames (any thread).</summary>
    public void Fail(CaptureFailure failure)
    {
        using (NonPumpingLock.Enter(stateGate))
        {
            accepting = false;
        }
        Transition(new CaptureState.Failed(failure));
    }

    /// <summary>Moves to <paramref name="next"/> and tells every consumer (any thread).</summary>
    public void Transition(CaptureState next)
    {
        using (NonPumpingLock.Enter(notifyGate))
        {
            CaptureService.Lease[] targets;
            using (NonPumpingLock.Enter(stateGate))
            {
                if (state.Equals(next))
                {
                    return;
                }
                state = next;
                targets = leases;
            }
            NotifyState(targets, next);
        }
    }

    /// <summary>
    /// Pacer thread: reports a stall when no frame arrived for <paramref name="stallTicks"/>, and fails a start that got
    /// no frame within <paramref name="firstFrameTicks"/>. Allocates only when the state changes.
    /// </summary>
    public void CheckHealth(long now, long stallTicks, long firstFrameTicks)
    {
        long seen;
        bool stall = false, timedOut = false;
        using (NonPumpingLock.Enter(stateGate))
        {
            if (!accepting)
            {
                return;
            }
            seen = sequence;
            if (state is CaptureState.Live && now - lastFrame > stallTicks)
            {
                stall = true;
            }
            else if (state is CaptureState.Starting && now - started > firstFrameTicks)
            {
                timedOut = true;
                accepting = false;
            }
        }
        if (stall)
        {
            using (NonPumpingLock.Enter(notifyGate))
            {
                CaptureService.Lease[] targets;
                CaptureState next;
                using (NonPumpingLock.Enter(stateGate))
                {
                    // A frame that arrived meanwhile keeps the source live.
                    if (state is not CaptureState.Live || sequence != seen || !accepting)
                    {
                        return;
                    }
                    next = new CaptureState.Stalled(Stopwatch.GetElapsedTime(lastFrame, now));
                    state = next;
                    targets = leases;
                }
                NotifyState(targets, next);
            }
        }
        else if (timedOut)
        {
            Transition(new CaptureState.Failed(new CaptureFailure(CaptureFailureKind.NoFrames, "Wait for the first captured frame")));
            service.StopPixelsLater(this);
        }
    }

    /// <summary>The newest frame with <paramref name="lease"/>'s content version (see <see cref="CaptureFrame.ContentVersion"/>).</summary>
    public bool TryAcquire(CaptureService.Lease lease, out CaptureFrame frame)
    {
        using (NonPumpingLock.Enter(stateGate))
        {
            if (state is not CaptureState.Live || newest < 0 || devices is null)
            {
                frame = default;
                return false;
            }
            ref var slot = ref slots[newest];
            slot.Uses++;
            long version;
            if (slot.Sequence > resolvedThrough && lease.Hot)
            {
                // Not compared yet, and the lease's areas were changing: drawn at once.
                version = slot.Sequence;
                lease.LastProvisional = Math.Max(lease.LastProvisional, slot.Sequence);
            }
            else
            {
                version = lease.Version;
            }
            frame = new CaptureFrame(this, newest, slot.Texture, slot.Width, slot.Height, slot.Sequence, devices, slot.Time, version);
            return true;
        }
    }

    public void Release(int slot, long frameSequence)
    {
        FrameSlot released = default;
        using (NonPumpingLock.Enter(stateGate))
        {
            if ((uint)slot >= SlotCount)
            {
                return;
            }
            ref var entry = ref slots[slot];
            if (!entry.InUse || entry.Sequence != frameSequence || entry.Uses == 0)
            {
                return;
            }
            entry.Uses--;
            if (entry.Uses == 0 && entry.Retired)
            {
                released = entry;
                entry = default;
            }
        }
        FrameReferences.Release(released.Closable, released.Texture);
    }

    public void OnFrame(nint closable, nint texture, int width, int height, TimeSpan time)
    {
        using (NonPumpingLock.Enter(notifyGate))
        {
            ResolveReady();
            FrameSlot retired = default;
            var becameLive = false;
            var live = false;
            var stored = false;
            var pinned = -1;
            long pinnedSequence = 0, frameSequence = 0;
            nint previous = 0;
            GraphicsDevices? bundle = null;
            nint completed = 0;
            CaptureService.Lease[] targets;
            using (NonPumpingLock.Enter(stateGate))
            {
                targets = leases;
                if (accepting && width == Display.Bounds.Width && height == Display.Bounds.Height && resultCount < ResultLimit
                    && FreeSlot() is var index and >= 0)
                {
                    if (newest >= 0)
                    {
                        // Pinned until its comparison with the new frame is on the GPU's queue.
                        ref var before = ref slots[newest];
                        before.Uses++;
                        pinned = newest;
                        pinnedSequence = before.Sequence;
                        previous = before.Texture;
                    }
                    bundle = devices;
                    completed = completion;
                    frameSequence = ++sequence;
                    slots[index] = new FrameSlot
                    {
                        Closable = closable,
                        Texture = texture,
                        Width = width,
                        Height = height,
                        Sequence = frameSequence,
                        Time = time,
                        InUse = true,
                    };
                    if (newest >= 0)
                    {
                        ref var old = ref slots[newest];
                        if (old.Uses == 0)
                        {
                            retired = old;
                            old = default;
                        }
                        else
                        {
                            old.Retired = true;
                        }
                    }
                    newest = index;
                    lastFrame = Stopwatch.GetTimestamp();
                    stored = true;
                    if (state is CaptureState.Starting or CaptureState.Stalled)
                    {
                        state = CaptureState.Live.Instance;
                        becameLive = true;
                    }
                    live = state is CaptureState.Live;
                }
            }
            if (!stored)
            {
                Interlocked.Increment(ref framesDropped);
                FrameReferences.Release(closable, texture);
                Tell();
                return;
            }
            Interlocked.Increment(ref framesReceived);
            FrameReferences.Release(retired.Closable, retired.Texture);
            var result = Begin(frameSequence, previous, texture, width, height, bundle, completed);
            if (pinned >= 0)
            {
                Release(pinned, pinnedSequence);
            }
            results[(resultHead + resultCount) % ResultLimit] = result;
            resultCount++;
            ResolveReady();
            if (!live)
            {
                told.Clear();
                return;
            }
            if (becameLive)
            {
                NotifyState(targets, CaptureState.Live.Instance);
            }
            // Everyone hears of this frame, which covers the leases a result just told of a change.
            told.Clear();
            foreach (var lease in targets)
            {
                try
                {
                    lease.Consumer.OnFrameAvailable(lease);
                }
                catch (Exception error)
                {
                    NativeCallbackErrors.Report(error);
                }
            }
        }
    }

    /// <summary>
    /// Pacer thread, when the GPU set the completion event or as a backstop: applies the comparisons that are done and tells
    /// the leases whose areas changed in a frame they were not drawing yet.
    /// </summary>
    public void ResolvePending()
    {
        if (Volatile.Read(ref resultCount) == 0)
        {
            return;
        }
        using (NonPumpingLock.Enter(notifyGate))
        {
            ResolveReady();
            Tell();
        }
    }

    public void OnClosed(CaptureFailure failure)
    {
        Fail(failure);
        service.StopPixelsLater(this);
    }

    private static void NotifyState(CaptureService.Lease[] targets, CaptureState next)
    {
        foreach (var lease in targets)
        {
            try
            {
                lease.Consumer.OnStateChanged(lease, next);
            }
            catch (Exception error)
            {
                NativeCallbackErrors.Report(error);
            }
        }
    }

    /// <summary>
    /// Starts the comparison of a stored frame with the one before it (under notifyGate). Without a comparer, an earlier
    /// frame or a free comparison, the frame counts as changed everywhere; when no lease shows any pixel it changes nothing.
    /// </summary>
    private Result Begin(long frame, nint previous, nint current, int width, int height, GraphicsDevices? bundle, nint completed)
    {
        if (previous == 0 || bundle is null || service.FrameComparer is not { } comparer)
        {
            Interlocked.Increment(ref framesUncompared);
            return new Result(frame, ResultKind.Everything, 0);
        }
        using (NonPumpingLock.Enter(stateGate))
        {
            if (builtInterest != interestVersion)
            {
                BuildTiles(frame, comparer.TileSize, width, height);
            }
        }
        if (tilesEmpty)
        {
            return new Result(frame, ResultKind.Nothing, 0);
        }
        try
        {
            if (tracker is null)
            {
                tracker = comparer.Create(bundle, width, height);
                trackerHasTiles = false;
            }
            if (!trackerHasTiles)
            {
                tracker.SetTiles(tiles);
                trackerHasTiles = true;
            }
            if (tracker.TryBegin(previous, current, completed, out var ticket))
            {
                Interlocked.Increment(ref framesCompared);
                return new Result(frame, ResultKind.Pending, ticket, Stopwatch.GetTimestamp());
            }
        }
        catch (Exception error) when (error is NativeCallException or ObjectDisposedException or InvalidOperationException)
        {
            // A lost or released capture device: the frame counts as changed, and the device's loss is handled elsewhere.
            DropTracker();
        }
        Interlocked.Increment(ref framesUncompared);
        return new Result(frame, ResultKind.Everything, 0);
    }

    /// <summary>
    /// The lease bits of every tile from the leases' areas (under notifyGate and stateGate). Results of frames compared before
    /// these tiles count as changed for a lease whose areas changed since.
    /// </summary>
    private void BuildTiles(long frame, int tileSize, int width, int height)
    {
        int columns = (width + tileSize - 1) / tileSize, rows = (height + tileSize - 1) / tileSize;
        if (tiles.Length != columns * rows)
        {
            tiles = new uint[columns * rows];
        }
        else
        {
            Array.Clear(tiles);
        }
        var any = false;
        foreach (var lease in leases)
        {
            var bit = 1u << lease.Bit;
            if (lease.CountedFrom == long.MaxValue)
            {
                lease.CountedFrom = frame;
            }
            if (lease.WholeDisplay)
            {
                for (var index = 0; index < tiles.Length; index++)
                {
                    tiles[index] |= bit;
                }
                any = true;
                continue;
            }
            for (var area = 0; area < lease.InterestCount; area++)
            {
                var rect = lease.Interest[area];
                int left = Math.Max(rect.Left, 0), top = Math.Max(rect.Top, 0), right = Math.Min(rect.Right, width), bottom = Math.Min(rect.Bottom, height);
                if (right <= left || bottom <= top)
                {
                    continue;
                }
                for (var row = top / tileSize; row <= (bottom - 1) / tileSize; row++)
                {
                    for (var column = left / tileSize; column <= (right - 1) / tileSize; column++)
                    {
                        tiles[row * columns + column] |= bit;
                    }
                }
                any = true;
            }
        }
        tilesEmpty = !any;
        trackerHasTiles = false;
        builtInterest = interestVersion;
    }

    /// <summary>Applies, in frame order, the comparisons that are done (under notifyGate); leases to tell go into <see cref="told"/>.</summary>
    private void ResolveReady()
    {
        while (resultCount > 0)
        {
            var next = results[resultHead];
            uint changed = 0;
            var everything = false;
            switch (next.Kind)
            {
                case ResultKind.Everything:
                    everything = true;
                    break;
                case ResultKind.Nothing:
                    break;
                default:
                    if (tracker is not { } current)
                    {
                        everything = true;
                        break;
                    }
                    try
                    {
                        if (!current.TryRead(next.Ticket, out changed))
                        {
                            return;
                        }
                        var delay = Stopwatch.GetTimestamp() - next.Started;
                        Interlocked.Increment(ref resultsRead);
                        Interlocked.Add(ref resultDelayTicks, delay);
                        if (delay > resultDelayMax)
                        {
                            Interlocked.Exchange(ref resultDelayMax, delay);
                        }
                    }
                    catch (Exception error) when (error is NativeCallException or ObjectDisposedException or InvalidOperationException)
                    {
                        everything = true;
                        DropTracker();
                    }
                    break;
            }
            resultHead = (resultHead + 1) % ResultLimit;
            resultCount--;
            Apply(next.Frame, everything, changed);
        }
    }

    /// <summary>One frame's comparison, applied to every lease's content version (under notifyGate).</summary>
    private void Apply(long frame, bool everything, uint changedBits)
    {
        var anyChanged = false;
        using (NonPumpingLock.Enter(stateGate))
        {
            foreach (var lease in leases)
            {
                var changed = everything || frame < lease.CountedFrom || (changedBits & (1u << lease.Bit)) != 0;
                anyChanged |= changed;
                if (frame <= lease.Version)
                {
                    // The lease came or was told of a newer frame already.
                    lease.Hot = changed;
                    continue;
                }
                if (changed)
                {
                    lease.Version = frame;
                    if (lease.LastProvisional < frame)
                    {
                        // The lease got the version before this frame: it skipped the change, so it hears of it now.
                        told.Add(lease);
                    }
                }
                else if (lease.LastProvisional >= frame)
                {
                    // The lease drew this frame under its own version, which stays right: nothing changed.
                    lease.Version = frame;
                }
                lease.Hot = changed;
            }
            resolvedThrough = frame;
        }
        if (anyChanged)
        {
            Volatile.Write(ref unchangedInARow, 0);
        }
        else
        {
            Interlocked.Increment(ref framesUnchanged);
            Volatile.Write(ref unchangedInARow, unchangedInARow + 1);
        }
    }

    /// <summary>Tells the leases in <see cref="told"/> of the newest frame while the source is live (under notifyGate).</summary>
    private void Tell()
    {
        if (told.Count == 0)
        {
            return;
        }
        bool live;
        using (NonPumpingLock.Enter(stateGate))
        {
            live = state is CaptureState.Live;
        }
        foreach (var lease in told)
        {
            if (!live)
            {
                break;
            }
            Interlocked.Increment(ref renotified);
            try
            {
                lease.Consumer.OnFrameAvailable(lease);
            }
            catch (Exception error)
            {
                NativeCallbackErrors.Report(error);
            }
        }
        told.Clear();
    }

    /// <summary>
    /// Releases every stored frame that no consumer holds, retires the others and drops the comparisons with the tracker
    /// (under notifyGate). Every frame so far counts as compared, so the next one starts a new chain.
    /// </summary>
    private void LetFramesGo()
    {
        Span<FrameSlot> toRelease = stackalloc FrameSlot[SlotCount];
        var count = 0;
        using (NonPumpingLock.Enter(stateGate))
        {
            newest = -1;
            for (var index = 0; index < SlotCount; index++)
            {
                ref var slot = ref slots[index];
                if (!slot.InUse)
                {
                    continue;
                }
                if (slot.Uses == 0)
                {
                    toRelease[count++] = slot;
                    slot = default;
                }
                else
                {
                    slot.Retired = true;
                }
            }
            resolvedThrough = sequence;
        }
        resultHead = 0;
        resultCount = 0;
        told.Clear();
        DropTracker();
        for (var index = 0; index < count; index++)
        {
            FrameReferences.Release(toRelease[index].Closable, toRelease[index].Texture);
        }
    }

    private void DropTracker()
    {
        var current = tracker;
        tracker = null;
        trackerHasTiles = false;
        current?.Dispose();
    }

    private int FreeSlot()
    {
        for (var index = 0; index < SlotCount; index++)
        {
            if (!slots[index].InUse)
            {
                return index;
            }
        }
        return -1;
    }

    private enum ResultKind
    {
        /// <summary>The GPU compares the frame; <see cref="Result.Ticket"/> reads the result.</summary>
        Pending,

        /// <summary>The frame counts as changed for every lease.</summary>
        Everything,

        /// <summary>No lease shows any pixel: the frame changes nothing.</summary>
        Nothing,
    }

    private readonly record struct Result(long Frame, ResultKind Kind, int Ticket, long Started = 0);

    private struct FrameSlot
    {
        public nint Closable;
        public nint Texture;
        public int Width;
        public int Height;
        public long Sequence;
        public TimeSpan Time;
        public int Uses;
        public bool InUse;
        public bool Retired;
    }
}
