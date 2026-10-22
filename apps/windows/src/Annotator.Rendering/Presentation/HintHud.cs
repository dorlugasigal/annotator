using System.Diagnostics;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Motion;
using Annotator.Core.Scene;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Drawing;
using Annotator.Workspace.Canvas;
using Windows.UI.Composition;

namespace Annotator.Rendering.Presentation;

/// <summary>
/// The on-screen tip at the canvas's top-left corner (Mac WorkspaceHintView with its HUDFader): the box of
/// <see cref="HintBox"/> on its own surface, fading in over 0.16 s with a 3-point rise and out over 0.12 s. A new text while
/// the tip shows crossfades over 0.14 s, unless the text changed less than 0.14 s before, as a value changing every frame
/// updates in place. Two <see cref="OverlayHud"/>s take turns for the crossfade; a resting tip costs nothing. Use it on the
/// overlay thread that created it.
/// </summary>
public sealed class HintHud : IDisposable
{
    private readonly OverlayHud[] huds = new OverlayHud[2];
    private readonly ITextMeasurer measurer;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private int current;
    private double lastTextChange = double.NegativeInfinity;
    private bool disposed;

    public HintHud(Compositor compositor, ContainerVisual root, GraphicsDevices? devices, ISystemColorResolver colors, double scale,
                   ITextMeasurer measurer)
    {
        ArgumentNullException.ThrowIfNull(measurer);
        this.measurer = measurer;
        huds[0] = new OverlayHud(compositor, root, devices, colors, scale);
        try
        {
            huds[1] = new OverlayHud(compositor, root, devices, colors, scale);
        }
        catch
        {
            huds[0].Dispose();
            throw;
        }
    }

    /// <summary>The text shown or fading, or null before the first.</summary>
    public string? Text { get; private set; }

    /// <summary>The view the text lays out in (Mac hint view frame), in canvas points.</summary>
    public Rect View { get; private set; }

    /// <summary>The drawn box, in canvas points.</summary>
    public Rect Box { get; private set; }

    /// <summary>Whether the tip shows or fades in.</summary>
    public bool IsVisible => huds[current].IsVisible;

    /// <summary>The last drawing failure, or null.</summary>
    public Exception? Failure => huds[current].Failure;

    /// <summary>
    /// Shows <paramref name="text"/> in <paramref name="view"/>, or hides the tip. <paramref name="animate"/> is false when the
    /// overlay itself appears or goes, where the tip changes at once like the Mac's.
    /// </summary>
    public void Update(string text, Rect view, bool shown, bool animate, bool reduceMotion)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(text);
        if (text != Text || view != View)
        {
            (DisplayList list, Rect box) = HintBox.Build(text, view, measurer);
            double now = clock.Elapsed.TotalSeconds;
            bool crossfade = animate && shown && IsVisible && Text is not null && text != Text &&
                now - lastTextChange >= MotionTiming.HintTextCrossfadeDuration;
            if (text != Text)
            {
                lastTextChange = now;
            }
            Text = text;
            View = view;
            Box = box;
            Rect area = Outer(box);
            if (crossfade)
            {
                OverlayHud leaving = huds[current];
                current = 1 - current;
                huds[current].Show(list, area);
                huds[current].SetVisible(true, animate: true, reduceMotion, MotionTiming.HintTextCrossfadeDuration);
                leaving.SetVisible(false, animate: true, reduceMotion, MotionTiming.HintTextCrossfadeDuration);
                return;
            }
            huds[current].Show(list, area);
        }
        huds[current].SetVisible(shown, animate, reduceMotion);
        if (!shown)
        {
            huds[1 - current].SetVisible(false, animate, reduceMotion);
        }
    }

    public void ReplaceDevices(GraphicsDevices? devices)
    {
        foreach (OverlayHud hud in huds)
        {
            hud.ReplaceDevices(devices);
        }
    }

    public void Invalidate()
    {
        foreach (OverlayHud hud in huds)
        {
            hud.Invalidate();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        foreach (OverlayHud hud in huds)
        {
            hud.Dispose();
        }
    }

    /// <summary>The box on whole points, so its surface covers every pixel the box touches.</summary>
    private static Rect Outer(Rect box)
    {
        double left = Math.Floor(box.X), top = Math.Floor(box.Y);
        return new Rect(left, top, Math.Ceiling(box.MaxX) - left, Math.Ceiling(box.MaxY) - top);
    }
}
