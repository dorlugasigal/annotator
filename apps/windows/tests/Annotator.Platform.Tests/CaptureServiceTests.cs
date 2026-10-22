using System.Diagnostics;
using System.Runtime.InteropServices;
using Annotator.Platform.Capture;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Platform.Threading;
using Annotator.Platform.Windowing;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.Platform.Tests;

/// <summary>The capture service's states, leases, frames and exclusions, with a fake pixel source on WARP.</summary>
public sealed class CaptureServiceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly DisplayInfo Display = new(1, @"\\.\ANNOTATOR-CAPTURE1", new PixelRect(0, 0, 64, 48), new PixelRect(0, 0, 64, 40), 120, true);
    private static readonly DisplayInfo Second = new(2, @"\\.\ANNOTATOR-CAPTURE2", new PixelRect(64, 0, 96, 32), new PixelRect(64, 0, 96, 32), 96, false);

    private static CaptureService Service(FakeCapturePlatform platform, WindowRegistry? registry = null, TimeSpan? stall = null, TimeSpan? firstFrame = null,
                                          ICaptureFrameComparer? comparer = null, TimeSpan? linger = null) =>
        CaptureService.Create(new CaptureServiceOptions
        {
            Registry = registry ?? new WindowRegistry(),
            Platform = platform,
            StallThreshold = stall ?? TimeSpan.FromSeconds(30),
            FirstFrameTimeout = firstFrame ?? TimeSpan.FromSeconds(30),
            FrameComparer = comparer,
            Linger = linger ?? TimeSpan.Zero,
        });

    private static void Until(Func<bool> condition, string what)
    {
        if (!SpinWait.SpinUntil(condition, Wait))
        {
            throw new TimeoutException($"Timed out waiting for {what}.");
        }
    }

    [Fact]
    public void OneSourceServesEveryLeaseOfADisplayAndTheLastReleaseStopsIt()
    {
        var platform = new FakeCapturePlatform();
        using var service = Service(platform);
        var first = new RecordingConsumer();
        var second = new RecordingConsumer();
        Assert.False(service.IsCapturing, "Nothing runs before a lease.");
        var a = service.Acquire(Display, first);
        var b = service.Acquire(Display, second);
        Assert.IsType<CaptureState.Starting>(a.State);
        Until(() => platform.Started.Count == 1, "the source to start");
        Assert.True(service.IsCapturing);
        Assert.False(a.TryAcquireFrame(out _), "No pixels before the first frame.");
        var source = platform.Started[0];
        // The pacer asks once per compositor clock tick, and the clock does not tick while the display is off or asleep.
        if (CompositorClock.Ticks())
        {
            Until(() => source.Invalidations > 0, "the pacer to ask for frames");
        }

        source.Push();
        Assert.IsType<CaptureState.Live>(a.State);
        Assert.Equal(1, first.Frames);
        Assert.Equal(1, second.Frames);
        Assert.Contains(first.States, state => state is CaptureState.Live);
        Assert.True(b.TryAcquireFrame(out var frame));
        using (frame)
        {
            Assert.Equal((64, 48), (frame.Width, frame.Height));
            Assert.Same(platform.Devices[0], frame.Devices);
        }

        a.Dispose();
        Assert.False(source.Disposed, "Another lease still needs the display.");
        Assert.IsType<CaptureState.Stopped>(a.State);
        Assert.True(service.IsCapturing, "Another lease still needs the display.");
        b.Dispose();
        Until(() => source.Disposed, "the source to stop");
        Until(() => platform.Uninitialized == 1, "the service to release the magnifier");
        Until(() => platform.Devices[0].State is DeviceState.Disposed, "the capture device to be released");
        Assert.False(service.IsCapturing);
        Assert.Equal(1, platform.Initialized);
        var asked = source.Invalidations;
        Thread.Sleep(100);
        Assert.Equal(asked, source.Invalidations);
    }

    [Fact]
    public void DisplaysHaveTheirOwnSources()
    {
        var platform = new FakeCapturePlatform();
        using var service = Service(platform);
        using var a = service.Acquire(Display, new RecordingConsumer());
        using var b = service.Acquire(Second, new RecordingConsumer());
        Until(() => platform.Started.Count == 2, "both sources to start");
        Assert.Equal([Display, Second], platform.Started.Select(source => source.Display).OrderBy(display => display.Handle));
        Assert.Single(platform.Devices);
    }

    [Fact]
    public void HeldFramesStayValidUntilReleasedAndOnlyTheNewestIsHandedOut()
    {
        var platform = new FakeCapturePlatform();
        using var service = Service(platform);
        using var lease = service.Acquire(Display, new RecordingConsumer());
        Until(() => platform.Started.Count == 1, "the source to start");
        var source = platform.Started[0];
        var first = source.Push();
        Assert.True(lease.TryAcquireFrame(out var held));
        Assert.Equal(first, held.Texture);
        var second = source.Push();
        Assert.Equal(2, FakePixelSource.References(first));
        Assert.True(lease.TryAcquireFrame(out var newest));
        Assert.Equal(second, newest.Texture);
        Assert.Equal(held.Sequence + 1, newest.Sequence);
        newest.Dispose();
        held.Dispose();
        Assert.Equal(1, FakePixelSource.References(first));
        source.Push();
        Assert.Equal(1, FakePixelSource.References(second));
        FakePixelSource.Drop(first);
        FakePixelSource.Drop(second);
    }

    /// <summary>A comparer whose trackers answer only when the test says so, like a GPU that has not finished yet.</summary>
    private sealed class ScriptedComparer(int tileSize = 16) : ICaptureFrameComparer
    {
        private readonly List<ScriptedTracker> trackers = [];

        public int TileSize { get; } = tileSize;

        public ScriptedTracker[] Trackers
        {
            get
            {
                lock (trackers)
                {
                    return [.. trackers];
                }
            }
        }

        public ScriptedTracker Current => Trackers[^1];

        public ICaptureChangeTracker Create(GraphicsDevices devices, int width, int height)
        {
            var tracker = new ScriptedTracker(width, height, TileSize);
            lock (trackers)
            {
                trackers.Add(tracker);
            }
            return tracker;
        }
    }

    private sealed class ScriptedTracker(int width, int height, int tileSize) : ICaptureChangeTracker
    {
        private readonly Lock gate = new();
        private readonly Dictionary<int, uint> done = [];
        private readonly List<uint[]> tiles = [];
        private readonly List<(nint Previous, nint Current, int Ticket)> begun = [];
        private int next;

        public int Columns { get; } = (width + tileSize - 1) / tileSize;

        public int Rows { get; } = (height + tileSize - 1) / tileSize;

        /// <summary>Answers given at once to the next comparisons, in order.</summary>
        public Queue<uint> AtOnce { get; } = new();

        public bool Refuse { get; set; }

        public bool Disposed { get; private set; }

        public uint[] Tiles
        {
            get
            {
                lock (gate)
                {
                    return tiles[^1];
                }
            }
        }

        public (nint Previous, nint Current, int Ticket)[] Begun
        {
            get
            {
                lock (gate)
                {
                    return [.. begun];
                }
            }
        }

        public void SetTiles(ReadOnlySpan<uint> leases)
        {
            Assert.Equal(Columns * Rows, leases.Length);
            lock (gate)
            {
                tiles.Add(leases.ToArray());
            }
        }

        public bool TryBegin(nint previous, nint current, nint completed, out int ticket)
        {
            lock (gate)
            {
                if (Refuse)
                {
                    ticket = 0;
                    return false;
                }
                ticket = ++next;
                begun.Add((previous, current, ticket));
                if (AtOnce.TryDequeue(out var bits))
                {
                    done[ticket] = bits;
                }
                return true;
            }
        }

        /// <summary>The GPU has compared the frame: these lease bits changed.</summary>
        public void Finish(int ticket, uint changed)
        {
            lock (gate)
            {
                done[ticket] = changed;
            }
        }

        public bool TryRead(int ticket, out uint changed)
        {
            lock (gate)
            {
                return done.Remove(ticket, out changed);
            }
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>The bit a lease got: its tiles' bits after its area was set, minus those of the other leases.</summary>
    private static uint BitOf(uint[] tiles, int column, int row, int columns) => tiles[row * columns + column];

    [Fact]
    public void ComparisonsAreReadWhenTheGpuIsDoneAndSetTheContentVersion()
    {
        var platform = new FakeCapturePlatform();
        var comparer = new ScriptedComparer();
        using var service = Service(platform, comparer: comparer);
        var consumer = new RecordingConsumer();
        var lease = service.Acquire(Display, consumer);
        Until(() => platform.Started.Count == 1, "the source to start");
        var source = platform.Started[0];
        var captured = service.Sources[0];
        List<nint> pushed = [source.Push()];
        Assert.Empty(comparer.Trackers);
        Assert.True(lease.TryAcquireFrame(out var first));
        var version = first.ContentVersion;
        first.Dispose();

        // Compared on the GPU; nothing waits. The first frame changed everything, so the next one is drawn at once under its
        // own version while its comparison runs.
        pushed.Add(source.Push());
        var tracker = comparer.Current;
        Assert.Equal((pushed[0], pushed[1]), (tracker.Begun[0].Previous, tracker.Begun[0].Current));
        Assert.All(tracker.Tiles, bits => Assert.NotEqual(0u, bits));
        Assert.True(lease.TryAcquireFrame(out var drawing));
        Assert.NotEqual(version, drawing.ContentVersion);
        var drawn = drawing.ContentVersion;
        drawing.Dispose();
        // The compared frame went back to the pool once the comparison was on the GPU's queue.
        Assert.Equal(1, FakePixelSource.References(pushed[0]));

        // The GPU found nothing new: that frame's version stays the one it was drawn with.
        tracker.Finish(tracker.Begun[0].Ticket, 0);
        captured.ResolvePending();
        Assert.True(lease.TryAcquireFrame(out var same));
        Assert.Equal(drawn, same.ContentVersion);
        same.Dispose();

        // Still now: a frame waits for its comparison under the version before it.
        pushed.Add(source.Push());
        Assert.True(lease.TryAcquireFrame(out var waiting));
        Assert.Equal(drawn, waiting.ContentVersion);
        waiting.Dispose();
        var heard = consumer.Frames;
        tracker.Finish(tracker.Begun[1].Ticket, 0);
        captured.ResolvePending();
        Assert.Equal(heard, consumer.Frames);

        // A change found later makes a new version, and the lease hears of the frame again.
        pushed.Add(source.Push());
        Assert.True(lease.TryAcquireFrame(out var skipped));
        Assert.Equal(drawn, skipped.ContentVersion);
        skipped.Dispose();
        heard = consumer.Frames;
        tracker.Finish(tracker.Begun[2].Ticket, uint.MaxValue);
        captured.ResolvePending();
        Assert.Equal(heard + 1, consumer.Frames);
        Assert.True(lease.TryAcquireFrame(out var changed));
        Assert.NotEqual(drawn, changed.ContentVersion);
        Assert.Equal(changed.Sequence, changed.ContentVersion);
        changed.Dispose();
        var statistics = service.Statistics;
        Assert.Equal((4L, 0L, 2L, 3L, 1L, 1L), (statistics.Received, statistics.Dropped, statistics.Unchanged, statistics.Compared, statistics.Uncompared,
                                                statistics.Renotified));

        lease.Dispose();
        Until(() => platform.Devices[0].State is DeviceState.Disposed, "the capture device to be released");
        Assert.True(tracker.Disposed, "The tracker goes with the source, before its device.");
        foreach (var texture in pushed)
        {
            FakePixelSource.Drop(texture);
        }
    }

    [Fact]
    public void LeasesOnlyHearOfChangesInTheirOwnAreas()
    {
        var platform = new FakeCapturePlatform();
        var comparer = new ScriptedComparer(tileSize: 16);
        using var service = Service(platform, comparer: comparer);
        var leftConsumer = new RecordingConsumer();
        var rightConsumer = new RecordingConsumer();
        using var left = service.Acquire(Display, leftConsumer);
        using var right = service.Acquire(Display, rightConsumer);
        Until(() => platform.Started.Count == 1, "the source to start");
        // The display is 64 x 48 pixels: 4 x 3 tiles of 16.
        left.SetInterest([new PixelRect(0, 0, 20, 16)]);
        right.SetInterest([new PixelRect(48, 32, 64, 48)]);
        var source = platform.Started[0];
        var captured = service.Sources[0];
        List<nint> pushed = [source.Push(), source.Push()];
        var tracker = comparer.Current;
        var tiles = tracker.Tiles;
        uint leftBit = BitOf(tiles, 0, 0, 4), rightBit = BitOf(tiles, 3, 2, 4);
        Assert.NotEqual(0u, leftBit);
        Assert.NotEqual(0u, rightBit);
        Assert.Equal(0u, leftBit & rightBit);
        Assert.Equal(leftBit, BitOf(tiles, 1, 0, 4));
        Assert.Equal([leftBit, leftBit, 0, 0, 0, 0, 0, 0, 0, 0, 0, rightBit], tiles);

        // Both were drawing; the second frame changed nothing either looks at.
        tracker.Finish(tracker.Begun[0].Ticket, 0);
        captured.ResolvePending();
        Assert.True(left.TryAcquireFrame(out var leftShown));
        Assert.True(right.TryAcquireFrame(out var rightShown));
        long leftVersion = leftShown.ContentVersion, rightVersion = rightShown.ContentVersion;
        leftShown.Dispose();
        rightShown.Dispose();

        // A change on the right only.
        pushed.Add(source.Push());
        var leftHeard = leftConsumer.Frames;
        var rightHeard = rightConsumer.Frames;
        tracker.Finish(tracker.Begun[1].Ticket, rightBit);
        captured.ResolvePending();
        Assert.Equal(leftHeard, leftConsumer.Frames);
        Assert.Equal(rightHeard + 1, rightConsumer.Frames);
        Assert.True(left.TryAcquireFrame(out var leftNow));
        Assert.True(right.TryAcquireFrame(out var rightNow));
        Assert.Equal(leftVersion, leftNow.ContentVersion);
        Assert.NotEqual(rightVersion, rightNow.ContentVersion);
        leftNow.Dispose();
        rightNow.Dispose();

        // An empty area looks at nothing: no tile carries the lease's bit.
        right.SetInterest([]);
        pushed.Add(source.Push());
        Assert.Equal([leftBit, leftBit, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], tracker.Tiles);
        foreach (var texture in pushed)
        {
            FakePixelSource.Drop(texture);
        }
    }

    [Fact]
    public void ResultsApplyInFrameOrderAndAFrameWithoutAComparisonCountsAsChanged()
    {
        var platform = new FakeCapturePlatform();
        var comparer = new ScriptedComparer();
        using var service = Service(platform, comparer: comparer);
        var consumer = new RecordingConsumer();
        using var lease = service.Acquire(Display, consumer);
        Until(() => platform.Started.Count == 1, "the source to start");
        var source = platform.Started[0];
        var captured = service.Sources[0];
        List<nint> pushed = [source.Push(), source.Push()];
        var tracker = comparer.Current;
        // Still: the second frame changed nothing, so later frames wait for their comparisons.
        tracker.Finish(tracker.Begun[0].Ticket, 0);
        captured.ResolvePending();
        Assert.True(lease.TryAcquireFrame(out var shown));
        var version = shown.ContentVersion;
        shown.Dispose();

        pushed.Add(source.Push());
        tracker.Refuse = true;
        pushed.Add(source.Push());
        // The fourth frame had no free comparison; it counts as changed, but only after the third frame's result.
        Assert.True(lease.TryAcquireFrame(out var waiting));
        Assert.Equal(version, waiting.ContentVersion);
        waiting.Dispose();
        tracker.Finish(tracker.Begun[1].Ticket, 0);
        captured.ResolvePending();
        Assert.True(lease.TryAcquireFrame(out var changed));
        Assert.Equal(changed.Sequence, changed.ContentVersion);
        changed.Dispose();
        Assert.Equal((2L, 2L), (service.Statistics.Compared, service.Statistics.Uncompared));
        foreach (var texture in pushed)
        {
            FakePixelSource.Drop(texture);
        }
    }

    [Fact]
    public void StillSourcesAskForFramesAtTheStillRateUntilAFrameChangesOrInputArrives()
    {
        var platform = new FakeCapturePlatform();
        var comparer = new ScriptedComparer();
        using var service = CaptureService.Create(new CaptureServiceOptions { Registry = new WindowRegistry(), Platform = platform, FrameComparer = comparer });
        using var devices = GraphicsDevices.Create(new GraphicsDeviceOptions { UseWarp = true });
        // A source of its own, outside the service's pacer, so the test alone asks.
        var source = new CaptureSource(service, Display, CaptureState.Starting.Instance);
        var consumer = new RecordingConsumer();
        var lease = new CaptureService.Lease(service, source, consumer);
        source.AddLease(lease);
        source.Prepare(devices, 0);
        var pixels = new FakePixelSource(Display, devices, [], source);
        const int StillFrames = 3;
        var period = Stopwatch.Frequency / 60;
        var now = Stopwatch.GetTimestamp();
        var pushed = new List<nint> { pixels.Push() };
        void Push(uint changed)
        {
            pushed.Add(pixels.Push());
            comparer.Current.Finish(comparer.Current.Begun[^1].Ticket, changed);
            source.ResolvePending();
        }
        for (var frame = 0; frame < StillFrames - 1; frame++)
        {
            Push(0);
        }
        Assert.True(source.WantsFrame(now, input: false, StillFrames, period), "Two repeated frames do not make a source still yet.");
        Assert.True(source.WantsFrame(now + 1, input: false, StillFrames, period));

        Push(0);
        Assert.False(source.WantsFrame(now + 2, input: false, StillFrames, period), "Still: at most one frame per still period.");
        Assert.True(source.WantsFrame(now + 1 + period, input: false, StillFrames, period), "The next still period asks again.");
        Assert.False(source.WantsFrame(now + 2 + period, input: false, StillFrames, period));
        Assert.True(source.WantsFrame(now + 3 + period, input: true, StillFrames, period), "Input asks at once.");
        Assert.True(source.WantsFrame(now + 4 + period, input: false, StillFrames, period), "And keeps asking on every tick.");

        for (var frame = 0; frame < StillFrames; frame++)
        {
            Push(0);
        }
        Assert.False(source.WantsFrame(now + 5 + period, input: false, StillFrames, period), "Still again.");
        Push(uint.MaxValue);
        Assert.True(source.WantsFrame(now + 6 + period, input: false, StillFrames, period), "A changed frame ends the still rate.");

        // Changes outside every lease's area keep the source still: the comparison only reports the leases' bits.
        lease.SetInterest([]);
        for (var frame = 0; frame < StillFrames; frame++)
        {
            pushed.Add(pixels.Push());
        }
        Assert.False(source.WantsFrame(now + 7 + period, input: false, StillFrames, period), "Nobody looks at anything.");
        source.Detach();
        foreach (var texture in pushed)
        {
            FakePixelSource.Drop(texture);
        }
    }

    [Fact]
    public void ThePausedCaptureServesALeaseThatComesBackWithNewFramesOnly()
    {
        var platform = new FakeCapturePlatform();
        using var service = Service(platform, linger: TimeSpan.FromSeconds(30));
        var consumer = new RecordingConsumer();
        var lease = service.Acquire(Display, consumer);
        Until(() => platform.Started.Count == 1, "the source to start");
        var source = platform.Started[0];
        var old = source.Push();
        Assert.True(service.IsCapturing);

        lease.Dispose();
        Until(() => !service.IsCapturing, "the source to pause");
        Assert.True(service.HoldsCapture, "The paused capture stays open.");
        Assert.False(source.Disposed);
        Assert.Equal(1, FakePixelSource.References(old));
        var asked = source.Invalidations;
        Thread.Sleep(100);
        Assert.Equal(asked, source.Invalidations);

        var again = new RecordingConsumer();
        using var back = service.Acquire(Display, again);
        Until(() => service.IsCapturing && back.State is CaptureState.Starting, "the paused capture to resume");
        Assert.Single(platform.Started);
        Assert.Equal((1L, 1L), (service.Statistics.SessionsStarted, service.Statistics.SessionsReused));
        Assert.False(back.TryAcquireFrame(out _), "No frame from before the pause.");
        Assert.True(source.Exclusions.Count >= 3, "The exclude list was applied again before frames were taken.");
        var fresh = source.Push();
        Assert.IsType<CaptureState.Live>(back.State);
        Assert.True(back.TryAcquireFrame(out var frame));
        using (frame)
        {
            Assert.Equal(fresh, frame.Texture);
        }
        FakePixelSource.Drop(old);
        FakePixelSource.Drop(fresh);
    }

    [Fact]
    public async Task WindowsRegisteredWhilePausedAreLeftOutBeforeTheCaptureResumes()
    {
        var platform = new FakeCapturePlatform();
        var registry = new WindowRegistry();
        using var service = Service(platform, registry, linger: TimeSpan.FromSeconds(30));
        var thread = DispatcherThread.Start("Annotator capture test windows");
        try
        {
            var lease = service.Acquire(Display, new RecordingConsumer());
            Until(() => platform.Started.Count == 1, "the source to start");
            var source = platform.Started[0];
            FakePixelSource.Drop(source.Push());
            lease.Dispose();
            Until(() => !service.IsCapturing && service.HoldsCapture, "the source to pause");

            // A paused capture takes no frames: registering and showing a window waits for nothing and applies nothing.
            nint late = 0;
            WindowRegistration? registration = null;
            var whilePaused = source.Exclusions.Count;
            await thread.Run(() =>
            {
                late = TestWindow.Create();
                registration = registry.Register(late, WindowLayer.Popover);
                PInvoke.ShowWindow((HWND)late, SHOW_WINDOW_CMD.SW_SHOWNA);
                PInvoke.ShowWindow((HWND)late, SHOW_WINDOW_CMD.SW_HIDE);
            });
            Assert.Equal(whilePaused, source.Exclusions.Count);

            using var back = service.Acquire(Display, new RecordingConsumer());
            Until(() => source.Exclusions.Count > whilePaused, "the resumed capture to apply the list");
            Assert.Contains(late, source.Exclusions[^1]);
            Assert.True(service.IsCapturing);
            Assert.False(back.TryAcquireFrame(out _), "No frame before the list was applied.");

            // Back to taking frames, the first show of a window that was never shown applies the list again inside ShowWindow.
            nint fresh = 0;
            int beforeShow = 0, afterShow = 0;
            await thread.Run(() =>
            {
                fresh = TestWindow.Create();
                using (registry.Register(fresh, WindowLayer.Popover))
                {
                    beforeShow = source.Exclusions.Count;
                    PInvoke.ShowWindow((HWND)fresh, SHOW_WINDOW_CMD.SW_SHOWNA);
                    afterShow = source.Exclusions.Count;
                }
                TestWindow.Destroy(fresh);
            });
            Assert.True(afterShow > beforeShow, "The resumed capture watches windows that were never shown.");
            registration!.Dispose();
            await thread.Run(() => TestWindow.Destroy(late));
        }
        finally
        {
            await thread.Stop();
        }
    }

    [Fact]
    public void APausedCaptureEndsAfterItsLingerTimeOrASuspension()
    {
        var platform = new FakeCapturePlatform();
        using (var service = Service(platform, linger: TimeSpan.FromMilliseconds(300)))
        {
            var first = service.Acquire(Display, new RecordingConsumer());
            Until(() => platform.Started.Count == 1, "the source to start");
            first.Dispose();
            Until(() => platform.Started[0].Disposed && service.Sources.Length == 0, "the linger time to end the capture");
            Until(() => platform.Uninitialized == 1 && platform.Devices[0].State is DeviceState.Disposed, "the service to release everything");
            Assert.False(service.HoldsCapture);
        }

        platform = new FakeCapturePlatform();
        using (var service = Service(platform, linger: TimeSpan.FromSeconds(30)))
        {
            var first = service.Acquire(Display, new RecordingConsumer());
            Until(() => platform.Started.Count == 1, "the source to start");
            first.Dispose();
            Until(() => !service.IsCapturing && service.HoldsCapture, "the source to pause");
            service.Suspend(CaptureSuspension.SessionLocked);
            Until(() => platform.Started[0].Disposed && !service.HoldsCapture && service.Sources.Length == 0, "the suspension to end the paused capture");
            service.Resume();
            using var lease = service.Acquire(Display, new RecordingConsumer());
            Until(() => platform.Started.Count == 2, "a new capture for the new lease");
        }
    }

    [Fact]
    public void FramesOfAnotherSizeAreDropped()
    {
        var platform = new FakeCapturePlatform();
        using var service = Service(platform);
        var consumer = new RecordingConsumer();
        using var lease = service.Acquire(Display, consumer);
        Until(() => platform.Started.Count == 1, "the source to start");
        platform.Started[0].Push(width: 32);
        Assert.IsType<CaptureState.Starting>(lease.State);
        Assert.Equal(0, consumer.Frames);
    }

    [Fact]
    public void StartsWithoutAFirstFrameFailAndResumeTriesAgain()
    {
        var platform = new FakeCapturePlatform();
        using var service = Service(platform, firstFrame: TimeSpan.FromMilliseconds(150));
        var consumer = new RecordingConsumer();
        using var lease = service.Acquire(Display, consumer);
        Until(() => lease.State is CaptureState.Failed, "the first-frame timeout");
        var failed = Assert.IsType<CaptureState.Failed>(lease.State);
        Assert.Equal(CaptureFailureKind.NoFrames, failed.Failure.Kind);
        Until(() => platform.Started[0].Disposed, "the failed source to stop");
        Until(() => platform.Uninitialized == 1, "the idle service to release everything");

        service.Resume();
        Until(() => platform.Started.Count == 2, "the source to start again");
        platform.Started[1].Push();
        Assert.IsType<CaptureState.Live>(lease.State);
    }

    [Fact]
    public void StalledSourcesProvideNoPixelsAndRecoverWithTheNextFrame()
    {
        var platform = new FakeCapturePlatform();
        using var service = Service(platform, stall: TimeSpan.FromMilliseconds(120));
        var consumer = new RecordingConsumer();
        using var lease = service.Acquire(Display, consumer);
        Until(() => platform.Started.Count == 1, "the source to start");
        platform.Started[0].Push();
        Until(() => lease.State is CaptureState.Stalled, "the stall");
        Assert.False(lease.TryAcquireFrame(out _), "A stalled source never hands out its old frame.");
        Assert.True(Assert.IsType<CaptureState.Stalled>(lease.State).Waited >= TimeSpan.FromMilliseconds(120));
        platform.Started[0].Push();
        Assert.IsType<CaptureState.Live>(lease.State);
        Assert.Equal(["Live", "Stalled", "Live"], consumer.States.Select(state => state.GetType().Name));
    }

    [Fact]
    public void StartFailuresAreTypedAndReleaseEverything()
    {
        var platform = new FakeCapturePlatform
        {
            FailStart = new CaptureFailure(CaptureFailureKind.BorderRequired, "GraphicsCaptureSession.IsBorderRequired stayed true"),
        };
        using var service = Service(platform);
        using var lease = service.Acquire(Display, new RecordingConsumer());
        Until(() => lease.State is CaptureState.Failed, "the start failure");
        Assert.Equal(CaptureFailureKind.BorderRequired, Assert.IsType<CaptureState.Failed>(lease.State).Failure.Kind);
        Until(() => platform.Uninitialized == 1, "the service to release the magnifier");
        Until(() => platform.Devices[0].State is DeviceState.Disposed, "the device to be released");
    }

    /// <summary>
    /// A packaged app that Windows has not asked yet fails closed at once and keeps the capture thread free: a window
    /// registered meanwhile is still left out in time, and capture starts once the app asked and the person allowed it.
    /// </summary>
    [Fact]
    public void AStartWithoutConsentFailsAtOnceAndStartsAgainOnceAllowed()
    {
        var platform = new FakeCapturePlatform
        {
            FailStart = new CaptureConsent(CaptureAccessStatus.UserPromptRequired, CaptureAccessStatus.UserPromptRequired).Failure,
        };
        var registry = new WindowRegistry();
        using var service = Service(platform, registry);
        var clock = Stopwatch.StartNew();
        using var lease = service.Acquire(Display, new RecordingConsumer());
        Until(() => lease.State is CaptureState.Failed, "the start failure");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"The start waited {clock.Elapsed.TotalSeconds:F1} s for an answer.");
        Assert.Equal(CaptureFailureKind.AccessNeeded, Assert.IsType<CaptureState.Failed>(lease.State).Failure.Kind);
        Assert.Empty(platform.Started);

        platform.FailStart = null;
        service.Resume();
        Until(() => platform.Started.Count == 1, "the source to start after consent");
        var window = TestWindow.Create();
        try
        {
            using var registration = registry.Register(window, WindowLayer.Popover);
            var source = platform.Started[0];
            Until(() =>
            {
                lock (source.Exclusions)
                {
                    return source.Exclusions.Any(list => list.Contains(window));
                }
            }, "the new window to be left out");
        }
        finally
        {
            TestWindow.Destroy(window);
        }
        platform.Started[0].Push();
        Assert.IsType<CaptureState.Live>(lease.State);
    }

    [Fact]
    public void UnsupportedSystemsFail()
    {
        var platform = new FakeCapturePlatform { Supported = false };
        using var service = Service(platform);
        using var lease = service.Acquire(Display, new RecordingConsumer());
        Until(() => lease.State is CaptureState.Failed, "the failure");
        Assert.Equal(CaptureFailureKind.Unsupported, Assert.IsType<CaptureState.Failed>(lease.State).Failure.Kind);
        Assert.Empty(platform.Started);
    }

    [Fact]
    public void SourcesThatCloseFailClosed()
    {
        var platform = new FakeCapturePlatform();
        using var service = Service(platform);
        using var lease = service.Acquire(Display, new RecordingConsumer());
        Until(() => platform.Started.Count == 1, "the source to start");
        var source = platform.Started[0];
        source.Push();
        source.Close(new CaptureFailure(CaptureFailureKind.StreamClosed, "GraphicsCaptureItem.Closed"));
        Assert.Equal(CaptureFailureKind.StreamClosed, Assert.IsType<CaptureState.Failed>(lease.State).Failure.Kind);
        Assert.False(lease.TryAcquireFrame(out _));
        Until(() => source.Disposed, "the closed source to stop");
    }

    [Fact]
    public void SuspensionStopsCaptureUntilResume()
    {
        var platform = new FakeCapturePlatform();
        using var service = Service(platform);
        var consumer = new RecordingConsumer();
        using var lease = service.Acquire(Display, consumer);
        Until(() => platform.Started.Count == 1, "the source to start");
        platform.Started[0].Push();

        service.Suspend(CaptureSuspension.RequestedByApp);
        Assert.Equal(CaptureSuspension.RequestedByApp, service.Suspension);
        Until(() => lease.State is CaptureState.Suspended, "the suspension");
        Until(() => platform.Started[0].Disposed && platform.Uninitialized == 1, "capture to stop");
        using var later = service.Acquire(Second, new RecordingConsumer());
        Assert.Equal(new CaptureState.Suspended(CaptureSuspension.RequestedByApp), later.State);
        Thread.Sleep(100);
        Assert.Single(platform.Started);

        service.Resume();
        Until(() => platform.Started.Count == 3, "both displays to start again");
        Assert.Null(service.Suspension);
        Assert.IsType<CaptureState.Starting>(lease.State);
    }

    [Fact]
    public void ALockedSessionKeepsCaptureSuspended()
    {
        var platform = new FakeCapturePlatform { Locked = true };
        using var service = Service(platform);
        using var lease = service.Acquire(Display, new RecordingConsumer());
        Until(() => lease.State is CaptureState.Suspended, "the locked session to suspend capture");
        Assert.Equal(new CaptureState.Suspended(CaptureSuspension.SessionLocked), lease.State);
        service.Resume();
        Thread.Sleep(100);
        Assert.Equal(CaptureSuspension.SessionLocked, service.Suspension);
        Assert.Empty(platform.Started);

        platform.Locked = false;
        service.Resume();
        Until(() => platform.Started.Count == 1, "capture to start after the unlock");
    }

    [Fact]
    public async Task WindowsRegisteredWhileCapturingAreLeftOutBeforeRegisterReturns()
    {
        var platform = new FakeCapturePlatform();
        var registry = new WindowRegistry();
        using var service = Service(platform, registry);
        var thread = DispatcherThread.Start("Annotator capture test windows");
        try
        {
            nint early = await thread.Run(() => TestWindow.Create());
            using var earlyRegistration = registry.Register(early, WindowLayer.Toolbar);
            using var lease = service.Acquire(Display, new RecordingConsumer());
            Until(() => platform.Started.Count == 1, "the source to start");
            var source = platform.Started[0];
            Until(() => source.Exclusions.Count >= 2, "the list to be applied with the windows' visibility known");
            Assert.Equal([early], source.Exclusions[^1]);

            nint late = 0;
            WindowRegistration? lateRegistration = null;
            int beforeRegister = 0, afterRegister = 0, beforeShow = 0, afterShow = 0, afterSecondShow = 0;
            await thread.Run(() =>
            {
                late = TestWindow.Create();
                beforeRegister = source.Exclusions.Count;
                lateRegistration = registry.Register(late, WindowLayer.Popover);
                // Register returned: the capture thread has already applied the new list.
                afterRegister = source.Exclusions.Count;

                // The magnifier leaves out only windows shown at least once, so the first show applies the list again,
                // inside the ShowWindow call, before this thread can paint.
                beforeShow = source.Exclusions.Count;
                PInvoke.ShowWindow((HWND)late, SHOW_WINDOW_CMD.SW_SHOWNA);
                afterShow = source.Exclusions.Count;
                PInvoke.ShowWindow((HWND)late, SHOW_WINDOW_CMD.SW_HIDE);
                PInvoke.ShowWindow((HWND)late, SHOW_WINDOW_CMD.SW_SHOWNA);
                afterSecondShow = source.Exclusions.Count;
            });
            Assert.True(afterRegister > beforeRegister, "Registering applied the list before Register returned.");
            Assert.True(afterShow > beforeShow, "The first show applied the list inside ShowWindow.");
            Assert.Equal(afterShow, afterSecondShow);
            Assert.Equal([early, late], source.Exclusions[^1]);

            var beforeRemoval = source.Exclusions.Count;
            lateRegistration!.Dispose();
            Until(() => source.Exclusions.Count > beforeRemoval, "the removal to be applied");
            Assert.Equal([early], source.Exclusions[^1]);
            await thread.Run(() =>
            {
                TestWindow.Destroy(late);
                TestWindow.Destroy(early);
            });
        }
        finally
        {
            await thread.Stop();
        }
    }

    [Fact]
    public async Task FailedExclusionsFailClosed()
    {
        var platform = new FakeCapturePlatform();
        var registry = new WindowRegistry();
        using var service = Service(platform, registry);
        using var lease = service.Acquire(Display, new RecordingConsumer());
        Until(() => platform.Started.Count == 1, "the source to start");
        var source = platform.Started[0];
        source.Push();
        source.FailExclusions = true;
        var thread = DispatcherThread.Start("Annotator capture test windows");
        try
        {
            await thread.Run(() =>
            {
                var window = TestWindow.Create();
                using (registry.Register(window, WindowLayer.Popover))
                {
                }
                TestWindow.Destroy(window);
            });
        }
        finally
        {
            await thread.Stop();
        }
        Assert.Equal(CaptureFailureKind.ExclusionFailed, Assert.IsType<CaptureState.Failed>(lease.State).Failure.Kind);
        Assert.False(lease.TryAcquireFrame(out _));
        Until(() => source.Disposed, "the source to stop");
    }

    [Fact]
    public void ALostDeviceRestartsCaptureOnANewOne()
    {
        var platform = new FakeCapturePlatform();
        using var service = Service(platform);
        var consumer = new RecordingConsumer();
        using var lease = service.Acquire(Display, consumer);
        Until(() => platform.Started.Count == 1, "the source to start");
        platform.Started[0].Push();
        platform.Devices[0].ReportFailure(DeviceLoss.DXGI_ERROR_DEVICE_REMOVED);
        Until(() => platform.Started.Count == 2, "the source to start on a new device");
        Assert.True(platform.Started[0].Disposed);
        Assert.Equal(2, platform.Devices.Count);
        Assert.IsType<DeviceState.Disposed>(platform.Devices[0].State);
        Assert.IsType<CaptureState.Starting>(lease.State);
        platform.Started[1].Push();
        Assert.True(lease.TryAcquireFrame(out var frame));
        using (frame)
        {
            Assert.Same(platform.Devices[1], frame.Devices);
        }
    }

    [Fact]
    public void DisposingTheServiceStopsEverything()
    {
        var platform = new FakeCapturePlatform();
        var consumer = new RecordingConsumer();
        ICaptureLease lease;
        using (var service = Service(platform))
        {
            lease = service.Acquire(Display, consumer);
            Until(() => platform.Started.Count == 1, "the source to start");
            platform.Started[0].Push();
        }
        Assert.True(platform.Started[0].Disposed);
        Assert.Equal(1, platform.Uninitialized);
        Assert.IsType<CaptureState.Stopped>(lease.State);
        lease.Dispose();
    }

    /// <summary>
    /// Explicit: needs the real desktop and shows a small magenta window in the primary display's top-left corner for
    /// about a second. The real source captures the display at the display's rate and leaves the registered window out.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task RealCaptureLeavesRegisteredWindowsOut()
    {
        var display = DisplayEnumerator.GetDisplays().Single(candidate => candidate.IsPrimary);
        var registry = new WindowRegistry();
        using var service = CaptureService.Create(new CaptureServiceOptions { Registry = registry });
        var thread = DispatcherThread.Start("Annotator capture test magenta");
        nint window = 0;
        WindowRegistration? registration = null;
        try
        {
            var consumer = new PixelProbe(new PixelRect(display.Bounds.Left + 20, display.Bounds.Top + 20, display.Bounds.Left + 60, display.Bounds.Top + 60), display);
            using var lease = service.Acquire(display, consumer);
            Until(() => lease.State is CaptureState.Live, "real frames");
            await thread.Run(() =>
            {
                window = TestWindow.Create(display.Bounds.Left + 10, display.Bounds.Top + 10, 64, 64, magenta: true);
                registration = registry.Register(window, WindowLayer.Popover);
                PInvoke.ShowWindow((HWND)window, SHOW_WINDOW_CMD.SW_SHOWNA);
            });
            var clock = Stopwatch.StartNew();
            consumer.Reset();
            Until(() => consumer.Checked >= 60, "60 checked frames");
            Assert.Equal(0, consumer.Magenta);
            Assert.True(consumer.Frames / clock.Elapsed.TotalSeconds > 50, $"{consumer.Frames / clock.Elapsed.TotalSeconds:F1} frames per second");
        }
        finally
        {
            await thread.Run(() =>
            {
                registration?.Dispose();
                if (window != 0)
                {
                    TestWindow.Destroy(window);
                }
            });
            await thread.Stop();
        }
    }

    /// <summary>
    /// Explicit: needs the real desktop and shows a small magenta window in the primary display's top-left corner for
    /// about two seconds. A capture that starts while a registered window shows leaves it out from its very first frame:
    /// the magnifier's default picture (the screen from its corner, with no exclude list) never reaches a frame.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task RealCaptureLeavesRegisteredWindowsOutOfItsFirstFrames()
    {
        var display = DisplayEnumerator.GetDisplays().Single(candidate => candidate.IsPrimary);
        var registry = new WindowRegistry();
        var thread = DispatcherThread.Start("Annotator capture test magenta");
        nint window = 0;
        WindowRegistration? registration = null;
        try
        {
            await thread.Run(() =>
            {
                window = TestWindow.Create(display.Bounds.Left + 10, display.Bounds.Top + 10, 64, 64, magenta: true);
                registration = registry.Register(window, WindowLayer.Popover);
                PInvoke.ShowWindow((HWND)window, SHOW_WINDOW_CMD.SW_SHOWNA);
            });
            Thread.Sleep(200);
            for (var start = 0; start < 4; start++)
            {
                using var service = CaptureService.Create(new CaptureServiceOptions { Registry = registry, Linger = TimeSpan.Zero });
                var consumer = new PixelProbe(new PixelRect(display.Bounds.Left + 20, display.Bounds.Top + 20, display.Bounds.Left + 60, display.Bounds.Top + 60), display);
                using var lease = service.Acquire(display, consumer);
                Until(() => consumer.Checked >= 10, "10 checked frames from the start");
                Assert.Equal(0, consumer.Magenta);
            }
        }
        finally
        {
            await thread.Run(() =>
            {
                registration?.Dispose();
                if (window != 0)
                {
                    TestWindow.Destroy(window);
                }
            });
            await thread.Stop();
        }
    }

    private sealed class RecordingConsumer : ICaptureConsumer
    {
        private readonly List<CaptureState> states = [];
        private int frames;

        public int Frames => Volatile.Read(ref frames);

        public CaptureState[] States
        {
            get
            {
                lock (states)
                {
                    return [.. states];
                }
            }
        }

        public void OnFrameAvailable(ICaptureLease lease) => Interlocked.Increment(ref frames);

        public void OnStateChanged(ICaptureLease lease, CaptureState state)
        {
            lock (states)
            {
                states.Add(state);
            }
        }
    }

    /// <summary>Counts frames that show pure magenta inside an area, by reading the area back from the GPU.</summary>
    private sealed unsafe class PixelProbe(PixelRect area, DisplayInfo display) : ICaptureConsumer
    {
        public int Frames;
        public int Checked;
        public int Magenta;

        public void Reset()
        {
            Volatile.Write(ref Frames, 0);
            Volatile.Write(ref Checked, 0);
            Volatile.Write(ref Magenta, 0);
        }

        public void OnFrameAvailable(ICaptureLease lease)
        {
            Interlocked.Increment(ref Frames);
            if (!lease.TryAcquireFrame(out var frame))
            {
                return;
            }
            using (frame)
            {
                var pixels = Read(frame);
                foreach (var pixel in pixels)
                {
                    if ((pixel & 0x00FFFFFF) == 0x00FF00FF)
                    {
                        Interlocked.Increment(ref Magenta);
                        break;
                    }
                }
                Interlocked.Increment(ref Checked);
            }
        }

        public void OnStateChanged(ICaptureLease lease, CaptureState state)
        {
        }

        private uint[] Read(CaptureFrame frame)
        {
            var device = (ID3D11Device*)frame.Devices.D3D11Device;
            var context = (ID3D11DeviceContext*)frame.Devices.D3D11DeviceContext;
            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)area.Width,
                Height = (uint)area.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
                CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
            };
            ID3D11Texture2D* staging;
            device->CreateTexture2D(&desc, null, &staging).ThrowOnFailure();
            try
            {
                var box = new D3D11_BOX
                {
                    left = (uint)(area.Left - display.Bounds.Left),
                    top = (uint)(area.Top - display.Bounds.Top),
                    right = (uint)(area.Right - display.Bounds.Left),
                    bottom = (uint)(area.Bottom - display.Bounds.Top),
                    back = 1,
                };
                context->CopySubresourceRegion((ID3D11Resource*)staging, 0, 0, 0, 0, (ID3D11Resource*)frame.Texture, 0, &box);
                D3D11_MAPPED_SUBRESOURCE mapped;
                context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped).ThrowOnFailure();
                var pixels = new uint[area.Width * area.Height];
                for (var row = 0; row < area.Height; row++)
                {
                    new ReadOnlySpan<uint>((byte*)mapped.pData + (row * mapped.RowPitch), area.Width).CopyTo(pixels.AsSpan(row * area.Width));
                }
                context->Unmap((ID3D11Resource*)staging, 0);
                return pixels;
            }
            finally
            {
                staging->Release();
            }
        }
    }
}

/// <summary>A test pixel source: frames are WARP textures pushed by the test.</summary>
internal sealed unsafe class FakePixelSource(DisplayInfo display, GraphicsDevices devices, nint[] exclusions, IPixelSink sink) : IPixelSource
{
    private int invalidations;

    public DisplayInfo Display { get; } = display;

    public List<nint[]> Exclusions { get; } = [exclusions];

    public int Invalidations => Volatile.Read(ref invalidations);

    public bool Disposed { get; private set; }

    public bool FailExclusions { get; set; }

    public static int References(nint texture)
    {
        Marshal.AddRef(texture);
        return Marshal.Release(texture);
    }

    public static void Drop(nint texture) => Marshal.Release(texture);

    /// <summary>Delivers a new frame and returns its texture; the test keeps one reference until <see cref="Drop"/>.</summary>
    public nint Push(int? width = null)
    {
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)(width ?? Display.Bounds.Width),
            Height = (uint)Display.Bounds.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
        };
        ID3D11Texture2D* texture;
        ((ID3D11Device*)devices.D3D11Device)->CreateTexture2D(&desc, null, &texture).ThrowOnFailure();
        Marshal.AddRef((nint)texture);
        sink.OnFrame(0, (nint)texture, (int)desc.Width, (int)desc.Height, TimeSpan.FromTicks(Stopwatch.GetTimestamp()));
        return (nint)texture;
    }

    public void Close(CaptureFailure failure) => sink.OnClosed(failure);

    public void Invalidate() => Interlocked.Increment(ref invalidations);

    public void ApplyExclusions(ReadOnlySpan<nint> windows)
    {
        if (FailExclusions)
        {
            throw new NativeCallException("MagSetWindowFilterList(MW_FILTERMODE_EXCLUDE)", unchecked((int)0x80004005));
        }
        lock (Exclusions)
        {
            Exclusions.Add(windows.ToArray());
        }
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeCapturePlatform : ICapturePlatform
{
    public bool Supported { get; set; } = true;

    public bool Locked { get; set; }

    public CaptureFailure? FailStart { get; set; }

    public int Initialized { get; private set; }

    public int Uninitialized { get; private set; }

    public List<GraphicsDevices> Devices { get; } = [];

    public List<FakePixelSource> Started { get; } = [];

    public bool IsSupported() => Supported;

    public bool IsSessionLocked() => Locked;

    public IReadOnlyList<DisplayInfo> GetDisplays() => [];

    public GraphicsDevices CreateDevices(GraphicsDeviceOptions? options)
    {
        var created = GraphicsDevices.Create(new GraphicsDeviceOptions { UseWarp = true });
        Devices.Add(created);
        return created;
    }

    public void Initialize() => Initialized++;

    public void Uninitialize() => Uninitialized++;

    public IPixelSource Start(DisplayInfo display, GraphicsDevices devices, ReadOnlySpan<nint> exclusions, IPixelSink sink, CaptureServiceOptions options)
    {
        if (FailStart is { } failure)
        {
            throw new CaptureStartException(failure);
        }
        var source = new FakePixelSource(display, devices, exclusions.ToArray(), sink);
        lock (Started)
        {
            Started.Add(source);
        }
        return source;
    }
}

/// <summary>Small top-level windows owned by a test thread.</summary>
internal static unsafe class TestWindow
{
    public static nint Create(int x = -32000, int y = -32000, int width = 16, int height = 16, bool magenta = false)
    {
        var className = magenta ? "Annotator.Tests.Magenta" : "Static";
        if (magenta)
        {
            Register(className);
        }
        HWND hwnd = PInvoke.CreateWindowEx(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_TRANSPARENT | WINDOW_EX_STYLE.WS_EX_LAYERED,
                                           className, null, WINDOW_STYLE.WS_POPUP, x, y, width, height, HWND.Null, HMENU.Null, HINSTANCE.Null, null);
        if (hwnd.IsNull)
        {
            throw new InvalidOperationException($"CreateWindowEx failed with {Marshal.GetLastPInvokeError()}.");
        }
        PInvoke.SetLayeredWindowAttributes(hwnd, new COLORREF(0), 255, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
        return (nint)hwnd;
    }

    public static void Destroy(nint window) => PInvoke.DestroyWindow((HWND)window);

    private static bool registered;

    private static void Register(string name)
    {
        if (registered)
        {
            return;
        }
        fixed (char* className = name)
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &MagentaProcedure,
                hInstance = (HINSTANCE)PInvoke.GetModuleHandle((PCWSTR)null).Value,
                lpszClassName = className,
            };
            PInvoke.RegisterClassEx(&windowClass);
        }
        registered = true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static LRESULT MagentaProcedure(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        if (message == PInvoke.WM_ERASEBKGND)
        {
            // GDI fill: pure magenta (COLORREF is 0x00BBGGRR).
            var brush = CreateSolidBrush(0x00FF00FF);
            RECT rect;
            GetClientRect(hwnd, &rect);
            FillRect((nint)wParam.Value, &rect, brush);
            DeleteObject(brush);
            return (LRESULT)1;
        }
        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }

    [DllImport("gdi32.dll")]
    private static extern nint CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    private static extern int DeleteObject(nint handle);

    [DllImport("user32.dll")]
    private static extern int FillRect(nint dc, RECT* rect, nint brush);

    [DllImport("user32.dll")]
    private static extern int GetClientRect(HWND window, RECT* rect);
}
