// Port of the toolbar control tests in apps/macos/Tests/AnnotationCoreTests/PreferencesTests.swift and MotionCoreTests.swift.
using System.Text;
using Annotator.Core.Motion;

namespace Annotator.Core.Tests;

public class ToolbarControlTests
{
    [Fact]
    public void ToolbarControlsCanBeTurnedOffAndRoundTrip()
    {
        var preferences = new WorkspacePreferences();
        Assert.All(ToolbarControl.AllCases, control => Assert.True(preferences.ShowsToolbarControl(control)));
        preferences.SetToolbarControl(ToolbarControl.Undo, shown: false);
        preferences.SetToolbarControl(ToolbarControl.Library, shown: false);
        preferences.SetToolbarControl(ToolbarControl.Undo, shown: false);
        Assert.Equal(["library", "undo"], preferences.HiddenToolbarControls!.Value.ToArray());
        Assert.False(preferences.ShowsToolbarControl(ToolbarControl.Undo));
        Assert.True(preferences.ShowsToolbarControl(ToolbarControl.Redo));
        preferences.ToolbarCollapseDelay = 5;
        Assert.Equal(preferences, WorkspacePreferences.Read(preferences.Data()));
        preferences.SetToolbarControl(ToolbarControl.Undo, shown: true);
        preferences.SetToolbarControl(ToolbarControl.Library, shown: true);
        Assert.Null(preferences.HiddenToolbarControls);
        Assert.Equal(ToolbarControl.AllCases.Count, ToolbarControl.AllCases.Select(control => control.Label).Distinct().Count());
        Assert.Equal(ShortcutAction.Board, ToolbarControl.Board.Action);
        Assert.Null(ToolbarControl.Undo.Action);
    }

    [Fact]
    public void ToolbarControlsFromANewerVersionAreDroppedWhenReading()
    {
        string json = Encoding.UTF8.GetString(new WorkspacePreferences().Data());
        string future = json.Insert(1, "\"hiddenToolbarControls\":[\"undo\",\"future\",\"undo\"],");
        Assert.Equal(["undo"], WorkspacePreferences.Read(Encoding.UTF8.GetBytes(future)).HiddenToolbarControls!.Value.ToArray());
        string unknown = json.Insert(1, "\"hiddenToolbarControls\":[\"future\"],");
        Assert.Null(WorkspacePreferences.Read(Encoding.UTF8.GetBytes(unknown)).HiddenToolbarControls);
    }

    [Fact]
    public void InvalidToolbarControlsAndCollapseDelaysAreRejected()
    {
        var preferences = new WorkspacePreferences { HiddenToolbarControls = ["undo", "undo"] };
        Assert.Throws<CanvasError>(() => preferences.Data());
        preferences.HiddenToolbarControls = ["dance"];
        Assert.Throws<CanvasError>(() => preferences.Data());
        preferences.HiddenToolbarControls = null;
        foreach (double delay in (double[])[0.5, 601, double.PositiveInfinity, double.NaN])
        {
            preferences.ToolbarCollapseDelay = delay;
            Assert.Throws<CanvasError>(() => preferences.Data());
        }
        foreach (double delay in WorkspacePreferences.ToolbarCollapseDelays)
        {
            preferences.ToolbarCollapseDelay = delay;
            _ = preferences.Data();
        }
    }

    [Fact]
    public void PropertiesCanShowAllTheirChoicesAndRoundTrip()
    {
        var preferences = new WorkspacePreferences();
        Assert.All(PropertyChoice.AllCases, choice => Assert.False(preferences.ShowsAllChoices(choice)));
        preferences.SetShowsAllChoices(PropertyChoice.Font, shown: true);
        preferences.SetShowsAllChoices(PropertyChoice.Pattern, shown: true);
        preferences.SetShowsAllChoices(PropertyChoice.Font, shown: true);
        Assert.Equal(["pattern", "font"], preferences.ExpandedPropertyChoices!.Value.ToArray());
        Assert.True(preferences.ShowsAllChoices(PropertyChoice.Pattern));
        Assert.Equal(preferences, WorkspacePreferences.Read(preferences.Data()));
        preferences.SetShowsAllChoices(PropertyChoice.Pattern, shown: false);
        preferences.SetShowsAllChoices(PropertyChoice.Font, shown: false);
        Assert.Null(preferences.ExpandedPropertyChoices);
        Assert.Equal([3, 3, 3, 2, 2, 2, 2, 3], PropertyChoice.AllCases.Select(choice => choice.ChoiceCount).ToArray());

        string json = Encoding.UTF8.GetString(new WorkspacePreferences().Data());
        string future = json.Insert(1, "\"expandedPropertyChoices\":[\"roughness\",\"sparkle\",\"roughness\"],");
        Assert.Equal(["roughness"], WorkspacePreferences.Read(Encoding.UTF8.GetBytes(future)).ExpandedPropertyChoices!.Value.ToArray());
        var invalid = new WorkspacePreferences { ExpandedPropertyChoices = ["font", "font"] };
        Assert.Throws<CanvasError>(() => invalid.Data());
        invalid.ExpandedPropertyChoices = ["sparkle"];
        Assert.Throws<CanvasError>(() => invalid.Data());
    }

    [Fact]
    public void SpotlightAndZoomMenusAreToolbarItemsWithoutKeys()
    {
        Assert.Equal([ShortcutAction.Spotlight, ShortcutAction.CursorHalo, ShortcutAction.PointerLens, ShortcutAction.SmartDraw,
                      ShortcutAction.ShareRegion, ShortcutAction.FocusMenu, ShortcutAction.ZoomMenu],
                     ShortcutAction.ToolbarChoices.ToArray());
        Assert.True(ShortcutAction.FocusMenu.IsMenu);
        Assert.False(ShortcutAction.Spotlight.IsMenu);
        var preferences = new WorkspacePreferences
        {
            ToolbarItems = ["tool.select", "action.focusMenu", "tool.pen", "action.zoomMenu"],
            ToolbarTools = ["select", "pen"],
        };
        Assert.Equal(preferences, WorkspacePreferences.Read(preferences.Data()));
        var error = Assert.Throws<CanvasError>(() =>
            preferences.Bind(LocalShortcut.Create("j"), ShortcutCommand.ForAction(ShortcutAction.ZoomMenu)));
        Assert.Contains("Zoom menu opens with a click, so it has no key. J stays free.", error.Message, StringComparison.Ordinal);
        Assert.Null(preferences.CommandShortcuts);
        preferences.CommandShortcuts = WorkspacePreferences.EmptyStrings.SetItem("j", "action.focusMenu");
        Assert.Throws<CanvasError>(() => preferences.Data());
    }

    [Fact]
    public void ToolbarMorphHandsOverBetweenFaces()
    {
        Assert.Equal(0, ToolbarMorph.Shape(0));
        Assert.Equal(1, ToolbarMorph.Shape(1));
        Assert.Equal(1, ToolbarMorph.OutgoingOpacity(0));
        Assert.Equal(0, ToolbarMorph.OutgoingOpacity(ToolbarMorph.OutgoingEnd));
        Assert.Equal(0, ToolbarMorph.IncomingOpacity(ToolbarMorph.IncomingStart));
        Assert.Equal(1, ToolbarMorph.IncomingOpacity(1));
        double middle = (ToolbarMorph.IncomingStart + ToolbarMorph.OutgoingEnd) / 2;
        Assert.True(ToolbarMorph.OutgoingOpacity(middle) > 0);
        Assert.True(ToolbarMorph.IncomingOpacity(middle) > 0);
        Assert.True(MotionTiming.ToolbarResizeDuration < MotionTiming.ToolbarCollapseDuration);
        Assert.True(MotionTiming.ToolbarCollapseDuration < MotionTiming.ToolbarExpandDuration);
    }
}
