namespace Annotator.Workspace.Files;

/// <summary>
/// Atomic saves (spec 005, WIN-SYS-04), like the Mac's <c>Data.write(to:options: .atomic)</c>: the data goes to a new file
/// beside the destination, reaches the disk, and then replaces the destination in one rename. A save that fails or is
/// interrupted leaves the earlier file as it was and no half-written file under its name.
/// </summary>
public static class AtomicFile
{
    /// <summary>Writes <paramref name="data"/> to <paramref name="path"/>, replacing an existing file. Throws the IO error.</summary>
    public static void Write(string path, ReadOnlySpan<byte> data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string destination = Path.GetFullPath(path);
        string folder = Path.GetDirectoryName(destination)
            ?? throw new IOException($"\"{destination}\" is not a file path.");
        // The same folder keeps the rename on one volume, where replacing the destination is a single step.
        string temporary = Path.Combine(folder, $"~{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 0))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The save already failed with its own error, which the caller reports; a leftover temporary file is harmless.
        }
    }
}
