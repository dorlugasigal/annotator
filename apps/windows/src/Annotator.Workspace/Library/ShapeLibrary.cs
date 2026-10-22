using Annotator.Core;

namespace Annotator.Workspace.Library;

/// <summary>
/// Port of ShapeLibraryModel: My shapes as the Tools window shows them, with one operation at a time and the Mac's messages.
/// Creating it does no file work; the folder is read on the first explicit use (spec 005). The person's library lives in the
/// app's local data folder (WIN-SYS-05); reference captures and checks use a bounded in-memory library that never reads or
/// writes the person's shapes. Use it on one thread: the store works on the thread pool and every result comes back to the
/// caller's synchronization context, the app thread in the app.
/// </summary>
public sealed class ShapeLibrary
{
    private readonly string? _directory;
    private ShapeLibraryStore? _store;

    private ShapeLibrary(string? directory) => _directory = directory;

    /// <summary>The library in <paramref name="directory"/>, one file per shape (Mac Application Support/Annotator/My Shapes).</summary>
    public static ShapeLibrary InFolder(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return new ShapeLibrary(directory);
    }

    /// <summary>A library in memory, limited like the folder, for reference captures and checks.</summary>
    public static ShapeLibrary InMemory() => new(null);

    /// <summary>The folder, or null for an in-memory library.</summary>
    public string? Directory => _directory;

    /// <summary>The saved shapes, sorted by name.</summary>
    public IReadOnlyList<SavedShape> Shapes { get; private set; } = [];

    /// <summary>True while an operation runs; another one is ignored, as on the Mac.</summary>
    public bool IsBusy { get; private set; }

    /// <summary>True once the library was read; saving waits for it.</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>The last failure, for example "Shape not saved. …", or null.</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>Raised after every change of <see cref="Shapes"/>, <see cref="IsBusy"/>, <see cref="IsLoaded"/> or <see cref="ErrorMessage"/>.</summary>
    public event Action? Changed;

    /// <summary>Reads the library again. A failure keeps nothing loaded and says why; it never replaces a damaged library.</summary>
    public async Task ReloadAsync()
    {
        if (!Begin())
        {
            return;
        }
        try
        {
            Shapes = await Store().LoadAsync();
            IsLoaded = true;
            ErrorMessage = null;
        }
        catch (Exception error) when (IsReportable(error))
        {
            IsLoaded = false;
            ErrorMessage = $"My shapes could not load. {Describe(error)}";
        }
        finally
        {
            End();
        }
    }

    /// <summary>
    /// Saves the selected drawings of <paramref name="document"/> under <paramref name="name"/> and returns the saved shape,
    /// whose name is trimmed; null when nothing was saved.
    /// </summary>
    public async Task<SavedShape?> SaveAsync(string name, CanvasDocument document)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!Begin())
        {
            return null;
        }
        try
        {
            // Like the Mac actor, the shape is checked and encoded away from the caller's thread.
            SavedShape shape = await Task.Run(() => new SavedShape(name, document));
            Shapes = await Store().SaveAsync(shape);
            IsLoaded = true;
            ErrorMessage = null;
            return shape;
        }
        catch (Exception error) when (IsReportable(error))
        {
            ErrorMessage = $"Shape not saved. {Describe(error)}";
            return null;
        }
        finally
        {
            End();
        }
    }

    /// <summary>Renames a saved shape; copies on canvases keep their objects.</summary>
    public async Task RenameAsync(SavedShape shape, string name)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(name);
        if (!Begin())
        {
            return;
        }
        try
        {
            Shapes = await Store().RenameAsync(shape.Id, name);
            ErrorMessage = null;
        }
        catch (Exception error) when (IsReportable(error))
        {
            ErrorMessage = $"Shape not renamed. {Describe(error)}";
        }
        finally
        {
            End();
        }
    }

    /// <summary>Deletes a saved shape; copies on canvases stay.</summary>
    public async Task DeleteAsync(SavedShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (!Begin())
        {
            return;
        }
        try
        {
            Shapes = await Store().DeleteAsync(shape.Id);
            ErrorMessage = null;
        }
        catch (Exception error) when (IsReportable(error))
        {
            ErrorMessage = $"Shape not deleted. {Describe(error)}";
        }
        finally
        {
            End();
        }
    }

    /// <summary>The store, created on first use; creating it reads nothing.</summary>
    private ShapeLibraryStore Store() => _store ??= new ShapeLibraryStore(_directory);

    private bool Begin()
    {
        if (IsBusy)
        {
            return false;
        }
        IsBusy = true;
        Changed?.Invoke();
        return true;
    }

    private void End()
    {
        IsBusy = false;
        Changed?.Invoke();
    }

    /// <summary>The Mac's error.localizedDescription: the library's and the file formats' own messages, else the system's.</summary>
    private static string Describe(Exception error) => error.Message;

    private static bool IsReportable(Exception error) =>
        error is ShapeLibraryError or CanvasError or DecodingError or EncodingError or IOException or UnauthorizedAccessException
            or System.Security.SecurityException;
}
