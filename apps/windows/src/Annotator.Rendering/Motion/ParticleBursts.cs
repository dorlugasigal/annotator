using System.Numerics;
using System.Runtime.InteropServices;
using Annotator.Core;
using Annotator.Core.Scene;
using Annotator.Platform;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Interop;
using Annotator.Workspace.Motion;
using Windows.UI.Composition;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;

namespace Annotator.Rendering.Motion;

/// <summary>A snapshot's premultiplied pixels as a texture for the particle shader, and where it rests on the overlay.</summary>
public sealed unsafe class ParticleSource : IDisposable
{
    private ID3D11ShaderResourceView* _view;

    internal ParticleSource(ID3D11ShaderResourceView* view, PixelRect pixels)
    {
        _view = view;
        Pixels = pixels;
    }

    /// <summary>Where the image rests, in the overlay's pixels.</summary>
    public PixelRect Pixels { get; }

    internal ID3D11ShaderResourceView* View => _view;

    public void Dispose() => Native.Release(ref _view);
}

/// <summary>
/// Dissolve on one overlay (Mac <c>ParticleBurstView</c>, one per snapshot): every burst draws its image, fading from the
/// edge that faces the toolbar, and up to 96,000 particles (32,000 for the toolbar) with Direct3D 11 into one composition
/// swap chain over the area the bursts can reach. The caller's frame clock calls <see cref="Render"/> once per composition
/// frame while a burst changes; the last frame stays until the bursts are removed, and nothing runs in between.
/// </summary>
public sealed unsafe class ParticleBursts : IDisposable
{
    private readonly Compositor _compositor;
    private readonly ContainerVisual _parent;
    private readonly GraphicsDevices _devices;
    private readonly double _scale;
    private readonly List<Burst> _bursts = [];
    private ID3D11Device* _device;
    private ID3D11DeviceContext* _context;
    private ID3D11VertexShader* _imageVertex;
    private ID3D11PixelShader* _imagePixel;
    private ID3D11VertexShader* _particleVertex;
    private ID3D11PixelShader* _particlePixel;
    private ID3D11SamplerState* _sampler;
    private ID3D11BlendState* _blend;
    private ID3D11Buffer* _constants;
    private IDXGISwapChain2* _swapChain;
    private ID3D11RenderTargetView* _target;
    private HANDLE _waitable;
    private SpriteVisual? _visual;
    private CompositionSurfaceBrush? _brush;
    private ICompositionSurface? _surface;
    private PixelRect _area;
    private bool _disposed;

    /// <param name="parent">The stage's container; the bursts draw above its snapshots.</param>
    /// <param name="bounds">The overlay's size in pixels, which the bursts' area never leaves.</param>
    public ParticleBursts(Compositor compositor, ContainerVisual parent, GraphicsDevices devices, double scale, PixelRect bounds)
    {
        _compositor = compositor ?? throw new ArgumentNullException(nameof(compositor));
        _parent = parent ?? throw new ArgumentNullException(nameof(parent));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _scale = scale;
        Bounds = bounds;
        try
        {
            _device = Native.AddRef((ID3D11Device*)devices.D3D11Device);
            ID3D11DeviceContext* context;
            _device->GetImmediateContext(&context);
            _context = context;
            _imageVertex = CreateVertexShader("image_vs");
            _imagePixel = CreatePixelShader("image_ps");
            _particleVertex = CreateVertexShader("particle_vs");
            _particlePixel = CreatePixelShader("particle_ps");
            var sampler = new D3D11_SAMPLER_DESC
            {
                Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_LINEAR,
                AddressU = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                AddressV = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                ComparisonFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_NEVER,
                MaxLOD = float.MaxValue,
            };
            ID3D11SamplerState* createdSampler;
            Check(_device->CreateSamplerState(&sampler, &createdSampler), "ID3D11Device::CreateSamplerState(particles)");
            _sampler = createdSampler;
            // Premultiplied "over", as the Mac's pipeline blends.
            var blend = default(D3D11_BLEND_DESC);
            blend.RenderTarget._0 = new D3D11_RENDER_TARGET_BLEND_DESC
            {
                BlendEnable = true,
                SrcBlend = D3D11_BLEND.D3D11_BLEND_ONE,
                DestBlend = D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA,
                BlendOp = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD,
                SrcBlendAlpha = D3D11_BLEND.D3D11_BLEND_ONE,
                DestBlendAlpha = D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA,
                BlendOpAlpha = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD,
                // D3D11_COLOR_WRITE_ENABLE_ALL.
                RenderTargetWriteMask = 0x0F,
            };
            ID3D11BlendState* createdBlend;
            Check(_device->CreateBlendState(&blend, &createdBlend), "ID3D11Device::CreateBlendState(particles)");
            _blend = createdBlend;
            var buffer = new D3D11_BUFFER_DESC
            {
                ByteWidth = (uint)sizeof(Constants),
                Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
                CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
            };
            ID3D11Buffer* createdBuffer;
            Check(_device->CreateBuffer(&buffer, null, &createdBuffer), "ID3D11Device::CreateBuffer(particle constants)");
            _constants = createdBuffer;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The overlay's size in pixels.</summary>
    public PixelRect Bounds { get; }

    /// <summary>The overlay pixels the swap chain covers now.</summary>
    public PixelRect Area => _area;

    public int Count => _bursts.Count;

    /// <summary>Particles of every burst, for measurements.</summary>
    public int ParticleCount => _bursts.Sum(burst => burst.Grid.Count);

    /// <summary>
    /// Adds or replaces the burst <paramref name="key"/>: <paramref name="source"/> dissolves along <paramref name="plan"/>
    /// while <paramref name="motion"/> (or nothing) moves it, with times in seconds from <paramref name="started"/> on the
    /// steady clock. Call <see cref="Prepare"/> after the last change.
    /// </summary>
    public void Set(string key, ParticleSource source, ParticlePlan plan, LayerMotion? motion, double started)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(plan);
        var width = source.Pixels.Width / _scale;
        var height = source.Pixels.Height / _scale;
        var burst = new Burst(key, source, plan, motion, started, ParticleField.Grid(width, height, Math.Max(1, plan.Limit)));
        var index = _bursts.FindIndex(existing => existing.Key == key);
        if (index >= 0)
        {
            _bursts[index] = burst;
        }
        else
        {
            _bursts.Add(burst);
        }
    }

    /// <summary>Removes the bursts whose keys are not in <paramref name="keep"/>; their sources stay with the caller.</summary>
    public void Retain(IReadOnlySet<string> keep) => _bursts.RemoveAll(burst => !keep.Contains(burst.Key));

    /// <summary>Makes the swap chain cover every burst's reach and draws the first frame at <paramref name="now"/>.</summary>
    public bool Prepare(double now)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_bursts.Count == 0)
        {
            ReleaseLayer();
            return false;
        }
        PixelRect? reach = null;
        foreach (var burst in _bursts)
        {
            var bounds = Reach(burst);
            reach = reach is { } union ? Union(union, bounds) : bounds;
        }
        var area = Intersect(reach!.Value, Bounds);
        if (area.Width <= 0 || area.Height <= 0)
        {
            ReleaseLayer();
            return false;
        }
        if (_swapChain is null || !Contains(_area, area))
        {
            CreateLayer(area);
        }
        return Render(now, force: true);
    }

    /// <summary>
    /// Draws every burst as it is at <paramref name="now"/> and presents. Returns whether a burst still changes after this
    /// frame. Skips the frame (and returns true) while the previous one is still queued.
    /// </summary>
    public bool Render(double now) => Render(now, force: false);

    /// <summary>Removes every burst and the swap chain in this commit.</summary>
    public void Clear()
    {
        _bursts.Clear();
        ReleaseLayer();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _bursts.Clear();
        ReleaseLayer();
        Native.Release(ref _constants);
        Native.Release(ref _blend);
        Native.Release(ref _sampler);
        Native.Release(ref _particlePixel);
        Native.Release(ref _particleVertex);
        Native.Release(ref _imagePixel);
        Native.Release(ref _imageVertex);
        Native.Release(ref _context);
        Native.Release(ref _device);
    }

    /// <summary>
    /// Renders display lists (points from the display's top-left) into a texture at <paramref name="pixels"/>; with
    /// <paramref name="clip"/>, only what the clip keeps.
    /// </summary>
    public ParticleSource Lists(DisplayListRenderer renderer, ReadOnlySpan<DisplayList> lists, PixelRect pixels, StageClip? clip = null)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        DisplayList[] drawn = lists.ToArray();
        var texture = CreateTexture(pixels, null, 0);
        try
        {
            foreach (var list in lists)
            {
                renderer.Enter(list);
            }
            renderer.CollectImages();
            try
            {
                Draw(texture, context =>
                {
                    var bounds = new PixelRect(0, 0, pixels.Width, pixels.Height);
                    var area = new DrawArea(_scale, -pixels.Left, -pixels.Top, clip?.KeepIn(bounds, pixels.Left, pixels.Top) ?? bounds);
                    renderer.Draw((nint)context, drawn, area);
                    clip?.EraseIn(context, bounds, pixels.Left, pixels.Top);
                });
            }
            finally
            {
                foreach (var list in lists)
                {
                    renderer.Leave(list);
                }
                renderer.CollectImages();
            }
            return new ParticleSource(View(texture), pixels);
        }
        finally
        {
            Native.Release(ref texture);
        }
    }

    /// <summary>A plain color, such as a whiteboard filling the display; with <paramref name="clip"/>, only what the clip keeps.</summary>
    public ParticleSource Color(RGBA color, PixelRect pixels, StageClip? clip = null)
    {
        var texture = CreateTexture(pixels, null, 0);
        try
        {
            Draw(texture, context =>
            {
                var bounds = new PixelRect(0, 0, pixels.Width, pixels.Height);
                clip?.BeginKeep(context, bounds, pixels.Left, pixels.Top);
                var fill = D2D.Color(color);
                context->Clear(&fill);
                clip?.EndKeep(context, bounds, pixels.Left, pixels.Top);
            });
            return new ParticleSource(View(texture), pixels);
        }
        finally
        {
            Native.Release(ref texture);
        }
    }

    /// <summary>Premultiplied BGRA pixels, such as the toolbar's snapshot; with <paramref name="clip"/>, only what the clip keeps.</summary>
    public ParticleSource Bitmap(ReadOnlySpan<byte> bgra, int stride, PixelRect pixels, StageClip? clip = null)
    {
        if (bgra.Length < stride * pixels.Height)
        {
            throw new ArgumentException("The pixels do not fill the image.", nameof(bgra));
        }
        if (clip is not null)
        {
            byte[] kept = bgra[..(stride * pixels.Height)].ToArray();
            clip.Apply(kept, stride, pixels);
            return Bitmap(kept, stride, pixels);
        }
        fixed (byte* data = bgra)
        {
            var texture = CreateTexture(pixels, data, stride);
            try
            {
                return new ParticleSource(View(texture), pixels);
            }
            finally
            {
                Native.Release(ref texture);
            }
        }
    }

    private bool Render(double now, bool force)
    {
        if (_disposed || _swapChain is null || _bursts.Count == 0)
        {
            return false;
        }
        if (!force && PInvoke.WaitForSingleObject(_waitable, 0) == WAIT_EVENT.WAIT_TIMEOUT)
        {
            return true;
        }
        var moving = false;
        var target = _target;
        _context->OMSetRenderTargets(1, &target, null);
        var clear = stackalloc float[4] { 0, 0, 0, 0 };
        _context->ClearRenderTargetView(target, clear);
        var viewport = new D3D11_VIEWPORT { Width = _area.Width, Height = _area.Height, MinDepth = 0, MaxDepth = 1 };
        _context->RSSetViewports(1, &viewport);
        _context->RSSetState(null);
        _context->IASetInputLayout(null);
        _context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        var factor = stackalloc float[4] { 1, 1, 1, 1 };
        _context->OMSetBlendState(_blend, factor, 0xFFFFFFFF);
        _context->OMSetDepthStencilState(null, 0);
        var sampler = _sampler;
        _context->VSSetSamplers(0, 1, &sampler);
        _context->PSSetSamplers(0, 1, &sampler);
        var constants = _constants;
        _context->VSSetConstantBuffers(0, 1, &constants);
        _context->PSSetConstantBuffers(0, 1, &constants);
        foreach (var burst in _bursts)
        {
            var time = now - burst.Started;
            var progress = burst.Plan.ProgressAt(time);
            var state = burst.Motion?.StateAt(time) ?? new LayerState(MotionTransform.Identity, 1);
            moving |= time < burst.End;
            if (state.Opacity <= 0)
            {
                continue;
            }
            var screen = state.Transform.ToScreen();
            var pixels = burst.Source.Pixels;
            var values = new Constants
            {
                Viewport = new Vector4(_area.Width, _area.Height, 0, 0),
                Image = new Vector4(pixels.Left - _area.Left, pixels.Top - _area.Top, (float)(pixels.Width / _scale), (float)(pixels.Height / _scale)),
                Grid = new Vector4(burst.Grid.Columns, burst.Grid.Rows, (float)_scale, (float)ParticleField.Distance),
                Motion = new Vector4((float)progress, (float)burst.Plan.SweepAngle, (float)state.Opacity, 0),
                Transform = new Vector4((float)screen.A, (float)screen.B, (float)(screen.Tx * _scale), (float)(screen.Ty * _scale)),
                Center = new Vector4(pixels.Left - _area.Left + pixels.Width / 2f, pixels.Top - _area.Top + pixels.Height / 2f, 0, 0),
            };
            D3D11_MAPPED_SUBRESOURCE mapped;
            Check(_context->Map((ID3D11Resource*)_constants, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "ID3D11DeviceContext::Map(particle constants)");
            *(Constants*)mapped.pData = values;
            _context->Unmap((ID3D11Resource*)_constants, 0);
            var view = burst.Source.View;
            _context->VSSetShaderResources(0, 1, &view);
            _context->PSSetShaderResources(0, 1, &view);
            _context->VSSetShader(_imageVertex, null, 0);
            _context->PSSetShader(_imagePixel, null, 0);
            _context->Draw(4, 0);
            if (progress > 0 && progress < 1)
            {
                _context->VSSetShader(_particleVertex, null, 0);
                _context->PSSetShader(_particlePixel, null, 0);
                _context->DrawInstanced(4, (uint)burst.Grid.Count, 0, 0);
            }
        }
        ID3D11ShaderResourceView* none = null;
        _context->VSSetShaderResources(0, 1, &none);
        _context->PSSetShaderResources(0, 1, &none);
        _context->OMSetRenderTargets(0, null, null);
        var parameters = default(DXGI_PRESENT_PARAMETERS);
        var presented = _swapChain->Present1(0, 0, &parameters);
        if (presented.Failed)
        {
            Check(presented.Value, "IDXGISwapChain1::Present1(particles)");
        }
        return moving;
    }

    /// <summary>The overlay pixels a burst can reach: its image and padding at every keyframe of its motion.</summary>
    private PixelRect Reach(Burst burst)
    {
        var pixels = burst.Source.Pixels;
        var padding = ParticleField.Padding * _scale;
        double left = pixels.Left - padding, top = pixels.Top - padding, right = pixels.Right + padding, bottom = pixels.Bottom + padding;
        double centerX = pixels.Left + pixels.Width / 2.0, centerY = pixels.Top + pixels.Height / 2.0;
        double minX = left, minY = top, maxX = right, maxY = bottom;
        if (burst.Motion is { } motion)
        {
            foreach (var value in motion.Transform.Values)
            {
                var screen = value.ToScreen();
                foreach (var (x, y) in new[] { (left, top), (right, top), (left, bottom), (right, bottom) })
                {
                    double dx = x - centerX, dy = y - centerY;
                    var px = centerX + screen.A * dx - screen.B * dy + screen.Tx * _scale;
                    var py = centerY + screen.B * dx + screen.A * dy + screen.Ty * _scale;
                    minX = Math.Min(minX, px);
                    minY = Math.Min(minY, py);
                    maxX = Math.Max(maxX, px);
                    maxY = Math.Max(maxY, py);
                }
            }
        }
        return new PixelRect((int)Math.Floor(minX) - 2, (int)Math.Floor(minY) - 2, (int)Math.Ceiling(maxX) + 2, (int)Math.Ceiling(maxY) + 2);
    }

    private void CreateLayer(PixelRect area)
    {
        ReleaseLayer();
        _swapChain = CreateSwapChain(area.Width, area.Height);
        Check(_swapChain->SetMaximumFrameLatency(1), "IDXGISwapChain2::SetMaximumFrameLatency(particles)");
        _waitable = _swapChain->GetFrameLatencyWaitableObject();
        Guid textureIid = ID3D11Texture2D.IID_Guid;
        void* buffer;
        Check(_swapChain->GetBuffer(0, &textureIid, &buffer), "IDXGISwapChain::GetBuffer(particles)");
        try
        {
            ID3D11RenderTargetView* target;
            Check(_device->CreateRenderTargetView((ID3D11Resource*)buffer, null, &target), "ID3D11Device::CreateRenderTargetView(particles)");
            _target = target;
        }
        finally
        {
            Marshal.Release((nint)buffer);
        }
        _surface = Annotator.Platform.Interop.CompositionInterop.CreateCompositionSurfaceForSwapChain(_compositor, (nint)_swapChain);
        _brush = _compositor.CreateSurfaceBrush(_surface);
        _brush.Stretch = CompositionStretch.Fill;
        _visual = _compositor.CreateSpriteVisual();
        _visual.Brush = _brush;
        _visual.Size = new Vector2(area.Width, area.Height);
        _visual.Offset = new Vector3(area.Left, area.Top, 0);
        _parent.Children.InsertAtTop(_visual);
        _area = area;
    }

    private void ReleaseLayer()
    {
        if (_visual is not null)
        {
            _parent.Children.Remove(_visual);
            _visual.Brush = null;
            _visual.Dispose();
            _visual = null;
        }
        _brush?.Dispose();
        _brush = null;
        (_surface as IDisposable)?.Dispose();
        _surface = null;
        Native.Release(ref _target);
        if (_waitable != HANDLE.Null)
        {
            PInvoke.CloseHandle(_waitable);
            _waitable = HANDLE.Null;
        }
        Native.Release(ref _swapChain);
        _area = default;
    }

    private IDXGISwapChain2* CreateSwapChain(int width, int height)
    {
        var dxgiDevice = (IDXGIDevice*)_devices.DxgiDevice;
        IDXGIAdapter* adapter = null;
        IDXGIFactory2* factory = null;
        IDXGISwapChain1* swapChain = null;
        try
        {
            Check(dxgiDevice->GetAdapter(&adapter), "IDXGIDevice::GetAdapter");
            Guid factoryIid = IDXGIFactory2.IID_Guid;
            void* parent;
            Check(adapter->GetParent(&factoryIid, &parent), "IDXGIAdapter::GetParent(IDXGIFactory2)");
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
                Flags = DXGI_SWAP_CHAIN_FLAG.DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT,
            };
            Check(factory->CreateSwapChainForComposition((IUnknown*)_devices.D3D11Device, &description, null, &swapChain),
                  "IDXGIFactory2::CreateSwapChainForComposition(particles)");
            return Native.Query<IDXGISwapChain2>(swapChain, IDXGISwapChain2.IID_Guid, "QueryInterface(IDXGISwapChain2)");
        }
        finally
        {
            Native.Release(ref swapChain);
            Native.Release(ref factory);
            Native.Release(ref adapter);
        }
    }

    private ID3D11Texture2D* CreateTexture(PixelRect pixels, byte* data, int stride)
    {
        if (pixels.Width <= 0 || pixels.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixels), "A particle image needs at least one pixel.");
        }
        var description = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)pixels.Width,
            Height = (uint)pixels.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = data == null
                ? D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET
                : D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
        };
        ID3D11Texture2D* texture;
        if (data == null)
        {
            Check(_device->CreateTexture2D(&description, null, &texture), "ID3D11Device::CreateTexture2D(particle image)");
        }
        else
        {
            var initial = new D3D11_SUBRESOURCE_DATA { pSysMem = data, SysMemPitch = (uint)stride };
            Check(_device->CreateTexture2D(&description, &initial, &texture), "ID3D11Device::CreateTexture2D(particle image)");
        }
        return texture;
    }

    private ID3D11ShaderResourceView* View(ID3D11Texture2D* texture)
    {
        ID3D11ShaderResourceView* view;
        Check(_device->CreateShaderResourceView((ID3D11Resource*)texture, null, &view), "ID3D11Device::CreateShaderResourceView(particle image)");
        return view;
    }

    private delegate void DrawCallback(ID2D1DeviceContext* context);

    /// <summary>Draws into a texture with Direct2D: cleared, then <paramref name="draw"/>.</summary>
    private void Draw(ID3D11Texture2D* texture, DrawCallback draw)
    {
        var surface = Native.Query<IDXGISurface>(texture, IDXGISurface.IID_Guid, "QueryInterface(IDXGISurface)");
        ID2D1DeviceContext* context = null;
        ID2D1Bitmap1* bitmap = null;
        try
        {
            Check(((ID2D1Device*)_devices.D2DDevice)->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS.D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &context),
                  "ID2D1Device::CreateDeviceContext(particle image)");
            var properties = new D2D1_BITMAP_PROPERTIES1
            {
                pixelFormat = new D2D1_PIXEL_FORMAT { format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED },
                dpiX = 96,
                dpiY = 96,
                bitmapOptions = D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
            };
            Check(context->CreateBitmapFromDxgiSurface(surface, &properties, &bitmap), "ID2D1DeviceContext::CreateBitmapFromDxgiSurface(particle image)");
            context->SetTarget((ID2D1Image*)bitmap);
            context->BeginDraw();
            HRESULT ended;
            try
            {
                var transparent = default(D2D1_COLOR_F);
                context->Clear(&transparent);
                draw(context);
            }
            finally
            {
                ended = context->EndDraw(null, null);
                context->SetTarget(null);
            }
            Check(ended.Value, "ID2D1DeviceContext::EndDraw(particle image)");
        }
        finally
        {
            Native.Release(ref bitmap);
            Native.Release(ref context);
            Native.Release(ref surface);
        }
    }

    private ID3D11VertexShader* CreateVertexShader(string entry)
    {
        var code = Bytecode(entry);
        ID3D11VertexShader* shader;
        fixed (byte* bytes = code)
        {
            Check(_device->CreateVertexShader(bytes, (nuint)code.Length, null, &shader), $"ID3D11Device::CreateVertexShader({entry})");
        }
        return shader;
    }

    private ID3D11PixelShader* CreatePixelShader(string entry)
    {
        var code = Bytecode(entry);
        ID3D11PixelShader* shader;
        fixed (byte* bytes = code)
        {
            Check(_device->CreatePixelShader(bytes, (nuint)code.Length, null, &shader), $"ID3D11Device::CreatePixelShader({entry})");
        }
        return shader;
    }

    private static byte[] Bytecode(string entry)
    {
        using var stream = typeof(ParticleBursts).Assembly.GetManifestResourceStream(entry + ".cso")
            ?? throw new InvalidOperationException($"The particle shader {entry} is missing from the build.");
        var code = new byte[stream.Length];
        stream.ReadExactly(code);
        return code;
    }

    private void Check(HRESULT result, string operation) => Check(result.Value, operation);

    private void Check(int result, string operation)
    {
        if (result < 0)
        {
            if (DeviceLoss.IsDeviceLoss(result))
            {
                _devices.ReportFailure(result);
            }
            throw new NativeCallException(operation, result);
        }
    }

    private static PixelRect Union(PixelRect a, PixelRect b) =>
        new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));

    private static PixelRect Intersect(PixelRect a, PixelRect b) =>
        new(Math.Max(a.Left, b.Left), Math.Max(a.Top, b.Top), Math.Max(Math.Max(a.Left, b.Left), Math.Min(a.Right, b.Right)),
            Math.Max(Math.Max(a.Top, b.Top), Math.Min(a.Bottom, b.Bottom)));

    private static bool Contains(PixelRect outer, PixelRect inner) =>
        inner.Left >= outer.Left && inner.Top >= outer.Top && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    /// <summary>The shader's constant buffer (particles.hlsl).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Vector4 Viewport;
        public Vector4 Image;
        public Vector4 Grid;
        public Vector4 Motion;
        public Vector4 Transform;
        public Vector4 Center;
    }

    private sealed record Burst(string Key, ParticleSource Source, ParticlePlan Plan, LayerMotion? Motion, double Started, ParticleField.GridSize Grid)
    {
        public double End => Math.Max(Plan.EndTime, Motion?.EndTime ?? 0);
    }
}
