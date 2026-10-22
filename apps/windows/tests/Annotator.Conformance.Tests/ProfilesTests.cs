using System.Text.Json.Nodes;
using Annotator.Core;

namespace Annotator.Conformance.Tests;

/// <summary>
/// Section <c>profiles</c>: preference files read with migration, the model as sorted-key JSON, the model after
/// saving and reading again, the local key bindings, and exact refusal messages.
/// </summary>
public class ProfilesTests
{
    public static TheoryData<string> Cases => CorpusCases.Of("profiles/index.jsonl");

    [Theory]
    [MemberData(nameof(Cases))]
    public void ProfilesReadMigrateAndSaveAsOnTheMac(string name)
    {
        var record = CorpusCases.Record(name);
        OpenDifferences.Check(CorpusCases.Id(name), () =>
        {
            var input = record["file"] is { } file
                ? Corpus.Bytes("profiles/" + file.GetValue<string>())
                : Enumerable.Repeat((byte)0x20, 256 * 1_024 + 1).ToArray();
            var actual = record.DeepClone().AsObject();
            actual["read"] = Outcome.Of(() => Outcome.Value(WorkspacePreferences.Read(input)));
            if (record.ContainsKey("afterSaving"))
            {
                var preferences = WorkspacePreferences.Read(input);
                actual["afterSaving"] = Outcome.Of(() => Outcome.Value(WorkspacePreferences.Read(preferences.Data())));
                actual["localShortcuts"] = Bindings(preferences);
            }
            JsonDiff.Equal(record, actual, NumberRule.Exact);
        });
    }

    [Fact]
    public void StylesToolbarAndDefaultKeysMatchTheMac()
    {
        var expected = Corpus.Json("profiles/visibility-styles.json");
        var actual = new JsonObject
        {
            ["available"] = new JsonArray(VisibilityAnimationStyle.Available.Select(style => (JsonNode)style.RawValue).ToArray()),
            ["defaultToolShortcuts"] = Strings(WorkspacePreferences.DefaultToolShortcuts),
            ["defaultToolbar"] = new JsonArray(WorkspacePreferences.DefaultToolbar.Select(tool => (JsonNode)tool.RawValue).ToArray()),
            ["legacyToolShortcuts"] = Strings(WorkspacePreferences.LegacyToolShortcuts),
            ["resolved"] = new JsonObject(VisibilityAnimationStyle.AllCases.Select(style =>
                KeyValuePair.Create(style.RawValue, (JsonNode?)style.Resolved.RawValue))),
        };
        JsonDiff.Equal(expected, actual, NumberRule.Exact);
    }

    /// <summary>The exporter's sorted (storage key, command ID) pairs.</summary>
    internal static JsonArray Bindings(in WorkspacePreferences preferences) => new(preferences.LocalShortcuts
        .Select(entry => (Key: entry.Key.StorageKey, Id: entry.Value.Id))
        .Order(Comparer<(string Key, string Id)>.Create((left, right) => SwiftText.Compare(left.Key, right.Key)))
        .Select(entry => (JsonNode)new JsonArray(entry.Key, entry.Id))
        .ToArray());

    internal static JsonObject Strings(IEnumerable<KeyValuePair<string, string>> dictionary) =>
        new(dictionary.Select(entry => KeyValuePair.Create(entry.Key, (JsonNode?)entry.Value)));
}
