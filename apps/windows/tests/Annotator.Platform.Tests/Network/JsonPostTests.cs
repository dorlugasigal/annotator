using System.Net;
using System.Net.Sockets;
using System.Text;
using Annotator.Platform.Network;

namespace Annotator.Platform.Tests.Network;

/// <summary>
/// The JSON post behind usage counts (WIN-PRV-01, WIN-PRV-03), against a one-request server on the loopback address. Every
/// wait is bounded; nothing leaves the computer.
/// </summary>
public sealed class JsonPostTests
{
    private const string Json = "{\"schema\":1,\"platform\":\"windows\"}";

    [Fact]
    public async Task PostsTheJsonWithoutCookiesAndReturnsTheStatus()
    {
        using var deadline = Deadline(20);
        using var listener = Listen(out Uri url);
        Task<string> server = ServeOnceAsync(listener, "HTTP/1.1 202 Accepted\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", deadline.Token);
        int status = await JsonPost.SendAsync(url, Json, TimeSpan.FromSeconds(10), deadline.Token);
        string request = await server.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(202, status);
        Assert.StartsWith("POST /api/metrics/app HTTP/1.1\r\n", request, StringComparison.Ordinal);
        Assert.Contains("\r\nContent-Type: application/json", request, StringComparison.OrdinalIgnoreCase);
        // The app's name only: no version, system or person (the payload says what it shares).
        Assert.Contains("\r\nUser-Agent: Annotator\r\n", request, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\nCookie:", request, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("\r\n\r\n" + Json, request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyHttpAndHttpsAddressesAreSent()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => JsonPost.SendAsync(new Uri("ftp://127.0.0.1/"), Json, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => JsonPost.SendAsync(new Uri("/api/metrics/app", UriKind.Relative), Json, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnErrorStatusIsReturnedNotThrown()
    {
        using var deadline = Deadline(20);
        using var listener = Listen(out Uri url);
        Task<string> server = ServeOnceAsync(listener, "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", deadline.Token);
        Assert.Equal(400, await JsonPost.SendAsync(url, Json, TimeSpan.FromSeconds(10), deadline.Token));
        await server.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AServerThatDoesNotAnswerFailsAfterTheTimeout()
    {
        using var deadline = Deadline(20);
        using var listener = Listen(out Uri url);
        Task<string> server = ServeOnceAsync(listener, response: null, deadline.Token);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        JsonPostFailure failure = await Assert.ThrowsAsync<JsonPostFailure>(() => JsonPost.SendAsync(url, Json, TimeSpan.FromMilliseconds(600), deadline.Token));
        Assert.InRange(clock.Elapsed.TotalSeconds, 0.5, 10);
        Assert.Contains("did not answer", failure.Message, StringComparison.Ordinal);
        deadline.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server);
    }

    [Fact]
    public async Task NoServerIsAFailure()
    {
        using var deadline = Deadline(20);
        Uri url;
        using (Listen(out url))
        {
        }
        await Assert.ThrowsAsync<JsonPostFailure>(() => JsonPost.SendAsync(url, Json, TimeSpan.FromSeconds(8), deadline.Token));
    }

    [Fact]
    public async Task TheCallersCancellationEndsTheRequest()
    {
        using var deadline = Deadline(20);
        using var listener = Listen(out Uri url);
        Task<string> server = ServeOnceAsync(listener, response: null, deadline.Token);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => JsonPost.SendAsync(url, Json, TimeSpan.FromSeconds(10), cancel.Token));
        deadline.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server);
    }

    private static CancellationTokenSource Deadline(int seconds)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        return deadline;
    }

    private static TcpListener Listen(out Uri url)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        url = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/api/metrics/app");
        return listener;
    }

    /// <summary>
    /// Accepts one connection, reads the request with its body and answers with <paramref name="response"/>, or keeps the
    /// connection open without answering until <paramref name="cancellation"/> when it is null. Returns the request.
    /// </summary>
    private static async Task<string> ServeOnceAsync(TcpListener listener, string? response, CancellationToken cancellation)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(cancellation);
        using NetworkStream stream = client.GetStream();
        var received = new List<byte>();
        var buffer = new byte[4096];
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int read = await stream.ReadAsync(buffer, cancellation);
            if (read == 0)
            {
                throw new IOException("The client closed the connection before its headers ended.");
            }
            received.AddRange(buffer.AsSpan(0, read));
            headerEnd = IndexOf(received, "\r\n\r\n"u8);
        }
        string headers = Encoding.ASCII.GetString(received.Take(headerEnd).ToArray());
        int length = 0;
        foreach (string line in headers.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                length = int.Parse(line["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (line.StartsWith("Expect:", StringComparison.OrdinalIgnoreCase) && line.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
            {
                await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), cancellation);
            }
        }
        while (received.Count < headerEnd + 4 + length)
        {
            int read = await stream.ReadAsync(buffer, cancellation);
            if (read == 0)
            {
                throw new IOException("The client closed the connection before its body ended.");
            }
            received.AddRange(buffer.AsSpan(0, read));
        }
        string request = Encoding.UTF8.GetString(received.ToArray());
        if (response is null)
        {
            await Task.Delay(Timeout.Infinite, cancellation);
        }
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response!), cancellation);
        return request;
    }

    private static int IndexOf(List<byte> bytes, ReadOnlySpan<byte> pattern)
    {
        for (int start = 0; start + pattern.Length <= bytes.Count; start++)
        {
            bool match = true;
            for (int offset = 0; offset < pattern.Length && match; offset++)
            {
                match = bytes[start + offset] == pattern[offset];
            }
            if (match)
            {
                return start;
            }
        }
        return -1;
    }
}
