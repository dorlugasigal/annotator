using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Networking.WinHttp;

namespace Annotator.Platform.Network;

/// <summary>A JSON request that could not reach the server or did not finish in time.</summary>
public sealed class JsonPostFailure(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Posts one JSON body with WinHTTP, Windows' HTTP stack for programs. Like the Mac's ephemeral URLSession it keeps no
/// cache, sends no cookies and never shows a prompt; it follows the system's proxy settings (automatic proxy). Usage counts
/// are its only caller (WIN-PRV-03). The request runs on a thread-pool thread, and closing its handle ends it at the timeout
/// or on cancellation.
/// </summary>
public static class JsonPost
{
    private const uint QueryStatusCode = 19;
    private const uint QueryFlagNumber = 0x20000000;
    private const uint OptionDisableFeature = 63;
    private const uint DisableCookies = 0x00000001;

    /// <summary>
    /// Returns the HTTP status. Throws <see cref="JsonPostFailure"/> when the server cannot be reached or does not answer
    /// within <paramref name="timeout"/>, and <see cref="OperationCanceledException"/> when <paramref name="cancellation"/>
    /// ends the request.
    /// </summary>
    public static async Task<int> SendAsync(Uri url, string json, TimeSpan timeout, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(json);
        if (!url.IsAbsoluteUri || url.Scheme is not ("https" or "http") || url.IdnHost.Length == 0)
        {
            throw new ArgumentException("The address must be an absolute http or https address.", nameof(url));
        }
        cancellation.ThrowIfCancellationRequested();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout);
        var request = new Request(url, Encoding.UTF8.GetBytes(json), timeout);
        using (limit.Token.Register(request.Abort))
        {
            try
            {
                return await Task.Run(request.Send, CancellationToken.None);
            }
            catch (JsonPostFailure) when (cancellation.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellation);
            }
            catch (JsonPostFailure error) when (limit.IsCancellationRequested)
            {
                throw new JsonPostFailure($"The server did not answer within {timeout.TotalSeconds:0} seconds.", error);
            }
        }
    }

    /// <summary>One synchronous WinHTTP request; <see cref="Abort"/> may close its handle from another thread.</summary>
    private sealed unsafe class Request(Uri url, byte[] body, TimeSpan timeout)
    {
        private nint _handle;
        private int _aborted;

        public int Send()
        {
            void* session = null, connection = null;
            try
            {
                session = PInvoke.WinHttpOpen("Annotator", WINHTTP_ACCESS_TYPE.WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, null, null, 0);
                Check(session is not null, "WinHttpOpen");
                int milliseconds = (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue);
                Check(PInvoke.WinHttpSetTimeouts(session, milliseconds, milliseconds, milliseconds, milliseconds), "WinHttpSetTimeouts");
                connection = PInvoke.WinHttpConnect(session, url.IdnHost, (ushort)url.Port, 0);
                Check(connection is not null, "WinHttpConnect");
                void* request;
                fixed (char* verb = "POST", path = url.PathAndQuery)
                {
                    request = PInvoke.WinHttpOpenRequest(connection, new PCWSTR(verb), new PCWSTR(path), default, default, null,
                        url.Scheme == "https" ? WINHTTP_OPEN_REQUEST_FLAGS.WINHTTP_FLAG_SECURE : 0);
                }
                Check(request is not null, "WinHttpOpenRequest");
                // A full fence: Abort sets the flag and then exchanges the handle, so one of the two always sees the other.
                Interlocked.Exchange(ref _handle, (nint)request);
                if (Volatile.Read(ref _aborted) != 0)
                {
                    throw new JsonPostFailure("The request was stopped.");
                }
                uint disable = DisableCookies;
                Check(PInvoke.WinHttpSetOption(request, OptionDisableFeature, &disable, sizeof(uint)), "WinHttpSetOption");
                const string Headers = "Content-Type: application/json\r\n";
                fixed (char* headers = Headers)
                fixed (byte* data = body)
                {
                    Check(PInvoke.WinHttpSendRequest(request, new PCWSTR(headers), (uint)Headers.Length, data, (uint)body.Length, (uint)body.Length, 0),
                        "WinHttpSendRequest");
                }
                Check(PInvoke.WinHttpReceiveResponse(request, null), "WinHttpReceiveResponse");
                uint status = 0, size = sizeof(uint), index = 0;
                Check(PInvoke.WinHttpQueryHeaders(request, QueryStatusCode | QueryFlagNumber, default, &status, &size, &index), "WinHttpQueryHeaders");
                return (int)status;
            }
            finally
            {
                CloseRequest();
                if (connection is not null)
                {
                    PInvoke.WinHttpCloseHandle(connection);
                }
                if (session is not null)
                {
                    PInvoke.WinHttpCloseHandle(session);
                }
            }
        }

        /// <summary>Closing the request handle ends a call that is still waiting (the timeout or a cancellation).</summary>
        public void Abort()
        {
            Volatile.Write(ref _aborted, 1);
            CloseRequest();
        }

        private void CloseRequest()
        {
            nint handle = Interlocked.Exchange(ref _handle, 0);
            if (handle != 0)
            {
                PInvoke.WinHttpCloseHandle((void*)handle);
            }
        }

        private static void Check(bool succeeded, string call)
        {
            if (!succeeded)
            {
                // The generated imports do not save the error; it is read at once and only describes the failure.
                throw new JsonPostFailure($"{call} failed (Windows error {Marshal.GetLastSystemError()}).");
            }
        }
    }
}
