using Annotator.Core;
using Annotator.Workspace.Artwork;
using Annotator.Workspace.Shortcuts;
using Annotator.Workspace.Toolbar;
using static Annotator.Workspace.Tests.TestWorkspace;

namespace Annotator.Workspace.Tests;

public sealed class ToolAndPreferenceTests
{
    [Fact]
    public void ChoosingADrawingToolEntersDrawClearsTheSelectionAndShowsItsSavedStyle()
    {
        WorkspaceModel model = Create();
        model.ShowToolbar();
        Assert.Equal(ToolChoice.Chosen, model.Choose(Tool.Highlighter));
        Assert.Equal(WorkspaceMode.Draw, model.Snapshot.Mode);
        Assert.Equal(Style.Defaults(Tool.Highlighter), model.Snapshot.Style);
        Assert.Equal(model.Snapshot.ActiveDisplay, model.Snapshot.FocusRequest?.Display);

        var shape = new Element(Tool.Rectangle);
        AssertAccepted(model.Edit(Primary, document =>
        {
            document[0].Add(shape);
            document[0].Select([shape.Id]);
        }));
        Assert.Equal(Tool.Rectangle, model.Snapshot.EffectiveTool);
        Assert.Equal(shape.Style, model.Snapshot.Style);
        model.Choose(Tool.Pen);
        Assert.Empty(model.Document(Primary).Selection);
        Assert.Equal(Tool.Pen, model.Snapshot.EffectiveTool);
        Assert.Equal(Style.FirstUse(Tool.Pen), model.Snapshot.Style);
    }

    // Mac testNewProfilesStartWithAGradientAndSavedProfilesKeepTheirColors.
    [Fact]
    public void NewProfilesStartWithAGradientAndSavedProfilesKeepTheirColors()
    {
        var fresh = new MemoryPreferencesStore();
        WorkspaceModel model = Create(store: fresh);
        Assert.Equal(0, fresh.SaveCount);
        model.ShowToolbar();
        model.Choose(Tool.Rectangle);
        Assert.Equal(GradientPreset.Sunset.Gradient, model.Snapshot.Style.StrokeGradient);
        model.Choose(Tool.Highlighter);
        Assert.Equal(Style.Defaults(Tool.Highlighter), model.Snapshot.Style);
        model.ApplyStyle((ref Style style) => style.Width = 6);
        Assert.Equal(GradientPreset.Sunset.Gradient, WorkspacePreferences.Read(fresh.Data!).ToolStyles["pen"].StrokeGradient);

        var existing = new MemoryPreferencesStore(new WorkspacePreferences().Data());
        WorkspaceModel returning = Create(store: existing);
        Assert.Equal(0, existing.SaveCount);
        returning.ShowToolbar();
        returning.Choose(Tool.Rectangle);
        Assert.Equal(Style.Defaults(Tool.Rectangle), returning.Snapshot.Style);
        Assert.Null(returning.Snapshot.Style.StrokeGradient);
    }

    [Fact]
    public void ChoosingSelectKeepsTheSelection()
    {
        WorkspaceModel model = Drawing();
        var shape = new Element(Tool.Ellipse);
        AssertAccepted(model.Edit(Primary, document =>
        {
            document[0].Add(shape);
            document[0].Select([shape.Id]);
        }));
        model.Choose(Tool.Select);
        Assert.Equal([shape.Id], model.Document(Primary).Selection);
        Assert.Equal(1, model.Snapshot.SelectionCount);
    }

    [Fact]
    public void ChoosingPasteImagePastesTheClipboardImageAndKeepsTheTool()
    {
        // Mac choose(.image) calls pasteImage(): an image on the clipboard becomes a selected image object, and the tool
        // becomes Select as after any insertion; without an image the Mac's message is reported and nothing changes.
        var clipboard = new MemoryClipboard();
        var model = new WorkspaceModel(new WorkspaceOptions
        {
            Displays = () => [PrimaryDisplay, LeftDisplay],
            Pointer = () => new Point(10, 10),
            Clipboard = clipboard,
        });
        Assert.True(WorkspaceModel.IsAvailable(Tool.Image));
        Assert.Equal(ToolChoice.Chosen, model.Choose(Tool.Image));
        Assert.Equal(
            "The drawing could not be opened: Copy an image or image file, then choose Paste image. A web link alone does not contain image pixels.",
            model.Snapshot.ErrorMessage);
        Assert.Equal(WorkspaceMode.Off, model.Snapshot.Mode);
        model.DismissError();

        clipboard.SetImage(new ClipboardImage(ValueList.Create<byte>([137, 80, 78, 71, 13, 10, 26, 10, 1]), 24, 16));
        Assert.Equal(ToolChoice.Chosen, model.Choose(Tool.Image));
        Element image = Assert.Single(model.Elements(Primary));
        Assert.Equal(Tool.Image, image.Tool);
        Assert.Equal([image.Id], model.Document(Primary).Selection);
        Assert.Equal(Tool.Select, model.Snapshot.Tool);
        Assert.Equal(Tool.Image, model.Snapshot.EffectiveTool);
        Assert.Equal(WorkspaceMode.Draw, model.Snapshot.Mode);
        foreach (Tool tool in new[] { Tool.Cover, Tool.Blur, Tool.Pixelate, Tool.Distort, Tool.Database })
        {
            Assert.True(WorkspaceModel.IsAvailable(tool));
        }
    }

    [Fact]
    public void ToolbarItemsFollowTheMacProfileRules()
    {
        WorkspaceModel model = Create();
        Assert.Equal(WorkspacePreferences.DefaultToolbar.Select(ShortcutCommand.ForTool), model.Snapshot.ToolbarItems);

        var preferences = new WorkspacePreferences
        {
            ToolbarTools = ValueList.Create(["select", "pen", "blur"]),
            ToolbarItems = ValueList.Create(["tool.select", "tool.pen", "action.spotlight", "tool.cover"]),
        };
        WorkspaceModel custom = Create(store: new MemoryPreferencesStore(preferences.Data()));
        Assert.Equal(
            [ShortcutCommand.ForTool(Tool.Select), ShortcutCommand.ForTool(Tool.Pen), ShortcutCommand.ForAction(ShortcutAction.Spotlight),
             ShortcutCommand.ForTool(Tool.Cover)],
            custom.Snapshot.ToolbarItems);

        var outdated = preferences with { ToolbarTools = ValueList.Create(["select", "rectangle"]) };
        WorkspaceModel legacy = Create(store: new MemoryPreferencesStore(outdated.Data()));
        Assert.Equal([ShortcutCommand.ForTool(Tool.Select), ShortcutCommand.ForTool(Tool.Rectangle)], legacy.Snapshot.ToolbarItems);
    }

    [Fact]
    public void UnreadableSettingsAreReportedAndDefaultsUsed()
    {
        WorkspaceModel model = Create(store: new MemoryPreferencesStore("{ not json"u8.ToArray()));
        Assert.StartsWith("Saved settings were not loaded: ", model.Snapshot.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(model.Snapshot.ErrorMessage, model.Snapshot.Status);
        Assert.Equal(new WorkspacePreferences(), model.Snapshot.Preferences);
    }

    [Fact]
    public void FailedSavesAreReported()
    {
        WorkspaceModel model = Create(store: new FailingStore());
        model.BeginDrawing();
        model.ApplyInkKey("r");
        Assert.Equal("The disk is full.", model.Snapshot.ErrorMessage);
    }

    [Fact]
    public void ShortcutStatesAreUserVisible()
    {
        WorkspaceModel model = Create();
        Assert.Equal("Ready. Ctrl+2 opens drawing.", model.Snapshot.Status);
        Assert.All(model.Snapshot.GlobalShortcuts, state => Assert.Equal(GlobalShortcutStatus.NotRegistered, state.Status));
        Assert.Equal("Ctrl+Alt+Shift+2", model.Snapshot.Shortcut(GlobalShortcutAction.HideOnly)?.Label);

        model.SetGlobalShortcutState(new GlobalShortcutState(GlobalShortcutAction.ShowHide, "Ctrl+2", GlobalShortcutStatus.Registered, null));
        Assert.Equal("Ready. Ctrl+2 opens drawing or hides the tools.", model.Snapshot.Status);
        string message = WindowsShortcuts.RegistrationFailure("Ctrl+Shift+2", 1409);
        model.SetGlobalShortcutState(new GlobalShortcutState(GlobalShortcutAction.ResumeDrawing, "Ctrl+Shift+2", GlobalShortcutStatus.AlreadyUsed, message));
        Assert.Equal(message, model.Snapshot.ErrorMessage);
        Assert.Equal(GlobalShortcutStatus.AlreadyUsed, model.Snapshot.Shortcut(GlobalShortcutAction.ResumeDrawing)?.Status);
        Assert.Equal(5, model.Snapshot.GlobalShortcuts.Length);
        Assert.Equal("Ctrl+3", model.Snapshot.Shortcut(GlobalShortcutAction.BreakTimer)?.Label);
        Assert.Equal(
            "The Ctrl+Shift+2 shortcut could not be registered (error 1409). Another app or Windows may be using it. " +
            "Change it in Settings > Toolbar & Shortcuts, or use the tray menu command.", message);
        model.DismissError();
        Assert.Null(model.Snapshot.ErrorMessage);
    }

    [Fact]
    public void ModeRawValuesMatchTheMac()
    {
        Assert.Equal("Off", WorkspaceMode.Off.RawValue);
        Assert.Equal("Interact", WorkspaceMode.UseApps.RawValue);
        Assert.Equal("Draw", WorkspaceMode.Draw.RawValue);
    }

    private sealed class FailingStore : IWorkspacePreferencesStore
    {
        public byte[]? Load() => null;

        public void Save(byte[] data) => throw new IOException("The disk is full.");
    }
}

public sealed class WindowsShortcutTests
{
    [Fact]
    public void DefaultsFollowDecisionD5()
    {
        var preferences = new WorkspacePreferences();
        Assert.Equal("Ctrl+2", WindowsShortcuts.Chord(GlobalShortcutAction.ShowHide, preferences)?.Label);
        Assert.Equal("Ctrl+Shift+2", WindowsShortcuts.Chord(GlobalShortcutAction.ResumeDrawing, preferences)?.Label);
        Assert.Equal("Ctrl+Alt+Shift+2", WindowsShortcuts.Chord(GlobalShortcutAction.HideOnly, preferences)?.Label);
        Assert.Equal("Ctrl+1", WindowsShortcuts.Chord(GlobalShortcutAction.LiveZoom, preferences)?.Label);
        Assert.Equal(0x03u, WindowsShortcuts.ShowHideDefault.ScanCode);
        Assert.Equal(WindowsShortcuts.ShowHideDefault, WindowsShortcuts.Chord(ActivationShortcut.Standard));
        Assert.Equal(WindowsShortcuts.ResumeDrawingDefault, WindowsShortcuts.Chord(ActivationShortcut.ResumeDrawing));
        Assert.Equal(WindowsShortcuts.LiveZoomDefault, WindowsShortcuts.Chord(ActivationShortcut.LiveZoom));
    }

    [Fact]
    public void StoredShortcutsMapModifiersAndPhysicalKeys()
    {
        var shortcut = new ActivationShortcut(15, "r", control: true, option: true, command: true, shift: true);
        ShortcutChord chord = Assert.IsType<ShortcutChord>(WindowsShortcuts.Chord(shortcut));
        Assert.Equal(ChordModifiers.Ctrl | ChordModifiers.Alt | ChordModifiers.Shift | ChordModifiers.Win, chord.Modifiers);
        Assert.Equal(0x13u, chord.ScanCode);
        Assert.Equal("Ctrl+Alt+Shift+Win+R", chord.Label);
        var preferences = new WorkspacePreferences { HideShortcut = ActivationShortcut.HideTools };
        Assert.Equal("Ctrl+Alt+2", WindowsShortcuts.Label(GlobalShortcutAction.HideOnly, preferences));
        Assert.Null(WindowsShortcuts.Chord(new ActivationShortcut(0x7F, "x", control: true)));
        Assert.Equal("Ctrl+X", WindowsShortcuts.Label(GlobalShortcutAction.ShowHide, new WorkspacePreferences { ActivationShortcut = new ActivationShortcut(0x7F, "x", control: true) }));
    }

    [Fact]
    public void EveryAlphanumericMacKeyHasADistinctScanCode()
    {
        uint[] codes = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, 0x11,
            0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x19, 0x1A, 0x1C, 0x1D, 0x1F, 0x20, 0x22, 0x23, 0x25, 0x26, 0x28, 0x2D, 0x2E];
        Assert.Equal(36, codes.Select(code => WindowsShortcuts.ScanCode(code)).Distinct().Count(scan => scan is not null));
        Assert.Equal(0x0Bu, WindowsShortcuts.ScanCode(0x1D));
    }

    [Fact]
    public void LocalKeysUseTextKeycaps()
    {
        Assert.Equal("7", WindowsShortcuts.Label(LocalShortcut.Create("7")!.Value));
        Assert.Equal("Shift+T", WindowsShortcuts.Label(LocalShortcut.Create("t", shift: true)!.Value));
    }
}

public sealed class ToolbarLayoutTests
{
    private static readonly ShortcutCommand[] Defaults = [.. WorkspacePreferences.DefaultToolbar.Select(ShortcutCommand.ForTool)];

    // Mac InspectorInteractionRegressionTests.testPropertiesToggleIsSharedAndShowsTheWidthInImageWorkspace and
    // PropertiesToggleTests: the Properties button shows or hides the properties, and the choice is saved.
    [Fact]
    public void PropertiesShowOrHideAndPersistLikeTheMac()
    {
        var store = new MemoryPreferencesStore();
        WorkspaceModel model = Create(store: store);
        Assert.Equal(ToolbarPropertiesMode.Shown, ToolbarPropertiesMode.From(model.Snapshot.Preferences));
        Assert.Equal(new[] { ToolbarPropertiesMode.Shown, ToolbarPropertiesMode.Hidden }, Enum.GetValues<ToolbarPropertiesMode>());
        model.ApplyPropertiesMode(ToolbarPropertiesMode.Hidden);
        ToolbarPropertiesMode mode = ToolbarPropertiesMode.From(model.Snapshot.Preferences);
        Assert.Equal(ToolbarPropertiesMode.Hidden, mode);
        foreach (ToolbarPropertiesMode expected in new[] { ToolbarPropertiesMode.Shown, ToolbarPropertiesMode.Hidden, ToolbarPropertiesMode.Shown })
        {
            model.ApplyPropertiesMode(mode.Next);
            mode = ToolbarPropertiesMode.From(model.Snapshot.Preferences);
            Assert.Equal(expected, mode);
        }
        Assert.Equal("Shown", mode.RawValue);
        Assert.Equal("Hidden", mode.Next.RawValue);
        Assert.Equal(ToolbarPropertiesMode.Shown, ToolbarPropertiesMode.From(WorkspacePreferences.Read(store.Data!)));
        Assert.True(model.Snapshot.Preferences.InspectorPinned);
        model.SetPropertiesShown(false);
        Assert.Equal(ToolbarPropertiesMode.Hidden, ToolbarPropertiesMode.From(model.Snapshot.Preferences));
        // A profile from before, with quick controls, shows the properties.
        var quick = new WorkspacePreferences { QuickPreferencesExpanded = true, InspectorPinned = false };
        Assert.Equal(ToolbarPropertiesMode.Shown, ToolbarPropertiesMode.From(quick));
    }

    [Fact]
    public void DefaultToolsFitTheMacMeasurements()
    {
        // 12 tools of 44 points: select | 5 shapes and connectors | 4 ink tools with the laser pointer | eraser | effects.
        Assert.Equal(12 * 44 + 7 * 2 + 4 * 11, ToolbarToolLayout.Width(Defaults));
        Assert.Equal(969, ToolbarToolLayout.HorizontalWidth(Defaults, 1000));
        Assert.Equal(44 + 383, ToolbarToolLayout.HorizontalWidth([ShortcutCommand.ForTool(Tool.Select)], 1000));
        Assert.Equal(15, new ToolbarMetrics(ToolbarSize.Expanded).CornerRadius);
        Assert.Equal(44, new ToolbarMetrics(ToolbarSize.Expanded).ButtonWidth);
        Assert.Equal(48, new ToolbarMetrics(ToolbarSize.Expanded).ButtonHeight);
        Assert.Equal(34, new ToolbarMetrics(ToolbarSize.Minimal).ButtonWidth);
    }

    [Fact]
    public void FamiliesAndIdentitiesMatchTheMac()
    {
        Assert.Equal(0, ToolbarToolLayout.Family(Tool.Select));
        Assert.Equal(1, ToolbarToolLayout.Family(Tool.Arrow));
        Assert.Equal(1, ToolbarToolLayout.Family(Tool.Server));
        Assert.Equal(2, ToolbarToolLayout.Family(Tool.Highlighter));
        Assert.Equal(2, ToolbarToolLayout.Family(Tool.Laser));
        Assert.Equal(3, ToolbarToolLayout.Family(Tool.Eraser));
        Assert.Equal(4, ToolbarToolLayout.Family(Tool.Blur));
        Assert.Equal(5, ToolbarToolLayout.Family(Tool.Ruler));
        Assert.Equal(6, ToolbarToolLayout.Family(ShortcutCommand.ForAction(ShortcutAction.ClearAll)));
        Assert.Equal(Tool.Cover, ToolbarToolLayout.Identity(Tool.Pixelate, Defaults));
        Assert.Equal(Tool.Rectangle, ToolbarToolLayout.Identity(Tool.Database, Defaults));
        Assert.Equal(Tool.Pen, ToolbarToolLayout.Identity(Tool.Pen, Defaults));
    }

    [Fact]
    public void VisibleToolsScrollToKeepTheSelectionShown()
    {
        ShortcutCommand[] visible = ToolbarToolLayout.Visible(Defaults, ShortcutCommand.ForTool(Tool.Select), 44 * 3 + 11 + 2);
        Assert.Equal([ShortcutCommand.ForTool(Tool.Select), ShortcutCommand.ForTool(Tool.Rectangle), ShortcutCommand.ForTool(Tool.Diamond)], visible);
        ShortcutCommand[] scrolled = ToolbarToolLayout.Visible(Defaults, ShortcutCommand.ForTool(Tool.Eraser), 44 * 2 + 11);
        Assert.Contains(ShortcutCommand.ForTool(Tool.Eraser), scrolled);
        Assert.Equal(2, scrolled.Length);
    }

    [Fact]
    public void PlacementCentersAtTheTopOfTheWorkAreaAndClampsDrags()
    {
        var workArea = new Rect(0, 0, 2048, 1112);
        Rect frame = ToolbarPlacement.Frame(1000, 130, workArea, ToolbarOrientation.Horizontal, ToolbarDockEdge.Automatic);
        Assert.Equal(new Rect(524, 12, 1000, 130), frame);
        Rect bottom = ToolbarPlacement.Frame(1000, 130, workArea, ToolbarOrientation.Horizontal, ToolbarDockEdge.Bottom);
        Assert.Equal(970, bottom.Y);
        Rect right = ToolbarPlacement.Frame(72, 600, workArea, ToolbarOrientation.Vertical, ToolbarDockEdge.Automatic);
        Assert.Equal(2048 - 12 - 72, right.X);
        Rect clamped = ToolbarPlacement.ConstrainedDragFrame(new Rect(-50, 2000, 300, 100), workArea);
        Assert.Equal(new Rect(0, 1012, 300, 100), clamped);
        Rect wide = ToolbarPlacement.Frame(3000, 130, workArea, ToolbarOrientation.Horizontal, ToolbarDockEdge.Top);
        Assert.Equal(2048 - 24, wide.Width);
    }

    [Fact]
    public void DockEdgesNeedTheOutermostPoint()
    {
        var frame = new Rect(0, 0, 1000, 800);
        Assert.Equal(ToolbarDockEdge.Top, ToolbarPlacement.DockEdge(new Point(500, 0), frame));
        Assert.Equal(ToolbarDockEdge.Bottom, ToolbarPlacement.DockEdge(new Point(500, 800), frame));
        Assert.Equal(ToolbarDockEdge.Left, ToolbarPlacement.DockEdge(new Point(0.5, 400), frame));
        Assert.Equal(ToolbarDockEdge.Right, ToolbarPlacement.DockEdge(new Point(1000, 400), frame));
        Assert.Null(ToolbarPlacement.DockEdge(new Point(500, 400), frame));
        Assert.Null(ToolbarPlacement.DockEdge(new Point(-5, 400), frame));
    }
}

public sealed class DisplayDescriptorTests
{
    [Fact]
    public void PointsAreLocalToEachDisplay()
    {
        DisplayDescriptor display = PrimaryDisplay;
        Assert.Equal(new Rect(0, 0, 640, 480), display.Bounds);
        Assert.Equal(new Rect(0, 24, 640, 456), display.WorkArea);
        Assert.Equal(new Point(200, 100), display.ToDesktop(new Point(100, 50)));
        Assert.Equal(new Point(100, 50), display.ToLocal(new Point(200, 100)));
        Assert.True(display.ContainsDesktopPoint(new Point(0, 0)));
        Assert.False(display.ContainsDesktopPoint(new Point(1280, 10)));
        Assert.Throws<CanvasError>(() => new DisplayDescriptor(new DisplayId("x"), new Rect(0, 0, 0, 10), new Rect(0, 0, 10, 10), 1));
        Assert.Throws<CanvasError>(() => new DisplayDescriptor(new DisplayId("x"), new Rect(0, 0, 10, 10), new Rect(0, 0, 10, 10), double.NaN));
    }
}

public sealed class MarkGlyphTests
{
    [Fact]
    public void BoundsAreTheTightPathBounds()
    {
        Rect bounds = MarkGlyph.Bounds;
        Assert.Equal(48, bounds.MaxX, 6);
        Assert.InRange(bounds.Y, 16.80, 16.82);
        Assert.InRange(bounds.X, 13.0, 16.0);
        Assert.InRange(bounds.MaxY, 45.0, 47.0);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void RasterizesWithEvenOddCountersAndSoftEdges(int size)
    {
        float[] coverage = MarkGlyph.Coverage(size);
        (double scale, Point offset) = MarkGlyph.Fit(size);
        float At(double x, double y) => coverage[(int)(y * scale + offset.Y) * size + (int)(x * scale + offset.X)];
        Assert.Equal(1f, At(41, 30), 2);
        Assert.Equal(0f, At(28, 32), 2);
        Assert.Equal(0f, coverage[0]);
        Assert.Contains(coverage, value => value is > 0.05f and < 0.95f);
        Assert.All(coverage, value => Assert.InRange(value, 0f, 1.0001f));
    }

    [Fact]
    public void RendersPremultipliedPixels()
    {
        byte[] pixels = MarkGlyph.RenderBgra(24, new RGBA(1, 1, 1, 1));
        Assert.Equal(24 * 24 * 4, pixels.Length);
        for (int index = 0; index < pixels.Length; index += 4)
        {
            Assert.True(pixels[index] <= pixels[index + 3] && pixels[index + 2] <= pixels[index + 3]);
        }
        Assert.Contains(pixels.Where((_, index) => index % 4 == 3), alpha => alpha == 255);
    }
}
