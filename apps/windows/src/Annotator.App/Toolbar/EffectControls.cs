using Annotator.App.Shell;
using Annotator.Core;
using Annotator.Workspace;
using Annotator.Workspace.Toolbar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Glyphs = Annotator.App.Icons.Glyphs;

namespace Annotator.App.Toolbar;

/// <summary>
/// Port of CoverPropertiesView: the effect choice and its strength, or the note for a selected solid cover. The quick
/// properties and the full properties panel show it for every effect and cover (Mac <c>effectiveTool.isProtection</c>).
/// </summary>
internal static class EffectControls
{
    /// <summary>Builds the controls; <paramref name="strength"/> makes StyleValueControl(.effect) at a slider width and with or without its caption.</summary>
    public static FrameworkElement CoverProperties(WorkspaceModel model, Tool tool, bool hasSelection, bool compact, bool showsSlider,
                                                   Func<double, bool, FrameworkElement> strength, List<Action<WorkspaceSnapshot>> updates,
                                                   Action interacted)
    {
        var row = new MacStack { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        if (tool == Tool.Cover && hasSelection)
        {
            TextBlock note = PropertyMetrics.Text("Solid cover · 100% opaque", 10, "AnnotatorSecondaryText");
            ToolbarParts.SetToolTip(note, "This existing cover stays opaque. Use Rectangle for new solid blocks.");
            row.Children.Add(note);
            return row;
        }
        var picker = new EffectPicker(model, interacted) { Width = compact ? 100 : 120 };
        updates.Add(picker.Update);
        row.Children.Add(picker);
        if (tool.IsPixelEffect)
        {
            row.Children.Add(strength(showsSlider ? (compact ? 64 : 90) : 0, !compact));
        }
        return row;
    }
}

/// <summary>
/// Port of NativeEffectPicker: a small pop-up button, 20 points high, that shows the effect and offers Blur, Pixelate and
/// Distort. It shows the selected region's effect, else the last one chosen. UI Automation reads it as the Mac's
/// AXPopUpButton: a combo box named "Effect" with the identifier "cover-mode" whose value is the effect shown. A choice
/// changes the selected regions, else the tool (Mac setCoverMode).
/// </summary>
internal sealed partial class EffectPicker : Button
{
    private static readonly Tool[] s_effects = [Tool.Blur, Tool.Pixelate, Tool.Distort];
    private readonly WorkspaceModel _model;
    private readonly Action _interacted;
    private readonly TextBlock _title;

    public EffectPicker(WorkspaceModel model, Action interacted)
    {
        _model = model;
        _interacted = interacted;
        Style = AppResources.Style("AnnotatorPopUpButton");
        Height = 20;
        VerticalAlignment = VerticalAlignment.Center;
        _title = ToolbarParts.Label(Title(Shown), 11, "AnnotatorPrimaryText");
        _title.VerticalAlignment = VerticalAlignment.Center;
        _title.Margin = new Thickness(10.5, 0, 0, 0);
        // The Mac's up and down chevrons, 10 points high, centered 11 points from the right edge.
        FrameworkElement chevrons = Glyphs.ForSymbol("chevron.up.chevron.down", 15);
        chevrons.HorizontalAlignment = HorizontalAlignment.Right;
        chevrons.VerticalAlignment = VerticalAlignment.Center;
        chevrons.Margin = new Thickness(0, 0, 3.75, 0);
        var content = new Grid { IsHitTestVisible = false };
        content.Children.Add(_title);
        content.Children.Add(chevrons);
        Content = content;
        AutomationProperties.SetName(this, "Effect");
        AutomationProperties.SetAutomationId(this, "cover-mode");
        Click += (_, _) => Open();
    }

    /// <summary>The effect shown: the selected region's, else the last one chosen (Mac preferredEffectTool).</summary>
    public Tool Shown { get; private set; } = Tool.Blur;

    /// <summary>The Mac's item titles: the raw value capitalized.</summary>
    public static string Title(Tool effect) => InspectorPlan.Capitalized(effect.RawValue);

    public void Update(WorkspaceSnapshot snapshot)
    {
        Tool shown = snapshot.EffectiveTool.IsPixelEffect ? snapshot.EffectiveTool : WorkspaceModel.PreferredEffect(snapshot.Preferences);
        if (shown == Shown)
        {
            return;
        }
        string before = Title(Shown);
        Shown = shown;
        _title.Text = Title(shown);
        if (FrameworkElementAutomationPeer.FromElement(this) is AutomationPeer peer)
        {
            peer.RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, before, _title.Text);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    /// <summary>The menu of NSPopUpButton: the three effects, the shown one checked, below the button.</summary>
    private void Open()
    {
        _interacted();
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        foreach (Tool effect in s_effects)
        {
            var item = new ToggleMenuFlyoutItem { Text = Title(effect), IsChecked = effect == Shown };
            item.Click += (_, _) =>
            {
                _interacted();
                _model.SetCoverMode(effect);
            };
            menu.Items.Add(item);
        }
        menu.ShowAt(this);
    }

    private sealed partial class Peer(EffectPicker owner) : ButtonAutomationPeer(owner), IValueProvider
    {
        public bool IsReadOnly => true;

        public string Value => Title(owner.Shown);

        public void SetValue(string value) => throw new InvalidOperationException("Choose the effect from the picker's menu.");

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ComboBox;

        protected override string GetClassNameCore() => "ComboBox";

        protected override IList<AutomationPeer> GetChildrenCore() => new AutomationPeerList();

        protected override object? GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Value ? this : base.GetPatternCore(patternInterface);

        protected override Windows.Foundation.Rect GetBoundingRectangleCore() => AutomationFrames.Of(owner, base.GetBoundingRectangleCore());
    }
}
