using Windows.Win32;

namespace Annotator.Platform.Input;

public enum MouseInPointerResult
{
    Enabled,
    AlreadyEnabled,
}

/// <summary>
/// Routes the mouse through Windows pointer messages (WM_POINTER*) like pen and touch, so
/// overlays get one input path with full sample history (WIN-INP-01). The setting applies to
/// the whole process, including WinUI windows, and Windows allows turning it on only once, so
/// the app opts in at startup before creating windows.
/// </summary>
public static class MouseInPointer
{
    public static bool IsEnabled => PInvoke.IsMouseInPointerEnabled();

    /// <summary>Turns the setting on. Throws <see cref="NativeCallException"/> if Windows refuses.</summary>
    public static MouseInPointerResult Enable()
    {
        if (PInvoke.IsMouseInPointerEnabled())
        {
            return MouseInPointerResult.AlreadyEnabled;
        }
        if (!PInvoke.EnableMouseInPointer(true))
        {
            throw NativeCallException.FromLastError("EnableMouseInPointer");
        }
        return MouseInPointerResult.Enabled;
    }
}
