// Port of apps/macos/Tests/AnnotationCoreTests/ShortcutBindingTests.swift.
using static Annotator.Core.Tests.TestSupport;

namespace Annotator.Core.Tests;

public class ShortcutBindingTests
{
    [Fact]
    public void LocalShortcutsParseStorageKeysAndRejectUnsafeKeys()
    {
        Assert.Equal("t", LocalShortcut.Create("T")?.Key);
        Assert.Equal("⇧T", LocalShortcut.Create("t", shift: true)?.Label);
        Assert.Equal("Shift + T", LocalShortcut.Create("t", shift: true)?.SpokenLabel);
        Assert.Equal(LocalShortcut.Create("t", shift: true), LocalShortcut.FromStorageKey("⇧t"));
        Assert.Equal(LocalShortcut.Create("5"), LocalShortcut.FromStorageKey("5"));
        Assert.Null(LocalShortcut.Create("5", shift: true));
        Assert.Null(LocalShortcut.Create("ab"));
        Assert.Null(LocalShortcut.Create("-"));
        Assert.Null(LocalShortcut.FromStorageKey("T"));
        Assert.Equal(ShortcutCommand.ForTool(Tool.Star), ShortcutCommand.FromId("tool.star"));
        Assert.Equal(ShortcutCommand.ForAction(ShortcutAction.Spotlight), ShortcutCommand.FromId("action.spotlight"));
        Assert.Null(ShortcutCommand.FromId("action.unknown"));
    }

    [Fact]
    public void EveryToolAndButtonCanHaveAKeyAndMovedKeysAreReported()
    {
        var preferences = new WorkspacePreferences();
        var star = Assert.NotNull(LocalShortcut.Create("s", shift: true));
        Assert.Null(preferences.Bind(star, ShortcutCommand.ForTool(Tool.Star)));
        Assert.Equal(star, preferences.ShortcutFor(ShortcutCommand.ForTool(Tool.Star)));
        Assert.Equal(Tool.Cover.RawValue, preferences.ToolShortcuts["s"]);
        Assert.Equal(ShortcutCommand.ForTool(Tool.Star), preferences.CommandForPressed(star));

        var e = Assert.NotNull(LocalShortcut.Create("e"));
        Assert.Null(preferences.Bind(e, ShortcutCommand.ForAction(ShortcutAction.Spotlight)));
        Assert.Equal(ShortcutCommand.ForAction(ShortcutAction.Spotlight), preferences.CommandForPressed(e));

        // Taking a key that a tool uses moves it and says which tool lost it.
        var two = Assert.NotNull(LocalShortcut.Create("2"));
        Assert.Equal(ShortcutCommand.ForTool(Tool.Rectangle), preferences.Bind(two, ShortcutCommand.ForAction(ShortcutAction.Properties)));
        Assert.Null(preferences.ShortcutFor(ShortcutCommand.ForTool(Tool.Rectangle)));
        Assert.False(preferences.ToolShortcuts.ContainsKey("2"));
        Assert.Equal(ShortcutCommand.ForAction(ShortcutAction.Properties), preferences.CommandForPressed(two));

        // A tool can take a plain key from a button; it returns to the compatible tool map.
        Assert.Equal(ShortcutCommand.ForAction(ShortcutAction.Properties), preferences.Bind(two, ShortcutCommand.ForTool(Tool.Rectangle)));
        Assert.Equal(Tool.Rectangle.RawValue, preferences.ToolShortcuts["2"]);
        Assert.Null(preferences.ShortcutFor(ShortcutCommand.ForAction(ShortcutAction.Properties)));

        // Rebinding keeps one key per command, and clearing removes it.
        var j = Assert.NotNull(LocalShortcut.Create("j"));
        preferences.Bind(j, ShortcutCommand.ForTool(Tool.Star));
        Assert.Equal(j, preferences.ShortcutFor(ShortcutCommand.ForTool(Tool.Star)));
        Assert.Equal(ShortcutCommand.ForTool(Tool.Cover), preferences.CommandForPressed(star));
        preferences.Bind(null, ShortcutCommand.ForTool(Tool.Star));
        Assert.Null(preferences.ShortcutFor(ShortcutCommand.ForTool(Tool.Star)));
        Assert.Equal(preferences, WorkspacePreferences.Read(preferences.Data()));
    }

    [Fact]
    public void InkColorKeysStayReservedWithAndWithoutShift()
    {
        var preferences = new WorkspacePreferences();
        var before = preferences;
        foreach (var key in InkShortcut.Choices.Keys)
        {
            foreach (var shift in new[] { false, true })
            {
                var shortcut = Assert.NotNull(LocalShortcut.Create(key, shift));
                var candidate = preferences;
                Assert.Throws<CanvasError>(() => candidate.Bind(shortcut, ShortcutCommand.ForTool(Tool.Star)));
                Assert.Throws<CanvasError>(() => candidate.Bind(shortcut, ShortcutCommand.ForAction(ShortcutAction.Spotlight)));
                Assert.Equal(before, candidate);
            }
        }
        Assert.Equal(before, preferences);
    }

    [Fact]
    public void CommandShortcutValidationRejectsDuplicatesAndStaysOptional()
    {
        var preferences = new WorkspacePreferences();
        foreach (var invalid in new[]
                 {
                     ("1", ShortcutCommand.ForAction(ShortcutAction.Spotlight).Id), ("⇧a", "tool.pen"), ("j", "tool.star"),
                     ("⇧r", "action.spotlight"), ("j", "action.nothing"),
                 })
        {
            preferences.CommandShortcuts = WorkspacePreferences.EmptyStrings.SetItem(invalid.Item1, invalid.Item2);
            var candidate = preferences;
            Assert.Throws<CanvasError>(() => candidate.Validate());
        }
        preferences.CommandShortcuts = WorkspacePreferences.EmptyStrings
            .SetItem("j", "action.spotlight").SetItem("⇧j", "tool.star").SetItem("e", "action.board");
        preferences.ToolbarSize = ToolbarSize.Minimal;
        preferences.ShareToolStyles = true;
        var restored = WorkspacePreferences.Read(preferences.Data());
        Assert.Equal(preferences, restored);

        // Older profiles have none of the new fields; they read with no extra keys and the original toolbar.
        var json = JsonObjectOf(new WorkspacePreferences().Data());
        json.Remove("commandShortcuts");
        json.Remove("toolbarSize");
        json.Remove("shareToolStyles");
        var legacy = WorkspacePreferences.Read(Bytes(json));
        Assert.True(legacy.CommandShortcuts is null && legacy.ToolbarSize is null && legacy.ShareToolStyles is null);

        // The legacy single-key API still refuses a key a button uses.
        preferences = new WorkspacePreferences();
        preferences.Bind(LocalShortcut.Create("j"), ShortcutCommand.ForAction(ShortcutAction.ClearAll));
        var withButton = preferences;
        Assert.Throws<CanvasError>(() => withButton.AssignShortcut("j", Tool.Pen));
        preferences.ResetLocalShortcuts();
        Assert.Equal(WorkspacePreferences.DefaultToolShortcuts, preferences.ToolShortcuts);
        Assert.Null(preferences.CommandShortcuts);
    }

    [Fact]
    public void SharedStyleCopiesOnlyWhatChanged()
    {
        var pen = Style.Defaults(Tool.Pen);
        pen.Width = 6;
        var highlighter = Style.Defaults(Tool.Highlighter);
        var shared = highlighter;
        var changed = pen;
        changed.SetPaintColor(Palette.Blue, fill: false);
        shared.AdoptSharedChanges(pen, changed, supportsFill: false);
        Assert.Equal(Palette.Blue, shared.Color);
        Assert.Equal(highlighter.Width, shared.Width);

        var rectangle = Style.Defaults(Tool.Rectangle);
        rectangle.FontSize = 40;
        var filled = changed;
        filled.SetFillEnabled(true);
        filled.Pattern = StrokePattern.Dashed;
        filled.Roughness = 2;
        filled.EndHead = ArrowHead.Filled;
        filled.FontSize = 12;
        rectangle.AdoptSharedChanges(changed, filled, supportsFill: true);
        Assert.Equal(filled.Fill, rectangle.Fill);
        Assert.True(rectangle.Pattern == StrokePattern.Dashed && rectangle.Roughness == 2);
        Assert.Equal(40.0, rectangle.FontSize);
        Assert.Equal(Style.Defaults(Tool.Rectangle).EndHead, rectangle.EndHead);

        var line = Style.Defaults(Tool.Line);
        line.AdoptSharedChanges(changed, filled, supportsFill: false);
        Assert.Null(line.Fill);
        Assert.True(Tool.Pen.SharesStyle && Tool.Rectangle.SharesStyle && Tool.Text.SharesStyle);
        Assert.True(!Tool.Select.SharesStyle && !Tool.Eraser.SharesStyle && !Tool.Blur.SharesStyle && !Tool.Cover.SharesStyle);
    }

    [Fact]
    public void ToolbarItemsAllowPresentingButtonsButKeepSelectAndFixedButtons()
    {
        var preferences = new WorkspacePreferences();
        preferences.ToolbarItems = Strings("tool.select", "tool.pen", "tool.blur", "action.spotlight", "action.smartDraw");
        preferences.ToolbarTools = Strings("select", "pen", "blur");
        Assert.Equal(preferences, WorkspacePreferences.Read(preferences.Data()));
        string[][] invalidItems =
        [
            ["tool.pen", "action.spotlight"], ["tool.select", "action.properties"], ["tool.select", "action.board"],
            ["tool.select", "tool.pen", "tool.pen"], ["tool.select", "action.nothing"], [],
        ];
        foreach (var invalid in invalidItems)
        {
            preferences.ToolbarItems = Strings(invalid);
            var candidate = preferences;
            Assert.Throws<CanvasError>(() => candidate.Validate());
        }
        Assert.Equal(
            new[] { ShortcutAction.Spotlight, ShortcutAction.CursorHalo, ShortcutAction.PointerLens, ShortcutAction.SmartDraw, ShortcutAction.ShareRegion },
            ShortcutAction.ToolbarChoices);
        preferences.ToolbarItems = Strings("tool.select", "action.shareRegion");
        preferences.ToolbarTools = Strings("select");
        preferences.Bind(LocalShortcut.Create("j"), ShortcutCommand.ForAction(ShortcutAction.ShareRegion));
        Assert.Equal(preferences, WorkspacePreferences.Read(preferences.Data()));
        Assert.False(ShortcutAction.ClearAll.CanBeInToolbar);
    }
}
