using System.Diagnostics;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Interop;
using Windows.Win32.Graphics.Direct3D11;

namespace Annotator.Benchmarks;

/// <summary>
/// A Direct3D 11 event query: <see cref="Signal"/> after the work to time, then <see cref="Wait"/> until the GPU finished
/// it. The wait spins (measurement only, never product code) and gives up after <see cref="Timeout"/>.
/// </summary>
internal sealed unsafe class GpuFence : IDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly GraphicsDevices devices;
    private ID3D11Query* done;

    public GpuFence(GraphicsDevices devices)
    {
        this.devices = devices;
        var description = new D3D11_QUERY_DESC { Query = D3D11_QUERY.D3D11_QUERY_EVENT };
        ID3D11Query* query;
        Native.Check(((ID3D11Device*)devices.D3D11Device)->CreateQuery(&description, &query), "ID3D11Device::CreateQuery(event)");
        done = query;
    }

    /// <summary>Issues the query after everything submitted so far and flushes the immediate context.</summary>
    public void Signal()
    {
        var immediate = (ID3D11DeviceContext*)devices.D3D11DeviceContext;
        immediate->End((ID3D11Asynchronous*)done);
        immediate->Flush();
    }

    /// <summary>Waits for the last <see cref="Signal"/>; throws after <see cref="Timeout"/> or when the device was removed.</summary>
    public void Wait()
    {
        var immediate = (ID3D11DeviceContext*)devices.D3D11DeviceContext;
        var deadline = Stopwatch.GetTimestamp() + (long)(Timeout.TotalSeconds * Stopwatch.Frequency);
        var spinner = default(SpinWait);
        int finished = 0;
        while (true)
        {
            var result = immediate->GetData((ID3D11Asynchronous*)done, &finished, sizeof(int), 0).Value;
            if (result == 0 && finished != 0)
            {
                return;
            }
            Native.Check(result, "ID3D11DeviceContext::GetData(event)");
            if (Stopwatch.GetTimestamp() > deadline)
            {
                throw new BenchmarkFailure($"The GPU did not finish within {Timeout.TotalSeconds:F0} s.");
            }
            // Never Sleep(1): it waits a whole timer tick (about 15.6 ms) and would quantize the reading.
            spinner.SpinOnce(sleep1Threshold: -1);
        }
    }

    public void Dispose() => Native.Release(ref done);
}
