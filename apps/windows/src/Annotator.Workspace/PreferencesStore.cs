namespace Annotator.Workspace;

/// <summary>
/// Where the workspace keeps its preference profile (the Mac's workspace-preferences-v1 entry). Errors are
/// exceptions; the workspace reports them to the person and keeps running.
/// </summary>
public interface IWorkspacePreferencesStore
{
    /// <summary>The saved profile, or null when none was saved yet.</summary>
    byte[]? Load();

    /// <summary>Replaces the saved profile; implementations write atomically.</summary>
    void Save(byte[] data);
}

/// <summary>A store that keeps the profile in memory, for tests and for runs that must not write files.</summary>
public sealed class MemoryPreferencesStore : IWorkspacePreferencesStore
{
    private byte[]? _data;

    public MemoryPreferencesStore(byte[]? data = null)
    {
        _data = data;
    }

    /// <summary>How many times <see cref="Save"/> ran.</summary>
    public int SaveCount { get; private set; }

    public byte[]? Data => _data;

    public byte[]? Load() => _data;

    public void Save(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
        SaveCount++;
    }
}
