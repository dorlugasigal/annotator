using Annotator.App.Shell;
using Annotator.Workspace.Artwork;
using Annotator.Workspace.Settings;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;
using WinColor = Windows.UI.Color;
using WinRect = Windows.Foundation.Rect;

namespace Annotator.App.Settings;

/// <summary>
/// Settings > About (the Mac's CustomizationView.about): the app icon, name, version label (WIN-DST-04) and the Local
/// build badge of development builds, the tagline, update and support buttons, What's changed and the build details.
/// Check for updates... and the line under it follow the Store update service (WIN-SYS-03); the Downloads page link shows
/// while this copy cannot get Store updates. The build details add the copyright and the third-party notices that ship
/// next to the app.
/// </summary>
internal static class AboutPage
{
    private const double ColumnWidth = 480;

    public static FrameworkElement Make(SettingsActions actions)
    {
        SettingsEnvironment environment = actions.Environment;
        var column = new MacStack { Spacing = 16, Width = ColumnWidth, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 28, 0, 28) };
        column.Children.Add(new AppIconView(96) { HorizontalAlignment = HorizontalAlignment.Center });

        var titles = new MacStack { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        titles.Children.Add(Centered(new FormText(SettingsCopy.AppName, 26, weight: FontWeights.Bold)));
        titles.Children.Add(Centered(new FormText(SettingsCopy.VersionLine(environment.VersionLabel), 13, "AnnotatorSecondaryText")));
        if (environment.LocalBuild)
        {
            titles.Children.Add(new Border
            {
                Padding = new Thickness(9, 4, 9, 4),
                CornerRadius = new CornerRadius(10.5),
                Background = SettingsLook.Tint(SettingsLook.Orange, 0.14),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new FormText(SettingsCopy.LocalBuild, 10, weight: FontWeights.Medium),
            });
        }
        column.Children.Add(titles);
        column.Children.Add(Centered(new FormText(SettingsCopy.Tagline, 12)));

        var buttons = new MacStack { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        var check = new SettingsButton(SettingsCopy.CheckForUpdates, ButtonLook.Bordered, SettingsWidths.Button(SettingsCopy.CheckForUpdates));
        check.Click += (_, _) => actions.CheckForUpdates();
        buttons.Children.Add(check);
        var copy = new SettingsButton(SettingsCopy.CopySupportInfo, ButtonLook.Bordered, SettingsWidths.Button(SettingsCopy.CopySupportInfo));
        copy.Click += (_, _) => actions.CopySupportInfo();
        buttons.Children.Add(copy);
        column.Children.Add(buttons);

        FormText status = SettingsForm.Caption(actions.Updates.Status, wrapWidth: ColumnWidth);
        status.Block.TextAlignment = TextAlignment.Center;
        column.Children.Add(Centered(status));
        var downloads = new SettingsButton(SettingsCopy.OpenDownloads, ButtonLook.Link, SettingsWidths.Button(SettingsCopy.OpenDownloads), height: 16)
        {
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        downloads.Click += (_, _) => actions.OpenProductPage(ProductPage.Downloads);
        column.Children.Add(downloads);
        // The Mac shows the Downloads link only while updates are not set up.
        UpdatesBinding.Follow(status, actions.Updates, () =>
        {
            check.IsEnabled = actions.Updates.CanCheck;
            status.Text = actions.Updates.Status;
            downloads.Visibility = actions.Updates.IsConfigured ? Visibility.Collapsed : Visibility.Visible;
        });
        column.Children.Add(new Rectangle { Height = 1, HorizontalAlignment = HorizontalAlignment.Stretch, Style = AppResources.Style("AnnotatorDivider") });
        column.Children.Add(Stretched(ReleaseNotesView.Make(environment, ColumnWidth - 12)));
        column.Children.Add(Stretched(new SettingsDisclosure(SettingsCopy.BuildAndSupport, null, 13, () => Details(actions), contentTop: 8)));
        return SettingsForm.Scroll(column);
    }

    private static T Centered<T>(T element) where T : FrameworkElement
    {
        element.HorizontalAlignment = HorizontalAlignment.Center;
        return element;
    }

    private static T Stretched<T>(T element) where T : FrameworkElement
    {
        element.HorizontalAlignment = HorizontalAlignment.Stretch;
        return element;
    }

    /// <summary>The build stamp, the support info note, Save support info..., then the copyright and the notices.</summary>
    private static FrameworkElement Details(SettingsActions actions)
    {
        var details = new MacStack { Spacing = 10 };
        var stamp = new FormText(actions.Environment.BuildLabel, 10);
        stamp.Block.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        stamp.Block.IsTextSelectionEnabled = true;
        details.Children.Add(stamp);
        details.Children.Add(SettingsForm.Caption(SettingsCopy.SupportInfoReview, wrapWidth: ColumnWidth - 12));
        var save = new SettingsButton(SettingsCopy.SaveSupportInfo, ButtonLook.Bordered);
        save.Click += (_, _) => actions.SaveSupportInfo();
        details.Children.Add(save);
        details.Children.Add(SettingsForm.Caption(SettingsCopy.Copyright));
        details.Children.Add(new SettingsDisclosure(SettingsCopy.ThirdPartyNotices, "third-party-notices", 12, () => Notices(actions.Environment), contentTop: 8));
        return details;
    }

    /// <summary>The license notices the app ships (THIRD_PARTY_NOTICES.md): Lucide icons, Rough.js and the Unicode data.</summary>
    private static FrameworkElement Notices(SettingsEnvironment environment)
    {
        (string Title, string File)[] notices =
        [
            ("Lucide icons (ISC license, with Feather's MIT notice)", Path.Combine("Assets", "Lucide-LICENSE.txt")),
            ("Rough.js geometry (MIT license)", "RoughJS-LICENSE.txt"),
            ("Unicode character data (Unicode License V3)", Path.Combine("Text", "Unicode-LICENSE.txt")),
        ];
        var list = new MacStack { Spacing = 10 };
        foreach ((string title, string file) in notices)
        {
            list.Children.Add(new FormText(title, 11, weight: FontWeights.Bold));
            string text;
            try
            {
                text = File.ReadAllText(Path.Combine(environment.AppFolder, file)).Trim();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                text = SettingsCopy.NoticeMissing;
            }
            FormText notice = SettingsForm.Caption(text, wrapWidth: ColumnWidth - 36);
            notice.Block.IsTextSelectionEnabled = true;
            list.Children.Add(notice);
        }
        return list;
    }
}

/// <summary>
/// The Mac's ReleaseNotesView (WIN-UI-05): What's changed, a disclosure group that reads the notes bundled with the app
/// the first time it opens and shows this version's changes, or why there are none. Opening it never checks for updates.
/// </summary>
internal static class ReleaseNotesView
{
    public const string NotesFile = "notes.json";

    public static SettingsDisclosure Make(SettingsEnvironment environment, double textWidth) =>
        new(SettingsCopy.WhatsChanged, "release-notes", 13, () => Content(environment, textWidth), contentTop: 8);

    private static FrameworkElement Content(SettingsEnvironment environment, double width)
    {
        var stack = new MacStack { Spacing = 10, Margin = new Thickness(0, 0, 0, 8) };
        stack.Children.Add(SettingsForm.Caption(SettingsCopy.NotesCameWithApp, wrapWidth: width));
        ReleaseNotesEntry entry;
        try
        {
            entry = ReleaseNotesCatalog.Load(Path.Combine(environment.AppFolder, NotesFile), environment.NotesVersion, environment.BuildNumber);
        }
        catch (ReleaseNotesFailure failure)
        {
            stack.Children.Add(new FormText(failure.Message, 12, "AnnotatorSecondaryText", wrapWidth: width));
            return stack;
        }
        stack.Children.Add(new FormText($"{entry.Version} ({entry.Build})", 13, weight: FontWeights.SemiBold));
        stack.Children.Add(SettingsForm.Caption(entry.Date));
        foreach ((string category, IReadOnlyList<string> items) in entry.Changes)
        {
            stack.Children.Add(new FormText(category, 11, weight: FontWeights.Bold));
            foreach (string item in items)
            {
                stack.Children.Add(Selectable(new FormText(item, 12, wrapWidth: width)));
            }
        }
        if (entry.KnownIssues.Count > 0)
        {
            stack.Children.Add(new FormText(SettingsCopy.KnownLimits, 11, weight: FontWeights.Bold));
            foreach (string item in entry.KnownIssues)
            {
                stack.Children.Add(Selectable(new FormText(item, 12, wrapWidth: width)));
            }
        }
        return stack;
    }

    private static FormText Selectable(FormText text)
    {
        text.Block.IsTextSelectionEnabled = true;
        return text;
    }
}

/// <summary>
/// The app icon (apps/macos/Artwork/main.swift): the dark rounded tile, the A mark and the orange dot in the colors of the
/// Mac's icon files (<see cref="AppIconArtwork"/>), drawn from the shared mark geometry. UI Automation reads an image named
/// "Annotator app icon".
/// </summary>
internal sealed partial class AppIconView : Grid
{
    public AppIconView(double size)
    {
        Width = size;
        Height = size;
        var canvas = new Canvas { Width = size, Height = size };
        var tile = new Border
        {
            Width = size * 0.88,
            Height = size * 0.88,
            CornerRadius = new CornerRadius(size * 0.20),
            Background = Brush(AppIconArtwork.TileColor),
        };
        Canvas.SetLeft(tile, size * 0.06);
        Canvas.SetTop(tile, size * 0.06);
        canvas.Children.Add(tile);
        PathGeometry geometry = Icons.Glyphs.Geometry(MarkGlyph.PathData);
        geometry.FillRule = FillRule.EvenOdd;
        (double scale, Annotator.Core.Point offset) = MarkGlyph.Fit(size * 0.64);
        canvas.Children.Add(new XamlPath
        {
            Data = geometry,
            Fill = Brush(AppIconArtwork.MarkColor),
            RenderTransform = new MatrixTransform
            {
                Matrix = new Microsoft.UI.Xaml.Media.Matrix(scale, 0, 0, scale, offset.X + size * 0.18, offset.Y + size * 0.18),
            },
        });
        var dot = new Ellipse { Width = size * 0.10, Height = size * 0.10, Fill = Brush(AppIconArtwork.DotColor) };
        Canvas.SetLeft(dot, size * 0.70);
        Canvas.SetTop(dot, size * 0.20);
        canvas.Children.Add(dot);
        Children.Add(canvas);
        AutomationProperties.SetName(this, SettingsCopy.AppIconName);
        AutomationProperties.SetAutomationId(this, "about-app-icon");
        // The app's icon keeps its colors in a contrast theme (WIN-UI-07).
        OwnColors.Mark(this);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private static SolidColorBrush Brush((byte Red, byte Green, byte Blue) color) => new(WinColor.FromArgb(255, color.Red, color.Green, color.Blue));

    private sealed partial class Peer(AppIconView owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

        protected override string GetClassNameCore() => "Image";

        protected override bool IsControlElementCore() => true;

        protected override IList<AutomationPeer> GetChildrenCore() => new AutomationPeerList();

        protected override WinRect GetBoundingRectangleCore() => SettingsFrames.Of(owner, base.GetBoundingRectangleCore());
    }
}
