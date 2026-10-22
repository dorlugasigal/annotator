using Annotator.Platform.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.DirectWrite;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;

namespace Annotator.Platform.Graphics;

/// <summary>
/// The shared device bundle for overlay rendering: a hardware Direct3D 11 device (or WARP, see
/// <see cref="GraphicsDeviceOptions.UseWarp"/>) with BGRA support and multithread protection, its
/// DXGI device, a Direct2D 1.1 factory (multithreaded), device and device context, and a shared
/// DirectWrite factory.
/// </summary>
/// <remarks>
/// Pointer properties are borrowed COM pointers, valid until <see cref="Dispose"/>; AddRef one to
/// keep it longer. The Direct2D device context is for one thread at a time. A lost device is
/// reported as <see cref="DeviceState.Lost"/>, never hidden: Windows signals removal through
/// ID3D11Device4::RegisterDeviceRemovedEvent (a kernel wait, no polling), and renderers report
/// failed draws through <see cref="ReportFailure"/>. A lost bundle must be disposed and replaced.
/// </remarks>
public sealed unsafe class GraphicsDevices : IDisposable
{
    private readonly Lock _gate = new();
    private ID3D11Device* _d3dDevice;
    private ID3D11DeviceContext* _d3dContext;
    private IDXGIDevice* _dxgiDevice;
    private ID2D1Factory1* _d2dFactory;
    private ID2D1Device* _d2dDevice;
    private ID2D1DeviceContext* _d2dContext;
    private IDWriteFactory* _dwriteFactory;
    private RemovalWatch? _removalWatch;
    private DeviceState _state = new DeviceState.Ready();

    private GraphicsDevices()
    {
    }

    /// <summary>Creates every device. Throws <see cref="NativeCallException"/> naming the step that failed.</summary>
    public static GraphicsDevices Create(GraphicsDeviceOptions? options = null)
    {
        var devices = new GraphicsDevices();
        try
        {
            devices.CreateDevices(options ?? new GraphicsDeviceOptions());
        }
        catch
        {
            devices.Dispose();
            throw;
        }
        return devices;
    }

    /// <summary>
    /// Raised once when the bundle becomes <see cref="DeviceState.Lost"/>, on the thread that
    /// noticed (possibly a thread-pool thread). A handler must not throw.
    /// </summary>
    public event EventHandler<DeviceLostEventArgs>? DeviceLost;

    public DeviceState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>The Direct3D feature level, for example 11.1.</summary>
    public Version FeatureLevel { get; private set; } = new(0, 0);

    public GraphicsAdapterInfo Adapter { get; private set; } = new(string.Empty, 0, 0, 0);

    /// <summary>ID3D11Device*.</summary>
    public nint D3D11Device => Borrow(_d3dDevice);

    /// <summary>ID3D11DeviceContext* (the immediate context).</summary>
    public nint D3D11DeviceContext => Borrow(_d3dContext);

    /// <summary>IDXGIDevice*.</summary>
    public nint DxgiDevice => Borrow(_dxgiDevice);

    /// <summary>ID2D1Factory1*.</summary>
    public nint D2DFactory => Borrow(_d2dFactory);

    /// <summary>ID2D1Device*, for <see cref="CompositionInterop.CreateGraphicsDevice"/>.</summary>
    public nint D2DDevice => Borrow(_d2dDevice);

    /// <summary>ID2D1DeviceContext* for offscreen drawing on one thread at a time.</summary>
    public nint D2DDeviceContext => Borrow(_d2dContext);

    /// <summary>IDWriteFactory* (shared factory; QueryInterface for newer versions).</summary>
    public nint DWriteFactory => Borrow(_dwriteFactory);

    /// <summary>Asks the device whether Windows removed it (GetDeviceRemovedReason).</summary>
    public DeviceState CheckDevice()
    {
        int reason = RemovedReason(out DeviceState? notReady);
        if (notReady is not null)
        {
            return notReady;
        }
        return reason < 0 ? MarkLost(DeviceLoss.Classify(reason), reason) : State;
    }

    /// <summary>
    /// Reports the HRESULT of a failed draw, EndDraw or Present. A device-loss HRESULT moves the
    /// bundle to <see cref="DeviceState.Lost"/>; any other failure leaves the state unchanged and
    /// stays the caller's error to handle.
    /// </summary>
    public DeviceState ReportFailure(int hresult)
    {
        if (!DeviceLoss.IsDeviceLoss(hresult))
        {
            return State;
        }
        int reason = RemovedReason(out DeviceState? notReady);
        if (notReady is not null)
        {
            return notReady;
        }
        // Prefer the device's own removal reason. D2DERR_RECREATE_TARGET can arrive while the
        // Direct3D device still works; Direct2D resources must be recreated all the same.
        return reason < 0 ? MarkLost(DeviceLoss.Classify(reason), reason) : MarkLost(DeviceLoss.Classify(hresult), hresult);
    }

    /// <summary>
    /// Creates a flip-model swap chain for composition (BGRA, premultiplied alpha, two buffers),
    /// to show Direct3D output through <see cref="CompositionInterop.CreateCompositionSurfaceForSwapChain"/>.
    /// Returns an owned IDXGISwapChain1 pointer; release it with Marshal.Release.
    /// </summary>
    public nint CreateSwapChainForComposition(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ID3D11Device* device;
        IDXGIDevice* dxgiDevice;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_state is DeviceState.Disposed, this);
            device = _d3dDevice;
            dxgiDevice = _dxgiDevice;
            device->AddRef();
            dxgiDevice->AddRef();
        }

        IDXGIAdapter* adapter = null;
        IDXGIFactory2* factory = null;
        try
        {
            NativeCallException.ThrowIfFailed(dxgiDevice->GetAdapter(&adapter), "IDXGIDevice::GetAdapter");
            Guid factoryIid = IDXGIFactory2.IID_Guid;
            void* parent;
            NativeCallException.ThrowIfFailed(adapter->GetParent(&factoryIid, &parent), "IDXGIAdapter::GetParent(IDXGIFactory2)");
            factory = (IDXGIFactory2*)parent;
            var description = new DXGI_SWAP_CHAIN_DESC1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                BufferUsage = DXGI_USAGE.DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount = 2,
                Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH,
                SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL,
                AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_PREMULTIPLIED,
            };
            IDXGISwapChain1* swapChain;
            NativeCallException.ThrowIfFailed(
                factory->CreateSwapChainForComposition((IUnknown*)device, &description, null, &swapChain),
                "IDXGIFactory2::CreateSwapChainForComposition");
            return (nint)swapChain;
        }
        finally
        {
            ComPtr.Release(ref factory);
            ComPtr.Release(ref adapter);
            ComPtr.Release(ref dxgiDevice);
            ComPtr.Release(ref device);
        }
    }

    public void Dispose()
    {
        RemovalWatch? watch;
        ID3D11Device* d3dDevice;
        ID3D11DeviceContext* d3dContext;
        IDXGIDevice* dxgiDevice;
        ID2D1Factory1* d2dFactory;
        ID2D1Device* d2dDevice;
        ID2D1DeviceContext* d2dContext;
        IDWriteFactory* dwriteFactory;
        lock (_gate)
        {
            if (_state is DeviceState.Disposed)
            {
                return;
            }
            _state = new DeviceState.Disposed();
            (watch, _removalWatch) = (_removalWatch, null);
            d3dDevice = _d3dDevice;
            d3dContext = _d3dContext;
            dxgiDevice = _dxgiDevice;
            d2dFactory = _d2dFactory;
            d2dDevice = _d2dDevice;
            d2dContext = _d2dContext;
            dwriteFactory = _dwriteFactory;
            _d3dDevice = null;
            _d3dContext = null;
            _dxgiDevice = null;
            _d2dFactory = null;
            _d2dDevice = null;
            _d2dContext = null;
            _dwriteFactory = null;
        }

        // Native calls run outside the state lock (see RemovedReason).
        watch?.Dispose();
        ComPtr.Release(ref dwriteFactory);
        ComPtr.Release(ref d2dContext);
        ComPtr.Release(ref d2dDevice);
        ComPtr.Release(ref d2dFactory);
        ComPtr.Release(ref dxgiDevice);
        ComPtr.Release(ref d3dContext);
        ComPtr.Release(ref d3dDevice);
    }

    private void CreateDevices(GraphicsDeviceOptions options)
    {
        var flags = D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT;
        if (options.EnableDebugLayer)
        {
            flags |= D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_DEBUG;
        }

        D3D_FEATURE_LEVEL* levels = stackalloc D3D_FEATURE_LEVEL[2] { D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0 };
        ID3D11Device* device;
        ID3D11DeviceContext* context;
        D3D_FEATURE_LEVEL level;
        D3D_DRIVER_TYPE driver = options.UseWarp ? D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP : D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE;
        NativeCallException.ThrowIfFailed(
            PInvoke.D3D11CreateDevice(null, driver, HMODULE.Null, flags, levels, 2, PInvoke.D3D11_SDK_VERSION, &device, &level, &context),
            options.UseWarp ? "D3D11CreateDevice (WARP, BGRA support)" : "D3D11CreateDevice (hardware, BGRA support)");
        _d3dDevice = device;
        _d3dContext = context;
        FeatureLevel = new Version(((int)level >> 12) & 0xF, ((int)level >> 8) & 0xF);

        // Composition, capture and render workers may use the device from other threads.
        ID3D11Multithread* multithread = ComPtr.QueryInterface<ID3D11Multithread>(device, ID3D11Multithread.IID_Guid, "QueryInterface(ID3D11Multithread)");
        multithread->SetMultithreadProtected(true);
        ComPtr.Release(ref multithread);

        _dxgiDevice = ComPtr.QueryInterface<IDXGIDevice>(device, IDXGIDevice.IID_Guid, "QueryInterface(IDXGIDevice)");
        Adapter = ReadAdapter(_dxgiDevice);

        var factoryOptions = new D2D1_FACTORY_OPTIONS
        {
            debugLevel = options.EnableDebugLayer ? D2D1_DEBUG_LEVEL.D2D1_DEBUG_LEVEL_INFORMATION : D2D1_DEBUG_LEVEL.D2D1_DEBUG_LEVEL_NONE,
        };
        Guid factoryIid = ID2D1Factory1.IID_Guid;
        void* factory;
        NativeCallException.ThrowIfFailed(
            PInvoke.D2D1CreateFactory(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_MULTI_THREADED, &factoryIid, &factoryOptions, &factory),
            "D2D1CreateFactory(ID2D1Factory1, multithreaded)");
        _d2dFactory = (ID2D1Factory1*)factory;

        ID2D1Device* d2dDevice;
        NativeCallException.ThrowIfFailed(_d2dFactory->CreateDevice(_dxgiDevice, &d2dDevice), "ID2D1Factory1::CreateDevice");
        _d2dDevice = d2dDevice;

        ID2D1DeviceContext* d2dContext;
        NativeCallException.ThrowIfFailed(
            _d2dDevice->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS.D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &d2dContext),
            "ID2D1Device::CreateDeviceContext");
        _d2dContext = d2dContext;

        Guid dwriteIid = IDWriteFactory.IID_Guid;
        void* dwrite;
        NativeCallException.ThrowIfFailed(PInvoke.DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, &dwriteIid, &dwrite), "DWriteCreateFactory(shared)");
        _dwriteFactory = (IDWriteFactory*)dwrite;

        _removalWatch = RemovalWatch.Start(this, device);
    }

    private static GraphicsAdapterInfo ReadAdapter(IDXGIDevice* dxgiDevice)
    {
        IDXGIAdapter* adapter;
        NativeCallException.ThrowIfFailed(dxgiDevice->GetAdapter(&adapter), "IDXGIDevice::GetAdapter");
        try
        {
            DXGI_ADAPTER_DESC description;
            NativeCallException.ThrowIfFailed(adapter->GetDesc(&description), "IDXGIAdapter::GetDesc");
            ReadOnlySpan<char> name = description.Description.AsReadOnlySpan();
            int end = name.IndexOf('\0');
            long luid = ((long)description.AdapterLuid.HighPart << 32) | description.AdapterLuid.LowPart;
            return new GraphicsAdapterInfo(new string(end < 0 ? name : name[..end]), description.VendorId, description.DeviceId, luid);
        }
        finally
        {
            ComPtr.Release(ref adapter);
        }
    }

    /// <summary>
    /// Reads GetDeviceRemovedReason on a leased reference, outside the state lock, so a renderer
    /// that holds the Direct3D multithread lock can never deadlock with this bundle.
    /// </summary>
    private int RemovedReason(out DeviceState? notReady)
    {
        ID3D11Device* device;
        lock (_gate)
        {
            if (_state is not DeviceState.Ready)
            {
                notReady = _state;
                return 0;
            }
            device = _d3dDevice;
            device->AddRef();
        }
        notReady = null;
        try
        {
            return device->GetDeviceRemovedReason().Value;
        }
        finally
        {
            ComPtr.Release(ref device);
        }
    }

    /// <summary>Moves Ready to Lost once and raises <see cref="DeviceLost"/> outside the lock.</summary>
    private DeviceState MarkLost(DeviceLossKind kind, int hresult)
    {
        DeviceState.Lost lost;
        lock (_gate)
        {
            if (_state is not DeviceState.Ready)
            {
                return _state;
            }
            lost = new DeviceState.Lost(kind, hresult);
            _state = lost;
        }
        DeviceLost?.Invoke(this, new DeviceLostEventArgs(lost));
        return lost;
    }

    private nint Borrow<T>(T* pointer)
        where T : unmanaged
    {
        ObjectDisposedException.ThrowIf(pointer == null, this);
        return (nint)pointer;
    }

    /// <summary>Waits on the device-removed event on the thread pool and checks the device when it fires.</summary>
    private sealed class RemovalWatch : IDisposable
    {
        private readonly ID3D11Device4* _device;
        private readonly uint _cookie;
        private readonly EventWaitHandle _signal;
        private readonly RegisteredWaitHandle _wait;

        private RemovalWatch(ID3D11Device4* device, uint cookie, EventWaitHandle signal, RegisteredWaitHandle wait)
        {
            _device = device;
            _cookie = cookie;
            _signal = signal;
            _wait = wait;
        }

        public static RemovalWatch Start(GraphicsDevices owner, ID3D11Device* device)
        {
            // ID3D11Device4 exists on every supported Windows 11 build.
            ID3D11Device4* device4 = ComPtr.QueryInterface<ID3D11Device4>(device, ID3D11Device4.IID_Guid, "QueryInterface(ID3D11Device4)");
            var signal = new EventWaitHandle(false, EventResetMode.ManualReset);
            uint cookie = 0;
            bool registered = false;
            try
            {
                NativeCallException.ThrowIfFailed(
                    device4->RegisterDeviceRemovedEvent((HANDLE)signal.SafeWaitHandle.DangerousGetHandle(), &cookie),
                    "ID3D11Device4::RegisterDeviceRemovedEvent");
                registered = true;
                RegisteredWaitHandle wait = ThreadPool.RegisterWaitForSingleObject(
                    signal,
                    static (state, _) => ((GraphicsDevices)state!).CheckDevice(),
                    owner,
                    Timeout.Infinite,
                    executeOnlyOnce: true);
                return new RemovalWatch(device4, cookie, signal, wait);
            }
            catch
            {
                if (registered)
                {
                    device4->UnregisterDeviceRemoved(cookie);
                }
                signal.Dispose();
                ComPtr.Release(ref device4);
                throw;
            }
        }

        public void Dispose()
        {
            _wait.Unregister(null);
            _device->UnregisterDeviceRemoved(_cookie);
            ID3D11Device4* device = _device;
            ComPtr.Release(ref device);
            _signal.Dispose();
        }
    }
}
