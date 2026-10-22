using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Annotator.Platform.Diagnostics;

/// <summary>
/// Development measurement only (WIN-PERF-01): when the ANNOTATOR_LATENCY_LOG environment variable names a file, the
/// overlays record the times of each pointer message, and append them to that file at the end of each stroke, so a
/// measurement run (RenderHarness <c>input --probe</c>) can split input-to-visible time into delivery, the app's work and
/// composition. Otherwise it records nothing.
/// </summary>
public static class InputLatencyProbe
{
    private const int Capacity = 4096;

    private static readonly string? s_path = Environment.GetEnvironmentVariable("ANNOTATOR_LATENCY_LOG");
    private static readonly Record[] s_records = s_path is null ? [] : new Record[Capacity];
    private static readonly Lock s_file = new();
    private static int s_count;

    /// <summary>Whether a measurement run asked for the times.</summary>
    public static bool IsEnabled { get; } = s_path is not null;

    /// <summary>
    /// One pointer message: the pointer event's own time stamp (QPC; 0 when Windows gives none), when the overlay began to
    /// handle the message and when the app had drawn it (QPC), and the newest position in desktop pixels.
    /// </summary>
    internal readonly record struct Record(long Event, long Dispatch, long Handled, int X, int Y, int Kind, int Samples);

    /// <summary>Records one message (overlay thread).</summary>
    internal static void Add(in Record record)
    {
        if (s_count < s_records.Length)
        {
            s_records[s_count++] = record;
        }
    }

    /// <summary>Hands the records so far to a thread-pool thread, which appends them to the file (overlay thread).</summary>
    internal static void Flush()
    {
        if (s_count == 0 || s_path is not { } path)
        {
            return;
        }
        var batch = s_records.AsSpan(0, s_count).ToArray();
        s_count = 0;
        ThreadPool.QueueUserWorkItem(_ => Append(path, batch));
    }

    private static void Append(string path, Record[] batch)
    {
        var text = new StringBuilder();
        foreach (var record in batch)
        {
            text.Append(CultureInfo.InvariantCulture, $"{record.Kind},{record.X},{record.Y},{record.Event},{record.Dispatch},{record.Handled},{record.Samples}\n");
        }
        lock (s_file)
        {
            try
            {
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, $"kind,x,y,event_qpc,dispatch_qpc,handled_qpc,samples (QPC frequency {Stopwatch.Frequency})\n");
                }
                File.AppendAllText(path, text.ToString());
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A measurement file that cannot be written loses this stroke's times; the run reports missing samples.
            }
        }
    }
}
