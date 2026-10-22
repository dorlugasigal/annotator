using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;

namespace Annotator.Conformance.Tests;

/// <summary>
/// Corpus cases that still differ on Windows, each with its reason. Such a case is reported as skipped with the
/// reason and the difference; when it starts to match, its test fails until the entry is removed, so this list
/// always shows exactly what is open.
/// </summary>
internal static partial class OpenDifferences
{
    private static readonly Dictionary<string, string> Reasons = Entries().ToDictionary(entry => entry.Id, entry => entry.Reason, StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, string> All => Reasons;

    /// <summary>
    /// Runs the comparison of one corpus case, applying the open-difference rule. For an open case,
    /// <paramref name="bounded"/> (when given) must still pass: it checks that the difference stays within the bound the
    /// reason states, so a larger regression fails instead of hiding behind the entry.
    /// </summary>
    public static void Check(string caseId, Action compare, Action? bounded = null)
    {
        Exception? failure = null;
        try
        {
            compare();
        }
        catch (Exception error)
        {
            failure = error;
        }
        if (Reasons.TryGetValue(caseId, out var reason))
        {
            if (failure is null)
            {
                Assert.Fail($"{caseId} now matches the Mac corpus. Remove it from OpenDifferences.");
            }
            bounded?.Invoke();
            Assert.Skip($"Open difference: {reason}\n{failure.Message}");
        }
        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
    }
}

/// <summary>Theory data for the records of a corpus file: one case per record, named "file#index label".</summary>
internal static class CorpusCases
{
    public static TheoryData<string> Of(string path, Func<JsonObject, string?>? label = null)
    {
        var data = new TheoryData<string>();
        var records = Corpus.Lines(path);
        for (var index = 0; index < records.Count; index++)
        {
            var name = label?.Invoke(records[index]) ?? records[index]["name"]?.GetValue<string>();
            data.Add(name is null ? $"{path}#{index}" : $"{path}#{index} {name}");
        }
        return data;
    }

    /// <summary>The record a case name points at.</summary>
    public static JsonObject Record(string caseName) => Corpus.Lines(caseName[..caseName.IndexOf('#', StringComparison.Ordinal)])[Index(caseName)];

    /// <summary>The position of a case's record in its file.</summary>
    public static int Index(string caseName)
    {
        var rest = caseName[(caseName.IndexOf('#', StringComparison.Ordinal) + 1)..];
        var space = rest.IndexOf(' ', StringComparison.Ordinal);
        return int.Parse(space < 0 ? rest : rest[..space], System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The stable identifier used by <see cref="OpenDifferences"/>: the file and the record's name or index.</summary>
    public static string Id(string caseName)
    {
        var hash = caseName.IndexOf('#', StringComparison.Ordinal);
        var rest = caseName[(hash + 1)..];
        var space = rest.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? caseName : caseName[..hash] + "#" + rest[(space + 1)..];
    }
}
