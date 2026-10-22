using System.Numerics;
using Annotator.App.Pickers;
using Annotator.App.Shell;
using Annotator.Core;
using Annotator.Workspace.Motion;
using Annotator.Workspace.Settings;
using Annotator.Workspace.Shortcuts;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WinRect = Windows.Foundation.Rect;

namespace Annotator.App.Settings;

/// <summary>
/// Port of ShortcutKeycaps.swift with the text keycaps of specification 013 (keyboard mapping): one 28-point keycap per key
/// (Ctrl, Alt, Shift, Win and the key), 5 points apart, on the window background with a light outline and a 2-point hard
/// shadow. UI Automation reads one element named with the keys ("Ctrl + Shift + 2"), with the same words as its help, as
/// the Mac reads "Control + Shift + 2".
/// </summary>
internal sealed partial class ShortcutKeycaps : Grid
{
    private const double Size = 28;
    private readonly MacStack _row = new() { Orientation = Orientation.Horizontal, Spacing = 5 };
    private ActivationShortcut _shortcut;
    private bool _set;

    public ShortcutKeycaps(ActivationShortcut shortcut)
    {
        Children.Add(_row);
        Shortcut = shortcut;
    }

    public ActivationShortcut Shortcut
    {
        get => _shortcut;
        set
        {
            if (_set && _shortcut == value)
            {
                return;
            }
            string before = _set ? WindowsShortcuts.Spoken(_shortcut) : string.Empty;
            _shortcut = value;
            _set = true;
            _row.Children.Clear();
            foreach (string key in WindowsShortcuts.Keys(value))
            {
                _row.Children.Add(Keycap(key));
            }
            string spoken = WindowsShortcuts.Spoken(value);
            AutomationProperties.SetName(this, spoken);
            AutomationProperties.SetHelpText(this, spoken);
            ToolTipService.SetToolTip(this, spoken);
            if (before.Length > 0 && FrameworkElementAutomationPeer.FromElement(this) is AutomationPeer peer)
            {
                peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, before, spoken);
            }
        }
    }

    /// <summary>Mac Keycap: the key at 13 points semibold, 8 points of padding, at least 28 by 28 points.</summary>
    private static FrameworkElement Keycap(string text)
    {
        TextBlock label = SettingsText.Label(text, 13, "AnnotatorPrimaryText", FontWeights.SemiBold);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(8, 0, 8, 0);
        var cap = new Grid { MinWidth = Size, MinHeight = Size, IsHitTestVisible = false };
        // The Mac's .shadow(color: .black.opacity(0.08), radius: 0, y: 2): the keycap's shape again, 2 points lower.
        cap.Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = SettingsColors.Black(0.08), Margin = new Thickness(0, 2, 0, -2) });
        cap.Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = SettingsColors.Window });
        // A centered 1-point stroke reaches half a point outside the keycap, as SwiftUI's stroke does.
        cap.Children.Add(new Rectangle
        {
            RadiusX = 6.5,
            RadiusY = 6.5,
            Stroke = SettingsColors.Primary(0.18),
            StrokeThickness = 1,
            Margin = new Thickness(-0.5),
        });
        cap.Children.Add(label);
        ToolTipService.SetToolTip(cap, text);
        return cap;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed partial class Peer(ShortcutKeycaps owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;

        protected override string GetLocalizedControlTypeCore() => "shortcut";

        protected override string GetClassNameCore() => "ShortcutKeycaps";

        // Inside a shortcut button the keycaps are only its look (AccessibilityView.Raw); the button reads the keys.
        protected override bool IsControlElementCore() => AutomationProperties.GetAccessibilityView(owner) != AccessibilityView.Raw;

        protected override bool IsContentElementCore() => AutomationProperties.GetAccessibilityView(owner) == AccessibilityView.Content;

        protected override IList<AutomationPeer> GetChildrenCore() => new AutomationPeerList();

        /// <summary>The keycaps with their strokes' outer half point, as the Mac reports them.</summary>
        protected override WinRect GetBoundingRectangleCore() =>
            AutomationFrames.Of(owner, new WinRect(-0.5, -0.5, owner.ActualWidth + 1, owner.ActualHeight + 1), base.GetBoundingRectangleCore());
    }
}

/// <summary>
/// Port of KeyChip: the clickable keycap under a tool or button. Click it, then press a key. It shows the key, "Set" with a
/// dashed outline when there is none, or "Press a key" in the accent while it listens, with the listening pulse. A new key
/// makes it bounce, a refused one shakes it, and it grows a little under the pointer (ADR 0012).
/// </summary>
internal sealed class KeyChip
{
    private readonly bool _compact;
    private readonly Grid _surface = new() { IsHitTestVisible = false };
    private readonly Border _shadow = new() { CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 1, 0, -1) };
    private readonly Border _fill = new() { CornerRadius = new CornerRadius(5) };
    private readonly Rectangle _outline = new() { RadiusX = 5, RadiusY = 5 };
    private readonly TextBlock _text;
    private readonly string _name;
    private LocalShortcut? _shortcut;
    private bool _shown;
    private bool _recording;
    private bool _hovering;
    private IDisposable? _pulse;

    public KeyChip(RecordingTarget target, string name, bool compact, Action<RecordingTarget> toggle)
    {
        Target = target;
        _name = name;
        _compact = compact;
        _text = SettingsText.Label(string.Empty, compact ? 10 : 11.5, "AnnotatorPrimaryText", FontWeights.SemiBold);
        _text.HorizontalAlignment = HorizontalAlignment.Center;
        _text.VerticalAlignment = VerticalAlignment.Center;
        _surface.MinWidth = compact ? 22 : 28;
        _surface.MinHeight = compact ? 17 : 22;
        _surface.Children.Add(_shadow);
        _surface.Children.Add(_fill);
        _surface.Children.Add(_outline);
        _surface.Children.Add(_text);
        Button = new ParityButton
        {
            Style = AppResources.Style("AnnotatorMenuRowButton"),
            CornerRadius = new CornerRadius(5),
            Content = _surface,
            // A click keeps the keyboard where it is, so the key that records keeps listening (Mac buttons take no focus).
            AllowFocusOnInteraction = false,
            UseSystemFocusVisuals = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Button.Resources["AnnotatorMenuHoverBrush"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        if (!SettingsText.Scaled)
        {
            // Exactly the Mac's height for the stacks that place the key; as a minimum it rounds to a pixel less at 125%.
            Button.Height = _surface.MinHeight;
        }
        AutomationProperties.SetName(Button, $"{name} key");
        AutomationProperties.SetHelpText(Button, ShortcutPage.ChipHint);
        Button.Click += (_, _) => toggle(target);
        Button.PointerEntered += (_, _) => Hover(true);
        Button.PointerExited += (_, _) => Hover(false);
    }

    public RecordingTarget Target { get; }

    public ParityButton Button { get; }

    /// <summary>Shows the key, or the listening state, as the editor and the profile say.</summary>
    public void Show(LocalShortcut? shortcut, bool recording)
    {
        bool changed = _shown && shortcut != _shortcut;
        bool startsRecording = recording && !_recording;
        bool endsRecording = !recording && _recording;
        _shortcut = shortcut;
        _recording = recording;
        _shown = true;
        bool none = shortcut is null;
        string label = recording ? (_compact ? "Press" : "Press a key") : shortcut is { } key ? WindowsShortcuts.Label(key) : "Set";
        _text.Text = label;
        _text.Style = AppResources.Style(recording ? "AnnotatorAccentText" : none ? "AnnotatorSecondaryText" : "AnnotatorPrimaryText");
        double padding = _compact ? 4 : 6;
        // SF Pro's "Set" is wider than Segoe UI's; the Mac's width keeps the chip's frame.
        double width = label == "Set" ? (_compact ? 17 : 19) : SettingsText.Width(label, _compact ? 10 : 11.5, FontWeights.SemiBold);
        _surface.Width = Math.Max(_surface.MinWidth, Math.Ceiling((width + padding * 2) * 2) / 2);
        _fill.Background = recording ? SettingsColors.AccentBrush(0.14)
            : none ? SettingsColors.Primary(_hovering ? 0.06 : 0)
            : SettingsColors.ControlBackground;
        _outline.Stroke = recording ? SettingsColors.AccentBrush() : SettingsColors.Primary(none ? 0.25 : 0.16);
        _outline.StrokeThickness = recording ? 1.5 : 1;
        _outline.StrokeDashArray = none && !recording ? [3, 2] : null;
        _shadow.Background = none || recording ? null : SettingsColors.Black(0.1);
        Button.AccessibleValue = recording ? "Waiting for a key" : ShortcutPage.ChipValue(shortcut);
        ToolTipService.SetToolTip(Button, ShortcutPage.ChipHelp(_name, shortcut));
        if (startsRecording)
        {
            StartPulse();
            Scale(1, animate: false);
        }
        if (endsRecording)
        {
            StopPulse();
        }
        if (changed && shortcut is not null && ShortcutEditorMotion.ChipBounce(PageMotion.ReducesMotion) is { } bounce)
        {
            PageMotion.Scale(_surface, bounce, value => value);
        }
    }

    /// <summary>The refused key's damped shake (5 points of travel).</summary>
    public void Shake()
    {
        if (ShortcutEditorMotion.Shake(5, PageMotion.ReducesMotion) is { } shake)
        {
            PageMotion.Shake(_surface, shake);
        }
    }

    /// <summary>Stops the pulse when the page goes away.</summary>
    public void Detach() => StopPulse();

    private void Hover(bool hovering)
    {
        _hovering = hovering;
        if (_shortcut is null && !_recording)
        {
            _fill.Background = SettingsColors.Primary(hovering ? 0.06 : 0);
        }
        if (!_recording)
        {
            Scale(hovering ? ShortcutEditorMotion.HoverScale : 1, animate: true);
        }
    }

    private void Scale(double target, bool animate)
    {
        double from = _surface.Scale.X;
        if (from == target)
        {
            return;
        }
        if (animate && ShortcutEditorMotion.Hover(PageMotion.ReducesMotion) is { } progress)
        {
            PageMotion.Scale(_surface, progress, share => from + (target - from) * share);
            return;
        }
        _surface.CenterPoint = new Vector3((float)(_surface.ActualWidth / 2), (float)(_surface.ActualHeight / 2), 0);
        _surface.Scale = new Vector3((float)target, (float)target, 1);
    }

    private void StartPulse()
    {
        if (!PageMotion.ReducesMotion)
        {
            _pulse = PageMotion.Pulse(Button, _surface, 5);
        }
    }

    private void StopPulse()
    {
        _pulse?.Dispose();
        _pulse = null;
    }
}

/// <summary>
/// Port of GlobalKeyButton: the keycaps of a shortcut that works in any app, at least 120 points wide and trailing. While it
/// listens it shows the held modifiers ("Ctrl + Shift + key") in an accent capsule with the listening pulse. A new
/// shortcut makes it bounce, a refused one shakes it. UI Automation reads a button named "Draw shortcut" with the spoken
/// keys as its value and the identifier "global-shortcut-draw".
/// </summary>
internal sealed class GlobalKeyButton
{
    private readonly Grid _surface = new() { MinWidth = 120, IsHitTestVisible = false };
    private readonly ShortcutKeycaps _keycaps;
    private readonly Grid _capsule = new() { MinWidth = 120, MinHeight = 30, Visibility = Visibility.Collapsed };
    private readonly TextBlock _live;
    private ActivationShortcut _shortcut;
    private bool _recording;
    private IDisposable? _pulse;

    public GlobalKeyButton(GlobalShortcutKind kind, ActivationShortcut shortcut, Action<RecordingTarget> toggle)
    {
        Kind = kind;
        _shortcut = shortcut;
        _keycaps = new ShortcutKeycaps(shortcut) { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAccessibilityView(_keycaps, AccessibilityView.Raw);
        _live = SettingsText.Label("Press keys", 12, "AnnotatorAccentText", FontWeights.SemiBold);
        _live.HorizontalAlignment = HorizontalAlignment.Center;
        _live.Margin = new Thickness(12, 0, 12, 0);
        _capsule.Children.Add(new Border { CornerRadius = new CornerRadius(7), Background = SettingsColors.AccentBrush(0.14) });
        _capsule.Children.Add(new Rectangle { RadiusX = 7, RadiusY = 7, Stroke = SettingsColors.AccentBrush(), StrokeThickness = 1.5 });
        _capsule.Children.Add(_live);
        _surface.Children.Add(_keycaps);
        _surface.Children.Add(_capsule);
        Button = new ParityButton
        {
            Style = AppResources.Style("AnnotatorMenuRowButton"),
            CornerRadius = new CornerRadius(7),
            Content = _surface,
            AllowFocusOnInteraction = false,
            UseSystemFocusVisuals = true,
            VerticalAlignment = VerticalAlignment.Center,
            AccessibleValue = WindowsShortcuts.Spoken(shortcut),
        };
        Button.Resources["AnnotatorMenuHoverBrush"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        AutomationProperties.SetName(Button, $"{kind.Title} shortcut");
        AutomationProperties.SetAutomationId(Button, "global-shortcut-" + kind.RawValue);
        AutomationProperties.SetHelpText(Button, ShortcutPage.GlobalHint);
        ToolTipService.SetToolTip(Button, ShortcutPage.GlobalHelp(kind));
        Button.Click += (_, _) => toggle(RecordingTarget.Global(kind));
    }

    public GlobalShortcutKind Kind { get; }

    public ParityButton Button { get; }

    /// <summary>Shows the shortcut, or the held modifiers while it listens; unavailable while its feature is not built.</summary>
    public void Show(ActivationShortcut shortcut, bool recording, string liveModifiers, bool available)
    {
        bool changed = shortcut != _shortcut;
        bool startsRecording = recording && !_recording;
        bool endsRecording = !recording && _recording;
        _shortcut = shortcut;
        _recording = recording;
        _keycaps.Shortcut = shortcut;
        _keycaps.Visibility = recording ? Visibility.Collapsed : Visibility.Visible;
        _capsule.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        _live.Text = liveModifiers;
        Button.IsEnabled = available;
        _surface.Opacity = available ? 1 : 0.5;
        Button.AccessibleValue = recording ? "Waiting for keys" : WindowsShortcuts.Spoken(shortcut);
        if (startsRecording && !PageMotion.ReducesMotion)
        {
            _pulse = PageMotion.Pulse(Button, _capsule, 7);
        }
        if (endsRecording)
        {
            StopPulse();
        }
        if (changed && ShortcutEditorMotion.ButtonBounce(PageMotion.ReducesMotion) is { } bounce)
        {
            PageMotion.Scale(_surface, bounce, value => value);
        }
    }

    /// <summary>The refused shortcut's damped shake (6 points of travel).</summary>
    public void Shake()
    {
        if (ShortcutEditorMotion.Shake(6, PageMotion.ReducesMotion) is { } shake)
        {
            PageMotion.Shake(_surface, shake);
        }
    }

    public void Detach() => StopPulse();

    private void StopPulse()
    {
        _pulse?.Dispose();
        _pulse = null;
    }
}
