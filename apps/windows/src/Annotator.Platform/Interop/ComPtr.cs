using System.Runtime.InteropServices;

namespace Annotator.Platform.Interop;

/// <summary>Reference-counting helpers for raw COM pointers.</summary>
internal static unsafe class ComPtr
{
    /// <summary>QueryInterface that throws on failure. The caller owns the returned reference.</summary>
    public static nint QueryInterface(nint unknown, in Guid iid, string operation)
    {
        int hr = Marshal.QueryInterface(unknown, in iid, out nint result);
        NativeCallException.ThrowIfFailed(hr, operation);
        return result;
    }

    /// <summary>QueryInterface that throws on failure. The caller owns the returned reference.</summary>
    public static T* QueryInterface<T>(void* unknown, in Guid iid, string operation)
        where T : unmanaged =>
        (T*)QueryInterface((nint)unknown, iid, operation);

    public static void Release(ref nint pointer)
    {
        nint value = pointer;
        pointer = 0;
        if (value != 0)
        {
            Marshal.Release(value);
        }
    }

    public static void Release<T>(ref T* pointer)
        where T : unmanaged
    {
        T* value = pointer;
        pointer = null;
        if (value != null)
        {
            Marshal.Release((nint)value);
        }
    }
}
