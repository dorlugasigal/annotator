using System.Diagnostics;
using System.Runtime.InteropServices;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Interop;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Annotator.RenderHarness;

/// <summary>
/// Measurement only: DXGI desktop duplication of the primary output, the other way Windows hands out the composed
/// desktop, to check what Windows.Graphics.Capture leaves out (the ink mode's delegated ink trail). Synchronous: each
/// <see cref="Expect"/> waits for duplicated frames on the calling thread, at most its timeout. The DXGI output and
/// duplication interfaces are called through their vtables (the harness has no generated bindings for them).
/// </summary>
internal sealed unsafe class DesktopDuplication : IDisposable
{
    private static readonly Guid IID_IDXGIOutput1 = new("00CDDEA8-939B-4B83-A340-A685226666CC");
    private static readonly Guid IID_IDXGIOutput2 = new("595E39D1-2724-4663-99B1-DA969DE28364");
    private static readonly Guid IID_IDXGIOutput3 = new("8A6BB301-7E7E-41F4-A8E0-5B32F7F99B18");
    private static readonly Guid IID_IDXGIOutput6 = new("068346E8-AAEC-4B84-ADD7-137F513F77A1");
    private const int WaitTimeout = unchecked((int)0x887A0027);

    private readonly GraphicsDevices gpu = GraphicsDevices.Create();
    private nint duplication;

    public DesktopDuplication(DisplayInfo display)
    {
        var output = Output(gpu, display);
        nint output1 = 0;
        try
        {
            Native.Check(Marshal.QueryInterface(output, in IID_IDXGIOutput1, out output1), "QueryInterface(IDXGIOutput1)");
            nint created;
            var duplicate = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)(*(void***)output1)[22];
            Native.Check(duplicate(output1, gpu.D3D11Device, &created), "IDXGIOutput1::DuplicateOutput");
            duplication = created;
        }
        finally
        {
            if (output1 != 0)
            {
                Marshal.Release(output1);
            }
            Marshal.Release(output);
        }
    }

    /// <summary>
    /// What the display's output says about hardware overlay planes: IDXGIOutput2::SupportsOverlays,
    /// IDXGIOutput3::CheckOverlaySupport for BGRA swap chains of <paramref name="gpu"/>, and
    /// IDXGIOutput6::CheckHardwareCompositionSupport.
    /// </summary>
    public static string OverlaySupport(GraphicsDevices gpu, DisplayInfo display)
    {
        var output = Output(gpu, display);
        try
        {
            var parts = new List<string>();
            if (Marshal.QueryInterface(output, in IID_IDXGIOutput2, out var output2) >= 0)
            {
                var supports = ((delegate* unmanaged[Stdcall]<nint, int>)(*(void***)output2)[23])(output2);
                parts.Add($"SupportsOverlays {(supports != 0 ? "yes" : "no")}");
                Marshal.Release(output2);
            }
            if (Marshal.QueryInterface(output, in IID_IDXGIOutput3, out var output3) >= 0)
            {
                uint flags;
                var result = ((delegate* unmanaged[Stdcall]<nint, DXGI_FORMAT, nint, uint*, int>)(*(void***)output3)[24])(output3, DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, gpu.D3D11Device, &flags);
                parts.Add(result < 0 ? $"CheckOverlaySupport(BGRA) failed 0x{result:X8}" : $"CheckOverlaySupport(BGRA) flags 0x{flags:X}");
                Marshal.Release(output3);
            }
            if (Marshal.QueryInterface(output, in IID_IDXGIOutput6, out var output6) >= 0)
            {
                uint flags;
                var result = ((delegate* unmanaged[Stdcall]<nint, uint*, int>)(*(void***)output6)[28])(output6, &flags);
                parts.Add(result < 0 ? $"CheckHardwareCompositionSupport failed 0x{result:X8}" : $"CheckHardwareCompositionSupport flags 0x{flags:X} (1 full screen, 2 windowed, 4 stretched cursor)");
                Marshal.Release(output6);
            }
            return string.Join("; ", parts);
        }
        finally
        {
            Marshal.Release(output);
        }
    }

    private static nint Output(GraphicsDevices gpu, DisplayInfo display)
    {
        var dxgiDevice = (IDXGIDevice*)gpu.DxgiDevice;
        IDXGIAdapter* adapter = null;
        try
        {
            Native.Check(dxgiDevice->GetAdapter(&adapter), "IDXGIDevice::GetAdapter");
            var enumOutputs = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*(void***)adapter)[7];
            for (uint index = 0; ; index++)
            {
                nint candidate;
                if (enumOutputs((nint)adapter, index, &candidate) < 0)
                {
                    throw new InvalidOperationException($"No output of the adapter shows {display.DeviceName}.");
                }
                OutputDescription description;
                var getDesc = (delegate* unmanaged[Stdcall]<nint, OutputDescription*, int>)(*(void***)candidate)[7];
                Native.Check(getDesc(candidate, &description), "IDXGIOutput::GetDesc");
                if (description.Left == display.Bounds.Left && description.Top == display.Bounds.Top)
                {
                    return candidate;
                }
                Marshal.Release(candidate);
            }
        }
        finally
        {
            Native.Release(ref adapter);
        }
    }

    /// <summary>
    /// Runs <paramref name="submit"/>, then waits up to <paramref name="timeout"/> for a duplicated frame whose pixel at
    /// <paramref name="probe"/> matches; returns that frame's last present time (QPC) or null.
    /// </summary>
    public (long? Seen, long Start, long End) Expect(PixelRect probe, Func<(byte Red, byte Green, byte Blue), bool> matches, TimeSpan timeout,
                                                   Func<(long Start, long End)> submit)
    {
        Drain();
        var (start, end) = submit();
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            FrameInfo info;
            nint resource;
            var result = Acquire(20, &info, &resource);
            if (result == WaitTimeout)
            {
                continue;
            }
            Native.Check(result, "IDXGIOutputDuplication::AcquireNextFrame");
            try
            {
                if (info.LastPresentTime == 0)
                {
                    continue;
                }
                var texture = Native.Query<ID3D11Texture2D>((void*)resource, ID3D11Texture2D.IID_Guid, "QueryInterface(ID3D11Texture2D)");
                try
                {
                    if (matches(Read(texture, probe)))
                    {
                        return (info.LastPresentTime, start, end);
                    }
                }
                finally
                {
                    Native.Release(ref texture);
                }
            }
            finally
            {
                Marshal.Release(resource);
                ReleaseFrame();
            }
        }
        return (null, start, end);
    }

    public void Dispose()
    {
        if (duplication != 0)
        {
            Marshal.Release(duplication);
            duplication = 0;
        }
        gpu.Dispose();
    }

    private int Acquire(uint milliseconds, FrameInfo* info, nint* resource) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, FrameInfo*, nint*, int>)(*(void***)duplication)[8])(duplication, milliseconds, info, resource);

    private void ReleaseFrame() => ((delegate* unmanaged[Stdcall]<nint, int>)(*(void***)duplication)[14])(duplication);

    private void Drain()
    {
        for (var index = 0; index < 8; index++)
        {
            FrameInfo info;
            nint resource;
            if (Acquire(0, &info, &resource) < 0)
            {
                return;
            }
            Marshal.Release(resource);
            ReleaseFrame();
        }
    }

    private (byte Red, byte Green, byte Blue) Read(ID3D11Texture2D* texture, PixelRect area)
    {
        var context = (ID3D11DeviceContext*)gpu.D3D11DeviceContext;
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = 1,
            Height = 1,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
        };
        ID3D11Texture2D* staging;
        Native.Check(((ID3D11Device*)gpu.D3D11Device)->CreateTexture2D(&desc, null, &staging), "ID3D11Device::CreateTexture2D(staging)");
        try
        {
            var box = new D3D11_BOX { left = (uint)area.Left, top = (uint)area.Top, front = 0, right = (uint)area.Left + 1, bottom = (uint)area.Top + 1, back = 1 };
            context->CopySubresourceRegion((ID3D11Resource*)staging, 0, 0, 0, 0, (ID3D11Resource*)texture, 0, &box);
            D3D11_MAPPED_SUBRESOURCE mapped;
            Native.Check(context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped), "ID3D11DeviceContext::Map");
            var bytes = (byte*)mapped.pData;
            var pixel = (bytes[2], bytes[1], bytes[0]);
            context->Unmap((ID3D11Resource*)staging, 0);
            return pixel;
        }
        finally
        {
            Native.Release(ref staging);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OutputDescription
    {
        public fixed char DeviceName[32];
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int AttachedToDesktop;
        public int Rotation;
        public nint Monitor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameInfo
    {
        public long LastPresentTime;
        public long LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced;
        public int ProtectedContentMaskedOut;
        public int PointerX;
        public int PointerY;
        public int PointerVisible;
        public uint TotalMetadataBufferSize;
        public uint PointerShapeBufferSize;
    }
}
