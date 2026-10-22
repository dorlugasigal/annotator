using Annotator.Workspace;

namespace Annotator.App;

/// <summary>
/// Keeps the preference profile as workspace-preferences-v1.json in the app's data folder. Saves write a
/// temporary file and then replace the profile, so a failed save never leaves a partial file.
/// </summary>
internal sealed class FilePreferencesStore(string folder) : IWorkspacePreferencesStore
{
    private readonly string _path = Path.Combine(folder, "workspace-preferences-v1.json");

    public byte[]? Load()
    {
        try
        {
            return File.ReadAllBytes(_path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public void Save(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Directory.CreateDirectory(folder);
        string temporary = _path + ".tmp";
        File.WriteAllBytes(temporary, data);
        File.Move(temporary, _path, overwrite: true);
    }
}
