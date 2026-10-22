using System.Runtime.InteropServices;
using Annotator.Platform.Capture;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Effects;
using Annotator.Rendering.Interop;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Annotator.Rendering.Tests;

/// <summary>
/// The capture frame comparer on WARP: per tile, only for the leases that look at the tile, every pixel counts (including
/// the last one of a size that is not a multiple of the tile), and nothing waits for the GPU.
/// </summary>
public sealed unsafe class CaptureFrameComparerTests
{
    private const int Width = 333;
    private const int Height = 77;
    private const int Columns = (Width + CaptureFrameComparer.Tile - 1) / CaptureFrameComparer.Tile;
    private const int Rows = (Height + CaptureFrameComparer.Tile - 1) / CaptureFrameComparer.Tile;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static nint Texture(GraphicsDevices devices, byte[] pixels)
    {
        var description = new D3D11_TEXTURE2D_DESC
        {
            Width = Width,
            Height = Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
        };
        ID3D11Texture2D* texture;
        fixed (byte* bytes = pixels)
        {
            var data = new D3D11_SUBRESOURCE_DATA { pSysMem = bytes, SysMemPitch = Width * 4 };
            Native.Check(((ID3D11Device*)devices.D3D11Device)->CreateTexture2D(&description, &data, &texture), "CreateTexture2D");
        }
        return (nint)texture;
    }

    /// <summary>Starts a comparison and reads it once the GPU has set the event.</summary>
    private static uint Compare(ICaptureChangeTracker tracker, nint previous, nint current, AutoResetEvent done)
    {
        Assert.True(tracker.TryBegin(previous, current, done.SafeWaitHandle.DangerousGetHandle(), out var ticket));
        Assert.True(done.WaitOne(Wait), "The GPU set the event.");
        Assert.True(tracker.TryRead(ticket, out var changed), "A finished comparison reads without waiting.");
        return changed;
    }

    [Fact]
    public void TilesReportTheLeasesThatLookAtThem()
    {
        var devices = Warp.Devices;
        var pixels = new byte[Width * Height * 4];
        for (var index = 0; index < pixels.Length; index++)
        {
            pixels[index] = (byte)(index * 7 % 251);
        }
        // One bit of the bottom-right pixel, in the last, partly used tile.
        var corner = (byte[])pixels.Clone();
        corner[^2] ^= 1;
        // And one pixel in the top-left tile as well.
        var both = (byte[])corner.Clone();
        both[(5 * Width + 5) * 4] ^= 0x80;
        var a = Texture(devices, pixels);
        var b = Texture(devices, pixels);
        var c = Texture(devices, corner);
        var d = Texture(devices, both);
        var comparer = new CaptureFrameComparer();
        using var done = new AutoResetEvent(false);
        try
        {
            using var tracker = comparer.Create(devices, Width, Height);
            // Bit 4 looks at the whole frame, bit 1 at the top-left tile and bit 2 at the bottom-right one.
            var tiles = new uint[Columns * Rows];
            Array.Fill(tiles, 4u);
            tiles[0] |= 1;
            tiles[^1] |= 2;
            tracker.SetTiles(tiles);
            Assert.Equal(0u, Compare(tracker, a, b, done));
            Assert.Equal(6u, Compare(tracker, a, c, done));
            Assert.Equal(5u, Compare(tracker, c, d, done));
            Assert.Equal(7u, Compare(tracker, a, d, done));

            // Tiles that no lease looks at are not read: a change there reports nothing.
            Array.Clear(tiles);
            tiles[0] = 1;
            tracker.SetTiles(tiles);
            Assert.Equal(0u, Compare(tracker, a, c, done));
            Assert.Equal(1u, Compare(tracker, c, d, done));
            Array.Clear(tiles);
            tracker.SetTiles(tiles);
            Assert.Equal(0u, Compare(tracker, a, d, done));
            Assert.Equal((7L, 7L, 0L), (comparer.Counters.Started, comparer.Counters.Read, comparer.Counters.Full));
        }
        finally
        {
            Marshal.Release(a);
            Marshal.Release(b);
            Marshal.Release(c);
            Marshal.Release(d);
        }
    }

    [Fact]
    public void ComparisonsInFlightAreBoundedAndNeverWaitedFor()
    {
        var devices = Warp.Devices;
        var pixels = new byte[Width * Height * 4];
        var changed = (byte[])pixels.Clone();
        changed[0] = 1;
        var a = Texture(devices, pixels);
        var b = Texture(devices, changed);
        var comparer = new CaptureFrameComparer();
        try
        {
            using var tracker = comparer.Create(devices, Width, Height);
            var tiles = new uint[Columns * Rows];
            Array.Fill(tiles, 1u);
            tracker.SetTiles(tiles);
            var tickets = new List<int>();
            // Without an event the comparison is flushed and read later.
            while (tracker.TryBegin(a, b, 0, out var ticket))
            {
                tickets.Add(ticket);
                Assert.True(tickets.Count <= 8, "At most eight comparisons are in flight.");
            }
            Assert.Equal(8, tickets.Count);
            Assert.Equal(1, comparer.Counters.Full);
            var deadline = DateTime.UtcNow + Wait;
            foreach (var ticket in tickets)
            {
                uint bits;
                while (!tracker.TryRead(ticket, out bits))
                {
                    Assert.True(DateTime.UtcNow < deadline, "WARP finished the comparisons.");
                    Thread.Sleep(1);
                }
                Assert.Equal(1u, bits);
            }
            Assert.Throws<InvalidOperationException>(() => tracker.TryRead(tickets[0], out _));
            Assert.True(tracker.TryBegin(b, a, 0, out _), "Read comparisons free their place.");
        }
        finally
        {
            Marshal.Release(a);
            Marshal.Release(b);
        }
    }
}
