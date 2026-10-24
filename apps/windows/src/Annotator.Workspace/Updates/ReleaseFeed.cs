using System.Text.Json;

namespace Annotator.Workspace.Updates;

/// <summary>The newest release a portable copy can get, as the website's update feed describes it (WIN-SYS-03).</summary>
/// <param name="ReleaseVersion">The shared version label without the build, such as 0.1.0-beta.13.</param>
/// <param name="Build">The shared, always-increasing build number that decides whether the release is newer.</param>
/// <param name="DownloadPage">Where the person downloads it; Annotator downloads nothing itself.</param>
public readonly record struct ReleaseFeedEntry(string ReleaseVersion, int Build, Uri DownloadPage);

/// <summary>The website's feed for portable copies (<c>updates/windows-&lt;track&gt;.json</c>, written by tools/release).</summary>
public interface IReleaseFeed
{
    /// <summary>Reads the feed. Throws <see cref="ReleaseFeedFailure"/> when it cannot be read.</summary>
    Task<ReleaseFeedEntry> FetchAsync();
}

/// <summary>A feed that could not be read. The message goes to the log; the person reads <see cref="AppUpdates.Status"/>.</summary>
public sealed class ReleaseFeedFailure(string detail) : Exception(detail);

/// <summary>Reads and checks the portable update feed; nothing here touches the network.</summary>
public static class ReleaseFeed
{
    /// <summary>The largest feed Annotator reads; the real one is a few hundred bytes.</summary>
    public const int MaximumBytes = 16 * 1024;

    /// <summary>
    /// The feed's newest release: schema 1, a nonblank version, a positive build and an HTTPS download page without
    /// credentials. Anything else throws <see cref="ReleaseFeedFailure"/>.
    /// </summary>
    public static ReleaseFeedEntry Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaximumBytes)
        {
            throw new ReleaseFeedFailure("The update feed is too large.");
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(data.ToArray());
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !Number(root, "schemaVersion", out int schema) || schema != 1)
            {
                throw new ReleaseFeedFailure("The update feed has an unknown format.");
            }
            if (!Text(root, "releaseVersion", out string version) || string.IsNullOrWhiteSpace(version)
                || !Number(root, "build", out int build) || build <= 0
                || !Text(root, "downloadPage", out string page) || !Uri.TryCreate(page, UriKind.Absolute, out Uri? link)
                || link.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(link.UserInfo))
            {
                throw new ReleaseFeedFailure("The update feed is missing its version, build or download page.");
            }
            return new ReleaseFeedEntry(version, build, link);
        }
        catch (JsonException error)
        {
            throw new ReleaseFeedFailure($"The update feed is not valid JSON: {error.Message}");
        }
    }

    /// <summary>
    /// The feed address a portable build was made with (AnnotatorUpdateFeedURL), or null when it has none or it is not a
    /// plain HTTPS address.
    /// </summary>
    public static Uri? Address(string? configured) =>
        !string.IsNullOrWhiteSpace(configured) && !configured.Contains("$(", StringComparison.Ordinal)
            && Uri.TryCreate(configured, UriKind.Absolute, out Uri? address) && address.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(address.UserInfo) && address.IsDefaultPort && string.IsNullOrEmpty(address.Query)
            && string.IsNullOrEmpty(address.Fragment)
            ? address : null;

    private static bool Text(JsonElement owner, string name, out string value)
    {
        value = owner.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String ? element.GetString()! : "";
        return value.Length > 0;
    }

    private static bool Number(JsonElement owner, string name, out int value)
    {
        value = 0;
        return owner.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
    }
}

/// <summary>A feed with a fixed answer for tests, the live check and reference states; it never uses the network.</summary>
public sealed class FixedReleaseFeed(ReleaseFeedEntry answer) : IReleaseFeed
{
    public ReleaseFeedEntry Answer { get; set; } = answer;

    /// <summary>When set, the next fetches throw it, as an unreachable website does.</summary>
    public ReleaseFeedFailure? Failure { get; set; }

    /// <summary>When set, fetches wait for it, as a slow website would.</summary>
    public Task? Gate { get; set; }

    public int Fetches { get; private set; }

    public async Task<ReleaseFeedEntry> FetchAsync()
    {
        Fetches++;
        if (Gate is { } gate)
        {
            await gate;
        }
        return Failure is { } failure ? throw failure : Answer;
    }
}
