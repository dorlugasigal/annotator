// Port of apps/macos/Tests/AnnotationCoreTests/PreferencesTests.swift.
using System.Globalization;
using static Annotator.Core.Tests.TestSupport;

namespace Annotator.Core.Tests;

public class PreferencesTests
{
    [Fact]
    public void PreferencesRoundTripAndRejectConflicts()
    {
        var preferences = new WorkspacePreferences();
        var style = new Style();
        style.Width = 11;
        style.Color = Palette.Blue;
        preferences.ToolStyles = preferences.ToolStyles.SetItem("pen", style);
        preferences.AssignShortcut("v", Tool.Pen);
        Assert.False(preferences.ToolShortcuts.ContainsKey("7"));
        Assert.Equal("pen", preferences.ToolShortcuts["v"]);
        var beforeConflict = preferences;
        var candidate = preferences;
        Assert.Throws<CanvasError>(() => candidate.AssignShortcut("1", Tool.Pen));
        Assert.Equal(beforeConflict, candidate);
        Assert.Equal(preferences, WorkspacePreferences.Read(preferences.Data()));
    }

    [Theory]
    [InlineData(ToolCursorStyle.ToolPreview)]
    [InlineData(ToolCursorStyle.Crosshair)]
    [InlineData(ToolCursorStyle.System)]
    public void LegacyDrawingCursorPreferencesStillRoundTrip(ToolCursorStyle style)
    {
        var preferences = new WorkspacePreferences();
        preferences.CursorStyle = style;
        Assert.Equal(style, WorkspacePreferences.Read(preferences.Data()).CursorStyle);
    }

    [Fact]
    public void NumericDefaultsReserveColorsAndMigrateLegacyProfiles()
    {
        var defaults = new WorkspacePreferences();
        Tool[] ordered = [Tool.Select, Tool.Rectangle, Tool.Diamond, Tool.Ellipse, Tool.Arrow, Tool.Line, Tool.Pen, Tool.Text, Tool.Highlighter, Tool.Eraser];
        for (var index = 0; index < ordered.Length; index++)
        {
            Assert.Equal(ordered[index].RawValue, defaults.ToolShortcuts[((index + 1) % 10).ToString(CultureInfo.InvariantCulture)]);
        }
        foreach (var key in InkShortcut.Choices.Keys)
        {
            var preferences = defaults;
            Assert.Throws<CanvasError>(() => preferences.AssignShortcut(key, Tool.Pen));
            Assert.Equal(defaults, preferences);
        }
        var json = JsonObjectOf(defaults.Data());
        json.Remove("shortcutVersion");
        json["toolShortcuts"] = JsonObjectOf(WorkspacePreferences.LegacyToolShortcuts);
        var migrated = WorkspacePreferences.Read(Bytes(json));
        Assert.Equal(defaults.ToolShortcuts, migrated.ToolShortcuts);
        var custom = WorkspacePreferences.LegacyToolShortcuts.Remove("p").SetItem("j", "pen");
        json["toolShortcuts"] = JsonObjectOf(custom);
        var customMigration = WorkspacePreferences.Read(Bytes(json));
        Assert.Equal("pen", customMigration.ToolShortcuts["j"]);
        Assert.False(customMigration.ToolShortcuts.ContainsKey("7"));
        Assert.Equal(customMigration, WorkspacePreferences.Read(customMigration.Data()));
    }

    [Fact]
    public void GlobalShortcutMustBeDeliberateAndPreferencesValidated()
    {
        Assert.Throws<CanvasError>(() => new ActivationShortcut(0, "a", shift: true).Validate());
        var preferences = new WorkspacePreferences();
        preferences.SpotlightRadius = double.NaN;
        var invalidRadius = preferences;
        Assert.Throws<CanvasError>(() => invalidRadius.Data());
        preferences = new WorkspacePreferences();
        preferences.ToolStyles = preferences.ToolStyles.SetItem("unrecognized", new Style());
        var unknownTool = preferences;
        Assert.Throws<CanvasError>(() => unknownTool.Data());
        Assert.Equal("⌃2", ActivationShortcut.Standard.Label);
    }

    [Fact]
    public void ToolbarAndHideShortcutValidationPreserveRecoveryPaths()
    {
        var preferences = new WorkspacePreferences();
        preferences.HideShortcut = preferences.ActivationShortcut;
        var sameShortcut = preferences;
        Assert.Throws<CanvasError>(() => sameShortcut.Validate());
        preferences = new WorkspacePreferences();
        preferences.ToolbarTools = Strings("pen", "blur");
        var withoutSelect = preferences;
        Assert.Throws<CanvasError>(() => withoutSelect.Validate());
        preferences.ToolbarTools = Strings("select", "pen", "pen");
        var duplicated = preferences;
        Assert.Throws<CanvasError>(() => duplicated.Validate());
        preferences.ToolbarTools = Strings("select", "blur", "image");
        preferences.ToolbarOrientation = ToolbarOrientation.Vertical;
        preferences.CursorStyle = ToolCursorStyle.ToolPreview;
        Assert.Equal(preferences, WorkspacePreferences.Read(preferences.Data()));
    }
}
