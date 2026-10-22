using System.Text.Json.Nodes;
using Annotator.Core;
using Annotator.Workspace.Canvas;
using Annotator.Workspace.Shortcuts;

namespace Annotator.Conformance.Tests;

/// <summary>
/// Section <c>shortcuts</c>: local key parsing and labels, ink colors, command IDs, global shortcut checks, the scripted
/// bind and assign sessions, and the macOS key table of WIN-INP-04 that the Windows shortcut code uses. Exact.
/// </summary>
public class ShortcutsTests
{
    public static TheoryData<string> LocalKeyCases => CorpusCases.Of("shortcuts/local-keys.jsonl", record => record["input"]!.ToJsonString());

    [Theory]
    [MemberData(nameof(LocalKeyCases))]
    public void LocalKeysParseAndLabelAsOnTheMac(string name)
    {
        var record = CorpusCases.Record(name);
        OpenDifferences.Check(CorpusCases.Id(name), () =>
        {
            var input = record["input"]!;
            var shortcut = input["storageKey"] is { } storage
                ? LocalShortcut.FromStorageKey(storage.GetValue<string>())
                : LocalShortcut.Create(input["key"]!.GetValue<string>(), input["shift"]!.GetValue<bool>());
            JsonDiff.Equal(record["result"], Describe(shortcut), NumberRule.Exact);
        });
    }

    private static JsonObject? Describe(LocalShortcut? shortcut) => shortcut is not { } key ? null : new JsonObject
    {
        ["isInkColor"] = key.IsInkColor,
        ["key"] = key.Key,
        ["label"] = key.Label,
        ["shift"] = key.Shift,
        ["spokenLabel"] = key.SpokenLabel,
        ["storageKey"] = key.StorageKey,
    };

    [Fact]
    public void InkColorKeysMatchTheMac()
    {
        var actual = new JsonObject(InkShortcut.Choices.Select(entry => KeyValuePair.Create(entry.Key,
            (JsonNode?)new JsonObject { ["color"] = Outcome.Value(entry.Value.Color), ["name"] = entry.Value.Name })));
        JsonDiff.Equal(Corpus.Json("shortcuts/ink-colors.json"), actual, NumberRule.Exact);
    }

    [Fact]
    public void CommandsToolsAndActionsMatchTheMac()
    {
        var expected = Corpus.Json("shortcuts/commands.json");
        var parsing = new JsonArray();
        foreach (var record in expected["parsing"]!.AsArray())
        {
            var id = record!["id"]!.GetValue<string>();
            parsing.Add(new JsonObject
            {
                ["id"] = id,
                ["result"] = ShortcutCommand.FromId(id) is { } command ? new JsonObject { ["id"] = command.Id, ["label"] = command.Label } : null,
            });
        }
        var actual = new JsonObject
        {
            ["actions"] = new JsonArray(ShortcutAction.AllCases.Select(action => (JsonNode)new JsonObject
            {
                ["canBeInToolbar"] = action.CanBeInToolbar,
                ["detail"] = action.Detail,
                ["id"] = ShortcutCommand.ForAction(action).Id,
                ["label"] = action.Label,
                ["symbol"] = action.Symbol,
            }).ToArray()),
            ["parsing"] = parsing,
            ["toolbarChoices"] = new JsonArray(ShortcutAction.ToolbarChoices.Select(action => (JsonNode)action.RawValue).ToArray()),
            ["tools"] = new JsonArray(Tool.AllCases.Select(tool => (JsonNode)new JsonObject
            {
                ["id"] = ShortcutCommand.ForTool(tool).Id,
                ["isProtection"] = tool.IsProtection,
                ["isShape"] = tool.IsShape,
                ["label"] = ShortcutCommand.ForTool(tool).Label,
                ["sharesStyle"] = tool.SharesStyle,
                ["supportsFill"] = tool.SupportsFill,
                ["systemShapeAspectRatio"] = tool.SystemShapeAspectRatio is { } ratio ? JsonValue.Create(ratio) : null,
                ["toolLabel"] = tool.Label,
            }).ToArray()),
        };
        JsonDiff.Equal(expected, actual, NumberRule.Exact);
    }

    public static TheoryData<string> GlobalCases => CorpusCases.Of("shortcuts/global.jsonl", record =>
        record["name"]?.GetValue<string>() ?? "usesSameKeys " + record["b"]!.ToJsonString());

    [Theory]
    [MemberData(nameof(GlobalCases))]
    public void GlobalShortcutsValidateAsOnTheMac(string name)
    {
        var record = CorpusCases.Record(name);
        OpenDifferences.Check(CorpusCases.Id(name), () =>
        {
            var actual = record.DeepClone().AsObject();
            if (record["name"] is { } label)
            {
                var shortcut = label.GetValue<string>() switch
                {
                    "standard" => ActivationShortcut.Standard,
                    "hideTools" => ActivationShortcut.HideTools,
                    "liveZoom" => ActivationShortcut.LiveZoom,
                    "resumeDrawing" => ActivationShortcut.ResumeDrawing,
                    _ => AnnotatorJson.Decode<ActivationShortcut>(System.Text.Encoding.UTF8.GetBytes(record["shortcut"]!.ToJsonString())),
                };
                actual["label"] = shortcut.Label;
                actual["shortcut"] = Outcome.Value(shortcut);
                actual["validate"] = Outcome.Of(() => { shortcut.Validate(); return null; });
            }
            else
            {
                var a = AnnotatorJson.Decode<ActivationShortcut>(System.Text.Encoding.UTF8.GetBytes(record["a"]!.ToJsonString()));
                var b = AnnotatorJson.Decode<ActivationShortcut>(System.Text.Encoding.UTF8.GetBytes(record["b"]!.ToJsonString()));
                actual["usesSameKeys"] = a.UsesSameKeys(b);
            }
            JsonDiff.Equal(record, actual, NumberRule.Exact);
        });
    }

    [Fact]
    public void BindScriptMatchesTheMac()
    {
        var expected = Corpus.Lines("shortcuts/bind-script.jsonl");
        var preferences = new WorkspacePreferences();
        var actual = new List<JsonObject> { State(preferences, "start with the defaults") };
        void Bind(string? key, string id, bool shift = false)
        {
            var shortcut = key is null ? null : LocalShortcut.Create(key, shift);
            var command = ShortcutCommand.FromId(id)!.Value;
            var label = key is null ? "no key" : (shift ? "\u21E7" : "") + key;
            var result = Outcome.Of(() => preferences.Bind(shortcut, command)?.Id ?? "");
            var state = State(preferences, $"bind {label} to {id}");
            state["result"] = result;
            actual.Add(state);
        }
        Bind("j", "action.spotlight");
        Bind("a", "tool.arrow", shift: true);
        Bind("a", "action.board");
        Bind("r", "tool.pen");
        Bind("b", "action.cursorHalo", shift: true);
        Bind(null, "action.spotlight");
        Bind("7", "tool.text");
        Bind("5", "action.clearAll");
        Bind("j", "tool.select", shift: true);
        Bind("j", "tool.select");
        Bind("q", "action.smartDraw", shift: true);
        Bind("q", "action.pointerLens", shift: true);
        preferences.ResetLocalShortcuts();
        actual.Add(State(preferences, "reset local shortcuts"));
        CompareScript(expected, actual);
    }

    [Fact]
    public void AssignScriptMatchesTheMac()
    {
        var expected = Corpus.Lines("shortcuts/assign-script.jsonl");
        var preferences = new WorkspacePreferences();
        var actual = new List<JsonObject>();
        void Assign(string key, Tool tool)
        {
            var result = Outcome.Of(() => { preferences.AssignShortcut(key, tool); return null; });
            var state = State(preferences, $"assign \"{key}\" to {tool.RawValue}");
            state["result"] = result;
            actual.Add(state);
        }
        Assign("z", Tool.Pen);
        Assign("1", Tool.Pen);
        Assign("", Tool.Pen);
        Assign("R", Tool.Rectangle);
        Assign("J", Tool.Star);
        Assign("j", Tool.Hexagon);
        try
        {
            preferences.Bind(LocalShortcut.Create("l"), ShortcutCommand.ForAction(ShortcutAction.Spotlight));
        }
        catch (CanvasError)
        {
        }
        Assign("l", Tool.Line);
        Assign("ab", Tool.Line);
        CompareScript(expected, actual);
    }

    private static void CompareScript(IReadOnlyList<JsonObject> expected, List<JsonObject> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            JsonDiff.Equal(expected[index], actual[index], NumberRule.Exact, $"step {index} ({expected[index]["step"]})");
        }
    }

    /// <summary>The exporter's <c>state(_:)</c>: bindings, pressed-key lookups and each command's key.</summary>
    private static JsonObject State(in WorkspacePreferences preferences, string step)
    {
        var pressed = new JsonObject();
        foreach (var key in new[] { "a", "j", "l", "1", "5", "7", "z" })
        {
            foreach (var shift in new[] { false, true })
            {
                if (LocalShortcut.Create(key, shift) is { } shortcut)
                {
                    pressed[shortcut.StorageKey] = preferences.CommandForPressed(shortcut)?.Id;
                }
            }
        }
        var shortcutFor = new JsonObject();
        foreach (var command in Tool.AllCases.Select(ShortcutCommand.ForTool).Concat(ShortcutAction.AllCases.Select(ShortcutCommand.ForAction)))
        {
            shortcutFor[command.Id] = preferences.ShortcutFor(command)?.StorageKey;
        }
        return new JsonObject
        {
            ["commandForPressed"] = pressed,
            ["commandShortcuts"] = preferences.CommandShortcuts is { } commands ? ProfilesTests.Strings(commands) : null,
            ["localShortcuts"] = ProfilesTests.Bindings(preferences),
            ["shortcutFor"] = shortcutFor,
            ["step"] = step,
            ["toolShortcuts"] = ProfilesTests.Strings(preferences.ToolShortcuts),
        };
    }

    /// <summary>
    /// WIN-INP-04: the Windows shortcut code stores the macOS key code of the same physical key through this table, so
    /// every key of the corpus table must be in <see cref="WindowsShortcuts.KeyTable"/> with its character or name, map
    /// to the PC scan code of the same position on a US keyboard and back, and keypad and special keys must carry the
    /// fixed Windows virtual key.
    /// </summary>
    [Fact]
    public void WindowsShortcutsUseTheMacKeyTable()
    {
        var table = Corpus.Json("shortcuts/macos-key-codes.json");
        var expected = new List<(uint Code, string Name, uint Scan, uint? VirtualKey)>();
        foreach (var entry in table["letters"]!.AsArray())
        {
            var character = entry!["character"]!.GetValue<string>();
            expected.Add((entry["keyCode"]!.GetValue<uint>(), character, UsScanCode(character[0]), null));
        }
        uint[] keypadScanCodes = [0x52, 0x4F, 0x50, 0x51, 0x4B, 0x4C, 0x4D, 0x47, 0x48, 0x49];
        foreach (var entry in table["keypad"]!.AsArray())
        {
            var digit = entry!["character"]!.GetValue<string>();
            expected.Add((entry["keyCode"]!.GetValue<uint>(), digit, keypadScanCodes[digit[0] - '0'], 0x60u + (uint)(digit[0] - '0')));
        }
        foreach (var entry in table["special"]!.AsArray())
        {
            var name = entry!["name"]!.GetValue<string>();
            (uint scan, int key) = name switch
            {
                "Return" => (0x1Cu, VirtualKeys.Enter),
                "Space" => (0x39u, VirtualKeys.Space),
                "Delete" => (0x0Eu, VirtualKeys.Back),
                "ForwardDelete" => (0xE053u, VirtualKeys.Delete),
                "Escape" => (0x01u, VirtualKeys.Escape),
                "LeftArrow" => (0xE04Bu, VirtualKeys.Left),
                "RightArrow" => (0xE04Du, VirtualKeys.Right),
                "DownArrow" => (0xE050u, VirtualKeys.Down),
                "UpArrow" => (0xE048u, VirtualKeys.Up),
                _ => throw new InvalidDataException($"Unknown special key {name}."),
            };
            expected.Add((entry["keyCode"]!.GetValue<uint>(), name, scan, (uint)key));
        }
        Assert.Equal(expected.Count, WindowsShortcuts.KeyTable.Count);
        foreach (var (code, name, scan, virtualKey) in expected)
        {
            Assert.Contains(new MacKey(code, name, scan, virtualKey), WindowsShortcuts.KeyTable);
            Assert.Equal(scan, WindowsShortcuts.ScanCode(code));
            Assert.Equal(code, WindowsShortcuts.MacKeyCode(scan));
            Assert.Equal(virtualKey, WindowsShortcuts.VirtualKey(code));
        }
        foreach (var entry in table["keypad"]!.AsArray())
        {
            var chord = WindowsShortcuts.Chord(new ActivationShortcut(entry!["keyCode"]!.GetValue<uint>(), entry["character"]!.GetValue<string>(), control: true));
            Assert.Equal(WindowsShortcuts.VirtualKey(entry["keyCode"]!.GetValue<uint>()), Assert.NotNull(chord).VirtualKey);
        }

        var digits = table["settingsDigitMap"]!.AsObject();
        for (uint code = 0; code < 128; code++)
        {
            Assert.Equal(digits[code.ToString(System.Globalization.CultureInfo.InvariantCulture)]?.GetValue<string>(), WindowsShortcuts.SettingsDigit(code));
        }

        JsonDiff.Equal(table["toolLibrary"]!["shortcut"], Outcome.Value(WindowsShortcuts.ToolLibraryShortcut), NumberRule.Exact);
        var preferences = new WorkspacePreferences();
        Assert.True(WindowsShortcuts.ToolLibraryKeyIsFree(preferences));
        preferences.HideShortcut = WindowsShortcuts.ToolLibraryShortcut;
        Assert.False(WindowsShortcuts.ToolLibraryKeyIsFree(preferences));
        preferences.HideShortcut = new ActivationShortcut(WindowsShortcuts.ToolLibraryShortcut.KeyCode, "l", control: true);
        Assert.False(WindowsShortcuts.ToolLibraryKeyIsFree(preferences));
    }

    /// <summary>PC set-1 scan codes of the US layout's number row and letter rows, by position.</summary>
    private static uint UsScanCode(char character)
    {
        const string NumberRow = "1234567890", TopRow = "qwertyuiop", HomeRow = "asdfghjkl", BottomRow = "zxcvbnm";
        if (NumberRow.IndexOf(character, StringComparison.Ordinal) is >= 0 and var digit)
        {
            return 0x02u + (uint)digit;
        }
        if (TopRow.IndexOf(character, StringComparison.Ordinal) is >= 0 and var top)
        {
            return 0x10u + (uint)top;
        }
        if (HomeRow.IndexOf(character, StringComparison.Ordinal) is >= 0 and var home)
        {
            return 0x1Eu + (uint)home;
        }
        return 0x2Cu + (uint)BottomRow.IndexOf(character, StringComparison.Ordinal);
    }
}
