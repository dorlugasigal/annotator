using System.Text.Json;
using Annotator.Core;
using Annotator.Core.Motion;
using Annotator.Workspace.Library;
using Annotator.Workspace.Pointer;
using Annotator.Workspace.Toolbar;

namespace Annotator.Workspace.Tests.Pointer;

/// <summary>
/// Spotlight and Cursor halo in the workspace and the timing the overlays follow: ports of the Mac's
/// PointerOverlayInvalidationTests, CursorHighlightWorkflowTests and the WorkspaceModel pointer rules, with a made-up clock.
/// </summary>
public sealed class PointerEffectsTests
{
    private const double Tolerance = 1e-4;

    [Fact]
    public void ButtonsTurnEffectsOnAndOffWithTheMacStatus()
    {
        WorkspaceModel model = TestWorkspace.Drawing();
        Assert.True(WorkspaceModel.IsAvailable(ShortcutAction.Spotlight));
        Assert.True(WorkspaceModel.IsAvailable(ShortcutAction.CursorHalo));
        Assert.True(WorkspaceModel.IsAvailable(ShortcutAction.PointerLens));
        Assert.False(model.Snapshot.IsOn(ShortcutAction.Spotlight));

        Assert.True(model.PerformShortcutAction(ShortcutAction.Spotlight));
        Assert.True(model.Snapshot.Spotlight);
        Assert.True(model.Snapshot.IsOn(ShortcutAction.Spotlight));
        Assert.Equal("Spotlight is on.", model.Snapshot.Status);
        Assert.True(model.PerformShortcutAction(ShortcutAction.CursorHalo));
        Assert.True(model.Snapshot.IsOn(ShortcutAction.CursorHalo));
        Assert.Equal("Cursor halo is on.", model.Snapshot.Status);
        // Drawing goes on: the effects never change the mode.
        Assert.Equal(WorkspaceMode.Draw, model.Snapshot.Mode);

        Assert.True(model.PerformShortcutAction(ShortcutAction.Spotlight));
        Assert.False(model.Snapshot.Spotlight);
        Assert.Equal("Spotlight is off.", model.Snapshot.Status);
        Assert.True(model.PerformShortcutAction(ShortcutAction.CursorHalo));
        Assert.Equal("Cursor halo is off.", model.Snapshot.Status);
    }

    [Fact]
    public void AnEffectWithoutASessionOpensOneInUseApps()
    {
        // Mac updatePointerEffects: beginDrawing(recordsUsage: false) then interact().
        WorkspaceModel model = TestWorkspace.Create();
        Assert.Equal(WorkspaceMode.Off, model.Snapshot.Mode);
        model.SetCursorHalo(true);
        Assert.Equal(WorkspaceMode.UseApps, model.Snapshot.Mode);
        Assert.Equal(SurfaceMode.UseApps, model.Snapshot.Surfaces);
        Assert.Equal(2, model.Snapshot.Displays.Length);
        Assert.All(model.Snapshot.Displays, display => Assert.False(display.TakesInput));

        // While the workspace is hidden only the setting changes.
        WorkspaceModel hidden = TestWorkspace.Create();
        hidden.OpenDrawing();
        hidden.HideTools();
        hidden.SetSpotlight(true);
        Assert.True(hidden.Snapshot.Spotlight);
        Assert.True(hidden.Snapshot.WorkspaceHidden);
        Assert.Equal(SurfaceMode.Hidden, hidden.Snapshot.Surfaces);
    }

    [Fact]
    public void EverySettingChangeRestartsPointerWatching()
    {
        WorkspaceModel model = TestWorkspace.Drawing();
        ulong serial = model.Snapshot.PointerEffectsSerial;
        model.SetSpotlight(true);
        Assert.True(model.Snapshot.PointerEffectsSerial > serial);
        serial = model.Snapshot.PointerEffectsSerial;
        // Swift's didSet runs even when the value does not change.
        model.SetSpotlight(true);
        Assert.True(model.Snapshot.PointerEffectsSerial > serial);
        serial = model.Snapshot.PointerEffectsSerial;
        model.SetCursorHalo(false);
        Assert.True(model.Snapshot.PointerEffectsSerial > serial);
        serial = model.Snapshot.PointerEffectsSerial;
        model.SetSpotlightRadius(200);
        Assert.Equal(serial, model.Snapshot.PointerEffectsSerial);
    }

    [Fact]
    public void PointerSettingsAreValidatedAndSaved()
    {
        var store = new MemoryPreferencesStore();
        WorkspaceModel model = TestWorkspace.Create(store: store);
        var settings = new CursorHighlightSettings
        {
            Shape = CursorHighlightShape.Hexagon,
            BorderStyle = StrokePattern.Dotted,
            GlowRadius = 12,
            ClickEffect = CursorClickEffect.Turn,
            Visibility = CursorHighlightVisibility.Moving,
            Diameter = 80,
        };
        model.SetCursorHighlight(settings);
        Assert.Equal(settings, model.Snapshot.CursorHighlight);
        Assert.Null(model.Snapshot.ErrorMessage);
        Assert.Equal(settings, WorkspacePreferences.Read(store.Load()!).CursorHighlight);

        var invalid = settings;
        invalid.Diameter = 200;
        model.SetCursorHighlight(invalid);
        Assert.Equal(settings, model.Snapshot.CursorHighlight);
        Assert.Equal(CanvasError.InvalidStyle.ErrorDescription, model.Snapshot.ErrorMessage);
        invalid = settings;
        invalid.GlowRadius = 25;
        model.SetCursorHighlight(invalid);
        Assert.Equal(settings, model.Snapshot.CursorHighlight);

        // Defaults: a yellow ring of 36 points with a 3-point border, Pulse, shown while turned on.
        WorkspaceModel fresh = TestWorkspace.Create();
        Assert.Equal(new CursorHighlightSettings(), fresh.Snapshot.CursorHighlight);
        Assert.Equal(130, fresh.Snapshot.Preferences.SpotlightRadius);
        fresh.SetSpotlightRadius(210.4);
        Assert.Equal(210.4, fresh.Snapshot.Preferences.SpotlightRadius);
        fresh.SetSpotlightRadius(10);
        Assert.Equal(50, fresh.Snapshot.Preferences.SpotlightRadius);
        fresh.SetSpotlightRadius(double.NaN);
        Assert.Equal(50, fresh.Snapshot.Preferences.SpotlightRadius);

        // The laser pointer's Fade: tenths of a second from 0.5 to 5; the default saves nothing.
        Assert.Equal(LaserTrail.DefaultLifetime, fresh.LaserFade);
        fresh.SetLaserFade(2.46);
        Assert.Equal(2.5, fresh.Snapshot.Preferences.LaserFade);
        fresh.SetLaserFade(30);
        Assert.Equal(LaserTrail.MaximumLifetime, fresh.LaserFade);
        fresh.SetLaserFade(0.81);
        Assert.Null(fresh.Snapshot.Preferences.LaserFade);
        fresh.SetLaserFade(double.NaN);
        Assert.Equal(LaserTrail.DefaultLifetime, fresh.LaserFade);
    }

    [Fact]
    public void ModeStripListsActiveModesInTheMacOrder()
    {
        WorkspaceModel model = TestWorkspace.Drawing();
        Assert.Empty(ActiveToolbarModes.Active(model.Snapshot));
        model.SetBoard(Board.Blackboard);
        model.SetCursorHalo(true);
        model.SetSpotlight(true);
        Assert.Equal([ActiveToolbarMode.Spotlight, ActiveToolbarMode.CursorHalo, ActiveToolbarMode.Blackboard], ActiveToolbarModes.Active(model.Snapshot));
        Assert.Equal(["spotlight", "cursorHalo", "Spot", "Halo", "flashlight.on.fill", "circle.dotted"],
            [ActiveToolbarMode.Spotlight.RawValue, ActiveToolbarMode.CursorHalo.RawValue, ActiveToolbarMode.Spotlight.ShortTitle,
             ActiveToolbarMode.CursorHalo.ShortTitle, ActiveToolbarMode.Spotlight.Symbol, ActiveToolbarMode.CursorHalo.Symbol]);
        Assert.Equal("Turn off cursor halo", ActiveToolbarMode.CursorHalo.StopHint);
        Assert.Equal("Return to the desktop background", ActiveToolbarMode.Whiteboard.StopHint);
        ActiveToolbarMode.Spotlight.Stop(model);
        Assert.False(model.Snapshot.Spotlight);
        ActiveToolbarMode.Blackboard.Stop(model);
        Assert.Equal(Board.Desktop, model.Snapshot.Board);
        Assert.Equal([ActiveToolbarMode.CursorHalo], ActiveToolbarModes.Active(model.Snapshot));
    }

    [Fact]
    public void ToolsWindowTilesFollowTheEffects()
    {
        WorkspaceModel model = TestWorkspace.Drawing();
        ToolLibraryTile Tile(string title) => ToolLibraryTiles.For(ToolLibrarySection.Focus, model.Snapshot).Single(tile => tile.Title == title);
        Assert.True(Tile("Spotlight").Available);
        Assert.True(Tile("Cursor halo").Available);
        Assert.False(Tile("Spotlight").Selected);
        Assert.True(Tile("Pointer lens").Available);
        Assert.True(Tile("Share region").Available);
        model.SetSpotlight(true);
        Assert.True(Tile("Spotlight").Selected);
        Assert.False(Tile("Cursor halo").Selected);
        model.SetCursorHalo(true);
        Assert.True(Tile("Cursor halo").Selected);
    }

    [Fact]
    public void SpotlightFadesFollowTheCorpus()
    {
        var fade = new SpotlightFade();
        fade.SetVisible(true);
        Assert.True(fade.IsAnimating);
        Compare("spotlight dims in", (fade, 10.0), time =>
        {
            fade.Step(10 + time);
            return fade.Intensity;
        });
        Assert.False(fade.IsAnimating);
        Assert.Equal(1, fade.Intensity);

        fade.SetVisible(false);
        Assert.True(fade.IsVisible, "The dimming fades out instead of disappearing.");
        Compare("spotlight dims out", (fade, 20.0), time =>
        {
            fade.Step(20 + time);
            return 1 - fade.Intensity;
        });
        Assert.False(fade.IsVisible);
        Assert.False(fade.IsAnimating);
    }

    [Fact]
    public void SpotlightSizeEasesOnlyWhileVisible()
    {
        var fade = new SpotlightFade();
        // Without a visible spotlight the size changes at once.
        fade.SetRadius(200, 130, reducesMotion: false);
        Assert.False(fade.IsAnimating);
        Assert.Equal(200, fade.Radius(200));

        fade.SetVisible(true);
        fade.Step(0);
        fade.Step(1);
        fade.SetRadius(260, 200, reducesMotion: false);
        Assert.True(fade.IsAnimating);
        Assert.Equal(200, fade.Radius(260));
        Compare("spotlight radius", (fade, 30.0), time =>
        {
            fade.Step(30 + time);
            return (fade.Radius(260) - 200) / 60;
        });
        Assert.Equal(260, fade.Radius(260));
        Assert.False(fade.IsAnimating);

        // Reduce Motion resizes at once; a target set elsewhere shows at once too.
        fade.SetRadius(130, 260, reducesMotion: true);
        Assert.False(fade.IsAnimating);
        Assert.Equal(130, fade.Radius(130));
        Assert.Equal(90, fade.Radius(90));
    }

    [Fact]
    public void SpotlightToggleTurnsAroundFromWhatShows()
    {
        // PointerOverlayInvalidationTests: rapid toggles settle on the latest state, and nothing ticks at rest.
        var fade = new SpotlightFade();
        fade.SetVisible(true);
        fade.Step(0);
        fade.Step(0.09);
        double halfway = fade.Intensity;
        Assert.InRange(halfway, 0.2, 0.95);
        fade.SetVisible(false);
        fade.Step(0.10);
        Assert.Equal(halfway, fade.Intensity, 12);
        // The way back takes the fade-out time scaled by the strength shown.
        double back = MotionTiming.SpotlightFadeOutDuration * halfway;
        fade.Step(0.10 + back * 0.5);
        Assert.True(fade.IsAnimating);
        fade.Step(0.10 + back + 1e-9);
        Assert.False(fade.IsAnimating);
        Assert.Equal(0, fade.Intensity);

        fade.SetVisible(true);
        fade.SetVisible(false);
        fade.SetVisible(true);
        fade.Step(1);
        fade.Step(2);
        Assert.Equal(1, fade.Intensity);
        Assert.False(fade.IsAnimating);

        fade.SetVisible(false);
        fade.Finish();
        Assert.Equal(0, fade.Intensity);
        Assert.False(fade.IsAnimating);
    }

    [Fact]
    public void HaloVisibilityFollowsTheSetting()
    {
        WorkspaceModel model = TestWorkspace.Drawing();
        model.SetCursorHalo(true);
        var effects = new PointerEffects();
        effects.Update(model.Snapshot, reducesMotion: false);
        Assert.True(effects.Watching);
        Assert.True(effects.HaloVisible(5));
        Assert.Null(effects.NextChange(5));

        var settings = new CursorHighlightSettings { Visibility = CursorHighlightVisibility.Moving };
        model.SetCursorHighlight(settings);
        effects.Update(model.Snapshot, reducesMotion: false);
        Assert.False(effects.HaloVisible(5));
        effects.NoteMove(5);
        Assert.True(effects.HaloVisible(5.7));
        Assert.Equal(5.75, effects.NextChange(5.1)!.Value, 12);
        Assert.False(effects.HaloVisible(5.75));
        Assert.Null(effects.NextChange(5.8));
        // A click shows it too, for the click's 0.45 s.
        effects.NoteClick(10, new Point(40, 50), reducesMotion: false);
        Assert.True(effects.HaloVisible(10.44));
        Assert.False(effects.HaloVisible(10.46));
        Assert.Equal(10.45, effects.NextChange(10)!.Value, 12);

        settings.Visibility = CursorHighlightVisibility.Clicks;
        model.SetCursorHighlight(settings);
        effects.Update(model.Snapshot, reducesMotion: false);
        effects.NoteMove(20);
        Assert.False(effects.HaloVisible(20));
        effects.NoteClick(21, new Point(1, 2), reducesMotion: false);
        Assert.True(effects.HaloVisible(21.2));
        Assert.Equal(new Point(1, 2), effects.ClickLocation);
        Assert.False(effects.HaloVisible(21.5));
    }

    [Fact]
    public void ClicksAnimateOnceThenNeedNoFrames()
    {
        WorkspaceModel model = TestWorkspace.Drawing();
        model.SetCursorHalo(true);
        var effects = new PointerEffects();
        effects.Update(model.Snapshot, reducesMotion: false);
        Assert.False(effects.NeedsFrames(0), "An idle highlight must not own a display link.");
        effects.NoteClick(1, new Point(10, 10), reducesMotion: false);
        Assert.True(effects.NeedsFrames(1.1));
        Assert.Equal(0.1 / 0.45, effects.ClickProgress(1.1)!.Value, 12);
        Assert.False(effects.NeedsFrames(1.46));
        Assert.Null(effects.ClickProgress(1.46));

        // Off and Reduce Motion show a static emphasis for the click, with no frames, and end it by time.
        effects.NoteClick(2, new Point(10, 10), reducesMotion: true);
        Assert.False(effects.NeedsFrames(2.1));
        Assert.NotNull(effects.ClickProgress(2.1));
        Assert.Equal(2.45, effects.NextChange(2.1)!.Value, 12);
        model.SetCursorHighlight(new CursorHighlightSettings { ClickEffect = CursorClickEffect.Off });
        effects.Update(model.Snapshot, reducesMotion: false);
        effects.NoteClick(3, new Point(10, 10), reducesMotion: false);
        Assert.False(effects.NeedsFrames(3.1));
    }

    [Fact]
    public void TurningEffectsOffOrHidingStopsClicksAndForgetsMovement()
    {
        // CursorHighlightWorkflowTests.testDisablingTheRealHaloStopsClickFrames.
        WorkspaceModel model = TestWorkspace.Drawing();
        model.SetCursorHalo(true);
        var effects = new PointerEffects();
        effects.Update(model.Snapshot, reducesMotion: false);
        effects.NoteClick(1, new Point(0, 0), reducesMotion: false);
        Assert.True(effects.NeedsFrames(1.1));
        model.SetCursorHalo(false);
        effects.Update(model.Snapshot, reducesMotion: false);
        Assert.False(effects.NeedsFrames(1.1));
        Assert.False(effects.Watching);
        effects.NoteMove(1.2);
        Assert.Null(effects.LastMovement);

        model.SetCursorHalo(true);
        effects.Update(model.Snapshot, reducesMotion: false);
        effects.NoteClick(2, new Point(0, 0), reducesMotion: false);
        effects.NoteMove(2.01);
        model.HideTools();
        effects.Update(model.Snapshot, reducesMotion: false);
        Assert.False(effects.Shown);
        Assert.False(effects.NeedsFrames(2.1));
        Assert.False(effects.HaloVisible(2.1));
        Assert.Null(effects.LastMovement);
        Assert.Null(effects.ClickLocation);
    }

    [Fact]
    public void HiddenWorkspaceLandsTheSpotlightAtOnce()
    {
        WorkspaceModel model = TestWorkspace.Drawing();
        var effects = new PointerEffects();
        effects.Update(model.Snapshot, reducesMotion: false);
        model.SetSpotlight(true);
        effects.Update(model.Snapshot, reducesMotion: false);
        Assert.True(effects.NeedsFrames(0));
        effects.Advance(0);
        effects.Advance(0.05);
        Assert.True(effects.SpotlightVisible);
        model.HideTools();
        effects.Update(model.Snapshot, reducesMotion: false);
        Assert.False(effects.NeedsFrames(0.06));
        Assert.Equal(1, effects.Spotlight.Intensity);
        Assert.False(effects.SpotlightVisible);
        model.ResumeDrawing();
        effects.Update(model.Snapshot, reducesMotion: false);
        // Shown again at full strength, as the Mac redraws its finished fade.
        Assert.True(effects.SpotlightVisible);
        Assert.False(effects.NeedsFrames(1));

        // A size change while hidden shows at the new size.
        model.SetSpotlightRadius(300);
        effects.Update(model.Snapshot, reducesMotion: false);
        Assert.True(effects.NeedsFrames(1));
        effects.Advance(1);
        effects.Advance(1.2);
        Assert.Equal(300, effects.SpotlightRadius);
    }

    private static void Compare(string name, (SpotlightFade Fade, double Start) first, Func<double, double> value)
    {
        var (duration, samples) = Series(name);
        first.Fade.Step(first.Start);
        var largest = 0.0;
        for (var millisecond = 0; millisecond < samples.Length; millisecond++)
        {
            largest = Math.Max(largest, Math.Abs(samples[millisecond] - value(millisecond / 1000.0)));
        }
        // The first display frame after the end finishes it.
        value(duration + 1e-6);
        Assert.True(largest <= Tolerance, $"{name}: largest difference {largest:E3}.");
    }

    private static (double Duration, double[] Samples) Series(string name)
    {
        foreach (var line in File.ReadLines(Corpus("timelines.jsonl")))
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement record = document.RootElement;
            if (record.GetProperty("name").GetString() == name)
            {
                return (record.GetProperty("duration").GetDouble(),
                        [.. record.GetProperty("samplesEveryMillisecond").EnumerateArray().Select(value => value.GetDouble())]);
            }
        }
        throw new Xunit.Sdk.XunitException($"The corpus has no series '{name}'.");
    }

    private static string Corpus(string file)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "reference", "conformance", "motion", file);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException("reference/conformance/motion was not found above the test output.", file);
    }
}
