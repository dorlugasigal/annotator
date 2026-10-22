using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Annotator.App.Toolbar;

/// <summary>
/// Mac PropertiesReveal (ToolbarView.swift): properties that appear because the person changed the Properties layout
/// fade in with Motion.fadeIn (ease-out, 0.16 s) after 0.04 s, moving down from 4 points above their place. With
/// reduced motion they fade in place with Motion.reduced (ease-out, 0.12 s). The animations run on the compositor once.
/// </summary>
internal static class PropertiesReveal
{
    public static void Start(FrameworkElement element)
    {
        element.Loaded += OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs args)
    {
        var element = (FrameworkElement)sender;
        element.Loaded -= OnLoaded;
        bool reduced = ToolbarMotion.ReducesMotion();
        Compositor compositor = ElementCompositionPreview.GetElementVisual(element).Compositor;
        // SwiftUI's easeOut curve.
        CubicBezierEasingFunction easeOut = compositor.CreateCubicBezierEasingFunction(new Vector2(0, 0), new Vector2(0.58f, 1));
        TimeSpan duration = TimeSpan.FromSeconds(reduced ? 0.12 : 0.16);
        TimeSpan delay = TimeSpan.FromSeconds(reduced ? 0 : 0.04);

        ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity";
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, easeOut);
        fade.Duration = duration;
        fade.DelayTime = delay;
        fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        element.StartAnimation(fade);
        if (reduced)
        {
            return;
        }
        Vector3KeyFrameAnimation rise = compositor.CreateVector3KeyFrameAnimation();
        rise.Target = "Translation";
        rise.InsertKeyFrame(0, new Vector3(0, -4, 0));
        rise.InsertKeyFrame(1, Vector3.Zero, easeOut);
        rise.Duration = duration;
        rise.DelayTime = delay;
        rise.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        element.StartAnimation(rise);
    }
}
