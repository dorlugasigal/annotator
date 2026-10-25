using System.Text.Json.Nodes;
using Annotator.Core;
using Annotator.Workspace.Settings;
using Annotator.Workspace.Shortcuts;
using Annotator.Workspace.Toolbar;
using static Annotator.Workspace.Tests.TestWorkspace;

namespace Annotator.Workspace.Tests.Settings;

/// <summary>Installed keyboard layouts for the recording tests: the chords that type a character, by label.</summary>
internal sealed class TestLayouts(params string[] typing) : IKeyboardLayouts
{
    public string? TypedCharacter(ShortcutChord chord) => typing.Contains(chord.Label) ? "€" : null;
}

/// <summary>A hotkey service stand-in: registers what it is given unless told to refuse, and remembers each attempt.</summary>
internal sealed class TestRegistrar : IGlobalShortcutRegistrar
{
    public List<(GlobalShortcutAction Action, ActivationShortcut Shortcut)> Attempts { get; } = [];

    public bool Refuse { get; set; }

    public bool Supports(GlobalShortcutAction action) => action != GlobalShortcutAction.LiveZoom || ZoomBuilt;

    public bool ZoomBuilt { get; set; } = true;

    public GlobalShortcutState Replace(GlobalShortcutAction action, ActivationShortcut shortcut)
    {
        Attempts.Add((action, shortcut));
        string label = WindowsShortcuts.Label(shortcut);
        return Refuse
            ? new GlobalShortcutState(action, label, GlobalShortcutStatus.AlreadyUsed, WindowsShortcuts.RegistrationFailure(label, 1409))
            : new GlobalShortcutState(action, label, GlobalShortcutStatus.Registered, null);
    }
}

/// <summary>Key presses as the page reports them, with US layout characters unless a test gives another.</summary>
internal static class Keys
{
    public static RecordedKey Letter(char letter, ChordModifiers modifiers = ChordModifiers.None)
    {
        uint scan = WindowsShortcuts.KeyTable.First(key => key.Name == char.ToLowerInvariant(letter).ToString()).ScanCode;
        string typed = modifiers.HasFlag(ChordModifiers.Shift) ? char.ToUpperInvariant(letter).ToString() : char.ToLowerInvariant(letter).ToString();
        return new RecordedKey(char.ToUpperInvariant(letter), scan, modifiers, typed);
    }

    /// <summary>A number-row key; <paramref name="typed"/> is what the layout gives with Shift alone (US "@" for Shift+2).</summary>
    public static RecordedKey Digit(char digit, ChordModifiers modifiers = ChordModifiers.None, string? typed = null)
    {
        uint scan = WindowsShortcuts.KeyTable.First(key => key.Name == digit.ToString() && key.VirtualKey is null).ScanCode;
        return new RecordedKey(digit, scan, modifiers, typed ?? digit.ToString());
    }

    public static RecordedKey Escape => new(0x1B, 0x01, ChordModifiers.None, "\u001B");

    public static RecordedKey Backspace => new(0x08, 0x0E, ChordModifiers.None, "\b");

    public static RecordedKey F5(ChordModifiers modifiers) => new(0x74, 0x3F, modifiers, null);
}

public sealed class ShortcutEditorTests
{
    private static (WorkspaceModel Model, ShortcutEditor Editor, TestRegistrar Registrar) Editor(params string[] typing)
    {
        WorkspaceModel model = Create();
        var registrar = new TestRegistrar();
        model.GlobalShortcutRegistrar = registrar;
        return (model, new ShortcutEditor(model, new TestLayouts(typing)), registrar);
    }

    private static ShortcutCommand ToolCommand(Tool tool) => ShortcutCommand.ForTool(tool);

    // Mac ToolbarShortcutSettingsTests.testClickingAKeyThenPressingOneAssignsShiftKeysAndUndoesMovedKeys.
    [Fact]
    public void ClickingAKeyThenPressingOneAssignsShiftKeysAndUndoesMovedKeys()
    {
        (WorkspaceModel model, ShortcutEditor editor, _) = Editor();
        RecordingTarget star = RecordingTarget.Local(ToolCommand(Tool.Star));
        editor.Toggle(star);
        Assert.Equal(star, editor.Recording);
        Assert.Contains("Press a key for Star", editor.Toast!.Message);
        Assert.True(editor.Toast.Persistent);
        Assert.True(editor.HandleKey(Keys.Letter('S', ChordModifiers.Shift)));
        Assert.Equal(LocalShortcut.Create("s", shift: true), model.Snapshot.Preferences.ShortcutFor(ToolCommand(Tool.Star)));
        Assert.Equal(Tool.Cover.RawValue, model.Snapshot.Preferences.ToolShortcuts["s"]);
        Assert.Null(editor.Recording);
        Assert.Equal("Shift+S is now the key for Star.", editor.Toast.Message);

        RecordingTarget spotlight = RecordingTarget.Local(ShortcutCommand.ForAction(ShortcutAction.Spotlight));
        editor.Toggle(spotlight);
        editor.HandleKey(Keys.Digit('2'));
        Assert.Equal("2", model.Snapshot.Preferences.ShortcutFor(ShortcutCommand.ForAction(ShortcutAction.Spotlight))?.Label);
        Assert.Null(model.Snapshot.Preferences.ShortcutFor(ToolCommand(Tool.Rectangle)));
        Assert.Contains("Rectangle has no key now", editor.Toast!.Message);
        Assert.Equal("Give 2 back to Rectangle", editor.Toast.UndoTitle);
        Assert.Equal(TimeSpan.FromSeconds(6), editor.Toast.Lifetime);
        editor.Undo();
        Assert.Equal("2", model.Snapshot.Preferences.ShortcutFor(ToolCommand(Tool.Rectangle))?.Label);
        Assert.Null(model.Snapshot.Preferences.ShortcutFor(ShortcutCommand.ForAction(ShortcutAction.Spotlight)));
        Assert.Equal("Done. The keys are back as they were.", editor.Toast!.Message);
    }

    // Mac testRejectedKeysExplainInPlaceAndDeleteClearsAKey.
    [Fact]
    public void RejectedKeysExplainInPlaceAndDeleteClearsAKey()
    {
        (WorkspaceModel model, ShortcutEditor editor, _) = Editor();
        RecordingTarget pen = RecordingTarget.Local(ToolCommand(Tool.Pen));
        var refused = new List<RecordingTarget>();
        editor.Refused += refused.Add;
        editor.Toggle(pen);
        editor.HandleKey(Keys.Letter('r'));
        Assert.Equal("7", model.Snapshot.Preferences.ShortcutFor(ToolCommand(Tool.Pen))?.Label);
        Assert.Contains("changes the ink color", editor.Toast!.Message);
        Assert.Equal(pen, editor.Recording);
        Assert.True(editor.Toast.Persistent, "A rejected key keeps listening.");
        editor.HandleKey(Keys.Letter('k', ChordModifiers.Ctrl));
        Assert.Contains("without Ctrl", editor.Toast!.Message);
        Assert.Equal([pen, pen], refused);
        Assert.Equal(2, editor.Shakes);
        editor.HandleKey(Keys.Escape);
        Assert.Null(editor.Recording);
        Assert.Null(editor.Toast);
        Assert.Equal("7", model.Snapshot.Preferences.ShortcutFor(ToolCommand(Tool.Pen))?.Label);

        editor.Toggle(pen);
        editor.HandleKey(Keys.Backspace);
        Assert.Null(model.Snapshot.Preferences.ShortcutFor(ToolCommand(Tool.Pen)));
        Assert.Contains("Pen has no key now", editor.Toast!.Message);
        Assert.Equal("Give 7 back to Pen", editor.Toast.UndoTitle);
        editor.Undo();
        Assert.Equal("7", model.Snapshot.Preferences.ShortcutFor(ToolCommand(Tool.Pen))?.Label);

        // On AZERTY, Shift and the "&" key type "1"; that records the digit.
        RecordingTarget star = RecordingTarget.Local(ToolCommand(Tool.Star));
        editor.Toggle(star);
        editor.HandleKey(Keys.Digit('1', ChordModifiers.Shift, typed: "1"));
        Assert.Equal(LocalShortcut.Create("1"), model.Snapshot.Preferences.ShortcutFor(ToolCommand(Tool.Star)));
        Assert.Null(model.Snapshot.Preferences.ShortcutFor(ToolCommand(Tool.Select)));

        // On US, Shift and the 1 key type "!": not a letter or a number.
        editor.Toggle(star);
        editor.HandleKey(Keys.Digit('1', ChordModifiers.Shift, typed: "!"));
        Assert.Equal("Press a letter or a number. Shift works with letters only.", editor.Toast!.Message);
        Assert.Equal(LocalShortcut.Create("1"), model.Snapshot.Preferences.ShortcutFor(ToolCommand(Tool.Star)));
    }

    // Mac testGlobalShortcutsRecordModifierKeysAndRejectClashes, with the Windows defaults (D-5).
    [Fact]
    public void GlobalShortcutsRecordModifierKeysAndRejectClashes()
    {
        (WorkspaceModel model, ShortcutEditor editor, TestRegistrar registrar) = Editor();
        Assert.Equal("Ctrl + Shift + 2", WindowsShortcuts.Spoken(GlobalShortcutKind.Draw.Value(model.Snapshot.Preferences)));
        RecordingTarget zoom = RecordingTarget.Global(GlobalShortcutKind.LiveZoom);
        editor.Toggle(zoom);
        Assert.Equal("Press Ctrl, Alt or Win with a letter or number for Live zoom.", editor.Toast!.Message);
        editor.UpdateModifiers(ChordModifiers.Ctrl | ChordModifiers.Shift);
        Assert.Equal("Ctrl + Shift + key", editor.LiveModifierText);
        editor.HandleKey(Keys.Digit('2', ChordModifiers.Ctrl));
        Assert.Equal("Ctrl+2 already does Show / hide. Choose another combination.", editor.Toast!.Message);
        Assert.Null(model.Snapshot.Preferences.ZoomShortcut);
        Assert.Equal(zoom, editor.Recording);
        editor.HandleKey(Keys.Digit('9', ChordModifiers.Ctrl | ChordModifiers.Alt));
        Assert.Equal("⌃⌥9", model.Snapshot.Preferences.ZoomShortcut?.Label);
        Assert.Equal("Ctrl+Alt+9 now does Live zoom in any app.", editor.Toast!.Message);
        Assert.Equal((GlobalShortcutAction.LiveZoom, new ActivationShortcut(0x19, "9", control: true, option: true)), registrar.Attempts[^1]);
        Assert.Equal("Ctrl+Alt+9", model.Snapshot.Shortcut(GlobalShortcutAction.LiveZoom)?.Label);

        // Shift with a digit records the digit, whatever the layout types (the Mac's key-code digit table).
        editor.Toggle(RecordingTarget.Global(GlobalShortcutKind.Draw));
        editor.HandleKey(Keys.Digit('3', ChordModifiers.Ctrl | ChordModifiers.Shift, typed: "#"));
        Assert.Equal("⌃⇧3", model.Snapshot.Preferences.ResumeShortcut?.Label);

        // The Windows default for Hide only is Ctrl+Alt+Shift+2, so that chord is taken.
        editor.Toggle(RecordingTarget.Global(GlobalShortcutKind.Draw));
        editor.HandleKey(Keys.Digit('2', ChordModifiers.Ctrl | ChordModifiers.Alt | ChordModifiers.Shift, typed: "@"));
        Assert.Equal("Ctrl+Alt+Shift+2 already does Hide only. Choose another combination.", editor.Toast!.Message);
    }

    [Fact]
    public void GlobalShortcutsRefuseWhatWindowsCannotUse()
    {
        (WorkspaceModel model, ShortcutEditor editor, TestRegistrar registrar) = Editor("Ctrl+Alt+E");
        editor.Toggle(RecordingTarget.Global(GlobalShortcutKind.ShowHide));
        editor.HandleKey(Keys.Letter('a', ChordModifiers.Shift));
        Assert.Equal("Hold Ctrl, Alt or Win and press a letter or number.", editor.Toast!.Message);
        editor.HandleKey(Keys.F5(ChordModifiers.Ctrl));
        Assert.Equal("Hold Ctrl, Alt or Win and press a letter or number.", editor.Toast!.Message);
        // German ö sits where the US layout has a semicolon, outside the Mac's key table.
        editor.HandleKey(new RecordedKey(0xC0, 0x27, ChordModifiers.Ctrl, "ö"));
        Assert.Equal("Hold Ctrl, Alt or Win and press a letter from A to Z or a number.", editor.Toast!.Message);
        editor.HandleKey(Keys.Letter('e', ChordModifiers.Win));
        Assert.Equal("Windows uses Win+E for its own shortcut. Choose another combination.", editor.Toast!.Message);
        editor.HandleKey(Keys.Letter('e', ChordModifiers.Ctrl | ChordModifiers.Alt));
        Assert.Equal("Ctrl+Alt+E types a character with a keyboard layout on this computer. Choose another combination.", editor.Toast!.Message);
        Assert.Empty(registrar.Attempts);
        Assert.Equal(ActivationShortcut.Standard, model.Snapshot.Preferences.ActivationShortcut);

        // Windows or another app refuses the registration: the previous shortcut keeps working.
        registrar.Refuse = true;
        editor.HandleKey(Keys.Letter('j', ChordModifiers.Ctrl | ChordModifiers.Shift));
        Assert.Equal("Ctrl+Shift+J didn't work. Another app may use it. Show / hide still uses Ctrl+2.", editor.Toast!.Message);
        Assert.Equal(ActivationShortcut.Standard, model.Snapshot.Preferences.ActivationShortcut);
        Assert.Null(editor.Recording);
        Assert.False(editor.Toast.Persistent);
    }

    [Fact]
    public void AGlobalShortcutPressedWhileRecordingCountsAsItsKeys()
    {
        (WorkspaceModel model, ShortcutEditor editor, _) = Editor();
        Assert.False(editor.HandleHotkey(GlobalShortcutAction.ShowHide));
        editor.Toggle(RecordingTarget.Global(GlobalShortcutKind.HideOnly));
        Assert.True(editor.HandleHotkey(GlobalShortcutAction.ShowHide));
        Assert.Equal("Ctrl+2 already does Show / hide. Choose another combination.", editor.Toast!.Message);
        editor.Toggle(RecordingTarget.Local(ToolCommand(Tool.Pen)));
        Assert.True(editor.HandleHotkey(GlobalShortcutAction.ShowHide));
        Assert.Contains("without Ctrl", editor.Toast!.Message);
        Assert.Equal("7", model.Snapshot.Preferences.ShortcutFor(ToolCommand(Tool.Pen))?.Label);
    }

    [Fact]
    public void AShortcutTheRegistrarCannotTakeDoesNotChange()
    {
        (WorkspaceModel model, ShortcutEditor editor, TestRegistrar registrar) = Editor();
        registrar.ZoomBuilt = false;
        Assert.False(model.SupportsGlobalShortcut(GlobalShortcutAction.LiveZoom));
        Assert.True(model.SupportsGlobalShortcut(GlobalShortcutAction.ShowHide));
        editor.Toggle(RecordingTarget.Global(GlobalShortcutKind.LiveZoom));
        editor.HandleKey(Keys.Digit('9', ChordModifiers.Ctrl | ChordModifiers.Alt));
        Assert.IsType<GlobalShortcutChange.Unsupported>(model.SetGlobalShortcut(GlobalShortcutAction.LiveZoom, ActivationShortcut.LiveZoom));
        Assert.Null(model.Snapshot.Preferences.ZoomShortcut);
        Assert.Empty(registrar.Attempts);
        Assert.DoesNotContain(ShortcutPage.FixedKeys(model.Snapshot.Preferences, zoomAvailable: false), key => key.Keys == "Alt + scroll");
    }

    [Fact]
    public void ModifierKeysAloneOnlyShowWhatIsHeld()
    {
        (WorkspaceModel model, ShortcutEditor editor, _) = Editor();
        Assert.False(editor.HandleKey(Keys.Letter('j')));
        editor.Toggle(RecordingTarget.Global(GlobalShortcutKind.Draw));
        Assert.Equal("Press keys", editor.LiveModifierText);
        Assert.True(editor.HandleKey(new RecordedKey(0x11, 0x1D, ChordModifiers.Ctrl, null)));
        Assert.True(editor.HandleKey(new RecordedKey(0x5B, 0xE05B, ChordModifiers.Ctrl | ChordModifiers.Win, null)));
        Assert.Equal("Ctrl + Win + key", editor.LiveModifierText);
        Assert.NotNull(editor.Recording);
        Assert.Equal(ActivationShortcut.ResumeDrawing, GlobalShortcutKind.Draw.Value(model.Snapshot.Preferences));
        editor.Stop();
        Assert.Equal("Press keys", editor.LiveModifierText);
    }

    [Fact]
    public void ToastsExpireUnlessReplaced()
    {
        (_, ShortcutEditor editor, _) = Editor();
        editor.Say("Pen is in your toolbar now.");
        SettingsToast first = editor.Toast!;
        Assert.Equal("info.circle.fill", first.Symbol);
        Assert.Equal(TimeSpan.FromSeconds(3.2), first.Lifetime);
        editor.Say("Pen moved to More tools.");
        editor.Expire(first.Id);
        Assert.Equal("Pen moved to More tools.", editor.Toast!.Message);
        editor.Expire(editor.Toast.Id);
        Assert.Null(editor.Toast);
        editor.Toggle(RecordingTarget.Local(ShortcutCommand.ForTool(Tool.Pen)));
        Assert.Null(editor.Toast!.Lifetime);
        editor.Reset();
        Assert.Null(editor.Toast);
        Assert.Null(editor.Recording);
    }
}

public sealed class ToolbarArrangerTests
{
    private static IReadOnlyList<ShortcutCommand> Items(WorkspaceModel model) => [.. model.Snapshot.ToolbarItems];

    private static IReadOnlyList<Tool> Tools(WorkspaceModel model) =>
        [.. model.Snapshot.ToolbarItems.Where(item => item.Tool is not null).Select(item => item.Tool!.Value)];

    // Mac testArrangerAccessibilityActionsAddMoveAndRemoveTools.
    [Fact]
    public void AddMoveAndRemoveTools()
    {
        WorkspaceModel model = Create();
        ShortcutCommand star = ShortcutCommand.ForTool(Tool.Star);
        Assert.Equal("Star is in your toolbar now.", ToolbarArranger.Add(model, star));
        Assert.Null(ToolbarArranger.Add(model, star));
        Assert.Equal(Tool.Star, Tools(model)[^1]);
        Assert.True(ToolbarArranger.Move(model, star, -1));
        Assert.Equal([Tool.Star, Tool.Cover], Tools(model).TakeLast(2));
        Assert.False(ToolbarArranger.Move(model, ToolbarArranger.Select, -1));
        Assert.Equal("Star moved to More tools.", ToolbarArranger.Remove(model, star));
        Assert.DoesNotContain(Tool.Star, Tools(model));
        Assert.Contains(star, ToolbarArranger.Tray(Items(model)));
        Assert.Equal(ToolbarArranger.SelectStays, ToolbarArranger.Remove(model, ToolbarArranger.Select));
        Assert.Equal(Tool.Select, Tools(model)[0]);
    }

    // Mac testPresentingButtonsAndSingleEffectsCanBePutInTheToolbar (model part).
    [Fact]
    public void PresentingButtonsAndSingleEffectsCanBePutInTheToolbar()
    {
        WorkspaceModel model = Create();
        Assert.Equal([ShortcutAction.Properties, ShortcutAction.Board, ShortcutAction.ClearAll],
            ShortcutPage.AlwaysThere.Select(item => item.Action!.Value));
        ToolbarArranger.Add(model, ShortcutCommand.ForAction(ShortcutAction.Spotlight));
        ToolbarArranger.Add(model, ShortcutCommand.ForTool(Tool.Blur));
        Assert.Equal([ShortcutCommand.ForAction(ShortcutAction.Spotlight), ShortcutCommand.ForTool(Tool.Blur)], Items(model).TakeLast(2));
        Assert.Equal(new[] { "tool.select", "tool.rectangle", "tool.diamond", "tool.ellipse", "tool.arrow", "tool.line", "tool.pen", "tool.text",
                      "tool.highlighter", "tool.eraser", "tool.cover", "action.spotlight", "tool.blur" }, model.Snapshot.Preferences.ToolbarItems!.Value.ToArray());
        Assert.Equal(Tool.Blur, ToolbarToolLayout.Identity(Tool.Blur, [.. Items(model)]));
        Assert.Equal(Tool.Cover, ToolbarToolLayout.Identity(Tool.Distort, [.. Items(model)]));
        Assert.Equal("toolbar-action-spotlight", ToolbarArranger.Identifier(ShortcutCommand.ForAction(ShortcutAction.Spotlight), inToolbar: true));
        Assert.Equal("more-tool-blur", ToolbarArranger.Identifier(ShortcutCommand.ForTool(Tool.Blur), inToolbar: false));

        // A tools-only toolbar keeps the older format alone.
        model.ConfigureToolbar([Tool.Select, Tool.Pen, Tool.Blur]);
        Assert.Null(model.Snapshot.Preferences.ToolbarItems);
        Assert.Equal(new[] { "select", "pen", "cover" }, model.Snapshot.Preferences.ToolbarTools!.Value.ToArray());
        model.ConfigureToolbar([ShortcutCommand.ForTool(Tool.Pen)]);
        Assert.Equal("The drawing could not be opened: The toolbar must contain unique supported tools, including Select.",
            model.Snapshot.ErrorMessage);
        Assert.Equal(new[] { "select", "pen", "cover" }, model.Snapshot.Preferences.ToolbarTools!.Value.ToArray());
    }

    // Mac testDraggingToolsBetweenTheToolbarAndMoreTools, with the strip's tiles 48 points wide and 3 apart in one row.
    [Fact]
    public void DraggingToolsBetweenTheToolbarAndMoreTools()
    {
        WorkspaceModel model = Create();
        Rect? Frame(IReadOnlyList<ShortcutCommand> order, ShortcutCommand item)
        {
            int index = order.ToList().IndexOf(item);
            return index < 0 ? null : new Rect(12 + index * 51, 12, 48, 60);
        }
        ShortcutCommand star = ShortcutCommand.ForTool(Tool.Star);
        var drag = ArrangerDrag.Start(star, fromToolbar: false, new Point(100, 300), default, Items(model));
        Assert.False(drag.OverStrip);
        Point end = new(12 + 11 * 51 - 20, 40);
        Assert.True(drag.Update(end, overStrip: true, item => Frame(drag.Order, item)));
        Assert.True(drag.Adding);
        Assert.Equal("Star is in your toolbar now.", drag.End(model));
        Assert.Equal(Tool.Star, Tools(model)[^1]);
        Assert.True(drag.Settling);
        Assert.False(drag.Update(new Point(0, 0), overStrip: false, _ => null));

        ShortcutCommand pen = ShortcutCommand.ForTool(Tool.Pen);
        drag = ArrangerDrag.Start(pen, fromToolbar: true, new Point(12 + 6 * 51 + 24, 40), default, Items(model));
        drag.Update(new Point(16, 40), overStrip: true, item => Frame(drag.Order, item));
        Assert.Null(drag.End(model));
        Assert.Equal(Tool.Pen, Tools(model)[0]);

        drag = ArrangerDrag.Start(star, fromToolbar: true, new Point(12 + 11 * 51 + 24, 40), default, Items(model));
        drag.Update(new Point(300, 250), overStrip: false, item => Frame(drag.Order, item));
        Assert.True(drag.Removing);
        Assert.Equal(("Let go to remove it from your toolbar", "minus.circle.fill", true), drag.TrayMessage);
        Assert.Equal("Star moved to More tools.", drag.End(model));
        Assert.DoesNotContain(Tool.Star, Tools(model));
        Assert.Contains(Tool.Select, Tools(model));

        drag = ArrangerDrag.Start(ToolbarArranger.Select, fromToolbar: true, new Point(40, 40), default, Items(model));
        Assert.False(drag.Update(new Point(300, 250), overStrip: false, item => Frame(drag.Order, item)));
        Assert.False(drag.Removing);
        Assert.Equal(("Select always stays in the toolbar", "lock.fill", false), drag.TrayMessage);
        Assert.Equal(ToolbarArranger.SelectStays, drag.End(model));
        Assert.Contains(Tool.Select, Tools(model));
    }

    [Fact]
    public void InsertionFollowsRowsAndMiddles()
    {
        ShortcutCommand[] items = [.. WorkspacePreferences.DefaultToolbar.Take(4).Select(ShortcutCommand.ForTool)];
        // Two rows of two.
        Rect? Frame(ShortcutCommand item) => Array.IndexOf(items, item) switch
        {
            0 => new Rect(0, 0, 48, 60),
            1 => new Rect(51, 0, 48, 60),
            2 => new Rect(0, 66, 48, 60),
            3 => new Rect(51, 66, 48, 60),
            _ => null,
        };
        Assert.Equal(0, ToolbarArranger.InsertionIndex(new Point(10, 30), items, Frame));
        Assert.Equal(1, ToolbarArranger.InsertionIndex(new Point(60, 30), items, Frame));
        Assert.Equal(2, ToolbarArranger.InsertionIndex(new Point(90, 30), items, Frame));
        Assert.Equal(2, ToolbarArranger.InsertionIndex(new Point(90, 62), items, Frame));
        Assert.Equal(3, ToolbarArranger.InsertionIndex(new Point(60, 100), items, Frame));
        Assert.Equal(4, ToolbarArranger.InsertionIndex(new Point(120, 100), items, Frame));
        Assert.Equal(0, ToolbarArranger.InsertionIndex(new Point(500, -10), items, Frame));
    }

    /// <summary>The tray shows the Mac capture's tiles in its order, under its headings (settings/tab-toolbar).</summary>
    [Fact]
    public void TrayMatchesTheMacReference()
    {
        JsonNode reference = JsonNode.Parse(File.ReadAllText(Path.Combine(UXReference.Folder("settings"), "tab-toolbar-light.json")))!;
        var identifiers = new List<string>();
        var headings = new List<string>();
        var strip = new List<string>();
        void Walk(JsonNode? node)
        {
            if (node is JsonArray array)
            {
                foreach (JsonNode? child in array)
                {
                    Walk(child);
                }
                return;
            }
            if (node is not JsonObject item)
            {
                return;
            }
            string? identifier = item["identifier"]?.GetValue<string>();
            if (identifier?.StartsWith("more-", StringComparison.Ordinal) == true)
            {
                identifiers.Add(identifier);
            }
            if (identifier?.StartsWith("toolbar-tool-", StringComparison.Ordinal) == true)
            {
                strip.Add(identifier);
            }
            if (item["role"]?.GetValue<string>() == "AXHeading")
            {
                headings.Add(item["label"]!.GetValue<string>());
            }
            Walk(item["children"]);
        }
        Walk(reference["accessibility"]);
        IReadOnlyList<ShortcutCommand> order = [.. WorkspacePreferences.DefaultToolbar.Select(ShortcutCommand.ForTool)];
        Assert.Equal(strip, order.Select(item => ToolbarArranger.Identifier(item, inToolbar: true)));
        IReadOnlyList<(TrayGroup Group, IReadOnlyList<ShortcutCommand> Members)> groups = ToolbarArranger.TrayGroups(order);
        Assert.Equal(headings, groups.Select(group => group.Group.RawValue));
        Assert.Equal(identifiers, groups.SelectMany(group => group.Members).Select(item => ToolbarArranger.Identifier(item, inToolbar: false)));
        Assert.Equal("Drag one up to add it, or click its plus button.", ToolbarArranger.TrayHint(order));
        Assert.Equal("Everything is in your toolbar.", ToolbarArranger.TrayHint(WorkspaceModel.ToolbarCandidates));
    }
}

public sealed class ShortcutPreferenceTests
{
    [Fact]
    public void BindingReportsMovesAndClearsWithTheMacStatus()
    {
        WorkspaceModel model = Create();
        ShortcutCommand spotlight = ShortcutCommand.ForAction(ShortcutAction.Spotlight);
        var result = Assert.IsType<ShortcutBindingResult.Assigned>(model.BindShortcut(LocalShortcut.Create("2"), spotlight));
        Assert.Equal(ShortcutCommand.ForTool(Tool.Rectangle), result.Displaced);
        Assert.Equal("2 is now the key for Spotlight. Rectangle has no key now.", model.Snapshot.Status);
        Assert.IsType<ShortcutBindingResult.Cleared>(model.BindShortcut(null, spotlight));
        Assert.Equal("Spotlight has no key.", model.Snapshot.Status);
        var failed = Assert.IsType<ShortcutBindingResult.Failed>(model.BindShortcut(LocalShortcut.Create("r"), spotlight));
        Assert.Equal("R changes the ink color. Choose another key.", failed.Message);
        Assert.IsType<ShortcutBindingResult.Assigned>(model.BindShortcut(LocalShortcut.Create("j", shift: true), ShortcutCommand.ForTool(Tool.Pen)));
        Assert.Equal("Shift+J is now the key for Pen.", model.Snapshot.Status);
        WorkspacePreferences before = model.Snapshot.Preferences;
        model.ResetLocalShortcuts();
        Assert.Equal(WorkspacePreferences.DefaultToolShortcuts, model.Snapshot.Preferences.ToolShortcuts);
        Assert.Null(model.Snapshot.Preferences.CommandShortcuts);
        Assert.Equal("Tool keys are back to their defaults.", model.Snapshot.Status);
        model.RestoreLocalShortcuts(before);
        Assert.Equal("Shift+J", WindowsShortcuts.Label(model.Snapshot.Preferences.ShortcutFor(ShortcutCommand.ForTool(Tool.Pen))!.Value));
        Assert.Equal("Keys restored.", model.Snapshot.Status);
    }

    // Mac testMinimalToolbarIsSmallerInBothDirections (model and layout part).
    [Fact]
    public void MinimalToolbarIsStoredAndSmaller()
    {
        WorkspaceModel model = Create();
        model.SetToolbarSize(ToolbarSize.Minimal);
        Assert.Equal(ToolbarSize.Minimal, model.Snapshot.Preferences.ToolbarSize);
        ShortcutCommand[] items = [.. model.Snapshot.ToolbarItems];
        double minimal = ToolbarToolLayout.HorizontalWidth(items, 1200, propertiesVisible: false, ToolbarSize.Minimal);
        double expanded = ToolbarToolLayout.HorizontalWidth(items, 1200, propertiesVisible: false, ToolbarSize.Expanded);
        Assert.True(minimal < expanded - 150, $"{minimal} against {expanded}");
        Assert.Equal(50, new ToolbarMetrics(ToolbarSize.Minimal).VerticalRailWidth);
        model.SetToolbarSize(ToolbarSize.Expanded);
        Assert.Null(model.Snapshot.Preferences.ToolbarSize);
    }

    // Mac testSharedStyleIsOffByDefaultAndCopiesOnlyChangesWhenOn.
    [Fact]
    public void SharedStyleIsOffByDefaultAndCopiesOnlyChangesWhenOn()
    {
        // An existing profile with no tool styles, so every tool starts from its solid defaults.
        var store = new MemoryPreferencesStore(new WorkspacePreferences().Data());
        WorkspaceModel model = Create(store: store);
        model.ShowToolbar();
        model.Choose(Tool.Pen);
        model.ApplyStyle((ref Style style) => style.SetPaintColor(new RGBA(0, 0, 1), fill: false));
        Assert.False(model.Snapshot.Preferences.ToolStyles.ContainsKey(Tool.Rectangle.RawValue));
        Assert.Null(model.Snapshot.Preferences.ShareToolStyles);

        model.SetShareToolStyles(true);
        Assert.Equal("Your next style change applies to every drawing tool.", model.Snapshot.Status);
        model.ApplyStyle((ref Style style) => style.Width = 9);
        Style rectangle = model.Snapshot.Preferences.ToolStyles[Tool.Rectangle.RawValue];
        Assert.Equal(9, rectangle.Width);
        Assert.Equal(Style.Defaults(Tool.Rectangle).Color, rectangle.Color);
        var green = new RGBA(0.28, 0.69, 0.4);
        model.ApplyStyle((ref Style style) => style.SetPaintColor(green, fill: false));
        foreach (Tool tool in new[] { Tool.Rectangle, Tool.Highlighter, Tool.Text, Tool.Arrow, Tool.Star })
        {
            Assert.Equal(green, model.Snapshot.Preferences.ToolStyles[tool.RawValue].Color);
        }
        foreach (Tool tool in new[] { Tool.Blur, Tool.Eraser, Tool.Image, Tool.Cover, Tool.Select })
        {
            Assert.False(model.Snapshot.Preferences.ToolStyles.ContainsKey(tool.RawValue), tool.RawValue);
        }
        WorkspaceModel reloaded = Create(store: store);
        Assert.True(reloaded.Snapshot.Preferences.ShareToolStyles);
        Assert.Equal(9, reloaded.Snapshot.Preferences.ToolStyles[Tool.Star.RawValue].Width);

        model.SetShareToolStyles(false);
        Assert.Null(model.Snapshot.Preferences.ShareToolStyles);
        Assert.Equal("Each tool keeps its own style.", model.Snapshot.Status);
        model.ApplyStyle((ref Style style) => style.Width = 2);
        Assert.Equal(9, model.Snapshot.Preferences.ToolStyles[Tool.Rectangle.RawValue].Width);
    }

    [Fact]
    public void ShowTipsOnScreenIsSaved()
    {
        var store = new MemoryPreferencesStore();
        WorkspaceModel model = Create(store: store);
        Assert.Null(model.Snapshot.Preferences.ShowShortcutHints);
        model.SetShortcutHints(false);
        Assert.False(Create(store: store).Snapshot.Preferences.ShowShortcutHints);
        model.SetShortcutHints(true);
        Assert.True(model.Snapshot.Preferences.ShowShortcutHints);
    }

    [Fact]
    public void GlobalShortcutChangesKeepThePreviousOneWhenRegistrationFails()
    {
        WorkspaceModel model = Create();
        var registrar = new TestRegistrar { Refuse = true };
        model.GlobalShortcutRegistrar = registrar;
        model.SetGlobalShortcutState(new GlobalShortcutState(GlobalShortcutAction.ShowHide, "Ctrl+2", GlobalShortcutStatus.Registered, null));
        var refused = Assert.IsType<GlobalShortcutChange.NotRegistered>(
            model.SetGlobalShortcut(GlobalShortcutAction.ShowHide, new ActivationShortcut(0x26, "j", control: true, shift: true)));
        Assert.Equal(GlobalShortcutStatus.AlreadyUsed, refused.State.Status);
        Assert.Equal(ActivationShortcut.Standard, model.Snapshot.Preferences.ActivationShortcut);
        Assert.Equal(GlobalShortcutStatus.Registered, model.Snapshot.Shortcut(GlobalShortcutAction.ShowHide)!.Status);
        Assert.Equal("Ctrl+2", model.Snapshot.Shortcut(GlobalShortcutAction.ShowHide)!.Label);
        Assert.StartsWith("The Ctrl+Shift+J shortcut could not be registered (error 1409).", model.Snapshot.ErrorMessage);

        registrar.Refuse = false;
        Assert.IsType<GlobalShortcutChange.Changed>(
            model.SetGlobalShortcut(GlobalShortcutAction.ShowHide, new ActivationShortcut(0x26, "j", control: true, shift: true)));
        Assert.Equal("Ctrl+Shift+J", model.Snapshot.Shortcut(GlobalShortcutAction.ShowHide)!.Label);
        Assert.Equal("Ctrl+Shift+J opens drawing or hides the tools. Ctrl+Alt+Shift+2 always hides them.", model.Snapshot.Status);

        // A profile must stay valid on the Mac, where Hide only without its own shortcut is Control-Option-2.
        var invalid = Assert.IsType<GlobalShortcutChange.Invalid>(
            model.SetGlobalShortcut(GlobalShortcutAction.ResumeDrawing, new ActivationShortcut(0x13, "2", control: true, option: true)));
        Assert.Equal("Show / hide, Hide only, Live zoom and Resume drawing need different shortcuts.", invalid.Message);
        Assert.Null(model.Snapshot.Preferences.ResumeShortcut);
        Assert.Equal(2, registrar.Attempts.Count);
    }

    [Fact]
    public void WithoutARegistrarAChangeIsOnlySaved()
    {
        var store = new MemoryPreferencesStore();
        WorkspaceModel model = Create(store: store);
        Assert.True(model.SupportsGlobalShortcut(GlobalShortcutAction.HideOnly));
        Assert.True(model.SupportsGlobalShortcut(GlobalShortcutAction.LiveZoom));
        var changed = Assert.IsType<GlobalShortcutChange.Changed>(
            model.SetGlobalShortcut(GlobalShortcutAction.HideOnly, new ActivationShortcut(0x04, "h", control: true, option: true, shift: true)));
        Assert.Null(changed.State);
        Assert.Equal("Ctrl+Alt+Shift+H", model.Snapshot.Shortcut(GlobalShortcutAction.HideOnly)!.Label);
        Assert.Equal("⌃⌥⇧H", Create(store: store).Snapshot.Preferences.HideShortcut?.Label);
    }

    [Fact]
    public void KeycapsUseWindowsNames()
    {
        var preferences = new WorkspacePreferences();
        Assert.Equal(["Ctrl", "2"], WindowsShortcuts.Keys(GlobalShortcutKind.ShowHide.Value(preferences)));
        Assert.Equal(["Ctrl", "Shift", "2"], WindowsShortcuts.Keys(GlobalShortcutKind.Draw.Value(preferences)));
        Assert.Equal(["Ctrl", "Alt", "Shift", "2"], WindowsShortcuts.Keys(GlobalShortcutKind.HideOnly.Value(preferences)));
        Assert.Equal("Ctrl + 1", WindowsShortcuts.Spoken(GlobalShortcutKind.LiveZoom.Value(preferences)));
        Assert.Equal("Ctrl+Alt+Shift+2", WindowsShortcuts.Label(GlobalShortcutKind.HideOnly.Value(preferences)));
        Assert.Equal(WindowsShortcuts.HideOnlyDefault, WindowsShortcuts.Chord(WindowsShortcuts.HideOnlyDefaultShortcut));
        var win = new ActivationShortcut(0x00, "a", control: true, option: true, command: true, shift: true);
        Assert.Equal(["Ctrl", "Alt", "Shift", "Win", "A"], WindowsShortcuts.Keys(win));
        Assert.Equal("Ctrl+Alt+Shift+Win+A", WindowsShortcuts.Label(win));
        Assert.Equal(["global-shortcut-draw", "global-shortcut-showHide", "global-shortcut-hideOnly", "global-shortcut-liveZoom",
                      "global-shortcut-breakTimer"],
            GlobalShortcutKind.AllCases.Select(kind => "global-shortcut-" + kind.RawValue));
    }

    [Fact]
    public void FixedKeysUseWindowsKeys()
    {
        var preferences = new WorkspacePreferences();
        Assert.Equal(["R G B Y O P C W K, ink colors", "Ctrl+W, whiteboard", "Ctrl+B, blackboard", "Ctrl+Z, undo", "Ctrl+Shift+Z, redo",
                      "Ctrl+L, more tools", "Alt + scroll, zoom", "Esc, use apps", "Delete, delete selection", "← ↑ → ↓, nudge; add Shift for 10 px"],
            ShortcutPage.FixedKeys(preferences, zoomAvailable: true).Select(key => key.Spoken));
        preferences.ZoomShortcut = new ActivationShortcut(0x25, "l", control: true);
        Assert.DoesNotContain(ShortcutPage.FixedKeys(preferences, zoomAvailable: true), key => key.Keys == "Ctrl+L");
        Assert.Equal("Shift + S", ShortcutPage.ChipValue(LocalShortcut.Create("s", shift: true)));
        Assert.Equal("None", ShortcutPage.ChipValue(null));
        Assert.Equal("Pen: Shift+S. Click, then press a new key.", ShortcutPage.ChipHelp("Pen", LocalShortcut.Create("s", shift: true)));
        Assert.Equal("Click, then press a key for Star.", ShortcutPage.ChipHelp("Star", null));
    }

    [Fact]
    public void WindowsKeepsItsOwnShortcuts()
    {
        static ShortcutChord Chord(ChordModifiers modifiers, string key, uint? virtualKey = null) =>
            new(modifiers, 0, key, virtualKey);
        Assert.True(ReservedShortcuts.IsReserved(Chord(ChordModifiers.Win, "L")));
        Assert.True(ReservedShortcuts.IsReserved(Chord(ChordModifiers.Win | ChordModifiers.Shift, "S")));
        Assert.True(ReservedShortcuts.IsReserved(Chord(ChordModifiers.Win, "1")));
        Assert.True(ReservedShortcuts.IsReserved(Chord(ChordModifiers.Win | ChordModifiers.Ctrl | ChordModifiers.Shift, "5")));
        Assert.True(ReservedShortcuts.IsReserved(Chord(ChordModifiers.Win | ChordModifiers.Ctrl, "D")));
        Assert.False(ReservedShortcuts.IsReserved(Chord(ChordModifiers.Win | ChordModifiers.Ctrl | ChordModifiers.Alt, "J")));
        Assert.False(ReservedShortcuts.IsReserved(Chord(ChordModifiers.Win | ChordModifiers.Shift, "J")));
        Assert.False(ReservedShortcuts.IsReserved(Chord(ChordModifiers.Win, "1", 0x61)));
        Assert.False(ReservedShortcuts.IsReserved(Chord(ChordModifiers.Ctrl | ChordModifiers.Shift, "1")));
        Assert.False(ReservedShortcuts.IsReserved(Chord(ChordModifiers.Ctrl, "E")));
    }

    [Fact]
    public void ToolbarCandidatesFollowTheMacOrder()
    {
        IReadOnlyList<ShortcutCommand> candidates = WorkspaceModel.ToolbarCandidates;
        Assert.Equal(ShortcutCommand.ForTool(Tool.Select), candidates[0]);
        Assert.DoesNotContain(ShortcutCommand.ForTool(Tool.Blur), candidates.Take(candidates.Count - 8));
        Assert.Equal([Tool.Blur, Tool.Pixelate, Tool.Distort], candidates.Skip(candidates.Count - 8).Take(3).Select(item => item.Tool!.Value));
        Assert.Equal(ShortcutAction.ToolbarChoices, candidates.TakeLast(5).Select(item => item.Action!.Value));
        Assert.Equal(candidates.Count, candidates.Distinct().Count());
    }
}
