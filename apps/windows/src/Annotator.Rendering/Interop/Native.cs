using System.Runtime.InteropServices;
using Windows.Win32.Foundation;

namespace Annotator.Rendering.Interop;

/// <summary>HRESULT checks and reference counting for raw COM pointers.</summary>
internal static unsafe class Native
{
    /// <summary>Throws <see cref="Annotator.Platform.NativeCallException"/> naming the failed call.</summary>
    public static void Check(HRESULT result, string operation)
    {
        if (result.Failed)
        {
            throw new Annotator.Platform.NativeCallException(operation, result.Value);
        }
    }

    public static void Check(int hresult, string operation)
    {
        if (hresult < 0)
        {
            throw new Annotator.Platform.NativeCallException(operation, hresult);
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

    public static T* AddRef<T>(T* pointer)
        where T : unmanaged
    {
        if (pointer != null)
        {
            Marshal.AddRef((nint)pointer);
        }
        return pointer;
    }

    /// <summary>QueryInterface that throws on failure. The caller owns the returned reference.</summary>
    public static T* Query<T>(void* unknown, in Guid iid, string operation)
        where T : unmanaged
    {
        Check(Marshal.QueryInterface((nint)unknown, in iid, out nint result), operation);
        return (T*)result;
    }

    /// <summary>QueryInterface that returns null when the interface is not supported.</summary>
    public static T* TryQuery<T>(void* unknown, in Guid iid)
        where T : unmanaged =>
        Marshal.QueryInterface((nint)unknown, in iid, out nint result) < 0 ? null : (T*)result;
}
