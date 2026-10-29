using Annotator.App.Sharing;
using Annotator.App.Shell;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Platform.Accessibility;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Platform.Sharing;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Export;
using Annotator.Rendering.Imaging;
using Annotator.Rendering.Text;
using Annotator.Workspace;
using Annotator.Workspace.Sharing;
using Windows.Win32;
using Windows.Win32.Foundation;
using CoreRect = Annotator.Core.Rect;
using Size = Annotator.Core.Geometry.Size;

namespace Annotator.App.Reference;

/// <summary>
/// Region sharing's reference states (UXReferenceCaptureTests): the real Share a region window off screen on a 5120 by
/// 1440-point display named as in the Mac capture; the outline and the covered output drawn from the display lists and
/// geometry their windows use, with the output window's own UI Automation tree.
/// </summary>
internal sealed partial class ReferenceHost
{
    /// <summary>The Mac capture's display: an Odyssey G95SD at 5120 by 1440 points and one pixel per point.</summary>
    private static readonly DisplayDescriptor s_sharingDisplay =
        new(new Annotator.Workspace.DisplayId("reference-sharing"), new CoreRect(0, 0, 5120, 1440), new CoreRect(0, 0, 5120, 1415), 1);

    /// <summary>The Mac capture's outline view: 480 by 270 points.</summary>
    private static readonly Size s_outlineSize = new(480, 270);

    private RegionSharingWindow? _sharingWindow;
    private SharedRegionWindow? _outputWindow;

    private void ShowRegionSharing(ReferenceScenario scenario)
    {
        switch (scenario.SharingPart)
        {
            case RegionSharingPart.Chooser:
                var model = new WorkspaceModel(new WorkspaceOptions
                {
                    Displays = () => [s_sharingDisplay],
                    PreferencesStore = new MemoryPreferencesStore(),
                });
                var sharing = new RegionSharing(model, new ReferenceSharingHost());
                sharing.ShowControls();
                scenario.SharingSetup?.Invoke(sharing);
                var window = new RegionSharingWindow(_registry, sharing, static () => { });
                _sharingWindow = window;
                window.ShowForReference(OffscreenOrigin());
                model.Published += _ => window.Refresh();
                Watch(window.Surface, window.Hwnd);
                break;
            case RegionSharingPart.OutlineChoosing or RegionSharingPart.OutlineLive:
                WritePicture([Outline(scenario.SharingPart == RegionSharingPart.OutlineChoosing)], s_outlineSize, window: null);
                break;
            case RegionSharingPart.OutputCovered:
                ShowCoveredOutput();
                break;
        }
    }

    /// <summary>
    /// RegionOutlineView: the accent at 8% inside the region while choosing, and a 2-point border at 95% inside its edge.
    /// </summary>
    private static DisplayList Outline(bool choosing)
    {
        var builder = new DisplayListBuilder();
        var bounds = new CoreRect(0, 0, s_outlineSize.Width, s_outlineSize.Height);
        if (choosing)
        {
            builder.Add(DisplayItem.FillRect(bounds, Paint.System(SystemColor.Accent, RegionOutlineWindow.FillOpacity)));
        }
        double inset = RegionOutlineWindow.BorderWidth / 2;
        PathData border = new PathBuilder().AddRect(new CoreRect(inset, inset, bounds.Width - 2 * inset, bounds.Height - 2 * inset)).ToPathData();
        builder.Add(DisplayItem.StrokePath(border, new StrokeStyle(RegionOutlineWindow.BorderWidth), Paint.System(SystemColor.Accent, RegionOutlineWindow.BorderOpacity)));
        return builder.ToList();
    }

    /// <summary>
    /// The output while it starts: the real window off screen with its UI Automation tree, and the picture its cover shows,
    /// black with "Starting the region..." in the Mac's text box.
    /// </summary>
    private void ShowCoveredOutput()
    {
        var window = new SharedRegionWindow(_registry, WindowChrome.SystemCompositor(), RegionSharingText.OutputTitle(BuildInfo.IsDevelopment));
        _outputWindow = window;
        Windows.Graphics.PointInt32 origin = OffscreenOrigin();
        MonitorArea monitor = WindowChrome.MonitorAt(origin.X, origin.Y);
        var area = PixelRect.FromSize(origin.X, origin.Y, (int)Math.Round(1600 * monitor.Scale), (int)Math.Round(1000 * monitor.Scale));
        uint dpi = (uint)Math.Round(96 * monitor.Scale);
        // The Mac's reference output is 720 by 440 points, with the window 24 points from the top-right corner of its area.
        (int frameTop, _, _, int frameRight) = SharedRegionWindow.VisibleFrame(dpi);
        int clientWidth = (int)Math.Round(720 * monitor.Scale), clientHeight = (int)Math.Round(440 * monitor.Scale);
        int margin = (int)Math.Round(24 * monitor.Scale);
        window.Place(new DisplayInfo(0, "reference-sharing", area, area, dpi, IsPrimary: false),
                     PixelRect.FromSize(area.Right - margin - frameRight - clientWidth, area.Top + margin + frameTop, clientWidth, clientHeight));
        window.ShowBehind();
        (int width, int height) = window.ClientSize;
        string cover = RegionSharingText.StartingCover;
        window.Accessibility = new OverlayAccessibility(window.ContentHandle, window.ClientScreenRect(),
            SharedRegionOutput.CoverTree(cover, cover, width, height, window.Scale));
        using var measurer = new DirectWriteTextMeasurer();
        var size = new Size(width / window.Scale, height / window.Scale);
        DisplayList black = new DisplayListBuilder()
            .Add(DisplayItem.FillRect(new CoreRect(0, 0, size.Width, size.Height), Paint.Solid(new RGBA(0, 0, 0))))
            .ToList();
        WritePicture([black, SharedRegionOutput.CoverText(cover, size.Width, size.Height, measurer)], size, (HWND)window.ContentHandle, window.Scale);
    }

    /// <summary>Renders <paramref name="lists"/> at 2x and reports them, with the window whose UI Automation tree goes with them.</summary>
    private void WritePicture(DisplayList[] lists, Size size, HWND? window, double windowScale = 2)
    {
        using GraphicsDevices devices = GraphicsDevices.Create();
        using var measurer = new DirectWriteTextMeasurer();
        using var exporter = new PngExporter(devices, measurer,
            new FixedSystemColors(new RGBA(0, 0x7A / 255.0, 1), new RGBA(1, 1, 1), new RGBA(1, 0x3B / 255.0, 0x30 / 255.0)));
        RasterImage image = exporter.Render([SceneSnapshot.Create(lists)], size, 2);
        File.WriteAllBytes(ImagePath, image.Pixels.ToArray());
        RECT rect = default;
        if (window is { } shown && !PInvoke.GetWindowRect(shown, out rect))
        {
            throw new InvalidOperationException("The output window's rectangle is unavailable.");
        }
        Finish(writer =>
        {
            Status(writer, "shown", null);
            if (window is { } handle)
            {
                writer.WriteNumber("hwnd", (long)(nint)handle);
                writer.WriteBoolean("canvas", true);
                writer.WriteStartArray("window");
                writer.WriteNumberValue(rect.left);
                writer.WriteNumberValue(rect.top);
                writer.WriteNumberValue(rect.right - rect.left);
                writer.WriteNumberValue(rect.bottom - rect.top);
                writer.WriteEndArray();
            }
            // UI Automation frames are in the window's pixels; the picture is drawn at 2x either way.
            writer.WriteNumber("scale", windowScale);
            Pair(writer, "surface", size.Width, size.Height);
            writer.WriteString("image", Path.GetFileName(ImagePath));
            Pair(writer, "imageSize", image.Width, image.Height);
            if (_request.Accessibility)
            {
                AccessibilityReport.WriteNative(writer, _request, size.Width, size.Height);
            }
        }, wait: window is not null);
    }

    /// <summary>Region sharing's windows for a reference state: the Mac capture's display name and nothing else on screen.</summary>
    private sealed class ReferenceSharingHost : IRegionSharingHost
    {
        public string? DisplayName(Annotator.Workspace.DisplayId display) => "Odyssey G95SD";

        public bool DesktopIsLocked => false;

        public bool ControlsVisible { get; private set; }

        public void ShowControls() => ControlsVisible = true;

        public void HideControls() => ControlsVisible = false;

        public void RestoreControls(bool activate) => ControlsVisible = true;

        public void CloseControls() => ControlsVisible = false;

        public void ShowOutline(SharingRegion region, DisplayDescriptor display, bool selectionFill)
        {
        }

        public void HideOutline()
        {
        }

        public bool IsSelecting => false;

        public void ChooseRegion(IReadOnlyList<RegionSharingDisplay> displays, Annotator.Workspace.DisplayId preferred, Action<RegionSelectionResult> completed) =>
            completed(RegionSelectionResult.Cancelled.Instance);

        public void CancelSelection()
        {
        }

        public IDisposable Schedule(TimeSpan delay, Action action) => new Nothing();

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
