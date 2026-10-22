using System.Runtime.InteropServices;
using Windows.Win32.Foundation;

namespace Annotator.Platform;

/// <summary>
/// A Windows API call failed. <see cref="Exception.HResult"/> holds the HRESULT; Win32 error codes
/// are converted with HRESULT_FROM_WIN32.
/// </summary>
public sealed class NativeCallException : Exception
{
    private const int E_FAIL = unchecked((int)0x80004005);

    public NativeCallException(string operation, int hresult)
        : base($"{operation} failed with HRESULT 0x{hresult:X8}.")
    {
        Operation = operation;
        HResult = hresult;
    }

    /// <summary>The Windows call that failed, for example "CreateWindowEx (overlay)".</summary>
    public string Operation { get; }

    /// <summary>Builds the exception from the last Win32 error of a P/Invoke declared with SetLastError.</summary>
    internal static NativeCallException FromLastError(string operation)
    {
        int error = Marshal.GetLastPInvokeError();
        // Some APIs fail without setting an error code; never report that as success (0).
        return new NativeCallException(operation, error == 0 ? E_FAIL : HResultFromWin32(error));
    }

    internal static int HResultFromWin32(int error) =>
        error <= 0 ? error : unchecked((int)(((uint)error & 0x0000FFFF) | 0x80070000));

    internal static void ThrowIfFailed(HRESULT result, string operation)
    {
        if (result.Failed)
        {
            throw new NativeCallException(operation, result.Value);
        }
    }

    internal static void ThrowIfFailed(int hresult, string operation)
    {
        if (hresult < 0)
        {
            throw new NativeCallException(operation, hresult);
        }
    }
}
