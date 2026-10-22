namespace Annotator.Platform;

/// <summary>
/// Receives exceptions thrown by app code that Annotator.Platform calls from native callbacks:
/// window procedures, WinEvent hooks and enumeration callbacks. An exception cannot unwind
/// through Windows, so it stops here. With no handler attached the process ends through
/// <see cref="Environment.FailFast(string, Exception)"/>, so a failure is never ignored.
/// </summary>
public static class NativeCallbackErrors
{
    /// <summary>Raised on the thread of the failed callback. A handler must not throw.</summary>
    public static event Action<Exception>? Unhandled;

    internal static void Report(Exception exception)
    {
        Action<Exception>? handler = Unhandled;
        if (handler is null)
        {
            Environment.FailFast("Annotator.Platform: unhandled exception in a native callback.", exception);
        }
        handler(exception);
    }
}
