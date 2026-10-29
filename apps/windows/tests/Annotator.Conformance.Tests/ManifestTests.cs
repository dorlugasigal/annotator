namespace Annotator.Conformance.Tests;

/// <summary>
/// The corpus must stay byte for byte as the Mac exported it (WIN-PAR-02): every file of the sections these tests
/// compare matches its SHA-256 and size in <c>manifest.json</c>, and no unlisted file sits beside them.
/// </summary>
public class ManifestTests
{
    /// <summary>The sections this project compares; motion, render and effects have their own tests.</summary>
    internal static readonly string[] Sections =
        ["platform", "boards", "profiles", "shortcuts", "library", "editing", "geometry", "laser", "recognition", "hittest"];

    public static TheoryData<string> Files
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var path in Corpus.Paths.Where(path => Sections.Contains(path[..path.IndexOf('/', StringComparison.Ordinal)])).Order(StringComparer.Ordinal))
            {
                data.Add(path);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void CorpusFileIsUnchanged(string path) => Assert.NotEmpty(Corpus.Bytes(path));

    [Fact]
    public void SectionsHoldOnlyListedFiles()
    {
        var listed = Corpus.Paths.ToHashSet(StringComparer.Ordinal);
        var unlisted = new List<string>();
        foreach (var section in Sections)
        {
            var folder = Path.Combine(Corpus.Root, section);
            Assert.True(Directory.Exists(folder), $"The corpus section {section} is missing.");
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(Corpus.Root, file).Replace(Path.DirectorySeparatorChar, '/');
                if (!listed.Contains(relative) && !relative.EndsWith(".DS_Store", StringComparison.Ordinal))
                {
                    unlisted.Add(relative);
                }
            }
        }
        Assert.True(unlisted.Count == 0, "Files that manifest.json does not list: " + string.Join(", ", unlisted));
    }
}
