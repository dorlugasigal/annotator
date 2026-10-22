using Windows.Win32;
using Windows.Win32.UI.HiDpi;

namespace Annotator.Platform.Interop;

/// <summary>
/// Runs a block with the calling thread set to Per-Monitor DPI Aware v2, so monitor rectangles
/// are physical pixels and windows created inside the block are Per-Monitor v2 (WIN-REND-06),
/// whatever the host process manifest says.
/// </summary>
internal readonly ref struct PerMonitorDpiScope
{
    private const int E_FAIL = unchecked((int)0x80004005);
    private readonly DPI_AWARENESS_CONTEXT _previous;

    private PerMonitorDpiScope(DPI_AWARENESS_CONTEXT previous) => _previous = previous;

    public static PerMonitorDpiScope Enter()
    {
        DPI_AWARENESS_CONTEXT previous = PInvoke.SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        if (previous.IsNull)
        {
            throw new NativeCallException("SetThreadDpiAwarenessContext(PER_MONITOR_AWARE_V2)", E_FAIL);
        }
        return new PerMonitorDpiScope(previous);
    }

    public void Dispose()
    {
        if (!_previous.IsNull)
        {
            PInvoke.SetThreadDpiAwarenessContext(_previous);
        }
    }
}
