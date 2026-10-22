// Port of apps/macos/Tests/AnnotationCoreTests/ShapeLibraryStoreTests.swift.
// Folders are created under the test output folder instead of the system temporary folder.
using System.Text;
using static Annotator.Core.Tests.TestSupport;

namespace Annotator.Core.Tests;

public class ShapeLibraryStoreTests
{
    private static SavedShape Shape(string name = "Saved")
    {
        var element = new Element(Tool.Rectangle, new Rect(20, 30, 60, 50));
        var document = new CanvasDocument([element]);
        document.Select([element.Id]);
        return new SavedShape(name, document);
    }

    private static string FileName(Guid id) => $"{id.ToString("D").ToUpperInvariant()}.json";

    [Fact]
    public async Task SaveReopenAndDeleteUseUuidPaths()
    {
        var root = ScratchDirectory("annotator-shapes");
        try
        {
            var directory = Path.Combine(root, "Library");
            var store = new ShapeLibraryStore(directory);
            Assert.Empty(await store.LoadAsync());
            Assert.False(Directory.Exists(directory));
            var first = Shape("../../A / B");
            var second = Shape("../../A / B");
            Assert.Single(await store.SaveAsync(first));
            Assert.Equal(2, (await store.SaveAsync(second)).Count);
            var files = Directory.GetFiles(directory).Select(Path.GetFileName).ToHashSet();
            Assert.True(files.SetEquals([FileName(first.Id), FileName(second.Id)]));
            var reopened = new ShapeLibraryStore(directory);
            var entries = await reopened.LoadAsync();
            Assert.True(entries.Select(entry => entry.Id).ToHashSet().SetEquals([first.Id, second.Id]));
            Assert.All(entries, entry =>
            {
                Assert.Equal(first.Name, entry.Name);
                Assert.Single(entry.Elements);
            });
            Assert.Equal(new[] { second.Id }, (await reopened.DeleteAsync(first.Id)).Select(entry => entry.Id));
            Assert.Equal(new[] { second.Id }, (await store.LoadAsync()).Select(entry => entry.Id));
            Assert.Empty(await store.DeleteAsync(second.Id));
            Assert.Empty(await reopened.LoadAsync());
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task TransientLibrariesNeverResolveDirectoriesAndRemainIndependent()
    {
        var first = new ShapeLibraryStore();
        var second = new ShapeLibraryStore();
        Assert.Null(first.Directory);
        Assert.Null(second.Directory);
        Assert.Empty(await first.LoadAsync());
        var entry = Shape();
        Assert.Equal(new[] { entry.Id }, (await first.SaveAsync(entry)).Select(saved => saved.Id));
        Assert.Empty(await second.LoadAsync());
        Assert.Equal(entry.Elements, (await first.LoadAsync())[0].Elements);
        Assert.Empty(await first.DeleteAsync(entry.Id));
        Assert.Null(first.Directory);
    }

    [Fact]
    public async Task TransientLibraryUsesTheSameValidationAndCountBudget()
    {
        var store = new ShapeLibraryStore();
        var first = Shape();
        await store.SaveAsync(first);
        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync(first));
        await AssertShapeErrorAsync(ShapeLibraryError.NoSelection, () => store.SaveSelectionAsync("Empty", CanvasDocument.Empty()));
        for (var index = 1; index < ShapeLibraryStore.MaximumEntries; index++)
        {
            await store.SaveAsync(Shape());
        }
        await AssertShapeErrorAsync(ShapeLibraryError.LibraryFull, () => store.SaveAsync(Shape()));
        Assert.Equal(ShapeLibraryStore.MaximumEntries, (await store.LoadAsync()).Count);
        await store.DeleteAsync(first.Id);
        Assert.Equal(ShapeLibraryStore.MaximumEntries, (await store.SaveAsync(Shape())).Count);
    }

    [Fact]
    public async Task CorruptFileBlocksSaveWithoutReplacingAnything()
    {
        var directory = ScratchDirectory("annotator-shapes");
        try
        {
            var store = new ShapeLibraryStore(directory);
            var first = Shape();
            await store.SaveAsync(first);
            var corrupt = Path.Combine(directory, FileName(Guid.NewGuid()));
            var data = Encoding.UTF8.GetBytes("not a shape");
            File.WriteAllBytes(corrupt, data);
            await Assert.ThrowsAnyAsync<Exception>(() => store.LoadAsync());
            await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync(Shape("New")));
            Assert.Equal(data, File.ReadAllBytes(corrupt));
            Assert.Equal(2, Directory.GetFileSystemEntries(directory).Length);
            File.Delete(corrupt);
            Assert.Equal(new[] { first.Id }, (await store.LoadAsync()).Select(entry => entry.Id));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task DuplicateMissingMismatchedAndSymlinkFilesFail()
    {
        var root = ScratchDirectory("annotator-shapes");
        try
        {
            var directory = Path.Combine(root, "Library");
            var store = new ShapeLibraryStore(directory);
            var first = Shape();
            await store.SaveAsync(first);
            await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync(first));
            await Assert.ThrowsAnyAsync<Exception>(() => store.DeleteAsync(Guid.NewGuid()));
            var file = Path.Combine(directory, FileName(first.Id));
            var original = File.ReadAllBytes(file);
            File.WriteAllBytes(file, Shape("Wrong ID").Data());
            await Assert.ThrowsAnyAsync<Exception>(() => store.LoadAsync());
            File.WriteAllBytes(file, original);
            var target = Path.Combine(root, "outside.json");
            File.WriteAllBytes(target, original);
            File.Delete(file);
            try
            {
                File.CreateSymbolicLink(file, target);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Assert.Skip("Creating a symbolic link needs Developer Mode or administrator rights on this machine.");
            }
            await Assert.ThrowsAnyAsync<Exception>(() => store.LoadAsync());
            await Assert.ThrowsAnyAsync<Exception>(() => store.DeleteAsync(first.Id));
            Assert.Equal(original, File.ReadAllBytes(target));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task WriteAndDeleteFailuresAreNotReportedAsSuccess()
    {
        var root = ScratchDirectory("annotator-shapes");
        try
        {
            var file = Path.Combine(root, "not-a-directory");
            File.WriteAllBytes(file, Encoding.UTF8.GetBytes("keep"));
            var store = new ShapeLibraryStore(file);
            await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync(Shape()));
            await Assert.ThrowsAnyAsync<Exception>(() => store.DeleteAsync(Guid.NewGuid()));
            Assert.Equal(Encoding.UTF8.GetBytes("keep"), File.ReadAllBytes(file));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ExactEntryCountLimitAndOversizedFile()
    {
        var directory = ScratchDirectory("annotator-shapes");
        try
        {
            var ids = new List<Guid>();
            for (var index = 0; index < ShapeLibraryStore.MaximumEntries; index++)
            {
                var entry = Shape();
                ids.Add(entry.Id);
                File.WriteAllBytes(Path.Combine(directory, FileName(entry.Id)), entry.Data());
            }
            var store = new ShapeLibraryStore(directory);
            Assert.Equal(ShapeLibraryStore.MaximumEntries, (await store.LoadAsync()).Count);
            await AssertShapeErrorAsync(ShapeLibraryError.LibraryFull, () => store.SaveAsync(Shape()));
            var victim = ids[0];
            Assert.Equal(ShapeLibraryStore.MaximumEntries - 1, (await store.DeleteAsync(victim)).Count);
            Assert.Equal(ShapeLibraryStore.MaximumEntries, (await store.SaveAsync(Shape())).Count);
            var oversized = Path.Combine(directory, FileName(ids[^1]));
            File.WriteAllBytes(oversized, Repeated(32, SavedShape.MaximumBytes + 1));
            await AssertShapeErrorAsync(ShapeLibraryError.EntryTooLarge, () => store.LoadAsync());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ExactAggregateFileBudgetAndDeletionRecovery()
    {
        var directory = ScratchDirectory("annotator-shapes");
        try
        {
            var ids = new List<Guid>();
            for (var index = 0; index < ShapeLibraryStore.MaximumBytes / SavedShape.MaximumBytes; index++)
            {
                var entry = Shape();
                var data = entry.Data();
                var padded = new byte[SavedShape.MaximumBytes];
                Array.Fill(padded, (byte)32);
                data.CopyTo(padded, 0);
                ids.Add(entry.Id);
                File.WriteAllBytes(Path.Combine(directory, FileName(entry.Id)), padded);
            }
            var store = new ShapeLibraryStore(directory);
            Assert.Equal(ids.Count, (await store.LoadAsync()).Count);
            await AssertShapeErrorAsync(ShapeLibraryError.LibraryFull, () => store.SaveAsync(Shape()));
            await store.DeleteAsync(ids[0]);
            Assert.Equal(ids.Count, (await store.SaveAsync(Shape())).Count);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }
}
