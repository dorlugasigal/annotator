using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Annotator.Conformance.Tests;

/// <summary>
/// The macOS conformance corpus in <c>reference/conformance</c>, found by walking up from the test binaries to the
/// repository root. Every file is checked against its SHA-256 and size in <c>manifest.json</c> before use, so the
/// tests compare with the corpus exactly as the Mac exported it, line endings included (WIN-PAR-02).
/// </summary>
internal static class Corpus
{
    private static readonly Lazy<CorpusState> State = new(Load);
    private static readonly ConcurrentDictionary<string, byte[]> Verified = new(StringComparer.Ordinal);

    public static string Root => State.Value.Root;

    /// <summary>Every path the manifest lists, relative to the corpus folder with forward slashes.</summary>
    public static IReadOnlyCollection<string> Paths => State.Value.Files.Keys;

    /// <summary>The manifest's source block (commit and fingerprint), for reports.</summary>
    public static JsonObject Source => State.Value.Source;

    /// <summary>The bytes of a corpus file after checking them against the manifest.</summary>
    public static byte[] Bytes(string path) => Verified.GetOrAdd(path, Verify);

    public static string Text(string path) => Encoding.UTF8.GetString(Bytes(path));

    public static JsonNode Json(string path) =>
        JsonNode.Parse(Bytes(path)) ?? throw new InvalidDataException($"{path} holds a JSON null.");

    /// <summary>The records of a JSON Lines file, in order.</summary>
    public static IReadOnlyList<JsonObject> Lines(string path)
    {
        var text = Text(path);
        var records = new List<JsonObject>();
        foreach (var line in text.Split('\n'))
        {
            if (line.Length != 0)
            {
                records.Add(JsonNode.Parse(line)?.AsObject() ?? throw new InvalidDataException($"{path} has a null record."));
            }
        }
        return records;
    }

    /// <summary>Checks a file against the manifest; a missing, changed or unlisted file fails the test that uses it.</summary>
    private static byte[] Verify(string path)
    {
        if (!State.Value.Files.TryGetValue(path, out var entry))
        {
            throw new InvalidDataException($"reference/conformance/manifest.json does not list {path}.");
        }
        var full = Path.Combine(State.Value.Root, path.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"The corpus file {path} is missing. Restore reference/conformance from the repository.", full);
        }
        var bytes = File.ReadAllBytes(full);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (bytes.LongLength != entry.Bytes || !string.Equals(hash, entry.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The corpus file {path} differs from manifest.json ({bytes.LongLength} bytes, SHA-256 {hash}; expected {entry.Bytes} bytes, " +
                $"{entry.Sha256}). The corpus must stay byte for byte as the Mac exported it; check out reference/conformance without " +
                "line ending conversion.");
        }
        return bytes;
    }

    private static CorpusState Load()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "reference", "conformance", "manifest.json")))
        {
            directory = directory.Parent;
        }
        if (directory is null)
        {
            throw new DirectoryNotFoundException(
                $"No reference/conformance/manifest.json above {AppContext.BaseDirectory}. Run the tests from a repository checkout.");
        }
        var root = Path.Combine(directory.FullName, "reference", "conformance");
        var manifest = JsonNode.Parse(File.ReadAllBytes(Path.Combine(root, "manifest.json")))!.AsObject();
        var files = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
        foreach (var item in manifest["files"]!.AsArray())
        {
            var path = item!["path"]!.GetValue<string>();
            files.Add(path, new ManifestEntry(item["sha256"]!.GetValue<string>(), item["bytes"]!.GetValue<long>()));
        }
        if (manifest["fileCount"]!.GetValue<int>() != files.Count)
        {
            throw new InvalidDataException("reference/conformance/manifest.json lists a different number of files than its fileCount.");
        }
        return new CorpusState(root, files, manifest["source"]!.AsObject());
    }

    private sealed record ManifestEntry(string Sha256, long Bytes);

    private sealed record CorpusState(string Root, Dictionary<string, ManifestEntry> Files, JsonObject Source);
}
