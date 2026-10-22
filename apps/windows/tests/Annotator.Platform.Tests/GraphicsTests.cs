using System.Runtime.InteropServices;
using Annotator.Platform.Graphics;
using Annotator.Platform.Interop;
using Annotator.Platform.Threading;
using Windows.Graphics.DirectX;
using Windows.UI.Composition;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;

namespace Annotator.Platform.Tests;

public sealed class GraphicsTests
{
    [Fact]
    public void CreatesTheHardwareDeviceBundle()
    {
        GraphicsDevices devices = GraphicsDevices.Create();
        try
        {
            Assert.Equal(new DeviceState.Ready(), devices.State);
            Assert.True(devices.FeatureLevel >= new Version(11, 0), $"Feature level {devices.FeatureLevel}.");
            Assert.False(string.IsNullOrWhiteSpace(devices.Adapter.Description));
            Assert.NotEqual(0, devices.D3D11Device);
            Assert.NotEqual(0, devices.D3D11DeviceContext);
            Assert.NotEqual(0, devices.DxgiDevice);
            Assert.NotEqual(0, devices.D2DFactory);
            Assert.NotEqual(0, devices.D2DDevice);
            Assert.NotEqual(0, devices.D2DDeviceContext);
            Assert.NotEqual(0, devices.DWriteFactory);
            Assert.Equal(new DeviceState.Ready(), devices.CheckDevice());

            // Ordinary failures leave the state alone; a device-loss result is reported as Lost.
            Assert.Equal(new DeviceState.Ready(), devices.ReportFailure(unchecked((int)0x80070057)));
            DeviceState.Lost? raised = null;
            devices.DeviceLost += (_, args) => raised = args.State;
            DeviceState lost = devices.ReportFailure(DeviceLoss.D2DERR_RECREATE_TARGET);
            Assert.Equal(new DeviceState.Lost(DeviceLossKind.RecreateTarget, DeviceLoss.D2DERR_RECREATE_TARGET), lost);
            Assert.Equal(lost, raised);
            Assert.Equal(lost, devices.ReportFailure(DeviceLoss.DXGI_ERROR_DEVICE_REMOVED));
        }
        finally
        {
            devices.Dispose();
        }

        Assert.Equal(new DeviceState.Disposed(), devices.State);
        Assert.Throws<ObjectDisposedException>(() => devices.D2DDevice);
    }

    [Fact]
    public void CreatesTheWarpBundleForRepeatableImages()
    {
        using GraphicsDevices devices = GraphicsDevices.Create(new GraphicsDeviceOptions { UseWarp = true });
        Assert.Equal(new DeviceState.Ready(), devices.State);
        // WARP reports Microsoft's vendor ID and the Basic Render Driver device ID.
        Assert.Equal(0x1414u, devices.Adapter.VendorId);
        Assert.Equal(0x8Cu, devices.Adapter.DeviceId);
        Assert.NotEqual(0, devices.D2DDeviceContext);
        Assert.Equal(new DeviceState.Ready(), devices.CheckDevice());
    }

    [Theory]
    [InlineData(DeviceLoss.DXGI_ERROR_DEVICE_REMOVED, DeviceLossKind.Removed)]
    [InlineData(DeviceLoss.DXGI_ERROR_DEVICE_RESET, DeviceLossKind.Reset)]
    [InlineData(DeviceLoss.DXGI_ERROR_DEVICE_HUNG, DeviceLossKind.Hung)]
    [InlineData(DeviceLoss.DXGI_ERROR_DRIVER_INTERNAL_ERROR, DeviceLossKind.DriverInternalError)]
    [InlineData(DeviceLoss.D2DERR_RECREATE_TARGET, DeviceLossKind.RecreateTarget)]
    public void ClassifiesDeviceLossResults(int hresult, DeviceLossKind kind)
    {
        Assert.True(DeviceLoss.IsDeviceLoss(hresult));
        Assert.Equal(kind, DeviceLoss.Classify(hresult));
    }

    [Fact]
    public async Task CompositionInteropDrawsIntoSurfacesAndWrapsSwapChains()
    {
        using DispatcherThread thread = DispatcherThread.Start("Annotator test composition");
        await thread.Run(() =>
        {
            using GraphicsDevices devices = GraphicsDevices.Create();
            using var compositor = new Compositor();
            using CompositionGraphicsDevice graphicsDevice = CompositionInterop.CreateGraphicsDevice(compositor, devices.D2DDevice);
            using CompositionDrawingSurface surface = graphicsDevice.CreateDrawingSurface(
                new Windows.Foundation.Size(64, 64), DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);

            using (CompositionDrawingSession session = CompositionInterop.BeginDraw(surface, ID2D1DeviceContext.IID_Guid))
            {
                Assert.NotEqual(0, session.UpdateObject);
                ClearToTransparent(session.UpdateObject);
                session.EndDraw();
                Assert.True(session.IsEnded);
            }

            using var partial = CompositionInterop.BeginDraw(surface, ID2D1DeviceContext.IID_Guid, new Windows.Graphics.RectInt32(8, 8, 16, 16));
            Assert.NotEqual(0, partial.UpdateObject);
            partial.Dispose();
            Assert.Throws<ObjectDisposedException>(() => partial.UpdateObject);

            nint swapChain = devices.CreateSwapChainForComposition(32, 32);
            try
            {
                ICompositionSurface swapChainSurface = CompositionInterop.CreateCompositionSurfaceForSwapChain(compositor, swapChain);
                using CompositionSurfaceBrush brush = compositor.CreateSurfaceBrush(swapChainSurface);
                Assert.NotNull(brush.Surface);
            }
            finally
            {
                Marshal.Release(swapChain);
            }

            Assert.Equal(new DeviceState.Ready(), devices.CheckDevice());
        });
    }

    private static unsafe void ClearToTransparent(nint deviceContext)
    {
        var transparent = new D2D1_COLOR_F { r = 0, g = 0, b = 0, a = 0 };
        ((ID2D1DeviceContext*)deviceContext)->Clear(&transparent);
    }
}