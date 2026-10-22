using System.Collections.Concurrent;
using Annotator.Platform.Threading;

namespace Annotator.Platform.Tests;

public sealed class NonPumpingLockTests
{
    /// <summary>
    /// A contended <c>lock</c> on a dispatcher thread would run the queue's next work item inside the waiting one, which
    /// CoreMessaging ends the process for (the zoom crash of 2026-10-21). NonPumpingLock waits without running it.
    /// </summary>
    [Fact]
    public async Task WaitingOnADispatcherThreadRunsNoOtherQueuedWork()
    {
        using DispatcherThread thread = DispatcherThread.Start("Annotator non-pumping lock test");
        var gate = new Lock();
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using (gate.EnterScope())
            {
                held.Set();
                release.Wait();
            }
        });
        holder.Start();
        held.Wait(TestContext.Current.CancellationToken);
        var order = new ConcurrentQueue<string>();

        Task first = thread.RunAsync(() =>
        {
            using (NonPumpingLock.Enter(gate))
            {
                order.Enqueue("entered");
            }
        });
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Task second = thread.RunAsync(() => order.Enqueue("second"));
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.Empty(order);
        release.Set();
        await Task.WhenAll(first, second).WaitAsync(TestTimeouts.Work, TestContext.Current.CancellationToken);
        holder.Join();
        Assert.Equal(["entered", "second"], order);
    }

    [Fact]
    public void TryEnterHoldsOnlyAFreeLock()
    {
        var gate = new Lock();
        Assert.True(NonPumpingLock.TryEnter(gate, out NonPumpingLock.Held held));
        using (held)
        {
            Assert.True(gate.IsHeldByCurrentThread);
            bool other = true;
            var thread = new Thread(() => other = NonPumpingLock.TryEnter(gate, out _));
            thread.Start();
            thread.Join();
            Assert.False(other);
        }
        Assert.False(gate.IsHeldByCurrentThread);
    }
}
