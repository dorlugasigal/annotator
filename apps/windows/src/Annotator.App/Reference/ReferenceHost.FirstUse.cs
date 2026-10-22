using System.Runtime.InteropServices.WindowsRuntime;
using Annotator.App.Introduction;
using Annotator.App.Shell;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Platform.Graphics;
using Annotator.Platform.Windowing;
using Annotator.Rendering.Export;
using Annotator.Rendering.Imaging;
using Annotator.Rendering.Text;
using Annotator.Workspace;
using Annotator.Workspace.Canvas;
using Annotator.Workspace.FirstUse;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using CoreRect = Annotator.Core.Rect;

namespace Annotator.App.Reference;

/// <summary>The tour, the on-screen tip and the coffee reminder for the UX reference (spec 005, WIN-UI-04).</summary>
internal sealed partial class ReferenceHost
{
    private void ShowFirstUse(WorkspaceModel model, ReferenceScenario scenario)
    {
        switch (scenario.Surface)
        {
            case ReferenceSurface.Tour:
                ShowTour(model, scenario.TourPage);
                break;
            case ReferenceSurface.CanvasHint:
                ShowCanvasHint(model);
                break;
            case ReferenceSurface.CoffeeReminder:
                var reminder = new FirstUseReferenceWindow(_registry, SupportReminderText.Title, CoffeeReminder());
                _popover = reminder;
                reminder.ShowForReference(OffscreenOrigin());
                Watch((FrameworkElement)reminder.Content, reminder.Hwnd);
                break;
        }
    }

    /// <summary>
    /// The tour's content on <paramref name="page"/> with every story at its end (the Mac capture's review progress of 1),
    /// 520 by 530 points like the Mac's on a display with room, without the window's title bar like the Mac capture. The
    /// illustrations draw into bitmaps, which the capture reads; it waits for the check box's check to finish drawing in.
    /// </summary>
    private void ShowTour(WorkspaceModel model, IntroductionPage page)
    {
        IllustrationView.Stills = true;
        var navigator = new IntroductionNavigator { ReviewProgress = 1 };
        navigator.Select(page);
        navigator.SetPresented(true);
        var preferences = new IntroductionPreferences(AppDefaults.InMemory());
        var view = new TourView(navigator, preferences, () => model.Snapshot, ReferenceUsage(), static () => { }) { ContentHeight = 530 };
        view.Shown();
        var window = new FirstUseReferenceWindow(_registry, IntroductionText.WindowTitle, (FrameworkElement)view.Root);
        _popover = window;
        window.ShowForReference(OffscreenOrigin());
        Microsoft.UI.Dispatching.DispatcherQueueTimer settle = _queue.CreateTimer();
        settle.Interval = TimeSpan.FromSeconds(0.8);
        settle.IsRepeating = false;
        settle.Tick += (_, _) => Watch((FrameworkElement)window.Content, window.Hwnd);
        settle.Start();
    }

    /// <summary>
    /// The on-screen tip while drawing in the Mac capture's 540 by 130-point view: the box drawn by the canvas's own display
    /// list, and static text with the tip's words for UI Automation, as the Mac's view reads.
    /// </summary>
    private void ShowCanvasHint(WorkspaceModel model)
    {
        PointInt32 origin = OffscreenOrigin();
        double scale = WindowChrome.MonitorAt(origin.X, origin.Y).Scale;
        var measurer = new DirectWriteTextMeasurer();
        // The canvas passes the zoom facts, so the tip has the zoom line, as the Mac's has.
        string text = WorkspaceHint.Text(model.Snapshot, hasTextEditor: false, hasActiveEdit: false, model.Snapshot.ZoomHint);
        var view = new CoreRect(0, 0, HintBox.MaximumWidth, HintBox.ViewHeight);
        (DisplayList list, _) = HintBox.Build(text, view, measurer);
        RasterImage image;
        using (GraphicsDevices devices = GraphicsDevices.Create(new GraphicsDeviceOptions { UseWarp = true }))
        using (var exporter = new PngExporter(devices, measurer))
        {
            image = exporter.Render([SceneSnapshot.Create([list])], new Size(view.Width, view.Height), scale);
        }
        var bitmap = new WriteableBitmap(image.Width, image.Height);
        using (Stream pixels = bitmap.PixelBuffer.AsStream())
        {
            for (int row = 0; row < image.Height; row++)
            {
                pixels.Write(image.Pixels.Slice(row * image.Stride, image.Width * 4));
            }
        }
        bitmap.Invalidate();
        var surface = new Grid { Width = view.Width, Height = view.Height };
        var picture = new Image { Source = bitmap, Stretch = Stretch.Fill };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(picture, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        surface.Children.Add(picture);
        // The Mac's hint view is one static text element over its whole frame.
        surface.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Width = view.Width,
            Height = view.Height,
            IsHitTestVisible = false,
        });
        var window = new FirstUseReferenceWindow(_registry, "On-screen tips", surface);
        _popover = window;
        window.ShowForReference(origin);
        Watch((FrameworkElement)window.Content, window.Hwnd);
    }

    /// <summary>The Mac capture's coffee reminder: its 300-point content at (80, 16) in a 460 by 135-point window.</summary>
    private static FrameworkElement CoffeeReminder()
    {
        var surface = new Grid { Width = 460, Height = 135, Background = AppResources.Brush("AnnotatorWindowBackgroundBrush") };
        FrameworkElement body = SupportReminderPopover.Body(static () => false, static () => { });
        body.HorizontalAlignment = HorizontalAlignment.Left;
        body.VerticalAlignment = VerticalAlignment.Top;
        body.Margin = new Thickness(80, 16, 0, 0);
        surface.Children.Add(body);
        return surface;
    }

    /// <summary>Development only (UX reference capture): one first-use surface on its own; nothing closes it.</summary>
    private sealed class FirstUseReferenceWindow : PopoverWindow
    {
        public FirstUseReferenceWindow(WindowRegistry registry, string title, FrameworkElement surface)
            : base(registry, title) => Surface = surface;

        protected override void FocusFirst()
        {
        }
    }
}
