using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Annotator.App.Overlay;
using Annotator.Workspace.Motion;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Annotator.Core.Motion;

namespace Annotator.App.Toolbar;

/// <summary>
/// The toolbar's part of showing and hiding with motion (spec 009). Most styles move a snapshot of the toolbar on the
/// overlay while the real window is cloaked: it stays shown and keeps rendering, so it is back on screen at once, but DWM
/// does not draw it. Fade, and any style whose snapshot fails, animates the real toolbar's content instead, as the Mac
/// animates its toolbar panel's layer.
/// </summary>
internal sealed partial class ToolbarWindow
{
    private Visual? _motionVisual;
    private bool _cloaked;
    private bool _moving;
    private bool _translating;

    /// <summary>Hides the window from the screen without hiding it from Windows (DWM cloaking), or shows it again.</summary>
    public unsafe void SetCloaked(bool cloaked)
    {
        if (cloaked == _cloaked)
        {
            return;
        }
        BOOL value = cloaked;
        PInvoke.DwmSetWindowAttribute(Hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAK, &value, (uint)sizeof(BOOL)).ThrowOnFailure();
        _cloaked = cloaked;
    }

    /// <summary>
    /// Mac ignoresMouseEvents while the toolbar animates: its controls take no clicks and keep their look, so the toolbar
    /// that lands is the one that left.
    /// </summary>
    public void SetMotionInputBlocked(bool blocked) => _root.IsHitTestVisible = !blocked;

    /// <summary>
    /// Renders the toolbar's content as it is now into premultiplied BGRA pixels at the window's scale (Mac
    /// snapshotToolbar), for transition chain <paramref name="chain"/>. A failure gives an image without pixels.
    /// </summary>
    public async Task<ToolbarImage> CaptureAsync(ulong chain)
    {
        try
        {
            var bitmap = new RenderTargetBitmap();
            // Zoomed, the panel holds the toolbar as it shows (WIN-A11Y-03).
            await bitmap.RenderAsync(_zoom ?? (Microsoft.UI.Xaml.UIElement)_root);
            byte[] pixels = (await bitmap.GetPixelsAsync()).ToArray();
            int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
            if (width <= 0 || height <= 0 || pixels.Length < width * height * 4)
            {
                return new ToolbarImage(chain, null, 0, 0);
            }
            return new ToolbarImage(chain, pixels, width, height);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or COMException or UnauthorizedAccessException)
        {
            StartupLog.Note("toolbar-snapshot", error.Message);
            return new ToolbarImage(chain, null, 0, 0);
        }
    }

    /// <summary>Starts a fade in from nothing: the window shows with clear content until <see cref="PlayMotion"/> runs.</summary>
    public void PrepareFadeIn()
    {
        Visual visual = MotionVisual();
        visual.StopAnimation("Opacity");
        visual.Opacity = 0;
        _moving = true;
    }

    /// <summary>
    /// Plays the real toolbar's own motion (Mac applyLayerMotion): its content's opacity and movement on the WinUI
    /// compositor, starting <paramref name="delay"/> seconds from now. Positions are points, y down.
    /// </summary>
    public void PlayMotion(LayerMotion motion, double delay)
    {
        ArgumentNullException.ThrowIfNull(motion);
        Visual visual = MotionVisual();
        Compositor compositor = visual.Compositor;
        var duration = TimeSpan.FromSeconds(Math.Max(0.001, motion.EffectiveDuration));
        var wait = TimeSpan.FromSeconds(Math.Max(0, delay));
        ScalarKeyFrameAnimation opacity = compositor.CreateScalarKeyFrameAnimation();
        foreach ((float time, double eased, CompositionEasingFunction? easing) in Schedule(compositor, motion.Opacity.KeyTimes, motion.Opacity.Curve))
        {
            var value = (float)motion.Opacity.AtEased(eased);
            if (easing is null)
            {
                opacity.InsertKeyFrame(time, value);
            }
            else
            {
                opacity.InsertKeyFrame(time, value, easing);
            }
        }
        opacity.Duration = duration;
        opacity.DelayTime = wait;
        opacity.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        visual.Opacity = (float)motion.Opacity.Values[0];
        visual.StartAnimation("Opacity", opacity);
        if (motion.Transform.Values.Any(value => value.Tx != 0 || value.Ty != 0))
        {
            ElementCompositionPreview.SetIsTranslationEnabled(_root, true);
            Vector3KeyFrameAnimation translation = compositor.CreateVector3KeyFrameAnimation();
            foreach ((float time, double eased, CompositionEasingFunction? easing) in Schedule(compositor, motion.Transform.KeyTimes, motion.Transform.Curve))
            {
                MotionTransform screen = motion.Transform.AtEased(eased).ToScreen();
                var value = new Vector3((float)screen.Tx, (float)screen.Ty, 0);
                if (easing is null)
                {
                    translation.InsertKeyFrame(time, value);
                }
                else
                {
                    translation.InsertKeyFrame(time, value, easing);
                }
            }
            translation.Duration = duration;
            translation.DelayTime = wait;
            translation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            visual.StartAnimation("Translation", translation);
            _translating = true;
        }
        _moving = true;
    }

    /// <summary>Ends the real toolbar's own motion and puts its content back at rest.</summary>
    public void ResetMotion()
    {
        if (!_moving || _motionVisual is not { } visual)
        {
            return;
        }
        visual.StopAnimation("Opacity");
        visual.Opacity = 1;
        if (_translating)
        {
            // Translation exists only on a visual whose element enabled it.
            visual.StopAnimation("Translation");
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            _translating = false;
        }
        _moving = false;
    }

    private Visual MotionVisual() => _motionVisual ??= ElementCompositionPreview.GetElementVisual(_root);

    /// <summary>The Core Animation track as WinUI keyframes with exact per-segment easings (see KeyframeSchedule).</summary>
    private static List<(float Time, double Eased, CompositionEasingFunction? Easing)> Schedule(Compositor compositor, double[] keyTimes, CubicBezier curve)
    {
        var result = new List<(float, double, CompositionEasingFunction?)>();
        List<KeyframePoint> points = KeyframeSchedule.Exact(keyTimes, curve);
        var previous = -1f;
        for (var index = 0; index < points.Count; index++)
        {
            KeyframePoint point = points[index];
            var last = index == points.Count - 1;
            var time = last ? 1f : (float)Math.Clamp(point.Time, 0, 1);
            if (index > 0 && time <= previous && !last)
            {
                continue;
            }
            CompositionEasingFunction? easing = index == 0 ? null
                : point.Easing.IsLinear ? compositor.CreateLinearEasingFunction()
                : compositor.CreateCubicBezierEasingFunction(new Vector2((float)point.Easing.X1, (float)point.Easing.Y1),
                                                             new Vector2((float)point.Easing.X2, (float)point.Easing.Y2));
            result.Add((time, point.Eased, easing));
            previous = time;
        }
        return result;
    }
}
