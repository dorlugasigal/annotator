using System.Text.Json;

namespace Annotator.Workspace.Settings;

/// <summary>One release's notes (Mac ReleaseNotesCatalog.Entry).</summary>
/// <param name="Changes">Changes by category, in the catalog's order of <see cref="ReleaseNotesCatalog.Categories"/>.</param>
public sealed record ReleaseNotesEntry(string Version, int Build, string Date,
                                       IReadOnlyList<(string Category, IReadOnlyList<string> Items)> Changes,
                                       IReadOnlyList<string> KnownIssues);

/// <summary>Why the bundled notes cannot be shown (Mac ReleaseNotesFailure), with the Mac's words.</summary>
public sealed class ReleaseNotesFailure : Exception
{
    private ReleaseNotesFailure(bool invalid, string message)
        : base(message)
    {
        IsInvalid = invalid;
    }

    public bool IsInvalid { get; }

    public static ReleaseNotesFailure Invalid() =>
        new(true, "The release notes could not be read. Include the app version when reporting this problem.");

    public static ReleaseNotesFailure MissingVersion() =>
        new(false, "Release notes are not available for this build. Include the app version when reporting this problem.");
}

/// <summary>
/// Port of ReleaseNotesCatalog: the notes bundled with the app (releases/notes.json, schema 1), matched to the running
/// version and build (WIN-UI-05). Reading them is local; nothing checks for updates. An entry's own changes and known
/// limits describe the Mac app; when the entry has Windows notes (<c>platforms.windows</c>), this app shows those instead.
/// </summary>
public static class ReleaseNotesCatalog
{
    public static IReadOnlyList<string> Categories { get; } = ["Added", "Changed", "Fixed", "Removed"];

    /// <summary>The key of this edition's notes in an entry's optional <c>platforms</c> object.</summary>
    public const string Platform = "windows";

    /// <summary>
    /// Mac current(data:version:build:): validates the whole catalog (schema 1, releases with unique versions and builds,
    /// positive builds, a date, known categories with non-blank items) and returns the running version's entry.
    /// </summary>
    public static ReleaseNotesEntry Current(ReadOnlySpan<byte> data, string version, int build)
    {
        List<ReleaseNotesEntry> releases;
        try
        {
            using JsonDocument document = JsonDocument.Parse(data.ToArray());
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Integer(root, "schemaVersion") != 1
                || !root.TryGetProperty("releases", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
            {
                throw ReleaseNotesFailure.Invalid();
            }
            releases = [.. list.EnumerateArray().Select(Entry)];
        }
        catch (JsonException)
        {
            throw ReleaseNotesFailure.Invalid();
        }
        if (releases.Count == 0 || releases.Select(entry => entry.Version).Distinct(StringComparer.Ordinal).Count() != releases.Count
            || releases.Select(entry => entry.Build).Distinct().Count() != releases.Count
            || !releases.All(entry => entry.Build > 0 && entry.Version.Length > 0 && entry.Date.Length > 0 && entry.Changes.Count > 0
                && entry.Changes.All(change => change.Items.Count > 0 && change.Items.All(item => !string.IsNullOrWhiteSpace(item)))))
        {
            throw ReleaseNotesFailure.Invalid();
        }
        return releases.FirstOrDefault(entry => entry.Version == version && entry.Build == build)
            ?? throw ReleaseNotesFailure.MissingVersion();
    }

    /// <summary>
    /// Mac load(bundle:): the catalog file next to the app, for the running version (a plain X.Y.Z) and its derived build.
    /// Entries of earlier versions keep the versions and builds they were released with. A missing file is a missing
    /// version, as on the Mac; anything unreadable is invalid.
    /// </summary>
    public static ReleaseNotesEntry Load(string path, string version, int? build)
    {
        if (build is not { } number || !File.Exists(path))
        {
            throw ReleaseNotesFailure.MissingVersion();
        }
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw ReleaseNotesFailure.Invalid();
        }
        return Current(data, version, number);
    }

    private static ReleaseNotesEntry Entry(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            throw ReleaseNotesFailure.Invalid();
        }
        string version = Text(entry, "version");
        int build = Integer(entry, "build");
        string date = Text(entry, "date");
        (IReadOnlyList<(string, IReadOnlyList<string>)> changes, IReadOnlyList<string> issues) = Notes(entry);
        if (entry.TryGetProperty("platforms", out JsonElement platforms))
        {
            if (platforms.ValueKind != JsonValueKind.Object)
            {
                throw ReleaseNotesFailure.Invalid();
            }
            // The entry's own changes describe the Mac app; a Windows section replaces them in this app.
            if (platforms.TryGetProperty(Platform, out JsonElement windows))
            {
                (changes, issues) = Notes(windows);
            }
        }
        return new ReleaseNotesEntry(version, build, date, changes, issues);
    }

    private static (IReadOnlyList<(string, IReadOnlyList<string>)> Changes, IReadOnlyList<string> KnownIssues) Notes(JsonElement owner)
    {
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty("changes", out JsonElement changes)
            || changes.ValueKind != JsonValueKind.Object)
        {
            throw ReleaseNotesFailure.Invalid();
        }
        var byCategory = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (JsonProperty category in changes.EnumerateObject())
        {
            if (!Categories.Contains(category.Name, StringComparer.Ordinal) || !byCategory.TryAdd(category.Name, Texts(category.Value)))
            {
                throw ReleaseNotesFailure.Invalid();
            }
        }
        if (!owner.TryGetProperty("knownIssues", out JsonElement issues))
        {
            throw ReleaseNotesFailure.Invalid();
        }
        List<(string, IReadOnlyList<string>)> ordered = [.. Categories.Where(byCategory.ContainsKey).Select(name => (name, byCategory[name]))];
        if (ordered.Count == 0 || !ordered.All(change => change.Item2.Count > 0 && change.Item2.All(item => !string.IsNullOrWhiteSpace(item))))
        {
            throw ReleaseNotesFailure.Invalid();
        }
        return (ordered, Texts(issues));
    }

    private static string Text(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw ReleaseNotesFailure.Invalid();

    private static int Integer(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)
            ? number : throw ReleaseNotesFailure.Invalid();

    private static List<string> Texts(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw ReleaseNotesFailure.Invalid();
        }
        return [.. value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : throw ReleaseNotesFailure.Invalid())];
    }
}
