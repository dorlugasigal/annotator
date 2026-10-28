using Annotator.Core;
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
        double full = ToolbarToolLayout.HorizontalWidth(items.AsSpan(), 1400, propertiesVisible: false);
        Assert.Equal(932, full);
        Assert.Equal(full, ToolbarToolLayout.HorizontalWidth(items.AsSpan(), 1400, propertiesVisible: false, hidden: None));
        var all = new HashSet<ToolbarControl>(ToolbarControl.AllCases);
        double bare = ToolbarToolLayout.HorizontalWidth(items.AsSpan(), 1400, propertiesVisible: false, hidden: all);
        Assert.True(bare < full - 300, $"{bare}");
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
        model.SetInspectorPinned(true);
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
    public async Task CollapseWaitsForTheDelayAndHoverOpens()
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
        await WaitFor(() => expansions == 1);
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
