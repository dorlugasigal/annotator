// Port of apps/macos/Tests/AnnotationCoreTests/ResumeShortcutTests.swift.
using System.Text.Json.Nodes;
using static Annotator.Core.Tests.TestSupport;

namespace Annotator.Core.Tests;

public class ResumeShortcutTests
{
    [Fact]
    public void ResumeShortcutRoundTripsAndRejectsPhysicalKeyConflicts()
    {
        var preferences = new WorkspacePreferences();
        Assert.Equal("⌃⇧2", (preferences.ResumeShortcut ?? ActivationShortcut.ResumeDrawing).Label);
        preferences.ResumeShortcut = new ActivationShortcut(15, "r", control: true, shift: true);
        Assert.Equal(preferences, WorkspacePreferences.Read(preferences.Data()));
        var bindings = new[]
        {
            preferences.ActivationShortcut, preferences.HideShortcut ?? ActivationShortcut.HideTools,
            preferences.ZoomShortcut ?? ActivationShortcut.LiveZoom,
        };
        foreach (var binding in bindings)
        {
            var renamed = binding;
            renamed.Character = "X";
            preferences.ResumeShortcut = renamed;
            var candidate = preferences;
            Assert.Throws<CanvasError>(() => candidate.Validate());
        }
    }

    [Fact]
    public void OldProfilesKeepExistingShortcutsWhenResumeDefaultIsTaken()
    {
        var defaults = new WorkspacePreferences();
        Assert.Null(WorkspacePreferences.Read(defaults.Data()).ResumeShortcut);
        var json = JsonObjectOf(defaults.Data());
        ActivationShortcut[] existing =
        [
            ActivationShortcut.ResumeDrawing,
            new(19, "x", control: true, option: true, shift: true),
            new(19, "y", control: true, command: true, shift: true),
        ];
        string[] fields = ["activationShortcut", "hideShortcut", "zoomShortcut"];
        for (var index = 0; index < fields.Length; index++)
        {
            json[fields[index]] = JsonNode.Parse(AnnotatorJson.Encode(existing[index]));
        }
        var migrated = WorkspacePreferences.Read(Bytes(json));
        Assert.Equal(existing[0], migrated.ActivationShortcut);
        Assert.Equal(existing[1], migrated.HideShortcut);
        Assert.Equal(existing[2], migrated.ZoomShortcut);
        var resume = Assert.NotNull(migrated.ResumeShortcut);
        Assert.DoesNotContain(existing, shortcut => shortcut.UsesSameKeys(resume));
        Assert.Equal(migrated, WorkspacePreferences.Read(migrated.Data()));
    }
}
