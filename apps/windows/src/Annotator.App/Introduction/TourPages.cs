using System.Numerics;
using Annotator.App.Settings;
using Annotator.App.Shell;
using Annotator.App.Toolbar;
using Annotator.Workspace;
using Annotator.Workspace.FirstUse;
using Annotator.Workspace.Shortcuts;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Annotator.App.Introduction;

/// <summary>What the pages need from their window.</summary>
internal sealed class TourContext
{
    public required bool Dark { get; init; }

    public required WorkspaceSnapshot Snapshot { get; init; }

    public required IllustrationGraphics Graphics { get; init; }

    public required Func<bool> ReducesMotion { get; init; }

    public required Action Replay { get; init; }

    /// <summary>Opens Windows Settings > Personalization > Taskbar (DV-4).</summary>
    public required Action OpenTaskbarSettings { get; init; }

    /// <summary>Whether Before you share is open; it stays open across pages, like the Mac's sharingExpanded state.</summary>
    public required Func<bool> SharingExpanded { get; init; }

    public required Action<bool> SetSharingExpanded { get; init; }
}

/// <summary>
/// One tour page in its scroll view (Mac IntroductionView.page): built for each visit, like the Mac's page that the
/// navigator's page identifies, and moved by its story's progress while the story plays.
/// </summary>
internal abstract class TourPage
{
    protected TourPage(IntroductionPage page)
    {
        Page = page;
        Stack = new MacStack();
        Root = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalAlignment = VerticalAlignment.Stretch,
            Content = new Border { Padding = new Thickness(24, 12, 24, 18), Child = Stack },
        };
    }

    public IntroductionPage Page { get; }

    public ScrollViewer Root { get; }

    public IllustrationView? Illustration { get; protected set; }

    protected MacStack Stack { get; }

    public static TourPage Create(IntroductionPage page, TourContext context) => page switch
    {
        IntroductionPage.Draw => new DrawPage(context),
        IntroductionPage.Color => new ColorPage(context),
        IntroductionPage.Focus => new FocusPage(context),
        _ => new SetupPage(context),
    };

    /// <summary>Shows the story at <paramref name="progress"/>, <paramref name="elapsed"/> seconds after it started.</summary>
    public virtual void Apply(double progress, double elapsed)
    {
        if (Illustration is { } illustration)
        {
            illustration.Progress = progress;
        }
    }

    /// <summary>Follows the workspace: shortcuts and their state. Returns false when the page must be built again.</summary>
    public virtual bool Update(WorkspaceSnapshot snapshot) => true;

    protected void Add(FrameworkElement element, double top = 0)
    {
        // The space above is an exact spacer and a text's height is exactly its lines: WinUI rounds a margin apart from its
        // element and a line up to whole pixels, which moved the page's text down by up to 1.6 points at 125%. Both follow
        // the element's visibility.
        if (top > 0)
        {
            Stack.Children.Add(Follow(new Microsoft.UI.Xaml.Controls.Border { Height = top }, element));
        }
        Stack.Children.Add(element is TextBlock text ? Follow(new ExactLines(text, text.LineHeight), text) : element);
    }

    private static FrameworkElement Follow(FrameworkElement follower, FrameworkElement element)
    {
        follower.Visibility = element.Visibility;
        element.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => follower.Visibility = element.Visibility);
        return follower;
    }

    protected IllustrationView AddIllustration(TourContext context)
    {
        var view = new IllustrationView(Page, context.Graphics, context.ReducesMotion, context.Replay)
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Illustration = view;
        Add(view);
        return view;
    }

    protected void AddTitle(string title, double top) => Add(TourParts.Text(title, 22, weight: FontWeights.Bold), top);
}

/// <summary>Page 1: the illustration, three shortcut rows that light up with the story, the Escape line and Before you share.</summary>
internal sealed class DrawPage : TourPage
{
    private const double RowHeight = 40;
    private const double RowSpacing = 2;
    private readonly Microsoft.UI.Xaml.Controls.Border _highlight;
    private readonly List<(FrameworkElement Icon, FrameworkElement Keys, Microsoft.UI.Xaml.Controls.Border Glow)> _rows = [];
    private readonly double[] _presses = [-1, -1, -1];
    private (double Position, double Opacity) _focus = (-1, -1);
    private readonly ShortcutChord _resume;
    private readonly ShortcutChord _showHide;

    public DrawPage(TourContext context)
        : base(IntroductionPage.Draw)
    {
        _resume = Resume(context.Snapshot);
        _showHide = ShowHide(context.Snapshot);
        AddIllustration(context);
        AddTitle(IntroductionText.DrawTitle, 14);
        Add(TourParts.Text(IntroductionText.DrawLead, 13, TourParts.Secondary), 4);
        var rows = new MacStack { Spacing = RowSpacing };
        rows.Children.Add(Row(IntroductionText.DrawRow, IntroductionText.DrawRowDetail, "pencil.tip", "Pencil Tip",
            TourParts.Shortcut(_resume, context.Dark)));
        rows.Children.Add(Row(IntroductionText.UseAppsRow, IntroductionText.UseAppsRowDetail, "cursorarrow", "Cursor Arrow Pointer",
            TourParts.Keycap(IntroductionText.EscapeKeycap, context.Dark, IntroductionText.EscapeName)));
        rows.Children.Add(Row(IntroductionText.ShowHideRow, IntroductionText.ShowHideRowDetail, "eye", "Show",
            TourParts.Shortcut(_showHide, context.Dark)));
        _highlight = new Microsoft.UI.Xaml.Controls.Border
        {
            Height = RowHeight,
            CornerRadius = new CornerRadius(9),
            Background = TourParts.AccentFill(0.1),
            BorderBrush = AppResources.Brush("AnnotatorSelectedOutlineBrush"),
            BorderThickness = new Thickness(ContrastTheme.IsOn ? 2 : 0),
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Opacity = 0,
        };
        AutomationProperties.SetAccessibilityView(_highlight, AccessibilityView.Raw);
        var block = new Grid { Margin = new Thickness(-10, 0, -10, 0) };
        block.Children.Add(_highlight);
        block.Children.Add(rows);
        Add(block, 8);
        Add(TourParts.Text(IntroductionText.EscapeLine, 10, TourParts.Secondary), 6);
        var details = TourParts.Text(IntroductionText.SharingBodyFor(context.Snapshot.ScreenAccess), 12, TourParts.Secondary);
        var disclosure = new TourDisclosure(IntroductionText.SharingTitle, IntroductionText.SharingIdentifier, details, context.SharingExpanded());
        disclosure.ExpandedChanged += context.SetSharingExpanded;
        // 12 points above the disclosure's frame, as the UX reference has it (frame y 381 below the Escape line's 369).
        Add(disclosure, 12);
        Add(details, 8);
        details.Visibility = disclosure.Expanded ? Visibility.Visible : Visibility.Collapsed;
    }

    public override void Apply(double progress, double elapsed)
    {
        base.Apply(progress, elapsed);
        (double position, double opacity) = IntroductionStory.ShortcutFocus(progress);
        if (position != _focus.Position || opacity != _focus.Opacity)
        {
            _focus = (position, opacity);
            _highlight.Translation = new Vector3(0, (float)(position * (RowHeight + RowSpacing)), 0);
            _highlight.Opacity = opacity;
        }
        for (int row = 0; row < _rows.Count; row++)
        {
            double press = IntroductionStory.ShortcutPress(row, progress);
            if (press == _presses[row])
            {
                continue;
            }
            _presses[row] = press;
            TourParts.ScaleAroundCenter(_rows[row].Icon, 1 + 0.12 * press);
            TourParts.ScaleAroundCenter(_rows[row].Keys, 1 - 0.06 * press);
            _rows[row].Glow.Opacity = press;
        }
    }

    /// <summary>A changed shortcut builds the page again with its new keycaps.</summary>
    public override bool Update(WorkspaceSnapshot snapshot) => Resume(snapshot) == _resume && ShowHide(snapshot) == _showHide;

    private static ShortcutChord Resume(WorkspaceSnapshot snapshot) =>
        WindowsShortcuts.Chord(GlobalShortcutAction.ResumeDrawing, snapshot.Preferences) ?? WindowsShortcuts.ResumeDrawingDefault;

    private static ShortcutChord ShowHide(WorkspaceSnapshot snapshot) =>
        WindowsShortcuts.Chord(GlobalShortcutAction.ShowHide, snapshot.Preferences) ?? WindowsShortcuts.ShowHideDefault;

    /// <summary>
    /// Mac shortcutRow: a 28-point accent icon, the headline and detail 1 point apart, the keys on the right; 40 points
    /// tall with 10 points of padding. A press scales the icon up 12% and the keys down 6% with an accent glow.
    /// </summary>
    private Grid Row(string title, string detail, string symbol, string label, FrameworkElement keys)
    {
        var row = new Grid { Height = RowHeight, Padding = new Thickness(10, 0, 10, 0), ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        FrameworkElement icon = TourParts.Symbol(symbol, 18, "AnnotatorGlyphAccent", label);
        row.Children.Add(icon);
        var texts = new MacStack { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(TourParts.Text(title, 13, weight: FontWeights.SemiBold, wrap: false));
        texts.Children.Add(TourParts.Text(detail, 12, TourParts.Secondary, wrap: false));
        Grid.SetColumn(texts, 1);
        row.Children.Add(texts);
        var glow = new Microsoft.UI.Xaml.Controls.Border
        {
            CornerRadius = new CornerRadius(9),
            Background = TourParts.AccentFill(0.3),
            BorderBrush = AppResources.Brush("AnnotatorSelectedOutlineBrush"),
            BorderThickness = new Thickness(ContrastTheme.IsOn ? 2 : 0),
            Margin = new Thickness(-4),
            Opacity = 0,
            IsHitTestVisible = false,
        };
        AutomationProperties.SetAccessibilityView(glow, AccessibilityView.Raw);
        var keyArea = new Grid { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        keyArea.Children.Add(glow);
        keyArea.Children.Add(keys);
        Grid.SetColumn(keyArea, 2);
        row.Children.Add(keyArea);
        _rows.Add((icon, keyArea, glow));
        return row;
    }
}

/// <summary>Page 2: the illustration, the outline and fill lines, and the Undo line with its icon.</summary>
internal sealed class ColorPage : TourPage
{
    public ColorPage(TourContext context)
        : base(IntroductionPage.Color)
    {
        AddIllustration(context);
        AddTitle(IntroductionText.ColorTitle, 14);
        Add(TourParts.Text(IntroductionText.ColorLead, 13), 6);
        Add(TourParts.Text(IntroductionText.ColorDetail, 13, TourParts.Secondary), 8);
        var label = new MacStack { Orientation = Orientation.Horizontal, Spacing = 8 };
        FrameworkElement icon = Icons.Glyphs.ForSymbol("arrow.uturn.backward", 14, "AnnotatorGlyph");
        icon.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
        label.Children.Add(icon);
        TextBlock text = TourParts.Text(IntroductionText.ColorUndo, 12, wrap: false);
        text.VerticalAlignment = VerticalAlignment.Center;
        text.MinHeight = TourParts.LineHeight(12);
        AutomationProperties.SetAccessibilityView(text, AccessibilityView.Raw);
        label.Children.Add(text);
        // Mac Label(text, systemImage:): one static text whose frame starts at the symbol's drawn edge, 1.5 points in.
        var undo = new AccessibleText(IntroductionText.ColorUndo) { HorizontalAlignment = HorizontalAlignment.Left };
        undo.Children.Add(label);
        undo.SizeChanged += (_, args) => undo.Bounds = new Windows.Foundation.Rect(1.5, 0, Math.Max(0, args.NewSize.Width - 1.5), args.NewSize.Height);
        Add(undo, 12);
    }
}

/// <summary>Page 3: the illustration, where the focus tools are, and the line about boards and My shapes.</summary>
internal sealed class FocusPage : TourPage
{
    public FocusPage(TourContext context)
        : base(IntroductionPage.Focus)
    {
        AddIllustration(context);
        AddTitle(IntroductionText.FocusTitle, 14);
        Add(TourParts.Text(IntroductionText.FocusLead, 13), 6);
        Add(TourParts.Text(IntroductionText.FocusDetail, 13, TourParts.Secondary), 8);
        Add(TourParts.Text(IntroductionText.FocusBoards, 12), 12);
    }
}

/// <summary>
/// Page 4 (Mac permissionIntroduction) on Windows, where nothing needs a permission (WIN-INP-08): the tray icon card (DV-4)
/// with Open taskbar settings, and the shortcuts card (DV-10, WIN-INP-05), ready with a drawn check while every shortcut
/// works, or listing the ones Windows refused or another app took. Cards settle in one after another, then the sharing note
/// brightens.
/// </summary>
internal sealed class SetupPage : TourPage
{
    private readonly List<TourCard> _cards = [];
    private readonly TextBlock _note;
    private readonly TourBadge? _badge;
    private readonly string _shortcuts;

    public SetupPage(TourContext context)
        : base(IntroductionPage.Setup)
    {
        _shortcuts = ShortcutState(context.Snapshot);
        AddTitle(IntroductionText.SetupTitle, 0);
        Add(TourParts.Text(IntroductionText.SetupLeadFor(context.Snapshot.ScreenAccess), 13, TourParts.Secondary), 4);
        Button open = TourParts.PushButton(IntroductionText.TrayAction);
        AutomationProperties.SetHelpText(open, IntroductionText.TrayActionHelp);
        ToolTipService.SetToolTip(open, IntroductionText.TrayActionHelp);
        open.Click += (_, _) => context.OpenTaskbarSettings();
        var tray = new TourCard("chevron.up", "Chevron Up", IntroductionText.TrayTitle, IntroductionText.TrayDetail, [], ready: false,
            context.Dark, open);
        _cards.Add(tray);
        Add(tray.Root, 14);
        IReadOnlyList<GlobalShortcutState> unavailable = IntroductionText.UnavailableShortcuts(context.Snapshot);
        bool working = unavailable.Count == 0;
        FrameworkElement trailing;
        if (working)
        {
            _badge = new TourBadge(IntroductionText.ShortcutsWorking, IntroductionText.ShortcutsWorkingLabel, context.Dark);
            trailing = _badge.Root;
        }
        else
        {
            trailing = new Grid();
        }
        var shortcuts = new TourCard("keyboard", "Keyboard", IntroductionText.ShortcutsTitle, IntroductionText.ShortcutsDetail(context.Snapshot),
            [.. unavailable.Select(IntroductionText.ShortcutUnavailable)], working, context.Dark, trailing);
        _cards.Add(shortcuts);
        Add(shortcuts.Root, 8);
        _note = TourParts.Text(IntroductionText.SharingNote, 10, TourParts.Secondary);
        Add(_note, 14);
    }

    public override void Apply(double progress, double elapsed)
    {
        base.Apply(progress, elapsed);
        for (int index = 0; index < _cards.Count; index++)
        {
            _cards[index].Arrive(IntroductionStory.CardArrival(index, progress));
        }
        _note.Opacity = IntroductionStory.SharingNoteOpacity(progress);
        // The Mac's check draws over 0.38 s once its card has settled, timed from the page's appearance.
        _badge?.Draw(progress >= 1 && elapsed <= 0 ? 1 : Check(1, elapsed, progress));
    }

    public override bool Update(WorkspaceSnapshot snapshot) => ShortcutState(snapshot) == _shortcuts;

    private static double Check(int index, double elapsed, double progress)
    {
        if (elapsed <= 0)
        {
            return IntroductionStory.CheckDraw(index, progress);
        }
        double delay = IntroductionStory.CheckDelay(index, 1);
        return IntroductionEase.EaseOut(IntroductionEase.Clamp((elapsed - delay) / IntroductionMotion.CheckDuration));
    }

    /// <summary>What the shortcuts card shows, to know when it must change.</summary>
    private static string ShortcutState(WorkspaceSnapshot snapshot) =>
        IntroductionText.ShortcutsDetail(snapshot) + "|" + string.Join("|", IntroductionText.UnavailableShortcuts(snapshot).Select(IntroductionText.ShortcutUnavailable));
}
