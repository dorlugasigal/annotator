// Port of apps/macos/Sources/AnnotationCore/ShapeLibraryStore.swift.
// The Swift actor serializes access; here every operation runs on the thread pool under one lock.
namespace Annotator.Core;

/// <summary>My shapes: one `UUID.json` file per saved shape in a local folder, or in memory when there is no folder.</summary>
public sealed class ShapeLibraryStore
{
    public const int MaximumBytes = 64 * 1_024 * 1_024;
    public const int MaximumEntries = 256;

    private readonly Lock gate = new();
    private readonly List<SavedShape> transientShapes = [];

    public ShapeLibraryStore(string? directory = null)
    {
        Directory = directory;
    }

    /// <summary>The library folder, or null for an in-memory library.</summary>
    public string? Directory { get; }

    public Task<IReadOnlyList<SavedShape>> LoadAsync() => Run(() => ReadLibrary().Shapes);

    public Task<IReadOnlyList<SavedShape>> SaveSelectionAsync(string name, CanvasDocument document) =>
        Run(() => SaveCore(new SavedShape(name, document)));

    public Task<IReadOnlyList<SavedShape>> SaveAsync(SavedShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        return Run(() => SaveCore(shape));
    }

    public Task<IReadOnlyList<SavedShape>> RenameAsync(Guid id, string name) => Run(() => RenameCore(id, name));

    public Task<IReadOnlyList<SavedShape>> DeleteAsync(Guid id) => Run(() => DeleteCore(id));

    private Task<T> Run<T>(Func<T> operation) => Task.Run(() =>
    {
        lock (gate)
        {
            return operation();
        }
    });

    private IReadOnlyList<SavedShape> SaveCore(SavedShape shape)
    {
        var data = shape.Data();
        var library = ReadLibrary();
        if (library.Shapes.Any(saved => saved.Id == shape.Id))
        {
            throw ShapeLibraryError.InvalidFile("This shape is already saved.");
        }
        if (library.Shapes.Count >= MaximumEntries || library.Bytes + data.Length > MaximumBytes
            || library.Memory + shape.EstimatedBytes > MaximumBytes)
        {
            throw ShapeLibraryError.LibraryFull;
        }
        if (Directory is { } directory)
        {
            System.IO.Directory.CreateDirectory(directory);
            CheckDirectory(directory);
            var destination = PathFor(shape.Id, directory);
            if (File.Exists(destination) || System.IO.Directory.Exists(destination))
            {
                throw ShapeLibraryError.InvalidFile("A file already uses this shape ID.");
            }
            WriteAtomically(destination, data);
        }
        else
        {
            transientShapes.Add(shape);
        }
        var shapes = new List<SavedShape>(library.Shapes) { shape };
        return Sorted(shapes);
    }

    private IReadOnlyList<SavedShape> RenameCore(Guid id, string name)
    {
        var library = ReadLibrary();
        var shapes = new List<SavedShape>(library.Shapes);
        var index = shapes.FindIndex(shape => shape.Id == id);
        if (index < 0)
        {
            throw ShapeLibraryError.InvalidFile("This shape is no longer saved. Reload My shapes.");
        }
        var previous = shapes[index];
        var updated = previous.Renamed(name);
        var data = updated.Data();
        long oldBytes;
        if (Directory is { } directory)
        {
            var file = new FileInfo(PathFor(id, directory));
            if (!file.Exists)
            {
                throw ShapeLibraryError.InvalidFile("The saved shape size could not be read.");
            }
            oldBytes = file.Length;
        }
        else
        {
            oldBytes = previous.Data().Length;
        }
        if (library.Bytes - oldBytes + data.Length > MaximumBytes)
        {
            throw ShapeLibraryError.LibraryFull;
        }
        if (Directory is { } folder)
        {
            WriteAtomically(PathFor(id, folder), data);
        }
        else
        {
            var transientIndex = transientShapes.FindIndex(shape => shape.Id == id);
            if (transientIndex < 0)
            {
                throw ShapeLibraryError.InvalidFile("This shape is no longer saved.");
            }
            transientShapes[transientIndex] = updated;
        }
        shapes[index] = updated;
        return Sorted(shapes);
    }

    private IReadOnlyList<SavedShape> DeleteCore(Guid id)
    {
        var library = ReadLibrary();
        if (!library.Shapes.Any(shape => shape.Id == id))
        {
            throw ShapeLibraryError.InvalidFile("This shape is no longer saved. Reload My shapes.");
        }
        if (Directory is { } directory)
        {
            File.Delete(PathFor(id, directory));
        }
        else
        {
            transientShapes.RemoveAll(shape => shape.Id == id);
        }
        return Sorted(library.Shapes.Where(shape => shape.Id != id).ToList());
    }

    private static string PathFor(Guid id, string directory) => Path.Combine(directory, SwiftText.UuidString(id) + ".json");

    private static void CheckDirectory(string directory)
    {
        var info = new DirectoryInfo(directory);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
        {
            throw ShapeLibraryError.InvalidFile("The library folder must be a local folder, not a link.");
        }
    }

    private (IReadOnlyList<SavedShape> Shapes, long Bytes, long Memory) ReadLibrary()
    {
        if (Directory is not { } directory)
        {
            long transientBytes = 0;
            long transientMemory = 0;
            foreach (var shape in transientShapes)
            {
                transientBytes += shape.Data().Length;
                transientMemory += shape.EstimatedBytes;
            }
            return (Sorted([.. transientShapes]), transientBytes, transientMemory);
        }
        if (!File.Exists(directory) && !System.IO.Directory.Exists(directory))
        {
            return ([], 0, 0);
        }
        CheckDirectory(directory);
        var files = new DirectoryInfo(directory).EnumerateFileSystemInfos()
            .Where(entry => !IsHidden(entry) && string.Equals(entry.Extension, ".json", StringComparison.Ordinal))
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .ToList();
        if (files.Count > MaximumEntries)
        {
            throw ShapeLibraryError.LibraryFull;
        }
        var shapes = new List<SavedShape>(files.Count);
        long bytes = 0;
        long memory = 0;
        foreach (var file in files)
        {
            if (!SwiftText.TryParseUuid(Path.GetFileNameWithoutExtension(file.Name), out var id)
                || file.Name != Path.GetFileName(PathFor(id, directory)))
            {
                throw ShapeLibraryError.InvalidFile("A saved file has an invalid ID.");
            }
            if (file is not FileInfo regular || (regular.Attributes & FileAttributes.ReparsePoint) != 0 || regular.LinkTarget is not null)
            {
                throw ShapeLibraryError.InvalidFile("A saved shape is not a regular file.");
            }
            var size = regular.Length;
            if (size > SavedShape.MaximumBytes)
            {
                throw ShapeLibraryError.EntryTooLarge;
            }
            if (bytes + size > MaximumBytes)
            {
                throw ShapeLibraryError.LibraryFull;
            }
            var data = ReadPrefix(regular.FullName, SavedShape.MaximumBytes + 1);
            var shape = SavedShape.Read(data);
            if (shape.Id != id)
            {
                throw ShapeLibraryError.InvalidFile("A saved file has the wrong shape ID.");
            }
            bytes += data.Length;
            memory += shape.EstimatedBytes;
            if (bytes > MaximumBytes || memory > MaximumBytes)
            {
                throw ShapeLibraryError.LibraryFull;
            }
            shapes.Add(shape);
        }
        return (Sorted(shapes), bytes, memory);
    }

    // FileManager's .skipsHiddenFiles: dot files, and files marked hidden.
    private static bool IsHidden(FileSystemInfo entry) =>
        entry.Name.StartsWith('.') || (entry.Attributes & FileAttributes.Hidden) != 0;

    private static byte[] ReadPrefix(string path, int limit)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[limit];
        var total = 0;
        int read;
        while (total < limit && (read = stream.Read(buffer, total, limit - total)) > 0)
        {
            total += read;
        }
        return buffer.AsSpan(0, total).ToArray();
    }

    // Like Data.write(options: .atomic): write a hidden temporary file beside the target, then move it into place.
    private static void WriteAtomically(string destination, byte[] data)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, data);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static List<SavedShape> Sorted(List<SavedShape> shapes)
    {
        shapes.Sort((left, right) => left.Name == right.Name
            ? string.CompareOrdinal(SwiftText.UuidString(left.Id), SwiftText.UuidString(right.Id))
            : SwiftText.Compare(left.Name, right.Name));
        return shapes;
    }
}
