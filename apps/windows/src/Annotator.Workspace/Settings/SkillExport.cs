namespace Annotator.Workspace.Settings;

/// <summary>Why the AI skill could not be saved (Mac SkillExportFailure), with the Mac's words and the Windows terms.</summary>
public sealed class SkillExportFailure(string message) : Exception(message)
{
    public static SkillExportFailure MissingResource() => new("The AI skill is missing from this app. Install the latest build and try again.");

    public static SkillExportFailure FolderExists() =>
        new("An annotator folder already exists there. Choose another folder so your files are not replaced.");

    public static SkillExportFailure InvalidDestination() => new("Choose a folder on this PC for the skill.");
}

/// <summary>
/// Port of ComputerUseSkill (WIN-AI-01): saves the bundled annotator skill as an "annotator" folder with SKILL.md in a folder
/// the person chooses. It never replaces an existing folder or file.
/// </summary>
public static class ComputerUseSkill
{
    public const string FolderName = "annotator";

    public const string FileName = "SKILL.md";

    /// <summary>Copies <paramref name="source"/> to <paramref name="directory"/>\annotator\SKILL.md and returns the new folder.</summary>
    public static string Export(string directory, string source)
    {
        if (string.IsNullOrEmpty(directory) || !System.IO.Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
        {
            throw SkillExportFailure.InvalidDestination();
        }
        if (!File.Exists(source))
        {
            throw SkillExportFailure.MissingResource();
        }
        byte[] data = File.ReadAllBytes(source);
        string destination = System.IO.Path.Combine(directory, FolderName);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw SkillExportFailure.FolderExists();
        }
        Directory.CreateDirectory(destination);
        using (var file = new FileStream(System.IO.Path.Combine(destination, FileName), FileMode.CreateNew, FileAccess.Write))
        {
            file.Write(data);
        }
        return destination;
    }

    /// <summary>The status after a save (Mac exportComputerUseSkill).</summary>
    public static string Saved(string folder) => $"Saved the skill to {folder}. Open Setup guide under Help > Use an AI assistant.";
}
