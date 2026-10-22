using System.Runtime.InteropServices;
using Windows.System;

namespace Annotator.Platform.Interop;

/// <summary>
/// CreateDispatcherQueueController, written by hand for Native AOT (see <see cref="CompositionInterop"/>).
/// Windows.UI.Composition needs a Windows.System dispatcher queue on the thread that creates a
/// Compositor; a WinUI thread has only a Microsoft.UI queue until one is added (Gate 0 S-12).
/// </summary>
public static unsafe partial class DispatcherQueueInterop
{
    private const int DQTYPE_THREAD_DEDICATED = 1;
    private const int DQTYPE_THREAD_CURRENT = 2;
    private const int DQTAT_COM_NONE = 0;
    private const int DQTAT_COM_STA = 2;

    /// <summary>
    /// Adds a Windows.System dispatcher queue to the calling thread, which must already run a
    /// message loop. Keep the controller alive for as long as the queue is needed.
    /// </summary>
    public static DispatcherQueueController CreateOnCurrentThread()
    {
        if (DispatcherQueue.GetForCurrentThread() is not null)
        {
            throw new InvalidOperationException("This thread already has a Windows.System dispatcher queue.");
        }
        return Create(DQTYPE_THREAD_CURRENT, DQTAT_COM_NONE, "CreateDispatcherQueueController(DQTYPE_THREAD_CURRENT)");
    }

    /// <summary>
    /// Starts a new thread with a COM single-threaded apartment, its own message loop and a
    /// Windows.System dispatcher queue. Shut it down with ShutdownQueueAsync.
    /// </summary>
    public static DispatcherQueueController CreateOnDedicatedThread() =>
        Create(DQTYPE_THREAD_DEDICATED, DQTAT_COM_STA, "CreateDispatcherQueueController(DQTYPE_THREAD_DEDICATED, DQTAT_COM_STA)");

    private static DispatcherQueueController Create(int threadType, int apartmentType, string operation)
    {
        var options = new DispatcherQueueOptions
        {
            Size = sizeof(DispatcherQueueOptions),
            ThreadType = threadType,
            ApartmentType = apartmentType,
        };
        nint controller = 0;
        NativeCallException.ThrowIfFailed(CreateDispatcherQueueController(options, &controller), operation);
        try
        {
            return DispatcherQueueController.FromAbi(controller);
        }
        finally
        {
            Marshal.Release(controller);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int Size;
        public int ThreadType;
        public int ApartmentType;
    }

    [LibraryImport("CoreMessaging.dll")]
    private static partial int CreateDispatcherQueueController(DispatcherQueueOptions options, nint* controller);
}
