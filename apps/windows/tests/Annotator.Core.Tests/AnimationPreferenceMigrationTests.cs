// Port of apps/macos/Tests/AnnotationCoreTests/AnimationPreferenceMigrationTests.swift.
using static Annotator.Core.Tests.TestSupport;

namespace Annotator.Core.Tests;

public class AnimationPreferenceMigrationTests
{
    [Fact]
    public void RejectedFluidPreferenceMigratesToAnchoredSlideWithoutChangingOtherSettings()
    {
        var preferences = new WorkspacePreferences();
        preferences.VisibilityAnimationStyle = VisibilityAnimationStyle.Fluid;
        preferences.AnimateVisibility = true;
        preferences.ToolbarOrientation = ToolbarOrientation.Vertical;
        var restored = WorkspacePreferences.Read(AnnotatorJson.Encode(preferences));
        Assert.Equal(VisibilityAnimationStyle.Slide, restored.VisibilityAnimationStyle);
        Assert.Equal(ToolbarOrientation.Vertical, restored.ToolbarOrientation);
        Assert.True(restored.AnimateVisibility == true);
        preferences.VisibilityAnimationStyle = VisibilityAnimationStyle.None;
        preferences.AnimateVisibility = false;
        var disabled = WorkspacePreferences.Read(AnnotatorJson.Encode(preferences));
        Assert.True(disabled.AnimateVisibility == false);
        Assert.Equal(VisibilityAnimationStyle.None, disabled.VisibilityAnimationStyle);
    }

    [Theory]
    [InlineData(AppTheme.System)]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void ThemePreferenceRoundTripsWithoutChangingInk(AppTheme theme)
    {
        var preferences = new WorkspacePreferences();
        preferences.Theme = theme;
        preferences.ToolStyles = preferences.ToolStyles.SetItem("pen", Style.Defaults(Tool.Pen));
        var restored = WorkspacePreferences.Read(preferences.Data());
        Assert.Equal(preferences, restored);
        Assert.Equal(theme, restored.Theme);
    }

    [Fact]
    public void AnimationPickerHasDistinctEffectsAndOffWithoutBreakingOldProfiles()
    {
        Assert.Equal(
            new[]
            {
                VisibilityAnimationStyle.Dissolve, VisibilityAnimationStyle.Slide, VisibilityAnimationStyle.Magic,
                VisibilityAnimationStyle.Corners, VisibilityAnimationStyle.Pop, VisibilityAnimationStyle.Drop,
                VisibilityAnimationStyle.Focus, VisibilityAnimationStyle.Fade, VisibilityAnimationStyle.None,
            },
            VisibilityAnimationStyle.Available);
        foreach (var added in new[] { VisibilityAnimationStyle.Pop, VisibilityAnimationStyle.Drop, VisibilityAnimationStyle.Focus })
        {
            Assert.Equal(added, added.Resolved);
        }
        Assert.Equal(VisibilityAnimationStyle.Magic, VisibilityAnimationStyle.Magic.Resolved);
        Assert.Equal(VisibilityAnimationStyle.Slide, VisibilityAnimationStyle.Quick.Resolved);
        foreach (var old in new[] { VisibilityAnimationStyle.Reveal, VisibilityAnimationStyle.Curtain, VisibilityAnimationStyle.Fold, VisibilityAnimationStyle.Rise })
        {
            Assert.Equal(VisibilityAnimationStyle.Fade, old.Resolved);
        }
        Assert.Equal("slide", VisibilityAnimationStyle.Slide.RawValue);
    }

    [Fact]
    public void OldProfilesUseSystemThemeAndStillMigrateFluid()
    {
        var preferences = new WorkspacePreferences();
        preferences.VisibilityAnimationStyle = VisibilityAnimationStyle.Fluid;
        var json = JsonObjectOf(preferences.Data());
        json.Remove("theme");
        var migrated = WorkspacePreferences.Read(Bytes(json));
        Assert.Null(migrated.Theme);
        Assert.Equal(AppTheme.System, migrated.Theme ?? AppTheme.System);
        Assert.Equal(VisibilityAnimationStyle.Slide, migrated.VisibilityAnimationStyle);
        json["theme"] = "unsupported";
        Assert.Throws<DecodingError>(() => WorkspacePreferences.Read(Bytes(json)));
    }

    [Theory]
    [InlineData(VisibilityAnimationStyle.Reveal)]
    [InlineData(VisibilityAnimationStyle.Curtain)]
    [InlineData(VisibilityAnimationStyle.Fold)]
    [InlineData(VisibilityAnimationStyle.Rise)]
    [InlineData(VisibilityAnimationStyle.Magic)]
    [InlineData(VisibilityAnimationStyle.Corners)]
    [InlineData(VisibilityAnimationStyle.Pop)]
    [InlineData(VisibilityAnimationStyle.Drop)]
    [InlineData(VisibilityAnimationStyle.Focus)]
    public void ContainedAnimationPreferencesRoundTrip(VisibilityAnimationStyle style)
    {
        var preferences = new WorkspacePreferences();
        preferences.VisibilityAnimationStyle = style;
        preferences.AnimateVisibility = true;
        Assert.Equal(preferences, WorkspacePreferences.Read(preferences.Data()));
    }
}
