using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Annotator.RenderHarness;

/// <summary>
/// Measurement only: where a time falls in the display's refresh (DwmGetCompositionTimingInfo's vertical blank and
/// period) and the compositor's own frame statistics (DCompositionGetFrameId and DCompositionGetStatistics, Windows 11),
/// so latency samples can be sorted by the moment they started within a frame.
/// </summary>
internal static unsafe partial class CompositionTiming
{
    /// <summary>A recent vertical blank and the refresh period, in QPC ticks.</summary>
    public readonly record struct Grid(long VBlank, long Period, double RefreshHz)
    {
        public double PeriodMs => Period * 1000.0 / Stopwatch.Frequency;

        /// <summary>Milliseconds from the last vertical blank at or before <paramref name="qpc"/>, in [0, period).</summary>
        public double PhaseMs(long qpc)
        {
            var offset = (qpc - VBlank) % Period;
            if (offset < 0)
            {
                offset += Period;
            }
            return offset * 1000.0 / Stopwatch.Frequency;
        }
    }

    /// <summary>One composition frame of the compositor clock: when it started and when it is meant to reach the display (QPC).</summary>
    public readonly record struct FrameStats(ulong Id, long Start, long Target, long Period);

    /// <summary>The primary display's vertical blank grid, or null when the compositor does not report one.</summary>
    public static Grid? Read()
    {
        var info = new DwmTimingInfo { Size = (uint)sizeof(DwmTimingInfo) };
        if (DwmGetCompositionTimingInfo(0, &info) < 0 || info.RefreshPeriod == 0 || info.VBlank == 0)
        {
            return null;
        }
        var hz = info.RefreshDenominator == 0 ? 0 : (double)info.RefreshNumerator / info.RefreshDenominator;
        return new Grid((long)info.VBlank, (long)info.RefreshPeriod, hz);
    }

    /// <summary>The newest composition frame of a kind (0 created, 1 confirmed, 2 completed), or null before Windows 11.</summary>
    public static ulong? FrameId(int kind)
    {
        try
        {
            ulong id;
            return DCompositionGetFrameId(kind, &id) < 0 ? null : id;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The statistics of one composition frame, or null when the compositor no longer keeps them.</summary>
    public static FrameStats? Statistics(ulong id)
    {
        try
        {
            CompositionFrameStats stats;
            uint targets;
            if (DCompositionGetStatistics(id, &stats, 0, null, &targets) < 0)
            {
                return null;
            }
            return new FrameStats(id, (long)stats.StartTime, (long)stats.TargetTime, (long)stats.FramePeriod);
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>A capture frame's SystemRelativeTime as a QPC value.</summary>
    public static long Qpc(TimeSpan systemRelativeTime) => (long)Math.Round(systemRelativeTime.Ticks * (double)Stopwatch.Frequency / TimeSpan.TicksPerSecond);

    /// <summary>DCompositionBoostCompositorClock: asks the compositor for its highest frame rate (dynamic refresh rate displays).</summary>
    public static int Boost(bool enable) => DCompositionBoostCompositorClock(enable ? 1 : 0);

    public static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// Prints latency by the phase of its start in the refresh (eight bins), and where the captured frames' times fall in
    /// it. <paramref name="samples"/> holds (start phase ms, latency ms, frame time phase ms).
    /// </summary>
    public static void PrintPhases(string label, IReadOnlyList<(double StartPhase, double Latency, double FramePhase)> samples, double periodMs)
    {
        if (samples.Count == 0 || periodMs <= 0)
        {
            return;
        }
        const int Bins = 8;
        var parts = new List<string>();
        for (var bin = 0; bin < Bins; bin++)
        {
            double from = bin * periodMs / Bins, to = (bin + 1) * periodMs / Bins;
            var inBin = samples.Where(sample => sample.StartPhase >= from && sample.StartPhase < to).Select(sample => sample.Latency).ToList();
            parts.Add(inBin.Count == 0 ? $"{from:F1}-{to:F1}: -" : $"{from:F1}-{to:F1}: {Median(inBin):F1} (n {inBin.Count})");
        }
        var framePhases = samples.Select(sample => sample.FramePhase).ToList();
        Console.WriteLine($"  {label} by start in the refresh (ms after the vertical blank: median latency): {string.Join("; ", parts)}");
        Console.WriteLine($"  {label} frame times, ms after the vertical blank: {Stats.Summary(framePhases)}");
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        return values.Count % 2 == 1 ? values[values.Count / 2] : (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DwmTimingInfo
    {
        public uint Size;
        public uint RefreshNumerator;
        public uint RefreshDenominator;
        public ulong RefreshPeriod;
        public uint ComposeNumerator;
        public uint ComposeDenominator;
        public ulong VBlank;
        public ulong Refresh;
        public uint DXRefresh;
        public ulong Compose;
        public ulong Frame;
        public uint DXPresent;
        public ulong RefreshFrame;
        public ulong FrameSubmitted;
        public uint DXPresentSubmitted;
        public ulong FrameConfirmed;
        public uint DXPresentConfirmed;
        public ulong RefreshConfirmed;
        public uint DXRefreshConfirmed;
        public ulong FramesLate;
        public uint FramesOutstanding;
        public ulong FrameDisplayed;
        public ulong QpcFrameDisplayed;
        public ulong RefreshFrameDisplayed;
        public ulong FrameComplete;
        public ulong QpcFrameComplete;
        public ulong FramePending;
        public ulong QpcFramePending;
        public ulong FramesDisplayed;
        public ulong FramesComplete;
        public ulong FramesPending;
        public ulong FramesAvailable;
        public ulong FramesDropped;
        public ulong FramesMissed;
        public ulong RefreshNextDisplayed;
        public ulong RefreshNextPresented;
        public ulong RefreshesDisplayed;
        public ulong RefreshesPresented;
        public ulong RefreshStarted;
        public ulong PixelsReceived;
        public ulong PixelsDrawn;
        public ulong BuffersEmpty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionFrameStats
    {
        public ulong StartTime;
        public ulong TargetTime;
        public ulong FramePeriod;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetCompositionTimingInfo(nint window, DwmTimingInfo* info);

    [LibraryImport("dcomp.dll")]
    private static partial int DCompositionGetFrameId(int kind, ulong* frameId);

    [LibraryImport("dcomp.dll")]
    private static partial int DCompositionGetStatistics(ulong frameId, CompositionFrameStats* stats, uint targetIdCount, void* targetIds, uint* actualTargetIdCount);

    [LibraryImport("dcomp.dll")]
    private static partial int DCompositionBoostCompositorClock(int enable);
}
