using Annotator.App.Shell;
using Annotator.Platform.Text;
using Annotator.Platform.Windowing;
using Annotator.Workspace.Settings;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Annotator.App.Settings;

/// <summary>
/// Report a problem and Share feedback (the Mac's FeedbackView, WIN-UI-05): a title and details that stay on this PC
/// until the person copies them or opens a GitHub draft in the browser, with what each choice sends. Long reports offer
/// the plain GitHub form instead. Nothing is sent by the app itself.
/// </summary>
internal sealed class FeedbackView
{
    public const double Width = 460;
    public const double Height = 680;

    private readonly SettingsActions _actions;
    private readonly Func<nint> _owner;
    private readonly TextBox _title;
    private readonly TextBox _details;
    private readonly FormText _error;
    private readonly FormText _copied;
    private readonly SettingsButton _plainForm;
    private readonly FormText _plainFormNote;
    private readonly MacStack _stack;

    /// <param name="owner">The window that owns the clipboard while copying.</param>
    /// <param name="close">Closes the window (Close and Escape).</param>
    public FeedbackView(SettingsActions actions, FeedbackKind kind, Func<nint> owner, Action close)
    {
        _actions = actions;
        _owner = owner;
        Kind = kind;
        _stack = new MacStack { Spacing = 12, Margin = new Thickness(20), HorizontalAlignment = HorizontalAlignment.Stretch };
        _stack.Children.Add(new FormText(kind.Title, 17, weight: FontWeights.Bold));
        _stack.Children.Add(SettingsForm.Caption(SettingsCopy.VersionLine(actions.Environment.VersionLabel)));
        _stack.Children.Add(new FormText(FeedbackDraft.TitleHeading, 12, weight: FontWeights.Bold));
        _title = new TextBox
        {
            PlaceholderText = FeedbackDraft.TitlePlaceholder,
            FontSize = 13,
            Height = 24,
            MinHeight = 0,
            Padding = new Thickness(7, 2, 7, 1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(_title, FeedbackDraft.TitleName);
        _stack.Children.Add(_title);
        _stack.Children.Add(new FormText(FeedbackDraft.DetailsHeading, 12, weight: FontWeights.Bold));
        _stack.Children.Add(new FormText(kind.Prompt, 12, wrapWidth: Width - 40));
        _details = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Height = 140,
            MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_details, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(_details, FeedbackDraft.DetailsName);
        _stack.Children.Add(_details);
        _stack.Children.Add(SettingsForm.Caption(FeedbackDraft.Disclosure, wrapWidth: Width - 40));
        _stack.Children.Add(SettingsForm.Caption(FeedbackDraft.CopyExplanation, wrapWidth: Width - 40));
        if (actions.IssueUrl is null)
        {
            _stack.Children.Add(SettingsForm.Caption(FeedbackDraft.NotConfigured, wrapWidth: Width - 40));
        }
        _error = SettingsForm.Caption(string.Empty, wrapWidth: Width - 40);
        _error.Block.Foreground = SettingsLook.Red;
        _copied = new FormText(FeedbackDraft.Copied, 10);
        AutomationProperties.SetAutomationId(_copied, "feedback-copied");
        _plainForm = new SettingsButton(FeedbackDraft.PlainForm, ButtonLook.Bordered);
        AutomationProperties.SetAutomationId(_plainForm, "feedback-plain-form");
        _plainForm.Click += (_, _) => OpenPlainForm();
        _plainFormNote = SettingsForm.Caption(FeedbackDraft.PlainFormExplanation, wrapWidth: Width - 40);
        _stack.Children.Add(_error);
        _stack.Children.Add(_copied);
        _stack.Children.Add(_plainForm);
        _stack.Children.Add(_plainFormNote);
        ShowError(null);
        ShowCopied(false);
        ShowPlainForm(false);
        _title.TextChanged += (_, _) => Edited();
        _details.TextChanged += (_, _) => Edited();

        var copy = new SettingsButton(FeedbackDraft.CopyButton, ButtonLook.Bordered, SettingsWidths.Button(FeedbackDraft.CopyButton));
        copy.Click += (_, _) => Copy();
        var draft = new SettingsButton(FeedbackDraft.DraftButton, ButtonLook.Bordered, SettingsWidths.Button(FeedbackDraft.DraftButton))
        {
            IsEnabled = actions.IssueUrl is not null,
        };
        draft.Click += (_, _) => OpenDraft();
        var closeButton = new SettingsButton(FeedbackDraft.CloseButton, ButtonLook.Bordered, SettingsWidths.Button(FeedbackDraft.CloseButton))
        {
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        closeButton.Click += (_, _) => close();
        var buttons = new Grid { Margin = new Thickness(20), Height = 24 };
        var leading = new MacStack { Orientation = Orientation.Horizontal, Spacing = 8 };
        leading.Children.Add(copy);
        leading.Children.Add(draft);
        buttons.Children.Add(leading);
        buttons.Children.Add(closeButton);

        var root = new Grid { Width = Width, Background = SettingsLook.Window };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        ScrollViewer scroll = SettingsForm.Scroll(_stack);
        root.Children.Add(scroll);
        var divider = new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 1, Style = AppResources.Style("AnnotatorDivider") };
        Grid.SetRow(divider, 1);
        root.Children.Add(divider);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        root.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Escape)
            {
                args.Handled = true;
                close();
            }
        };
        Root = root;
    }

    public FeedbackKind Kind { get; }

    public Grid Root { get; }

    public void FocusTitle() => _title.Focus(FocusState.Programmatic);

    private FeedbackDraft Draft => new(Kind) { Title = _title.Text, Details = _details.Text };

    /// <summary>Mac FeedbackDraft.copy: the report on the clipboard, or why it could not go there.</summary>
    private void Copy()
    {
        try
        {
            string report = Draft.Report(_actions.Environment.VersionLabel);
            if (_actions.Environment.Live)
            {
                TextClipboard.SetText(_owner(), report);
            }
            ShowCopied(true);
            ShowError(null);
        }
        catch (FeedbackFailure failure)
        {
            ShowError(failure.Message);
        }
        catch (InvalidOperationException error)
        {
            StartupLog.Note("feedback", error.Message);
            ShowError(FeedbackFailure.Clipboard().Message);
        }
    }

    /// <summary>Mac Open GitHub draft: the issue form with the title and details in a browser link, or the plain form for long reports.</summary>
    private void OpenDraft()
    {
        ShowPlainForm(false);
        try
        {
            Uri link = Draft.GitHubUrl(_actions.Environment.IssuesUrl, _actions.Environment.VersionLabel);
            if (!_actions.OpenLink(link))
            {
                throw FeedbackFailure.Browser();
            }
            ShowError(null);
        }
        catch (FeedbackFailure failure) when (failure.Kind == FeedbackFailureKind.TooLongForLink)
        {
            ShowError(failure.Message);
            ShowPlainForm(true);
        }
        catch (FeedbackFailure failure)
        {
            ShowError(failure.Message);
        }
    }

    /// <summary>Mac Open GitHub form: a failure goes to the error window, as on the Mac.</summary>
    private void OpenPlainForm() => _actions.ReportIssue(Kind == FeedbackKind.Feedback);

    private void Edited()
    {
        ShowCopied(false);
        ShowPlainForm(false);
    }

    private void ShowError(string? message)
    {
        _error.Text = message ?? string.Empty;
        _error.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        _stack.InvalidateMeasure();
    }

    private void ShowCopied(bool copied)
    {
        _copied.Visibility = copied ? Visibility.Visible : Visibility.Collapsed;
        _stack.InvalidateMeasure();
    }

    private void ShowPlainForm(bool shown)
    {
        _plainForm.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        _plainFormNote.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        _stack.InvalidateMeasure();
    }
}

/// <summary>
/// The feedback window: the Mac's sheet on Settings as a window owned by Settings that keeps Settings disabled while it
/// is open, 460 points wide and 680 high (less on a short work area, as the Mac's min(680, visible height - 100)).
/// </summary>
internal sealed class FeedbackWindow : Window
{
    private readonly HWND _owner;
    private readonly WindowRegistration _registration;
    private readonly FeedbackView _view;
    /// <summary>The form at a larger text size (WIN-A11Y-03), zoomed; null at 100%.</summary>
    private readonly ZoomPanel? _zoom;
    /// <summary>Shows the window cloaked until its content drew, so its first click works (WindowCloak).</summary>
    private readonly WindowCloak _cloak;
    private bool _closed;

    public FeedbackWindow(WindowRegistry registry, SettingsActions actions, FeedbackKind kind, HWND owner)
    {
        _owner = owner;
        Title = kind.Title;
        Hwnd = new HWND(Win32Interop.GetWindowFromWindowId(AppWindow.Id));
        _registration = registry.Register((nint)Hwnd, WindowLayer.Toolbar);
        OverlappedPresenter presenter = OverlappedPresenter.CreateForDialog();
        presenter.IsResizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        // An owned window stays above its owner and closes with it.
        PInvoke.SetWindowLongPtr(Hwnd, Windows.Win32.UI.WindowsAndMessaging.WINDOW_LONG_PTR_INDEX.GWLP_HWNDPARENT, (nint)owner);
        _view = new FeedbackView(actions, kind, () => (nint)Hwnd, Close);
        var root = new WindowRoot();
        root.Children.Add(_view.Root);
        AutomationFrames.Nudge(root);
        AppResources.FollowTheme(root, AppWindow);
        _zoom = TextScaling.Zoom > 1 ? new ZoomPanel(root, TextScaling.Zoom) : null;
        Content = _zoom ?? (UIElement)root;
        _cloak = new WindowCloak(this, Hwnd, $"the {kind.Title} window");
        Closed += (_, _) =>
        {
            _closed = true;
            PInvoke.EnableWindow(_owner, true);
            if (PInvoke.IsWindow(_owner))
            {
                PInvoke.SetForegroundWindow(_owner);
            }
            _registration.Dispose();
        };
    }

    public HWND Hwnd { get; }

    public FeedbackKind Kind => _view.Kind;

    public bool IsClosed => _closed;

    /// <summary>Shows the window centered over Settings, disables Settings like a sheet, and puts focus in the title.</summary>
    public void Open(RectInt32 ownerFrame)
    {
        MonitorArea monitor = WindowChrome.MonitorFor(ownerFrame);
        double scale = monitor.Scale;
        double zoom = 1;
        if (_zoom is { } panel)
        {
            // As large as the text size makes it, while 460 by 300 points fit in the work area; the form scrolls.
            zoom = TextScaling.Fit(FeedbackView.Width, 300, new Windows.Foundation.Size(monitor.WorkArea.Width / scale - 32, monitor.WorkArea.Height / scale - 100));
            panel.Zoom = zoom;
        }
        double height = Math.Min(FeedbackView.Height, Math.Max(300, (monitor.WorkArea.Height / scale - 100) / zoom));
        _view.Root.Height = height;
        AppWindow.ResizeClient(new SizeInt32((int)Math.Round(FeedbackView.Width * zoom * scale), (int)Math.Round(height * zoom * scale)));
        SizeInt32 outer = AppWindow.Size;
        RectInt32 work = monitor.WorkArea;
        int x = Math.Clamp(ownerFrame.X + (ownerFrame.Width - outer.Width) / 2, work.X, Math.Max(work.X, work.X + work.Width - outer.Width));
        int y = Math.Clamp(ownerFrame.Y + (int)Math.Round(40 * scale), work.Y, Math.Max(work.Y, work.Y + work.Height - outer.Height));
        AppWindow.Move(new PointInt32(x, y));
        PInvoke.EnableWindow(_owner, false);
        _cloak.BeforeShow();
        AppWindow.Show(activateWindow: true);
        PInvoke.SetForegroundWindow(Hwnd);
        _view.Root.Loaded += (_, _) => _view.FocusTitle();
        _view.FocusTitle();
    }

    public void BringToFront() => PInvoke.SetForegroundWindow(Hwnd);
}
