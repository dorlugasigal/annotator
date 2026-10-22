using Annotator.App.Icons;
using Annotator.Platform;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Illustrations;
using Annotator.Workspace.Artwork;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;

namespace Annotator.App.Introduction;

/// <summary>
/// The tour's drawing resources: Direct3D and Direct2D devices (WARP when the hardware device cannot start), the
/// illustration renderer and WinUI's composition graphics device over them. Created when an illustration first draws and
/// released when the tour hides, so a closed tour holds no device. A lost device is replaced on the next draw; every view
/// sees <see cref="Generation"/> change and makes its surface again. Use it on the window's thread.
/// </summary>
internal sealed class IllustrationGraphics : IDisposable
{
    private readonly DispatcherQueue _queue;
    private GraphicsDevices? _devices;
    private IllustrationRenderer? _renderer;
    private CompositionGraphicsDevice? _composition;

    public IllustrationGraphics(DispatcherQueue queue) => _queue = queue;

    /// <summary>Changes whenever the devices are replaced or released.</summary>
    public int Generation { get; private set; }

    /// <summary>Raised after the devices are released, so views let go of their surfaces too.</summary>
    public event Action? Released;

    /// <summary>While the tour is hidden nothing draws, so no device is made.</summary>
    public bool Suspended { get; set; }

    /// <summary>Whether the last attempt fell back to WARP (diagnostics and measurements).</summary>
    public bool UsesWarp { get; private set; }

    /// <summary>The renderer, creating the devices first; null when even WARP failed (the failure is logged).</summary>
    public IllustrationRenderer? Renderer(Compositor? compositor)
    {
        if (Suspended)
        {
            return null;
        }
        if (_renderer is not null && (compositor is null || _composition is not null))
        {
            return _renderer;
        }
        try
        {
            if (_devices is null)
            {
                try
                {
                    _devices = GraphicsDevices.Create();
                    UsesWarp = false;
                }
                catch (NativeCallException)
                {
                    _devices = GraphicsDevices.Create(new GraphicsDeviceOptions { UseWarp = true });
                    UsesWarp = true;
                }
                GraphicsDevices devices = _devices;
                devices.DeviceLost += (_, _) => _queue.TryEnqueue(() =>
                {
                    if (ReferenceEquals(_devices, devices))
                    {
                        Release();
                    }
                });
                _renderer = new IllustrationRenderer(_devices, Symbol);
            }
            if (compositor is not null && _composition is null)
            {
                _composition = LiftedComposition.CreateGraphicsDevice(compositor, _devices.D2DDevice);
            }
            return _renderer;
        }
        catch (Exception error) when (error is NativeCallException or InvalidOperationException or ArgumentException)
        {
            StartupLog.Note("introduction-graphics", error.Message);
            Release();
            return null;
        }
    }

    /// <summary>WinUI's composition device over the renderer's Direct2D device, after <see cref="Renderer"/> with a compositor.</summary>
    public CompositionGraphicsDevice? Composition => _composition;

    /// <summary>A draw failed: the devices go, and the next draw makes new ones.</summary>
    public void Fail(Exception error)
    {
        StartupLog.Note("introduction-graphics", error.Message);
        Release();
    }

    /// <summary>Releases every device; the next draw makes new ones.</summary>
    public void Release()
    {
        if (_devices is null && _renderer is null && _composition is null)
        {
            return;
        }
        Released?.Invoke();
        _composition?.Dispose();
        _composition = null;
        _renderer?.Dispose();
        _renderer = null;
        _devices?.Dispose();
        _devices = null;
        Generation++;
    }

    public void Dispose() => Release();

    /// <summary>The Windows icon for a Mac SF Symbol in the illustrations (D-6).</summary>
    private static IReadOnlyList<IconRaster.Part>? Symbol(string symbol) =>
        LucideIcons.ForSymbol(symbol) is { } icon ? [.. icon.Parts.Select(part => new IconRaster.Part(part.Data, part.Filled))] : null;
}
