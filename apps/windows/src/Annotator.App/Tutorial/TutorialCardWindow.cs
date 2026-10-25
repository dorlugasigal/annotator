using System.Numerics;
using Annotator.App.Shell;
using Annotator.Core.Tutorial;
using Annotator.Platform.Windowing;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Annotator.App.Tutorial;

/// <summary>Where the card sits: centered on a display (welcome) or next to the toolbar (coach and finish).</summary>
internal enum CardPlacement
{
    Centered,
    NextToToolbar,
}

/// <summary>
/// The tutorial's card window (Mac TutorialCardPanel): a borderless tool window that never takes focus, so tool keys keep
/// reaching the canvas while the person follows a step. Its surface rises in when it shows, sinks away when it hides, and
/// swaps its content with the step motion: the old words leave upward and the new ones rise into place.
/// </summary>
internal sealed class TutorialCardWindow : Window
{
    /// <summary>Room around the surface for its motion, in points.</summary>
    private const double Margin = 18;

    private readonly WindowRegistration _registration;
    private readonly WindowCloak _cloak;
    private readonly Border _surface;
    private readonly Grid _host = new();
    private readonly Grid _root;
    private FrameworkElement? _content;
    private int _generation;
    private bool _closing;

    public TutorialCardWindow(WindowRegistry registry)
    {
        Title = "Annotator tutorial";
        Hwnd = WindowChrome.Configure(this, noActivate: true);
        // Registered before the first show so capture can leave it out (S-1).
        _registration = registry.Register((nint)Hwnd, WindowLayer.Popover);
        _surface = new Border
        {
            Style = AppResources.Style("AnnotatorToolbarSurface"),
            CornerRadius = new CornerRadius(13),
            Child = _host,
            Margin = new Thickness(Margin),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _root = new WindowRoot { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        _root.Children.Add(_surface);
        AutomationFrames.Nudge(_root);
        AppResources.FollowTheme(_root);
        _root.ActualThemeChanged += (_, _) => ThemeChanged?.Invoke();
        Content = _root;
        _cloak = new WindowCloak(this, Hwnd, "the tutorial");
        AppWindow.Closing += (_, args) => args.Cancel = !_closing;
        Closed += (_, _) => _registration.Dispose();
    }

    public HWND Hwnd { get; }

    public bool IsShown { get; private set; }

    /// <summary>Whether the card draws in the dark theme.</summary>
    public bool Dark => _root.ActualTheme == ElementTheme.Dark;

    /// <summary>Raised when the theme changes; the controller builds the content again.</summary>
    public event Action? ThemeChanged;

    /// <summary>The card's frame in physical pixels.</summary>
    public RectInt32 Frame => new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);

    /// <summary>The surface's frame in physical pixels, without the room for motion around it.</summary>
    public RectInt32 SurfaceFrame
    {
        get
        {
            int margin = (int)Math.Round(Margin * Scale());
            RectInt32 frame = Frame;
            return new RectInt32(frame.X + margin, frame.Y + margin, Math.Max(1, frame.Width - 2 * margin), Math.Max(1, frame.Height - 2 * margin));
        }
    }

    /// <summary>
    /// Shows <paramref name="content"/>: at once when the card is hidden (it then rises in), or with the step motion when the
    /// card shows already. <paramref name="place"/> chooses the window's top-left corner for the surface's size in pixels.
    /// </summary>
    public void Show(FrameworkElement content, Func<Windows.Foundation.Size, PointInt32> place, bool takeFocus, bool reduceMotion)
    {
        int generation = ++_generation;
        if (!IsShown)
        {
            SetContent(content);
            Fit(place);
            IsShown = true;
            _cloak.BeforeShow();
            AppWindow.Show(activateWindow: false);
            WindowChrome.KeepOnTop(Hwnd);
            if (takeFocus)
            {
                PInvoke.SetForegroundWindow(Hwnd);
            }
            Enter(reduceMotion);
            return;
        }
        if (_content is not { } old || reduceMotion)
        {
            SetContent(content);
            Fit(place);
            if (reduceMotion)
            {
                Fade(content, 0, 1, TutorialMotion.CardEnterFade, 0);
            }
            return;
        }
        Compositor compositor = Microsoft.UI.Xaml.Media.CompositionTarget.GetCompositorForCurrentThread();
        CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        Move(old, Vector3.Zero, new Vector3(0, -(float)TutorialMotion.StepOutRise, 0), TutorialMotion.StepOutDuration, 0, Annotator.Core.Motion.MotionCurves.Exit);
        Fade(old, 1, 0, TutorialMotion.StepOutDuration, 0);
        batch.End();
        batch.Completed += (_, _) =>
        {
            if (generation != _generation || _closing)
            {
                return;
            }
            SetContent(content);
            Fit(place);
            Move(content, new Vector3(0, (float)TutorialMotion.StepInRise, 0), Vector3.Zero, TutorialMotion.StepInDuration, TutorialMotion.StepInDelay,
                Annotator.Core.Motion.MotionCurves.Enter);
            Fade(content, 0, 1, TutorialMotion.StepInFade, TutorialMotion.StepInDelay);
        };
    }

    /// <summary>Swaps the content at once, keeping the card where it is (a goal of the same step is done).</summary>
    public void Replace(FrameworkElement content, Func<Windows.Foundation.Size, PointInt32> place)
    {
        if (!IsShown)
        {
            return;
        }
        ++_generation;
        SetContent(content);
        Fit(place);
    }

    /// <summary>Sizes the window to its content again, keeping its place (after a hint appeared).</summary>
    public void Refit(Func<Windows.Foundation.Size, PointInt32> place)
    {
        if (IsShown)
        {
            Fit(place);
        }
    }

    /// <summary>Hides the card: it sinks and fades, then the window hides (at once with Reduce Motion).</summary>
    public void Conceal(bool reduceMotion)
    {
        if (!IsShown)
        {
            return;
        }
        IsShown = false;
        int generation = ++_generation;
        if (reduceMotion)
        {
            _cloak.Hide();
            return;
        }
        Compositor compositor = Microsoft.UI.Xaml.Media.CompositionTarget.GetCompositorForCurrentThread();
        CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        Move(_surface, Vector3.Zero, new Vector3(0, (float)TutorialMotion.CardExitSink, 0), TutorialMotion.CardExitDuration, 0,
            Annotator.Core.Motion.MotionCurves.Exit);
        Scale(_surface, 1, (float)TutorialMotion.CardExitScale, TutorialMotion.CardExitDuration, Annotator.Core.Motion.MotionCurves.Exit);
        Fade(_surface, 1, 0, TutorialMotion.CardExitDuration, 0);
        batch.End();
        batch.Completed += (_, _) =>
        {
            if (generation == _generation && !IsShown)
            {
                _cloak.Hide();
            }
        };
    }

    /// <summary>Closes the window for good (Quit).</summary>
    public void Shutdown()
    {
        _closing = true;
        Close();
    }

    private void SetContent(FrameworkElement content)
    {
        _host.Children.Clear();
        _host.Children.Add(content);
        _content = content;
    }

    /// <summary>Measures the surface and moves the window so that the surface lands where <paramref name="place"/> says.</summary>
    private void Fit(Func<Windows.Foundation.Size, PointInt32> place)
    {
        var unlimited = new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);
        _surface.Measure(unlimited);
        Windows.Foundation.Size desired = _surface.DesiredSize;
        double scale = Scale();
        int margin = (int)Math.Round(Margin * scale);
        int width = (int)Math.Ceiling(desired.Width * scale);
        int height = (int)Math.Ceiling(desired.Height * scale);
        PointInt32 surfaceCorner = place(new Windows.Foundation.Size(width - 2 * margin, height - 2 * margin));
        AppWindow.MoveAndResize(new RectInt32(surfaceCorner.X - margin, surfaceCorner.Y - margin, width, height));
    }

    private double Scale() => _root.XamlRoot?.RasterizationScale ?? WindowChrome.MonitorFor(Frame).Scale;

    /// <summary>The surface rises 16 points, grows from 96% and fades in (Mac TutorialMotion card enter).</summary>
    private void Enter(bool reduceMotion)
    {
        if (reduceMotion)
        {
            Fade(_surface, 0, 1, 0.2, 0);
            return;
        }
        Move(_surface, new Vector3(0, (float)TutorialMotion.CardEnterRise, 0), Vector3.Zero, TutorialMotion.CardEnterDuration, 0,
            Annotator.Core.Motion.MotionCurves.Enter);
        Scale(_surface, (float)TutorialMotion.CardEnterScale, 1, TutorialMotion.CardEnterDuration, Annotator.Core.Motion.MotionCurves.Enter);
        Fade(_surface, 0, 1, TutorialMotion.CardEnterFade, 0);
    }

    private static void Move(UIElement element, Vector3 from, Vector3 to, double duration, double delay, Annotator.Core.Motion.CubicBezier curve)
    {
        Compositor compositor = Microsoft.UI.Xaml.Media.CompositionTarget.GetCompositorForCurrentThread();
        Vector3KeyFrameAnimation move = compositor.CreateVector3KeyFrameAnimation();
        move.Target = "Translation";
        move.InsertKeyFrame(0, from);
        move.InsertKeyFrame(1, to, TutorialCardView.Easing(compositor, curve));
        move.Duration = TimeSpan.FromSeconds(duration);
        move.DelayTime = TimeSpan.FromSeconds(delay);
        move.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        element.StartAnimation(move);
    }

    private static void Scale(FrameworkElement element, float from, float to, double duration, Annotator.Core.Motion.CubicBezier curve)
    {
        Compositor compositor = Microsoft.UI.Xaml.Media.CompositionTarget.GetCompositorForCurrentThread();
        element.CenterPoint = new Vector3((float)(element.ActualWidth / 2), (float)element.ActualHeight, 0);
        Vector3KeyFrameAnimation scale = compositor.CreateVector3KeyFrameAnimation();
        scale.Target = "Scale";
        scale.InsertKeyFrame(0, new Vector3(from, from, 1));
        scale.InsertKeyFrame(1, new Vector3(to, to, 1), TutorialCardView.Easing(compositor, curve));
        scale.Duration = TimeSpan.FromSeconds(duration);
        element.StartAnimation(scale);
    }

    private static void Fade(UIElement element, float from, float to, double duration, double delay)
    {
        Compositor compositor = Microsoft.UI.Xaml.Media.CompositionTarget.GetCompositorForCurrentThread();
        ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity";
        fade.InsertKeyFrame(0, from);
        fade.InsertKeyFrame(1, to, TutorialCardView.Easing(compositor, Annotator.Core.Motion.MotionCurves.Fade));
        fade.Duration = TimeSpan.FromSeconds(duration);
        fade.DelayTime = TimeSpan.FromSeconds(delay);
        fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        element.StartAnimation(fade);
    }
}
