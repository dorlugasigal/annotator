using System.Security.Cryptography;
using System.Text.Json;
using Annotator.Rendering.Imaging;

namespace Annotator.Rendering.Tests.Conformance;

/// <summary>
/// Read-only access to the macOS conformance corpus in <c>reference/conformance</c> (plan task M-1). Every file is checked
/// against the SHA-256 in <c>manifest.json</c> when it is read, so a test never compares with changed reference data.
/// </summary>
internal static class ConformanceCorpus
{
    private static readonly Lazy<string> RootPath = new(FindRoot);
    private static readonly Lazy<Dictionary<string, string>> Hashes = new(ReadManifest);

    /// <summary>The corpus folder.</summary>
    public static string Root => RootPath.Value;

    /// <summary>The bytes of a corpus file, after checking them against the manifest.</summary>
    /// <exception cref="InvalidDataException">The file is not in the manifest or its bytes differ.</exception>
    public static byte[] Read(string relativePath)
    {
        if (!Hashes.Value.TryGetValue(relativePath, out var expected))
        {
            throw new InvalidDataException($"{relativePath} is not listed in reference/conformance/manifest.json.");
        }
        var bytes = File.ReadAllBytes(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{relativePath} differs from the manifest (SHA-256 {actual}, expected {expected}). Restore the corpus; never edit it.");
        }
        return bytes;
    }

    /// <summary>The records of a JSON Lines file, one parsed document per line.</summary>
    public static IReadOnlyList<JsonElement> Lines(string relativePath)
    {
        var text = System.Text.Encoding.UTF8.GetString(Read(relativePath));
        var records = new List<JsonElement>();
        foreach (var line in text.Split('\n'))
        {
            if (line.Length > 0)
            {
                using var document = JsonDocument.Parse(line);
                records.Add(document.RootElement.Clone());
            }
        }
        return records;
    }

    /// <summary>A corpus PNG as premultiplied BGRA (see <see cref="PngImages.DecodePremultiplied"/>).</summary>
    public static RasterImage Png(string relativePath) => PngImages.DecodePremultiplied(Read(relativePath));

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "reference", "conformance");
            if (File.Exists(Path.Combine(candidate, "manifest.json")))
            {
                return candidate;
            }
        }
        throw new DirectoryNotFoundException($"reference/conformance was not found above {AppContext.BaseDirectory}.");
    }

    private static Dictionary<string, string> ReadManifest()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, "manifest.json")));
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in document.RootElement.GetProperty("files").EnumerateArray())
        {
            hashes[file.GetProperty("path").GetString()!] = file.GetProperty("sha256").GetString()!;
        }
        return hashes;
    }
}
