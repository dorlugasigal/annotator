using System.Runtime.InteropServices;

namespace Annotator.Platform.Tests;

/// <summary>The compositor clock that the capture pacer follows. It does not tick while the display is off or asleep.</summary>
internal static unsafe partial class CompositorClock
{
    private const uint Tick = 1;

    /// <summary>Whether a compositor clock tick arrives within about 300 ms.</summary>
    public static bool Ticks()
    {
        using var never = new ManualResetEvent(false);
        var handle = never.SafeWaitHandle.DangerousGetHandle();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (DCompositionWaitForCompositorClock(1, &handle, 100) == Tick)
            {
                return true;
            }
        }
        return false;
    }

    [LibraryImport("dcomp.dll")]
    private static partial uint DCompositionWaitForCompositorClock(uint count, nint* handles, uint timeoutInMs);
}
