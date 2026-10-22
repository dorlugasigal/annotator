using Annotator.App.Shell;
using Annotator.Workspace;
using Annotator.Workspace.Settings;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinColor = Windows.UI.Color;
using WinRect = Windows.Foundation.Rect;

namespace Annotator.App.Settings;

/// <summary>
/// Settings > Permissions (WIN-INP-08, WIN-CAP-04; the Mac's CustomizationView.permissions): drawing needs no permission;
/// one card for screenshots and screen recording with Windows' answer and the capture service's state, Allow screen access
/// while Windows has not allowed it (Open screen recording settings once it has), the capture failure that the toolbar's
/// warning points to or where to allow access after a refusal, and one card saying shortcuts and Alt+scroll need no permission.
/// </summary>
internal sealed partial class PermissionsPage
{
    private readonly SettingsActions _actions;
    private readonly MacStack _stack;
    private readonly FormText _captureStatus;
    private readonly ContentControl _captureIcon = new() { IsTabStop = false };
    private readonly SettingsButton _openSettings;
    private readonly SettingsButton _allow;
    private readonly FormText _failure;
    private string? _shownStatus;

    public PermissionsPage(SettingsActions actions)
    {
        _actions = actions;
        _stack = new MacStack { Spacing = 18, HorizontalAlignment = HorizontalAlignment.Stretch };
        FormText ready = SettingsLabel.Make("checkmark.circle.fill", SettingsCopy.NoPermissions, 13, "AnnotatorPrimaryText", "AnnotatorGlyph", 14, 6,
            FontWeights.SemiBold, SettingsLook.Green, iconColor: SettingsLook.Green.Color);
        ready.Margin = new Thickness(1, 0, 0, 0);
        _stack.Children.Add(ready);

        _openSettings = new SettingsButton(SettingsCopy.OpenCaptureSettings, ButtonLook.Bordered, SettingsWidths.Button(SettingsCopy.OpenCaptureSettings),
            tint: SettingsLook.Blue);
        _openSettings.Click += (_, _) => actions.OpenCaptureSettings();
        _allow = new SettingsButton(SettingsCopy.AllowScreenAccess, ButtonLook.Bordered, SettingsWidths.Button(SettingsCopy.AllowScreenAccess),
            tint: SettingsLook.Blue);
        _allow.Click += (_, _) => actions.AllowScreenAccess();
        // The Mac's one button, titled by the access: only the one that applies is shown and read.
        var action = new Grid { HorizontalAlignment = HorizontalAlignment.Right };
        _openSettings.HorizontalAlignment = HorizontalAlignment.Right;
        _allow.HorizontalAlignment = HorizontalAlignment.Right;
        action.Children.Add(_openSettings);
        action.Children.Add(_allow);
        _captureStatus = new FormText(string.Empty, 10, weight: FontWeights.Medium);
        _captureStatus.Block.Foreground = new SolidColorBrush(SettingsLook.Blue);
        _stack.Children.Add(Card(SettingsCopy.CaptureTitle, "display", "Display", SettingsLook.Blue, SettingsCopy.CaptureDetail, action, _captureStatus, _captureIcon));

        _failure = SettingsLabel.Make("exclamationmark.triangle.fill", string.Empty, 12, "AnnotatorPrimaryText", "AnnotatorGlyph", 13, 6,
            foreground: SettingsLook.Red, wrapWidth: 700, iconColor: SettingsLook.Red.Color);
        _failure.Margin = new Thickness(1, 0, 0, 0);
        _stack.Children.Add(_failure);

        FormText inputStatus = Status(SettingsCopy.InputStatus, SettingsLook.Orange, out FrameworkElement inputIcon);
        _stack.Children.Add(Card(SettingsCopy.InputTitle, "cursorarrow.rays", "Click Cursor", SettingsLook.Orange, SettingsCopy.InputDetail, null,
            inputStatus, inputIcon));

        var refresh = new SettingsButton(SettingsCopy.RefreshStatus, ButtonLook.Borderless, SettingsWidths.Button(SettingsCopy.RefreshStatus), height: 16,
            symbol: "arrow.clockwise")
        {
            // The Mac's borderless button sits half a point lower than its row and starts 1 point in.
            Margin = new Thickness(1, 0.5, 0, -0.5),
        };
        refresh.Click += (_, _) => Refresh();
        _stack.Children.Add(refresh);

        var recovery = new SettingsDisclosure(SettingsCopy.AccessNotWorking, "permission-recovery", 13, () =>
        {
            var content = new MacStack { Spacing = 12 };
            content.Children.Add(new FormText(SettingsCopy.RecoveryCheckSettings, 12, wrapWidth: 700));
            content.Children.Add(new FormText(SettingsCopy.RecoveryRestart, 12, wrapWidth: 700));
            return content;
        });
        _stack.Children.Add(recovery);

        FormText blur = SettingsLabel.Make("eye.trianglebadge.exclamationmark", SettingsCopy.BlurNotRedaction, 10, "AnnotatorSecondaryText",
            "AnnotatorGlyphSecondary", 12, 4);
        blur.Margin = new Thickness(1, 0, 0, 0);
        _stack.Children.Add(blur);
        Root = SettingsForm.Scroll(_stack, new Thickness(22));
        Apply(actions.Model.Snapshot);
    }

    public FrameworkElement Root { get; }

    /// <summary>
    /// The capture state in words (not allowed, the capture service's failure, a frame that arrived, or allowed), the button
    /// that applies, and the problem: the failure, or where to allow access after a refusal.
    /// </summary>
    public void Apply(WorkspaceSnapshot snapshot)
    {
        string status = SettingsStatus.Capture(snapshot);
        if (status != _shownStatus)
        {
            _shownStatus = status;
            _captureStatus.Text = status;
            _captureIcon.Content = StatusIcon(status, SettingsLook.Blue);
            _captureStatus.MinHeight = SettingsStatus.IsSettled(status) ? 13.5 : 0;
        }
        bool allowed = SettingsStatus.CaptureAction(snapshot) == SettingsCopy.OpenCaptureSettings;
        _openSettings.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
        _allow.Visibility = allowed ? Visibility.Collapsed : Visibility.Visible;
        string failure = SettingsStatus.CaptureProblem(snapshot);
        if (_failure.Text != failure)
        {
            _failure.Text = failure;
        }
        _failure.Visibility = failure.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _stack.InvalidateMeasure();
    }

    /// <summary>
    /// Refresh status: reads launch at sign-in and Windows' screen access again, starts a zoom or live effects that failed
    /// again, and shows the latest capture state.
    /// </summary>
    public void Refresh()
    {
        _actions.Model.RefreshScreenAccess();
        _ = _actions.Launch.RefreshAsync();
        _actions.RetryFailures();
        Apply(_actions.Model.Snapshot);
    }

    private static FormText Status(string text, WinColor tint, out FrameworkElement icon)
    {
        var status = new FormText(text, 10, weight: FontWeights.Medium);
        status.Block.Foreground = new SolidColorBrush(tint);
        status.MinHeight = SettingsStatus.IsSettled(text) ? 13.5 : 0;
        icon = StatusIcon(text, tint);
        return status;
    }

    /// <summary>The Mac shows a filled check for a working or settled state and an info circle otherwise.</summary>
    private static FrameworkElement StatusIcon(string status, WinColor tint) =>
        SettingsIcons.Make(SettingsStatus.IsSettled(status) ? "checkmark.circle.fill" : "info.circle", 11, tint);

    /// <summary>
    /// The Mac's permissionCard: a 44-point tile with the icon in the tint at 12%, the title and the detail, the action at
    /// the trailing edge, then the status in the tint, with 12 points above and below.
    /// </summary>
    private static FrameworkElement Card(string title, string symbol, string iconName, WinColor tint, string detail, FrameworkElement? action,
                                         FormText status, FrameworkElement statusIcon)
    {
        var icon = new PermissionIcon(symbol, iconName, tint);
        var tile = new Grid
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(10),
            Background = SettingsLook.Tint(tint, 0.12),
            VerticalAlignment = VerticalAlignment.Top,
        };
        tile.Children.Add(icon);
        var texts = new MacStack { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new FormText(title, 13, weight: FontWeights.SemiBold));
        texts.Children.Add(new FormText(detail, 12, "AnnotatorSecondaryText", wrapWidth: 440));
        var header = new Grid { ColumnSpacing = 12, Height = 44 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(tile);
        Grid.SetColumn(texts, 1);
        header.Children.Add(texts);
        if (action is not null)
        {
            action.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(action, 2);
            header.Children.Add(action);
        }
        status.Margin = new Thickness(1, 0, 0, 0);
        statusIcon.VerticalAlignment = VerticalAlignment.Center;
        var card = new MacStack { Spacing = 12, Margin = new Thickness(0, 12, 0, 12), HorizontalAlignment = HorizontalAlignment.Stretch };
        card.Children.Add(header);
        card.Children.Add(StatusLine(status, statusIcon));
        return card;
    }

    /// <summary>The status label: the icon before the words, read as one text (the Mac's Label).</summary>
    private static FrameworkElement StatusLine(FormText status, FrameworkElement icon)
    {
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(15) });
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(status.Block, 1);
        status.Block.VerticalAlignment = VerticalAlignment.Center;
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        status.Children.Insert(0, icon);
        AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
        return status;
    }

    /// <summary>The card's icon, read as an image named like the Mac's symbol description and identified by the symbol.</summary>
    private sealed partial class PermissionIcon : Grid
    {
        private readonly string _name;

        public PermissionIcon(string symbol, string name, WinColor tint)
        {
            _name = name;
            // The SF Symbols' frames in the capture: display is 20 by 17 points, cursorarrow.rays 18.5 by 18.
            Width = symbol == "display" ? 20 : 18.5;
            Height = symbol == "display" ? 17 : 18;
            FrameworkElement glyph = SettingsIcons.Make(symbol, Height, tint);
            glyph.HorizontalAlignment = HorizontalAlignment.Center;
            glyph.VerticalAlignment = VerticalAlignment.Center;
            Children.Add(glyph);
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetAutomationId(this, symbol);
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

        private sealed partial class Peer(PermissionIcon owner) : FrameworkElementAutomationPeer(owner)
        {
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

            protected override string GetClassNameCore() => "Image";

            protected override string GetNameCore() => owner._name;

            protected override bool IsControlElementCore() => true;

            protected override IList<AutomationPeer> GetChildrenCore() => new AutomationPeerList();

            protected override WinRect GetBoundingRectangleCore() => SettingsFrames.Of(owner, base.GetBoundingRectangleCore());
        }
    }
}
