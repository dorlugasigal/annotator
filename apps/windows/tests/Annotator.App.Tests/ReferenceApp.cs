using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Annotator.App.Tests;

/// <summary>
/// One reference state shown by Annotator.exe (<c>--ux-reference</c>): off screen beyond the desktop's right edge, never
/// activated and with no tray icon, shortcut or saved setting, beside a running Annotator. Every wait is bounded; a process
/// that does not quit in time is stopped.
/// </summary>
internal sealed class ReferenceApp : IDisposable
{
    private static readonly TimeSpan s_report = TimeSpan.FromSeconds(60);
    private readonly Process _process;
    private bool _released;

    private ReferenceApp(Process process, JsonObject ready, string folder)
    {
        _process = process;
        Ready = ready;
        Folder = folder;
    }

    /// <summary>The app's ready.json.</summary>
    public JsonObject Ready { get; }

    public string Folder { get; }

    public string Status => Ready["status"]?.GetValue<string>() ?? "missing";

    public string? Reason => Ready["reason"]?.GetValue<string>();

    public nint Window => Ready["hwnd"] is JsonNode hwnd ? (nint)hwnd.GetValue<long>() : 0;

    public double Scale => Ready["scale"]?.GetValue<double>() ?? 1;

    public JsonObject Accessibility => Ready["accessibility"] as JsonObject
        ?? throw new InvalidDataException("ready.json has no accessibility report; the app was started without --accessibility.");

    /// <summary>Shows <paramref name="state"/>; <paramref name="extra"/> adds options such as --contrast nightsky or --text-scale 2.25.</summary>
    public static ReferenceApp Start(string state, params string[] extra)
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "reference-runs", $"{state.Replace('/', '-')}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        string exe = Repository.App();
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (string argument in new[] { "--ux-reference", state, "--theme", "light", "--out", folder, "--timeout", "60", "--accessibility" }.Concat(extra))
        {
            info.ArgumentList.Add(argument);
        }
        Process process = Process.Start(info) ?? throw new InvalidOperationException($"{exe} did not start.");
        string ready = Path.Combine(folder, "ready.json");
        var clock = Stopwatch.StartNew();
        while (!File.Exists(ready))
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"Annotator.exe exited with code {process.ExitCode} before it showed {state}.");
            }
            if (clock.Elapsed > s_report)
            {
                Stop(process);
                throw new TimeoutException($"Annotator.exe did not show {state} within {s_report.TotalSeconds:F0} s.");
            }
            Thread.Sleep(100);
        }
        return new ReferenceApp(process, ReadReady(ready), folder);
    }

    /// <summary>Reads ready.json, retrying while another process (a virus scanner, the indexer) briefly holds it.</summary>
    private static JsonObject ReadReady(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            }
            catch (IOException) when (attempt < 30)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>The surface as WinUI rendered it: BGRA rows, premultiplied, <see cref="ImageWidth"/> pixels wide.</summary>
    public byte[] Pixels() => File.ReadAllBytes(Path.Combine(Folder, Ready["image"]!.GetValue<string>()));

    public int ImageWidth => Ready["imageSize"]?[0]?.GetValue<int>() ?? 0;

    public int ImageHeight => Ready["imageSize"]?[1]?.GetValue<int>() ?? 0;

    /// <summary>Tells the app the checks are done, then waits a bounded time for it to quit.</summary>
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
                // The app reported without waiting (a failed state) and is quitting by itself.
            }
        }
        _released = true;
        if (!_process.WaitForExit(15_000))
        {
            Stop(_process);
        }
        _process.Dispose();
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
            // The app may still hold a file for a moment; the next build's clean removes the folder.
        }
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
