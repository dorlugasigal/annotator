using Annotator.Core;
using Annotator.Workspace.Settings;
using Annotator.Workspace.Toolbar;
using static Annotator.Workspace.Tests.TestWorkspace;

namespace Annotator.Workspace.Tests;

/// <summary>
/// Removable toolbar buttons and the toolbar that shrinks to one button (Mac ToolbarCustomizationTests): the model's
/// settings, the room hidden buttons give back, and when the toolbar shrinks and opens.
/// </summary>
public sealed class ToolbarCustomizationTests
{
    private static readonly HashSet<ToolbarControl> None = [];

    [Fact]
    public void EveryToolbarButtonCanBeTurnedOffAndBackOn()
    {
        WorkspaceModel model = Drawing();
        foreach (ToolbarControl control in ToolbarControl.AllCases)
        {
            model.SetToolbarControl(control, shown: false);
            Assert.Contains(control, model.Snapshot.HiddenToolbarControls);
        }
        Assert.Equal(ToolbarControl.AllCases.Count, model.Snapshot.HiddenToolbarControls.Count);
        model.SetToolbarControl(ToolbarControl.Undo, shown: true);
        Assert.DoesNotContain(ToolbarControl.Undo, model.Snapshot.HiddenToolbarControls);
        model.ShowAllToolbarControls();
        Assert.Empty(model.Snapshot.HiddenToolbarControls);
        Assert.Null(model.Snapshot.Preferences.HiddenToolbarControls);
    }

    [Fact]
    public void TurningButtonsOffGivesTheirRoomBack()
    {
        ValueList<ShortcutCommand> items = WorkspacePreferences.DefaultToolbar.Map(ShortcutCommand.ForTool);
        double full = ToolbarToolLayout.HorizontalWidth(items.AsSpan(), 1400);
        Assert.Equal(969, full);
        Assert.Equal(full, ToolbarToolLayout.HorizontalWidth(items.AsSpan(), 1400, hidden: None));
        var all = new HashSet<ToolbarControl>(ToolbarControl.AllCases);
        double bare = ToolbarToolLayout.HorizontalWidth(items.AsSpan(), 1400, hidden: all);
        Assert.True(bare < full - 300, $"{bare}");
        // Only the padding and the gap after the tools stay.
        Assert.Equal(ToolbarToolLayout.Width(items.AsSpan()) + 24 + 6, bare);
        var metrics = new ToolbarMetrics(ToolbarSize.Expanded);
        Assert.Equal(metrics.RegularReserve - 30, metrics.RegularReserveHiding(new HashSet<ToolbarControl> { ToolbarControl.Undo }));
        // Undo shares its row with Redo in the expanded rail.
        Assert.Equal(metrics.VerticalReserve, metrics.VerticalReserveHiding(new HashSet<ToolbarControl> { ToolbarControl.Undo }));
        Assert.True(metrics.VerticalReserveHiding(new HashSet<ToolbarControl> { ToolbarControl.Undo, ToolbarControl.Redo })
                    < metrics.VerticalReserve);
    }

    [Fact]
    public void PropertiesCanShowAllTheirChoices()
    {
        WorkspaceModel model = Drawing();
        model.SetPropertiesShown(true);
        model.Choose(Tool.Arrow);
        InspectorPlan one = InspectorPlan.For(model.Snapshot);
        Assert.Empty(one.AllChoices);
        Assert.Equal("Curved", one.ArrowTypeTitle);
        model.SetShowsAllChoices(PropertyChoice.ArrowType, shown: true);
        model.SetShowsAllChoices(PropertyChoice.Pattern, shown: true);
        InspectorPlan all = InspectorPlan.For(model.Snapshot);
        Assert.Equal([PropertyChoice.Pattern, PropertyChoice.ArrowType], all.AllChoices.ToArray());
        Assert.True(all.ShowsAllChoices(PropertyChoice.Pattern));
        Assert.Equal("Arrow type", all.ArrowTypeTitle);
        Assert.NotEqual(one.Key, all.Key);
        Assert.Equal(["Straight arrow", "Curved arrow"], InspectorPlan.ChoiceNames(PropertyChoice.ArrowType).ToArray());
        Assert.Equal(["System font", "Mono font"], InspectorPlan.ChoiceNames(PropertyChoice.Font).ToArray());
        Assert.Equal(["Align left", "Align center", "Align right"], InspectorPlan.ChoiceNames(PropertyChoice.Alignment).ToArray());
        model.ResetPropertyChoices();
        Assert.Null(model.Snapshot.Preferences.ExpandedPropertyChoices);
        Assert.Equal(one.Key, InspectorPlan.For(model.Snapshot).Key);
    }

    [Fact]
    public void SpotlightAndZoomMenusLightUpAndHaveNoKey()
    {
        WorkspaceModel model = Drawing();
        Assert.False(model.Snapshot.IsOn(ShortcutAction.FocusMenu));
        model.SetSpotlight(true);
        Assert.True(model.Snapshot.IsOn(ShortcutAction.FocusMenu));
        Assert.False(model.Snapshot.IsOn(ShortcutAction.ZoomMenu));
        Assert.False(model.PerformShortcutAction(ShortcutAction.ZoomMenu));
        Assert.DoesNotContain(ShortcutCommand.ForAction(ShortcutAction.FocusMenu), model.Snapshot.ToolbarItems);
    }

    [Fact]
    public void SettingsTakesButtonsOffAndPutsThemBack()
    {
        WorkspaceModel model = Drawing();
        IReadOnlyList<ShortcutCommand> order = [.. model.Snapshot.ToolbarItems];
        Assert.Equal("Undo moved to More tools. Its key still works.", Annotator.Workspace.Settings.ToolbarArranger.RemoveControl(model, ToolbarControl.Undo));
        Assert.Null(Annotator.Workspace.Settings.ToolbarArranger.RemoveControl(model, ToolbarControl.Undo));
        Assert.Equal([ToolbarControl.Undo], Annotator.Workspace.Settings.ToolbarArranger.HiddenControls(model.Snapshot.HiddenToolbarControls));
        Assert.Equal("toolbar-control-undo", Annotator.Workspace.Settings.ToolbarArranger.ControlIdentifier(ToolbarControl.Undo, inToolbar: true));
        Assert.Equal("more-control-undo", Annotator.Workspace.Settings.ToolbarArranger.ControlIdentifier(ToolbarControl.Undo, inToolbar: false));
        Assert.Equal("Drag one up to add it, or click its plus button.",
            Annotator.Workspace.Settings.ToolbarArranger.TrayHint(WorkspaceModel.ToolbarCandidates, model.Snapshot.HiddenToolbarControls));
        Assert.Equal("Undo is back in your toolbar.", Annotator.Workspace.Settings.ToolbarArranger.AddControl(model, ToolbarControl.Undo));
        Assert.Null(Annotator.Workspace.Settings.ToolbarArranger.AddControl(model, ToolbarControl.Undo));
        Assert.Empty(model.Snapshot.HiddenToolbarControls);
        Assert.Equal("Everything is in your toolbar.",
            Annotator.Workspace.Settings.ToolbarArranger.TrayHint(WorkspaceModel.ToolbarCandidates, model.Snapshot.HiddenToolbarControls));
        Assert.Equal(order, model.Snapshot.ToolbarItems.ToArray());
    }

    /// <summary>Mac testFewToolsKeepOneRowWithoutGaps: few tools hug one row; only a narrow screen takes two rows.</summary>
    [Fact]
    public void FewToolsKeepOneRowWithoutGaps()
    {
        ShortcutCommand[] few = [ShortcutCommand.ForTool(Tool.Select), ShortcutCommand.ForTool(Tool.Pen), ShortcutCommand.ForTool(Tool.Eraser)];
        Assert.False(ToolbarToolLayout.UsesTwoRows(few, 1400));
        Assert.Equal(ToolbarToolLayout.Width(few) + 383, ToolbarToolLayout.HorizontalWidth(few, 1400));
        Assert.Equal(ToolbarToolLayout.Width(few, ToolbarSize.Minimal) + 307, ToolbarToolLayout.HorizontalWidth(few, 1400, ToolbarSize.Minimal));
        ValueList<ShortcutCommand> items = WorkspacePreferences.DefaultToolbar.Map(ShortcutCommand.ForTool);
        // A screen a little too narrow keeps one row and shows fewer tools.
        Assert.False(ToolbarToolLayout.UsesTwoRows(items.AsSpan(), 880));
        Assert.Equal(880, ToolbarToolLayout.HorizontalWidth(items.AsSpan(), 880));
        // A narrow screen moves Undo, Redo and the other buttons to a second row.
        Assert.True(ToolbarToolLayout.UsesTwoRows(items.AsSpan(), 400));
        Assert.Equal(400, ToolbarToolLayout.HorizontalWidth(items.AsSpan(), 400));
        Assert.False(ToolbarToolLayout.UsesTwoRows(items.AsSpan(), 400, hidden: new HashSet<ToolbarControl>(ToolbarToolLayout.SecondRowControls)));
        var metrics = new ToolbarMetrics(ToolbarSize.Expanded);
        Assert.Equal(24 + 6 * 30, metrics.SecondRowWidth(None));
    }

    /// <summary>Mac testShrinkDemoTimeline: still first, shrinks after the pointer leaves, opens with no wait, loops.</summary>
    [Fact]
    public void ShrinkDemoTimeline()
    {
        Assert.Equal(ShrinkDemoPose.At(0), ShrinkDemoPose.At(ShrinkDemoPose.Leave - 0.01));
        Assert.Equal(0, ShrinkDemoPose.At(0).Shrink);
        Assert.Equal(ShrinkDemoPose.OnToolbarX, ShrinkDemoPose.At(0).PointerX);
        Assert.Equal(ShrinkDemoPose.Open, ShrinkDemoPose.At(ShrinkDemoPose.Collapse - 0.01));
        ShrinkDemoPose shrinking = ShrinkDemoPose.At((ShrinkDemoPose.Collapse + ShrinkDemoPose.Rest) / 2);
        Assert.InRange(shrinking.Shrink, 0.01, 0.99);
        Assert.Equal(ShrinkDemoPose.Shrunk, ShrinkDemoPose.At(ShrinkDemoPose.Back - 0.01));
        Assert.Equal(ShrinkDemoPose.OnToolbarX, ShrinkDemoPose.At(ShrinkDemoPose.Expand).PointerX);
        Assert.True(ShrinkDemoPose.At(ShrinkDemoPose.Expand + 0.05).Shrink < 1, "It opens with no wait");
        Assert.Equal(0, ShrinkDemoPose.At(ShrinkDemoPose.Opened + 0.01).Shrink);
        Assert.Equal(ShrinkDemoPose.At(0.3), ShrinkDemoPose.At(ShrinkDemoPose.Period + 0.3));
        Assert.Equal("Pick a time in Shrink and the toolbar tucks into one button when you leave it. Point at the button and your tools are back.",
            ShrinkDemoText.Caption(null));
        Assert.Equal("Leave the toolbar alone for 5 seconds and it shrinks to one button that shows Draw or Apps. Point at it and your tools are back.",
            ShrinkDemoText.Caption(5));
    }

    [Fact]
    public void PropertiesMakeTheToolbarWiderUpToTheScreen()
    {
        Assert.Equal(932, ToolbarToolLayout.GrownWidth(932, 1400, 850));
        Assert.Equal(1001, ToolbarToolLayout.GrownWidth(932, 1400, 1000.2));
        Assert.Equal(1200, ToolbarToolLayout.GrownWidth(932, 1200, 1500));
        // Never narrower than the tools need.
        Assert.Equal(932, ToolbarToolLayout.GrownWidth(932, 400, 1500));
    }

    [Fact]
    public void TheShrinkDelayIsSavedAndTurningItOffOpensTheToolbar()
    {
        WorkspaceModel model = Drawing();
        Assert.Null(model.Snapshot.Preferences.ToolbarCollapseDelay);
        model.SetToolbarCollapseDelay(5);
        Assert.Equal(5, model.Snapshot.Preferences.ToolbarCollapseDelay);
        model.SetToolbarCollapsed(true);
        Assert.True(model.Snapshot.ToolbarCollapsed);
        model.SetToolbarCollapseDelay(0.5);
        Assert.Equal(5, model.Snapshot.Preferences.ToolbarCollapseDelay);
        Assert.NotNull(model.Snapshot.ErrorMessage);
        model.SetToolbarCollapseDelay(null);
        Assert.False(model.Snapshot.ToolbarCollapsed);
    }

    [Fact]
    public void HidingTheToolsOpensTheToolbarForNextTime()
    {
        WorkspaceModel model = Drawing();
        model.SetToolbarCollapseDelay(3);
        model.ShowToolbar();
        model.SetToolbarCollapsed(true);
        Assert.True(model.Snapshot.ToolbarCollapsed);
        model.HideTools();
        while (model.Snapshot.Visibility is { } transition)
        {
            model.CompleteVisibilityTransition(transition.Serial);
        }
        Assert.False(model.Snapshot.ToolbarVisible);
        Assert.False(model.Snapshot.ToolbarCollapsed);
    }

    [Fact]
    public async Task CollapseWaitsForTheDelayAndPointingOpens()
    {
        double? delay = null;
        bool collapsed = false;
        var timer = new ToolbarCollapseTimer(() => delay, () => collapsed)
        {
            Wait = static (_, token) => Task.Delay(20, token),
        };
        bool held = false;
        int collapses = 0, expansions = 0;
        timer.IsHeldOpen = () => held;
        timer.Collapse = () => { collapses++; collapsed = true; };
        timer.Expand = () => { expansions++; collapsed = false; };

        timer.Restart();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(0, collapses);

        delay = 3;
        held = true;
        timer.Restart();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(0, collapses);
        Assert.True(timer.IsWaitingToCollapse);
        held = false;
        await WaitFor(() => collapses == 1);
        Assert.True(collapsed);

        timer.PointerChanged(inside: true);
        // Pointing at the button opens the toolbar at once, with no wait.
        Assert.Equal(1, expansions);
        Assert.False(collapsed);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(1, collapses);
        timer.PointerChanged(inside: false);
        await WaitFor(() => collapses == 2);
        timer.Cancel();
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.True(condition());
    }
}
