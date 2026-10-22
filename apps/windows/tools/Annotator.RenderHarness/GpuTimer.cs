using System.Diagnostics;
using Annotator.Platform;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Interop;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;

namespace Annotator.RenderHarness;

/// <summary>
/// Measurement only: GPU time between two D3D11 timestamp queries, and the time from the start of a submit until an
/// event query reports the GPU done. Reading spins on GetData, which is fine for a benchmark and never product code,
/// but every read gives up after <see cref="ReadTimeout"/> and a failed GetData (a removed device) ends the read.
/// </summary>
internal sealed unsafe class GpuTimer : IDisposable
{
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    private readonly ID3D11DeviceContext* context;
    private ID3D11Query* disjoint;
    private ID3D11Query* start;
    private ID3D11Query* end;
    private ID3D11Query* done;

    public GpuTimer(GraphicsDevices devices)
    {
        var device = (ID3D11Device*)devices.D3D11Device;
        context = (ID3D11DeviceContext*)devices.D3D11DeviceContext;
        disjoint = Create(device, D3D11_QUERY.D3D11_QUERY_TIMESTAMP_DISJOINT);
        start = Create(device, D3D11_QUERY.D3D11_QUERY_TIMESTAMP);
        end = Create(device, D3D11_QUERY.D3D11_QUERY_TIMESTAMP);
        done = Create(device, D3D11_QUERY.D3D11_QUERY_EVENT);
    }

    /// <summary>Reads that gave up after <see cref="ReadTimeout"/>; their values are NaN.</summary>
    public int Timeouts { get; private set; }

    public void Begin()
    {
        context->Begin((ID3D11Asynchronous*)disjoint);
        context->End((ID3D11Asynchronous*)start);
    }

    public void End()
    {
        context->End((ID3D11Asynchronous*)end);
        context->End((ID3D11Asynchronous*)disjoint);
        context->End((ID3D11Asynchronous*)done);
        context->Flush();
    }

    /// <summary>
    /// GPU milliseconds between the timestamps (NaN if the clock was disjoint) and milliseconds from
    /// <paramref name="submitStart"/> until done; both NaN when the GPU did not answer within <see cref="ReadTimeout"/>.
    /// </summary>
    public (double GpuMs, double CompletionMs) Read(long submitStart)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(ReadTimeout.TotalSeconds * Stopwatch.Frequency);
        int finished = 0;
        if (!Wait(done, &finished, sizeof(int), deadline) || finished == 0)
        {
            return TimedOut();
        }
        var completion = Stopwatch.GetElapsedTime(submitStart).TotalMilliseconds;
        D3D11_QUERY_DATA_TIMESTAMP_DISJOINT clock;
        ulong first = 0, second = 0;
        if (!Wait(disjoint, &clock, (uint)sizeof(D3D11_QUERY_DATA_TIMESTAMP_DISJOINT), deadline)
            || !Wait(start, &first, sizeof(ulong), deadline) || !Wait(end, &second, sizeof(ulong), deadline))
        {
            return TimedOut();
        }
        var gpu = clock.Disjoint ? double.NaN : (second - first) * 1000.0 / clock.Frequency;
        return (gpu, completion);
    }

    public void Dispose()
    {
        Native.Release(ref done);
        Native.Release(ref end);
        Native.Release(ref start);
        Native.Release(ref disjoint);
    }

    /// <summary>The process's use of the adapter's local (dedicated) video memory, in bytes, after Direct3D destroyed released resources.</summary>
    public static ulong LocalVideoMemory(GraphicsDevices devices)
    {
        // Direct3D defers destroying released resources until the immediate context is flushed.
        ((ID3D11DeviceContext*)devices.D3D11DeviceContext)->Flush();
        var dxgi = (IDXGIDevice*)devices.DxgiDevice;
        IDXGIAdapter* adapter;
        Native.Check(dxgi->GetAdapter(&adapter), "IDXGIDevice::GetAdapter");
        try
        {
            var adapter3 = Native.Query<IDXGIAdapter3>(adapter, IDXGIAdapter3.IID_Guid, "QueryInterface(IDXGIAdapter3)");
            try
            {
                DXGI_QUERY_VIDEO_MEMORY_INFO info;
                Native.Check(adapter3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP.DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &info), "IDXGIAdapter3::QueryVideoMemoryInfo");
                return info.CurrentUsage;
            }
            finally
            {
                Native.Release(ref adapter3);
            }
        }
        finally
        {
            Native.Release(ref adapter);
        }
    }

    private (double, double) TimedOut()
    {
        Timeouts++;
        return (double.NaN, double.NaN);
    }

    /// <summary>Polls GetData until S_OK or the deadline; a failure HRESULT (a removed device) throws.</summary>
    private bool Wait(ID3D11Query* query, void* data, uint size, long deadline)
    {
        var spinner = default(SpinWait);
        while (true)
        {
            var result = context->GetData((ID3D11Asynchronous*)query, data, size, 0).Value;
            if (result == 0)
            {
                return true;
            }
            if (result < 0)
            {
                throw new NativeCallException("ID3D11DeviceContext::GetData (GPU timer)", result);
            }
            if (Stopwatch.GetTimestamp() > deadline)
            {
                return false;
            }
            // Never Sleep(1): with the default timer resolution it sleeps about 15.6 ms and would quantize the reading.
            spinner.SpinOnce(sleep1Threshold: -1);
        }
    }

    private static ID3D11Query* Create(ID3D11Device* device, D3D11_QUERY kind)
    {
        var description = new D3D11_QUERY_DESC { Query = kind };
        ID3D11Query* query;
        Native.Check(device->CreateQuery(&description, &query), "ID3D11Device::CreateQuery");
        return query;
    }
}

/// <summary>Percentiles of a sample, for reports.</summary>
internal static class Stats
{
    public static double Percentile(IReadOnlyList<double> values, double fraction)
    {
        var sorted = values.Where(double.IsFinite).OrderBy(value => value).ToArray();
        if (sorted.Length == 0)
        {
            return double.NaN;
        }
        var position = fraction * (sorted.Length - 1);
        var low = (int)Math.Floor(position);
        var high = Math.Min(sorted.Length - 1, low + 1);
        return sorted[low] + (sorted[high] - sorted[low]) * (position - low);
    }

    public static string Summary(IReadOnlyList<double> values, string unit = "ms") =>
        $"p50 {Percentile(values, 0.5):F3} {unit}, p95 {Percentile(values, 0.95):F3} {unit}, max {values.Where(double.IsFinite).DefaultIfEmpty(double.NaN).Max():F3} {unit} (n = {values.Count(double.IsFinite)})";
}
