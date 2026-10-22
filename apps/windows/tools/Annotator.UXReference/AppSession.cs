using System.IO;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Annotator.UXReference;

/// <summary>What Annotator.exe reported for one reference state (its ready.json).</summary>
internal sealed record AppReport(string Status, string? Reason, int ProcessId, nint Window, double Scale, double Width, double Height,
                                 int[]? WindowRect, string? ImagePath, int ImageWidth, int ImageHeight)
{
    /// <summary>A canvas state: the window's own element is the canvas, so the dump starts with it.</summary>
    public bool Canvas { get; init; }

    /// <summary>The objects a canvas shows, in the reference's shape.</summary>
    public JsonArray? Elements { get; init; }

    /// <summary>The role of the only subtree to compare, when the app shows part of a Mac window (a Settings page).</summary>
    public string? Scope { get; init; }
}

/// <summary>
/// Runs Annotator.exe with --ux-reference for one state and theme. Every wait is bounded: the app must report within
/// the timeout, and it quits after the done event or its own timeout; a process that is still running is stopped.
/// </summary>
internal sealed class AppSession : IDisposable
{
    private readonly Process _process;
    private bool _released;

    private AppSession(Process process, AppReport report)
    {
        _process = process;
        Report = report;
    }

    public AppReport Report { get; }

    /// <summary>Why the app is no longer running while the tool still reads it (its exit code), or null while it runs.</summary>
    public string? Exited => _process.HasExited ? $"the app exited with code 0x{_process.ExitCode:X8} before the tool was done" : null;

    public static AppSession Start(string exe, string state, string theme, string folder, TimeSpan timeout, bool usageCountsAvailable = false,
                                   IReadOnlyList<string>? extra = null)
    {
        Directory.CreateDirectory(folder);
        string ready = Path.Combine(folder, "ready.json");
        File.Delete(ready);
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        // The app waits at most this long for the done event; a Settings page is read again at each scroll (ScrolledFrames).
        foreach (string argument in new[] { "--ux-reference", state, "--theme", theme, "--out", folder, "--timeout", "90" }.Concat(extra ?? []))
        {
            info.ArgumentList.Add(argument);
        }
        if (usageCountsAvailable)
        {
            info.ArgumentList.Add("--usage-counts");
            info.ArgumentList.Add("available");
        }
        Process process = Process.Start(info) ?? throw new InvalidOperationException($"{exe} did not start.");
        var clock = Stopwatch.StartNew();
        while (!File.Exists(ready))
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"Annotator.exe exited with code {process.ExitCode} before it reported {state}.");
            }
            if (clock.Elapsed > timeout)
            {
                Stop(process);
                throw new TimeoutException($"Annotator.exe did not report {state} within {timeout.TotalSeconds:F0} s.");
            }
            Thread.Sleep(100);
        }
        JsonObject json;
        try
        {
            json = ReadReady(ready);
        }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException)
        {
            Stop(process);
            throw new InvalidOperationException($"Annotator.exe's report for {state} could not be read: {error.Message}", error);
        }
        int[]? window = json["window"] is JsonArray rect ? [.. rect.Select(item => item!.GetValue<int>())] : null;
        var report = new AppReport(
            json["status"]!.GetValue<string>(),
            json["reason"]?.GetValue<string>(),
            json["processId"]!.GetValue<int>(),
            json["hwnd"] is JsonNode hwnd ? (nint)hwnd.GetValue<long>() : 0,
            json["scale"]?.GetValue<double>() ?? 1,
            json["surface"]?[0]?.GetValue<double>() ?? 0,
            json["surface"]?[1]?.GetValue<double>() ?? 0,
            window,
            json["image"] is JsonNode image ? Path.Combine(folder, image.GetValue<string>()) : null,
            json["imageSize"]?[0]?.GetValue<int>() ?? 0,
            json["imageSize"]?[1]?.GetValue<int>() ?? 0)
        {
            Canvas = json["canvas"]?.GetValue<bool>() ?? false,
            Elements = json["elements"] is JsonArray elements ? (JsonArray)elements.DeepClone() : null,
            Scope = json["scope"]?.GetValue<string>(),
        };
        return new AppSession(process, report);
    }

    /// <summary>
    /// Reads ready.json, retrying for up to 3 seconds while the app is still writing it or another process (a virus scanner,
    /// the indexer) briefly holds it.
    /// </summary>
    private static JsonObject ReadReady(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            }
            catch (Exception error) when ((error is IOException or System.Text.Json.JsonException) && attempt < 30)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>Tells the app that the tool has read its window, then waits a bounded time for it to quit.</summary>
    public void Dispose()
    {
        if (!_released && !_process.HasExited)
        {
            try
            {
                using EventWaitHandle done = EventWaitHandle.OpenExisting($@"Local\Annotator.UXReference.{_process.Id}");
                done.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // The app reported without waiting (an unavailable or failed state) and is quitting by itself.
            }
        }
        _released = true;
        if (!_process.WaitForExit(15_000))
        {
            Stop(_process);
        }
        _process.Dispose();
    }

    private static void Stop(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5_000);
        }
        catch (InvalidOperationException)
        {
            // It ended in the meantime.
        }
    }
}
