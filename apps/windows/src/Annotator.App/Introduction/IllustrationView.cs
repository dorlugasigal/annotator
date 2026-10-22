using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Annotator.App.Icons;
using Annotator.App.Shell;
using Annotator.App.Toolbar;
using Annotator.Core.Motion;
using Annotator.Platform.Displays;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Illustrations;
using Annotator.Rendering.Imaging;
using Annotator.Workspace.FirstUse;
using Annotator.Workspace.Motion;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Graphics.DirectX;
using CoreSize = Annotator.Core.Geometry.Size;
using WinRect = Windows.Foundation.Rect;

namespace Annotator.App.Introduction;

/// <summary>
/// One page's illustration (Mac IntroductionIllustration in the tour page): 120 points tall across the page, drawn with
/// Direct2D into a composition surface whenever <see cref="Progress"/> changes, so it costs nothing once the story ends.
/// A click plays the story again, a mark in the corner shows that on hover, and UI Automation reads it as an image with
/// the page's description and a Replay animation action (Invoke). The keyboard reaches it too (WIN-A11Y-02): Enter or Space
/// plays it again, and its context menu (Shift+F10 or the Menu key) has the Mac's VoiceOver action Replay animation.
/// Reduce Motion (Animation effects off) shows the final still and turns replay off. <see cref="Stills"/> draws into a
/// bitmap instead, which the UX reference capture can read.
/// </summary>
internal sealed partial class IllustrationView : UserControl
{
    public const double ViewHeight = 120;

    private readonly IllustrationGraphics _graphics;
    private readonly Func<bool> _reducesMotion;
    private readonly Action _replay;
    private readonly Grid _layers = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly Grid _canvas = new();
    private readonly Border _mark;
    private readonly Tween _markOpacity;
    private SpriteVisual? _sprite;
    private CompositionDrawingSurface? _surface;
    private nint _interop;
    private int _generation = -1;
    private int _pixelWidth;
    private int _pixelHeight;
    private double _progress = 1;
    private bool _hovered;
    private Image? _still;

    public IllustrationView(IntroductionPage page, IllustrationGraphics graphics, Func<bool> reducesMotion, Action replay)
    {
        Page = page;
        _graphics = graphics;
        _reducesMotion = reducesMotion;
        _replay = replay;
        Height = ViewHeight;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        _layers.Children.Add(_canvas);
        // The picture keeps its colors in a contrast theme, as images do (WIN-UI-07); the replay mark follows the theme.
        OwnColors.Mark(_canvas);
        _mark = ReplayMark();
        _layers.Children.Add(_mark);
        Content = _layers;
        _markOpacity = Tween.Opacity(_mark, 0);
        IsTabStop = !reducesMotion();
        UseSystemFocusVisuals = true;
        AutomationProperties.SetName(this, page.IllustrationDescription ?? page.Title);
        AutomationProperties.SetAutomationId(this, IntroductionText.IllustrationIdentifier);
        ToolTipService.SetToolTip(this, IntroductionText.ReplayHelp);
        var menu = new MenuFlyout();
        menu.Items.Add(Library.LibraryParts.Item(IntroductionText.ReplayAction, Replay));
        ContextFlyout = menu;
        // Mac accessibilityActions: Replay animation exists only while motion is on.
        ContextRequested += (_, args) => args.Handled = _reducesMotion();
        KeyDown += (_, args) =>
        {
            if (args.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space && !_reducesMotion())
            {
                args.Handled = true;
                Replay();
            }
        };
        Loaded += (_, _) =>
        {
            _graphics.Released += ReleaseSurface;
            Redraw();
        };
        Unloaded += (_, _) =>
        {
            _graphics.Released -= ReleaseSurface;
            ReleaseSurface();
        };
        SizeChanged += (_, _) => Redraw();
        ActualThemeChanged += (_, _) => Redraw();
        PointerEntered += (_, _) => Hover(true);
        PointerExited += (_, _) => Hover(false);
        PointerCanceled += (_, _) => Hover(false);
        Tapped += (_, args) =>
        {
            args.Handled = true;
            Replay();
        };
    }

    /// <summary>Development only (UX reference capture): draw into a bitmap, which RenderTargetBitmap can read.</summary>
    public static bool Stills { get; set; }

    public IntroductionPage Page { get; }

    /// <summary>The story's progress from 0 to 1; each change draws a frame.</summary>
    public double Progress
    {
        get => _progress;
        set
        {
            if (value == _progress)
            {
                return;
            }
            _progress = value;
            Redraw();
            UpdateMark(animated: false);
        }
    }

    /// <summary>Frames drawn since the view was made (measurements).</summary>
    public int Frames { get; private set; }

    /// <summary>Milliseconds this thread spent drawing those frames (measurements).</summary>
    public double DrawMilliseconds { get; private set; }

    /// <summary>Plays the story again, unless Reduce Motion is on (Mac onTapGesture and the accessibility action).</summary>
    public void Replay()
    {
        if (!_reducesMotion())
        {
            _replay();
        }
    }

    /// <summary>Draws the current frame, making the surface first when needed.</summary>
    public void Redraw()
    {
        if (!IsLoaded || XamlRoot is null || ActualWidth < 1)
        {
            return;
        }
        // Sharp in a window that grows with the text size (WIN-A11Y-03).
        double scale = ZoomPanel.ScaleOf(this);
        int width = Math.Max(1, (int)Math.Round(ActualWidth * scale)), height = Math.Max(1, (int)Math.Round(ViewHeight * scale));
        var size = new CoreSize(ActualWidth, ViewHeight);
        bool dark = ActualTheme == ElementTheme.Dark;
        if (Stills)
        {
            DrawStill(size, scale, dark);
            return;
        }
        Compositor compositor = ElementCompositionPreview.GetElementVisual(_canvas).Compositor;
        if (_graphics.Renderer(compositor) is not { } renderer || _graphics.Composition is not { } device)
        {
            return;
        }
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            EnsureSurface(compositor, device, width, height);
            int begun = LiftedComposition.BeginDraw(_interop, width, height, out nint context, out int x, out int y);
            if (begun < 0)
            {
                throw new Platform.NativeCallException("ICompositionDrawingSurfaceInterop::BeginDraw", begun);
            }
            try
            {
                renderer.Draw(context, Page, _progress, dark, size, new DrawArea(scale, x, y, new PixelRect(x, y, x + width, y + height)), clear: true);
            }
            finally
            {
                Marshal.Release(context);
                int ended = LiftedComposition.EndDraw(_interop);
                if (ended < 0)
                {
                    throw new Platform.NativeCallException("ICompositionDrawingSurfaceInterop::EndDraw", ended);
                }
            }
            Frames++;
            DrawMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        catch (Exception error) when (error is Platform.NativeCallException or COMException or InvalidOperationException)
        {
            // A lost device or a refused frame: the next frame starts again with new devices and a new surface.
            ReleaseSurface();
            _graphics.Fail(error);
        }
    }

    private void EnsureSurface(Compositor compositor, CompositionGraphicsDevice device, int width, int height)
    {
        if (_generation != _graphics.Generation)
        {
            ReleaseSurface();
            _generation = _graphics.Generation;
        }
        if (_surface is not null && (_pixelWidth != width || _pixelHeight != height))
        {
            ReleaseSurface();
            _generation = _graphics.Generation;
        }
        if (_surface is null)
        {
            _surface = device.CreateDrawingSurface(new Windows.Foundation.Size(width, height), DirectXPixelFormat.B8G8R8A8UIntNormalized,
                DirectXAlphaMode.Premultiplied);
            _interop = LiftedComposition.DrawingInterop(_surface);
            _pixelWidth = width;
            _pixelHeight = height;
            CompositionSurfaceBrush brush = compositor.CreateSurfaceBrush(_surface);
            brush.Stretch = CompositionStretch.Fill;
            _sprite ??= compositor.CreateSpriteVisual();
            _sprite.Brush = brush;
            ElementCompositionPreview.SetElementChildVisual(_canvas, _sprite);
        }
        _sprite!.Size = new Vector2((float)ActualWidth, (float)ViewHeight);
    }

    private void ReleaseSurface()
    {
        if (_interop != 0)
        {
            Marshal.Release(_interop);
            _interop = 0;
        }
        if (_sprite is not null)
        {
            _sprite.Brush?.Dispose();
            _sprite.Brush = null;
        }
        _surface?.Dispose();
        _surface = null;
    }

    /// <summary>The reference capture's path: the frame drawn offscreen and read back once into a bitmap.</summary>
    private void DrawStill(CoreSize size, double scale, bool dark)
    {
        if (_graphics.Renderer(null) is not { } renderer)
        {
            return;
        }
        RasterImage image = renderer.Render(Page, _progress, dark, size, scale);
        var bitmap = new WriteableBitmap(image.Width, image.Height);
        using (Stream pixels = bitmap.PixelBuffer.AsStream())
        {
            for (int row = 0; row < image.Height; row++)
            {
                pixels.Write(image.Pixels.Slice(row * image.Stride, image.Width * 4));
            }
        }
        bitmap.Invalidate();
        if (_still is null)
        {
            _still = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
            _canvas.Children.Add(_still);
        }
        _still.Source = bitmap;
        Frames++;
    }

    /// <summary>The Mac's replay mark: arrow.counterclockwise, 10 points semibold, in a 22-point circle 8 points from the corner.</summary>
    private static Border ReplayMark()
    {
        var mark = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = AppResources.Brush("AnnotatorWindowBackgroundBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 8, 8),
            IsHitTestVisible = false,
            Child = Glyphs.ForSymbol("arrow.counterclockwise", 11, "AnnotatorGlyphSecondary", 2.5),
        };
        mark.Child.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        mark.Child.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        AutomationProperties.SetAccessibilityView(mark, AccessibilityView.Raw);
        return mark;
    }

    private void Hover(bool inside)
    {
        _hovered = inside;
        UpdateMark(animated: true);
    }

    /// <summary>The mark shows only on hover, after the story ends and with motion on (Mac !reduceMotion and progress at 1).</summary>
    private void UpdateMark(bool animated)
    {
        bool shown = _hovered && _progress >= 1 && !_reducesMotion();
        ToolTipService.SetToolTip(this, _reducesMotion() ? null : IntroductionText.ReplayHelp);
        double target = shown ? 1 : 0;
        if (animated && !_reducesMotion())
        {
            _markOpacity.To(target, ToolbarControlMotion.Timed(new TimedMotion(IntroductionMotion.ReplayHintFade, MotionCurves.EaseOut), 0, 1));
        }
        else
        {
            _markOpacity.Set(target);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    /// <summary>An image with the page's description that Invoke plays again (Mac accessibilityActions "Replay animation").</summary>
    private sealed partial class Peer(IllustrationView owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
    {
        public void Invoke() => owner.Replay();

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

        protected override string GetClassNameCore() => "Image";

        protected override bool IsControlElementCore() => true;

        protected override IList<AutomationPeer> GetChildrenCore() => new AutomationPeerList();

        protected override object? GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Invoke && !owner._reducesMotion() ? this : base.GetPatternCore(patternInterface);

        protected override WinRect GetBoundingRectangleCore() => AutomationFrames.Of(owner, base.GetBoundingRectangleCore());
    }
}
