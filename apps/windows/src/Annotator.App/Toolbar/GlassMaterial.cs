using Annotator.Core;
using Annotator.Workspace.Toolbar;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using CompositionTarget = Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop;
using WinColor = Windows.UI.Color;

namespace Annotator.App.Toolbar;

/// <summary>One toolbar surface on Glass toolbar's material, and the layer over the material that holds everything else.</summary>
internal sealed record GlassSurface(Grid Surface, Grid Layer, double CornerRadius);

/// <summary>
/// Glass toolbar's material for one toolbar window (WIN-UI-09): Windows' Acrylic behind each surface (<see cref="GlassBackdrop"/>),
/// clipped to the surface's corners (<see cref="SystemBackdropElement"/>), with a light rim drawn over it, the Mac's Liquid
/// Glass edge. Windows blurs what is behind the window: Annotator reads no screen pixels and runs no
/// shader of its own (DV-14). It also makes the raised lenses of the chosen tool and the buttons that are on
/// (<see cref="Lens"/>), in the build's theme.
/// <para>
/// The materials sit in <see cref="Host"/>, under the toolbar's controls, and stay there for as long as the window lives; after
/// each layout they are placed over the surfaces (<see cref="Place"/>). Rebuilding the toolbar never takes them out of the
/// window: one that left it would close its link to Windows' backdrop, and that link belongs to the app thread, so letting go of
/// it last on the garbage collector's finalizer thread ends the app.
/// </para>
/// </summary>
internal sealed partial class GlassMaterial
{
    private readonly List<Material> _materials = [];
    private int _needed;

    public GlassMaterial() => AutomationProperties.SetAccessibilityView(Host, AccessibilityView.Raw);

    /// <summary>The materials' own layer, which the window keeps under the toolbar's surfaces. It takes no room or input.</summary>
    public Grid Host { get; } = new MaterialLayer { IsHitTestVisible = false };

    /// <summary>Whether the material is tinted for the dark theme.</summary>
    public bool Dark { get; private set; }

    /// <summary>Starts a build for the light or <paramref name="dark"/> theme; the new surfaces ask for their materials again.</summary>
    public void Begin(bool dark)
    {
        _needed = 0;
        Dark = dark;
        foreach (Material material in _materials)
        {
            material.Backdrop.Dark = dark;
        }
    }

    /// <summary>
    /// Puts the glass in a surface: the layer over its material, for everything else on the surface. A fade must change the
    /// layer, never the material: Windows blurs the material only while it is fully opaque. The layer's clear background takes
    /// the surface's pointer presses, as the solid background does, so a drag can start anywhere on the surface.
    /// </summary>
    public Grid Layer(Panel surface, double cornerRadius)
    {
        if (++_needed > _materials.Count)
        {
            var material = new Material(Dark);
            _materials.Add(material);
            Host.Children.Add(material.Element);
        }
        var layer = new Grid();
        layer.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(cornerRadius),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        });
        surface.Children.Add(layer);
        return layer;
    }

    /// <summary>
    /// Places a material over each of <paramref name="shapes"/>, in <see cref="Host"/>'s coordinates, and hides the others;
    /// unchanged values are left alone, so a layout pass that changes nothing ends there.
    /// </summary>
    public void Place(IReadOnlyList<(Windows.Foundation.Rect Bounds, double CornerRadius)> shapes)
    {
        for (int index = 0; index < _materials.Count; index++)
        {
            SystemBackdropElement element = _materials[index].Element;
            if (index >= shapes.Count)
            {
                if (element.Visibility != Visibility.Collapsed)
                {
                    element.Visibility = Visibility.Collapsed;
                }
                continue;
            }
            (Windows.Foundation.Rect bounds, double radius) = shapes[index];
            var margin = new Thickness(bounds.X, bounds.Y, 0, 0);
            if (element.Margin != margin)
            {
                element.Margin = margin;
            }
            Set(element.Width, bounds.Width, value => element.Width = value);
            Set(element.Height, bounds.Height, value => element.Height = value);
            if (element.CornerRadius.TopLeft != radius)
            {
                element.CornerRadius = new CornerRadius(radius);
            }
            if (element.Visibility != Visibility.Visible)
            {
                element.Visibility = Visibility.Visible;
            }
        }
    }

    /// <summary>
    /// What a snapshot shows for the material, which it can't hold (<see cref="ToolbarGlass.TintUnder"/>): the theme's gray that
    /// sets the glass's brightness (<see cref="GlassBackdrop"/>), mostly opaque, as the material looks without its blur.
    /// </summary>
    public RGBA SnapshotTint => Dark ? Gray(0x58, 0.85) : Gray(0xCA, 0.85);

    /// <summary>A raised lens for <paramref name="role"/> in this build's theme (WIN-UI-09, <see cref="ToolbarLens"/>).</summary>
    public GlassLens Lens(ToolbarLensRole role, double cornerRadius, double outsetX = 0, double outsetY = 0) =>
        new(Look(role), cornerRadius, outsetX, outsetY);

    /// <summary><paramref name="content"/> over a raised lens for <paramref name="role"/>, which fills the same space.</summary>
    public Grid OnLens(UIElement content, ToolbarLensRole role, double cornerRadius, double outsetX = 0, double outsetY = 0)
    {
        var grid = new Grid();
        grid.Children.Add(Lens(role, cornerRadius, outsetX, outsetY));
        grid.Children.Add(content);
        return grid;
    }

    /// <summary>
    /// Draw's lens, for the Draw or Apps button and the one-button toolbar. For Apps, the neutral lens, its green tint fades
    /// out and its shadow fades to <c>AppsShadow</c>.
    /// </summary>
    public (GlassLens Lens, double AppsShadow) ModeLens(double cornerRadius)
    {
        ToolbarLensLook draw = Look(ToolbarLensRole.Draw);
        return (new GlassLens(draw, cornerRadius), Look(ToolbarLensRole.Neutral).ShadowOpacity / draw.ShadowOpacity);
    }

    /// <summary>
    /// The Liquid Glass edge, over a surface's controls: a light rim just inside its outline, a little brighter at the top, as
    /// strong as the Mac's measures.
    /// </summary>
    public UIElement Rim(double cornerRadius) => new Border
    {
        Margin = new Thickness(1),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(Math.Max(0, cornerRadius - 1)),
        BorderBrush = Gradient(White(Dark ? 0.14 : 0.3), White(Dark ? 0.06 : 0.2), 1),
        IsHitTestVisible = false,
    };

    private static void Set(double current, double value, Action<double> set)
    {
        if (double.IsNaN(current) || Math.Abs(current - value) > 0.001)
        {
            set(value);
        }
    }

    private ToolbarLensLook Look(ToolbarLensRole role) =>
        ToolbarLens.For(role, Dark, ThemeColor("AnnotatorAccentBrush"), ThemeColor("AnnotatorDrawDotBrush"), ThemeColor("AnnotatorOrangeBrush"));

    private static RGBA ThemeColor(string key) => AppResources.Brush(key) is SolidColorBrush { Color: var color }
        ? new RGBA(color.R / 255.0, color.G / 255.0, color.B / 255.0)
        : new RGBA(0, 0, 0);

    private static WinColor White(double alpha) => WinColor.FromArgb((byte)Math.Round(alpha * 255), 255, 255, 255);

    private static RGBA Gray(int level, double alpha) => new(level / 255.0, level / 255.0, level / 255.0, alpha);

    /// <summary>Top to bottom from <paramref name="top"/> to <paramref name="bottom"/>, which it reaches at <paramref name="end"/>.</summary>
    private static LinearGradientBrush Gradient(WinColor top, WinColor bottom, double end)
    {
        var brush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1) };
        brush.GradientStops.Add(new GradientStop { Color = top, Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = bottom, Offset = end });
        return brush;
    }

    /// <summary>
    /// A layer that takes no room, so only the toolbar's surfaces size the window, even before their materials follow them. A
    /// Grid, since a material in a Canvas doesn't show.
    /// </summary>
    private sealed partial class MaterialLayer : Grid
    {
        protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
        {
            base.MeasureOverride(availableSize);
            return default;
        }
    }

    /// <summary>One material element with its backdrop.</summary>
    private sealed class Material
    {
        public Material(bool dark)
        {
            Backdrop = new GlassBackdrop { Dark = dark };
            Element = new SystemBackdropElement
            {
                SystemBackdrop = Backdrop,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };
            AutomationProperties.SetAccessibilityView(Element, AccessibilityView.Raw);
        }

        public SystemBackdropElement Element { get; }

        public GlassBackdrop Backdrop { get; }
    }
}

/// <summary>
/// Windows' Acrylic behind one glass surface, in the Mac's colors (WIN-UI-09, <see cref="Tint"/>). It makes its controller when
/// its surface connects and closes it when the surface goes, as WinUI's own DesktopAcrylicBackdrop does, and follows the theme
/// without connecting again. It stays active while another app has the keyboard, since the toolbar never activates, so it
/// doesn't turn solid while the person draws.
/// </summary>
internal sealed partial class GlassBackdrop : SystemBackdrop
{
    /// <summary>
    /// Links whose material let them go, which happens when the window closes. Their wrappers stay, because the finalizer thread
    /// can't release them (see <see cref="GlassMaterial"/>).
    /// </summary>
    private static readonly List<CompositionTarget> s_closed = [];
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _configuration;
    private bool _dark;

    /// <summary>Whether the tint is the dark theme's; a connected material changes at once.</summary>
    public bool Dark
    {
        get => _dark;
        set
        {
            if (value == _dark)
            {
                return;
            }
            _dark = value;
            if (_controller is { } controller)
            {
                Tint(controller);
            }
            if (_configuration is { } configuration)
            {
                configuration.Theme = Theme;
            }
        }
    }

    private SystemBackdropTheme Theme => _dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;

    protected override void OnTargetConnected(CompositionTarget connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Thin };
        Tint(_controller);
        _configuration = new SystemBackdropConfiguration { IsInputActive = true, Theme = Theme };
        _controller.AddSystemBackdropTarget(connectedTarget);
        _controller.SetSystemBackdropConfiguration(_configuration);
    }

    protected override void OnTargetDisconnected(CompositionTarget disconnectedTarget)
    {
        // As WinUI's own DesktopAcrylicBackdrop: the base class first, then the target leaves the controller, which closes.
        base.OnTargetDisconnected(disconnectedTarget);
        _controller?.RemoveSystemBackdropTarget(disconnectedTarget);
        _controller?.Dispose();
        _controller = null;
        _configuration = null;
        s_closed.Add(disconnectedTarget);
    }

    /// <summary>
    /// The glass's look (WIN-UI-09): Acrylic keeps the colors behind the toolbar and sets their brightness to the theme's
    /// gray, with a little of that gray mixed in. Measured over the same purple (#744BA0), it gives the Mac's Liquid Glass
    /// with 25% of the window color: about #D9BAFA in Light and #67478B in Dark, where the Mac shows #DCB7FB and #6A458A.
    /// The fixed brightness keeps labels readable over white, black or busy screens. Whenever Windows can't draw the
    /// material, it shows the window color.
    /// </summary>
    private void Tint(DesktopAcrylicController controller)
    {
        byte gray = _dark ? (byte)0x58 : (byte)0xCA;
        controller.TintColor = WinColor.FromArgb(255, gray, gray, gray);
        controller.TintOpacity = _dark ? 0.2f : 0.1f;
        controller.LuminosityOpacity = 1;
        controller.FallbackColor = _dark ? WinColor.FromArgb(255, 0x1E, 0x1E, 0x1E) : WinColor.FromArgb(255, 0xFF, 0xFF, 0xFF);
    }
}
