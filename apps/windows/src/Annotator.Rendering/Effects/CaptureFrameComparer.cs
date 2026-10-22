using Annotator.Platform;
using Annotator.Platform.Capture;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Interop;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Annotator.Rendering.Effects;

/// <summary>
/// Compares captured frames on the capture device in 32 x 32-pixel tiles (<see cref="CaptureServiceOptions.FrameComparer"/>):
/// a compute shader reads only the tiles that leases look at and sets the bits of the leases whose tiles differ. Nothing
/// waits for the GPU: each comparison's bits are copied into one of eight small read-back buffers, Windows sets the capture
/// service's event when the GPU has done it (<c>IDXGIDevice2::EnqueueSetEvent</c>), and the source reads the bits then
/// with a map that does not wait. With eight comparisons in flight the next frame simply counts as changed.
/// </summary>
public sealed class CaptureFrameComparer : ICaptureFrameComparer
{
    /// <summary>The tile side the shader uses.</summary>
    public const int Tile = 32;

    private long started;
    private long read;
    private long notReady;
    private long full;

    public int TileSize => Tile;

    /// <summary>
    /// Comparisons started, results read, reads that found the GPU not done yet, and frames that found every read-back
    /// buffer in use, over every tracker, for measurements.
    /// </summary>
    public (long Started, long Read, long NotReady, long Full) Counters =>
        (Interlocked.Read(ref started), Interlocked.Read(ref read), Interlocked.Read(ref notReady), Interlocked.Read(ref full));

    public ICaptureChangeTracker Create(GraphicsDevices devices, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        return new Tracker(this, devices, width, height);
    }

    /// <summary>One source's shader, tile buffer, result buffer, read-back ring and frame views on its capture device.</summary>
    private sealed unsafe class Tracker : ICaptureChangeTracker
    {
        private const int DXGI_ERROR_WAS_STILL_DRAWING = unchecked((int)0x887A000A);

        /// <summary>D3D11_BUFFER_UAV_FLAG_RAW and D3D11_BUFFEREX_SRV_FLAG_RAW: buffers read and written as raw bytes.</summary>
        private const uint Raw = 1;

        private const int RingSize = 8;

        /// <summary>The capture pool reuses two textures; a few more views cover a pool that was made again.</summary>
        private const int ViewLimit = 6;

        private readonly CaptureFrameComparer owner;
        private readonly int width;
        private readonly int height;
        private readonly int columns;
        private readonly int rows;
        private readonly List<(nint Texture, nint View)> views = [];
        private readonly ID3D11Buffer*[] ring = new ID3D11Buffer*[RingSize];
        private readonly int[] ringTickets = new int[RingSize];
        private readonly bool[] ringBusy = new bool[RingSize];
        private ID3D11Device* device;
        private ID3D11DeviceContext* context;
        private ID3D11Multithread* multithread;
        private IDXGIDevice2* dxgi;
        private ID3D11ComputeShader* shader;
        private ID3D11Buffer* tiles;
        private ID3D11ShaderResourceView* tilesView;
        private ID3D11Buffer* constants;
        private ID3D11Buffer* result;
        private ID3D11UnorderedAccessView* resultView;
        private int firstColumn;
        private int firstRow;
        private int boxColumns;
        private int boxRows;
        private int nextTicket;
        private bool disposed;

        public Tracker(CaptureFrameComparer owner, GraphicsDevices devices, int width, int height)
        {
            this.owner = owner;
            this.width = width;
            this.height = height;
            columns = (width + Tile - 1) / Tile;
            rows = (height + Tile - 1) / Tile;
            try
            {
                device = (ID3D11Device*)devices.D3D11Device;
                device->AddRef();
                ID3D11DeviceContext* immediate;
                device->GetImmediateContext(&immediate);
                context = immediate;
                multithread = Native.Query<ID3D11Multithread>(device, ID3D11Multithread.IID_Guid, "QueryInterface(ID3D11Multithread)(frame comparison)");
                dxgi = Native.Query<IDXGIDevice2>(device, IDXGIDevice2.IID_Guid, "QueryInterface(IDXGIDevice2)(frame comparison)");
                var code = Bytecode("cs_compare");
                ID3D11ComputeShader* created;
                fixed (byte* bytes = code)
                {
                    Native.Check(device->CreateComputeShader(bytes, (nuint)code.Length, null, &created), "ID3D11Device::CreateComputeShader(cs_compare)");
                }
                shader = created;
                tiles = Buffer((uint)(columns * rows * sizeof(uint)), D3D11_USAGE.D3D11_USAGE_DEFAULT, D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
                               D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_BUFFER_ALLOW_RAW_VIEWS, 0, "frame comparison tiles");
                var tileViewDescription = new D3D11_SHADER_RESOURCE_VIEW_DESC
                {
                    Format = DXGI_FORMAT.DXGI_FORMAT_R32_TYPELESS,
                    ViewDimension = Windows.Win32.Graphics.Direct3D.D3D_SRV_DIMENSION.D3D_SRV_DIMENSION_BUFFEREX,
                };
                tileViewDescription.Anonymous.BufferEx = new D3D11_BUFFEREX_SRV { FirstElement = 0, NumElements = (uint)(columns * rows), Flags = Raw };
                ID3D11ShaderResourceView* tileView;
                Native.Check(device->CreateShaderResourceView((ID3D11Resource*)tiles, &tileViewDescription, &tileView),
                             "ID3D11Device::CreateShaderResourceView(frame comparison tiles)");
                tilesView = tileView;
                constants = Buffer(32, D3D11_USAGE.D3D11_USAGE_DEFAULT, D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER, 0, 0, "frame comparison constants");
                result = Buffer(16, D3D11_USAGE.D3D11_USAGE_DEFAULT, D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS,
                                D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_BUFFER_ALLOW_RAW_VIEWS, 0, "frame comparison result");
                var resultViewDescription = new D3D11_UNORDERED_ACCESS_VIEW_DESC
                {
                    Format = DXGI_FORMAT.DXGI_FORMAT_R32_TYPELESS,
                    ViewDimension = D3D11_UAV_DIMENSION.D3D11_UAV_DIMENSION_BUFFER,
                };
                resultViewDescription.Anonymous.Buffer = new D3D11_BUFFER_UAV { FirstElement = 0, NumElements = 4, Flags = Raw };
                ID3D11UnorderedAccessView* uav;
                Native.Check(device->CreateUnorderedAccessView((ID3D11Resource*)result, &resultViewDescription, &uav),
                             "ID3D11Device::CreateUnorderedAccessView(frame comparison result)");
                resultView = uav;
                for (var index = 0; index < RingSize; index++)
                {
                    ring[index] = Buffer(16, D3D11_USAGE.D3D11_USAGE_STAGING, 0, 0, D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ, "frame comparison read-back");
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void SetTiles(ReadOnlySpan<uint> leases)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (leases.Length != columns * rows)
            {
                throw new ArgumentException($"The tiles need {columns * rows} entries, not {leases.Length}.", nameof(leases));
            }
            int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    if (leases[row * columns + column] != 0)
                    {
                        left = Math.Min(left, column);
                        right = Math.Max(right, column);
                        top = Math.Min(top, row);
                        bottom = Math.Max(bottom, row);
                    }
                }
            }
            (firstColumn, firstRow, boxColumns, boxRows) = right < 0 ? (0, 0, 0, 0) : (left, top, right - left + 1, bottom - top + 1);
            var values = stackalloc uint[8] { (uint)firstColumn, (uint)firstRow, (uint)columns, (uint)width, (uint)height, 0, 0, 0 };
            multithread->Enter();
            try
            {
                fixed (uint* bits = leases)
                {
                    context->UpdateSubresource((ID3D11Resource*)tiles, 0, null, bits, 0, 0);
                }
                context->UpdateSubresource((ID3D11Resource*)constants, 0, null, values, 0, 0);
            }
            finally
            {
                multithread->Leave();
            }
        }

        public bool TryBegin(nint previous, nint current, nint completed, out int ticket)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ticket = 0;
            var slot = Array.IndexOf(ringBusy, false);
            if (slot < 0)
            {
                Interlocked.Increment(ref owner.full);
                return false;
            }
            multithread->Enter();
            try
            {
                var zero = stackalloc uint[4];
                context->ClearUnorderedAccessViewUint(resultView, zero);
                if (boxColumns > 0)
                {
                    var sources = stackalloc ID3D11ShaderResourceView*[3];
                    sources[0] = View(previous);
                    sources[1] = View(current);
                    sources[2] = tilesView;
                    var buffer = constants;
                    var target = resultView;
                    context->CSSetShader(shader, null, 0);
                    context->CSSetShaderResources(0, 3, sources);
                    context->CSSetUnorderedAccessViews(0, 1, &target, null);
                    context->CSSetConstantBuffers(0, 1, &buffer);
                    context->Dispatch((uint)boxColumns, (uint)boxRows, 1);
                    sources[0] = sources[1] = sources[2] = null;
                    context->CSSetShaderResources(0, 3, sources);
                    target = null;
                    context->CSSetUnorderedAccessViews(0, 1, &target, null);
                    buffer = null;
                    context->CSSetConstantBuffers(0, 1, &buffer);
                    context->CSSetShader(null, null, 0);
                }
                context->CopyResource((ID3D11Resource*)ring[slot], (ID3D11Resource*)result);
                if (completed != 0)
                {
                    // Flushes, and has Windows set the event once the GPU has done everything submitted so far.
                    Native.Check(dxgi->EnqueueSetEvent((HANDLE)completed), "IDXGIDevice2::EnqueueSetEvent(frame comparison)");
                }
                else
                {
                    context->Flush();
                }
            }
            finally
            {
                multithread->Leave();
            }
            ringBusy[slot] = true;
            ringTickets[slot] = ticket = (++nextTicket << 4) | slot;
            Interlocked.Increment(ref owner.started);
            return true;
        }

        public bool TryRead(int ticket, out uint changed)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            changed = 0;
            var slot = ticket & 0xF;
            if (slot >= RingSize || !ringBusy[slot] || ringTickets[slot] != ticket)
            {
                throw new InvalidOperationException("The comparison was read already or never started.");
            }
            int status;
            multithread->Enter();
            try
            {
                D3D11_MAPPED_SUBRESOURCE mapped;
                status = context->Map((ID3D11Resource*)ring[slot], 0, D3D11_MAP.D3D11_MAP_READ, (uint)D3D11_MAP_FLAG.D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped).Value;
                if (status >= 0)
                {
                    changed = *(uint*)mapped.pData;
                    context->Unmap((ID3D11Resource*)ring[slot], 0);
                }
            }
            finally
            {
                multithread->Leave();
            }
            if (status == DXGI_ERROR_WAS_STILL_DRAWING)
            {
                Interlocked.Increment(ref owner.notReady);
                return false;
            }
            Native.Check(status, "ID3D11DeviceContext::Map(frame comparison)");
            ringBusy[slot] = false;
            Interlocked.Increment(ref owner.read);
            return true;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            foreach (var (_, view) in views)
            {
                var shaderView = (ID3D11ShaderResourceView*)view;
                Native.Release(ref shaderView);
            }
            views.Clear();
            for (var index = 0; index < RingSize; index++)
            {
                var buffer = ring[index];
                Native.Release(ref buffer);
                ring[index] = null;
            }
            Native.Release(ref resultView);
            Native.Release(ref result);
            Native.Release(ref constants);
            Native.Release(ref tilesView);
            Native.Release(ref tiles);
            Native.Release(ref shader);
            Native.Release(ref dxgi);
            Native.Release(ref multithread);
            Native.Release(ref context);
            Native.Release(ref device);
        }

        private ID3D11Buffer* Buffer(uint size, D3D11_USAGE usage, D3D11_BIND_FLAG bind, D3D11_RESOURCE_MISC_FLAG misc, D3D11_CPU_ACCESS_FLAG cpu, string name)
        {
            var description = new D3D11_BUFFER_DESC { ByteWidth = size, Usage = usage, BindFlags = bind, MiscFlags = misc, CPUAccessFlags = cpu };
            ID3D11Buffer* buffer;
            Native.Check(device->CreateBuffer(&description, null, &buffer), $"ID3D11Device::CreateBuffer({name})");
            return buffer;
        }

        /// <summary>A shader view of a captured texture, kept while the capture pool reuses the texture.</summary>
        private ID3D11ShaderResourceView* View(nint texture)
        {
            foreach (var (known, view) in views)
            {
                if (known == texture)
                {
                    return (ID3D11ShaderResourceView*)view;
                }
            }
            ID3D11ShaderResourceView* created;
            Native.Check(device->CreateShaderResourceView((ID3D11Resource*)texture, null, &created), "ID3D11Device::CreateShaderResourceView(captured frame)");
            if (views.Count == ViewLimit)
            {
                var oldest = (ID3D11ShaderResourceView*)views[0].View;
                Native.Release(ref oldest);
                views.RemoveAt(0);
            }
            views.Add((texture, (nint)created));
            return created;
        }

        private static byte[] Bytecode(string entry)
        {
            using var stream = typeof(CaptureFrameComparer).Assembly.GetManifestResourceStream(entry + ".cso")
                ?? throw new InvalidOperationException($"The shader {entry} is missing from the build.");
            var code = new byte[stream.Length];
            stream.ReadExactly(code);
            return code;
        }
    }
}
