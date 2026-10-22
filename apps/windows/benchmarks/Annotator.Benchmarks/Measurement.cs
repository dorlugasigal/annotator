using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Annotator.Benchmarks;

/// <summary>A fixture, check, worker or measurement failed; the suite stops with a nonzero exit.</summary>
internal sealed class BenchmarkFailure(string message) : Exception(message)
{
    public static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new BenchmarkFailure(message);
        }
    }
}

/// <summary>
/// The Mac's MemoryReading with Windows counters under the same names: the working set as current RSS, the peak working
/// set as the process high-water RSS, and user plus kernel time (GetProcessTimes, 100 ns units) as CPU seconds.
/// </summary>
internal readonly partial record struct MemoryReading(ulong CurrentRssBytes, ulong ProcessPeakRssBytes, double ProcessCpuSeconds)
{
    public static MemoryReading Read()
    {
        var process = GetCurrentProcess();
        var counters = new ProcessMemoryCounters { Size = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
        if (!GetProcessMemoryInfo(process, ref counters, counters.Size))
        {
            throw new BenchmarkFailure($"GetProcessMemoryInfo failed: {Marshal.GetLastPInvokeError()}.");
        }
        if (!GetProcessTimes(process, out _, out _, out var kernel, out var user))
        {
            throw new BenchmarkFailure($"GetProcessTimes failed: {Marshal.GetLastPInvokeError()}.");
        }
        return new MemoryReading(counters.WorkingSetSize, counters.PeakWorkingSetSize, (kernel + user) / 10_000_000.0);
    }

    public void Write(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteNumber("currentRSSBytes", CurrentRssBytes);
        writer.WriteNumber("processCPUSeconds", ProcessCpuSeconds);
        writer.WriteNumber("processPeakRSSBytes", ProcessPeakRssBytes);
        writer.WriteEndObject();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMemoryInfo(nint process, ref ProcessMemoryCounters counters, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);
}

/// <summary>One measured run of a workload (Mac Sample).</summary>
internal sealed record Sample(double DurationMs, MemoryReading Before, MemoryReading After, SortedDictionary<string, ulong> Counters)
{
    public void Write(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("after");
        After.Write(writer);
        writer.WritePropertyName("before");
        Before.Write(writer);
        writer.WriteStartObject("counters");
        foreach (var (key, value) in Counters)
        {
            writer.WriteNumber(key, value);
        }
        writer.WriteEndObject();
        writer.WriteNumber("durationMS", DurationMs);
        writer.WriteEndObject();
    }
}

/// <summary>A memory reading at one stage of the cleanup workload (Mac MemoryStage).</summary>
internal sealed record MemoryStage(int Cycle, string Stage, MemoryReading Memory, double? IdleWallMs = null, double? IdleCpuTimeMs = null)
{
    public void Write(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteNumber("cycle", Cycle);
        if (IdleCpuTimeMs is { } cpu)
        {
            writer.WriteNumber("idleCPUTimeMS", cpu);
        }
        if (IdleWallMs is { } wall)
        {
            writer.WriteNumber("idleWallMS", wall);
        }
        writer.WritePropertyName("memory");
        Memory.Write(writer);
        writer.WriteString("stage", Stage);
        writer.WriteEndObject();
    }
}

/// <summary>One scenario's result (Mac Result), written with sorted keys like the Mac's JSONEncoder.</summary>
internal sealed record Result(string Scenario, SortedDictionary<string, long> Workload, string TimingScope, int WarmupCount, IReadOnlyList<Sample> Samples,
                              double P50Ms, double P95Ms, IReadOnlyList<MemoryStage> MemoryStages)
{
    public void Write(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("memoryStages");
        foreach (var stage in MemoryStages)
        {
            stage.Write(writer);
        }
        writer.WriteEndArray();
        writer.WriteNumber("p50MS", P50Ms);
        writer.WriteNumber("p95MS", P95Ms);
        writer.WriteStartArray("samples");
        foreach (var sample in Samples)
        {
            sample.Write(writer);
        }
        writer.WriteEndArray();
        writer.WriteString("scenario", Scenario);
        writer.WriteString("timingScope", TimingScope);
        writer.WriteNumber("warmupCount", WarmupCount);
        writer.WriteStartObject("workload");
        foreach (var (key, value) in Workload)
        {
            writer.WriteNumber(key, value);
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}

/// <summary>What one sample does: <see cref="Run"/> is timed, <see cref="Verify"/> checks the output afterwards.</summary>
internal sealed class Job(Action run, Func<SortedDictionary<string, ulong>> verify, Action? dispose = null) : IDisposable
{
    public Action Run { get; } = run;

    public Func<SortedDictionary<string, ulong>> Verify { get; } = verify;

    public void Dispose() => dispose?.Invoke();
}

internal static class Measurement
{
    /// <summary>Nearest rank, as on the Mac: sorted[ceil(p * n) - 1]; p95 of 11 samples is the maximum.</summary>
    public static double Percentile(IEnumerable<double> values, double fraction)
    {
        var sorted = values.OrderBy(value => value).ToArray();
        BenchmarkFailure.Require(sorted.Length > 0, "A percentile needs at least one sample.");
        return sorted[(int)Math.Ceiling(fraction * sorted.Length) - 1];
    }

    public static double Milliseconds(long start, long end) => (end - start) * 1_000.0 / Stopwatch.Frequency;

    /// <summary>
    /// The Mac's measure(): <paramref name="warmup"/> discarded and <paramref name="samples"/> measured runs, each with a fresh
    /// job from <paramref name="setup"/>; only <see cref="Job.Run"/> is timed (monotonic QueryPerformanceCounter).
    /// </summary>
    public static Result Measure(string name, SortedDictionary<string, long> workload, string scope, int samples, int warmup, Func<Job> setup)
    {
        var results = new List<Sample>(samples);
        for (var index = 0; index < samples + warmup; index++)
        {
            using var job = setup();
            var before = MemoryReading.Read();
            var start = Stopwatch.GetTimestamp();
            job.Run();
            var end = Stopwatch.GetTimestamp();
            var after = MemoryReading.Read();
            var counters = job.Verify();
            if (index >= warmup)
            {
                results.Add(new Sample(Milliseconds(start, end), before, after, counters));
            }
        }
        return Finish(name, workload, scope, warmup, results);
    }

    public static Result Finish(string name, SortedDictionary<string, long> workload, string scope, int warmup, IReadOnlyList<Sample> samples,
                                IReadOnlyList<MemoryStage>? stages = null) =>
        new(name, workload, scope, warmup, samples, Percentile(samples.Select(sample => sample.DurationMs), 0.5),
            Percentile(samples.Select(sample => sample.DurationMs), 0.95), stages ?? []);

    public static SortedDictionary<string, long> Workload(params (string Key, long Value)[] entries)
    {
        var workload = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            workload[key] = value;
        }
        return workload;
    }

    public static SortedDictionary<string, ulong> Counters(params (string Key, ulong Value)[] entries)
    {
        var counters = new SortedDictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            counters[key] = value;
        }
        return counters;
    }
}
