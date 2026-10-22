namespace Annotator.Platform.Threading;

/// <summary>
/// Enters a <see cref="Lock"/> without processing messages while it waits. A contended <c>lock</c> on a single-threaded
/// apartment thread, such as a <see cref="DispatcherThread"/>, waits with <c>CoWaitForMultipleHandles</c>, which dispatches
/// sent messages and so runs the thread's dispatcher queue inside the waiting work item. CoreMessaging does not allow that
/// and ends the process (0xE0464645 in <c>Dispatcher::Callback_DispatchLoop</c>). Use this for locks that dispatcher
/// threads share with other threads; the holder must not wait for the waiting thread.
/// </summary>
public static class NonPumpingLock
{
    /// <summary>Enters <paramref name="gate"/>, spinning and sleeping (which processes nothing) until it is free.</summary>
    public static Held Enter(Lock gate)
    {
        EnterWithoutPumping(gate);
        return new Held(gate);
    }

    /// <summary>Takes <paramref name="gate"/> only if it is free now; otherwise returns false and holds nothing.</summary>
    public static bool TryEnter(Lock gate, out Held held)
    {
        if (gate is null)
        {
            throw new ArgumentNullException(nameof(gate));
        }
        held = gate.TryEnter() ? new Held(gate) : default;
        return held.IsHeld;
    }

    /// <summary>As <see cref="Enter"/>, for a caller that exits <paramref name="gate"/> itself.</summary>
    public static void EnterWithoutPumping(Lock gate)
    {
        if (gate is null)
        {
            throw new ArgumentNullException(nameof(gate));
        }
        if (!gate.TryEnter())
        {
            var spinner = new SpinWait();
            do
            {
                spinner.SpinOnce();
            }
            while (!gate.TryEnter());
        }
    }

    /// <summary>A held lock; disposing it exits.</summary>
    public readonly ref struct Held
    {
        private readonly Lock gate;

        internal Held(Lock gate) => this.gate = gate;

        internal bool IsHeld => gate is not null;

        public void Dispose() => gate?.Exit();
    }
}
