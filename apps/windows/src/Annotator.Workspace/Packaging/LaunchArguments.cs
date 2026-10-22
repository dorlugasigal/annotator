namespace Annotator.Workspace.Packaging;

/// <summary>
/// What a launch of Annotator.exe asks for (WIN-SYS-01, WIN-SYS-02, WIN-SYS-04): the board file to open, named by
/// "Annotator.exe board.annotator" or by the package's file type association ("%1"), and whether the package's startup
/// task started the app at sign-in, which passes <see cref="StartupTaskSwitch"/>.
/// </summary>
public sealed record LaunchArguments(string? Board, bool StartupTask)
{
    /// <summary>The argument the startup task passes (uap10:Parameters in Package.appxmanifest).</summary>
    public const string StartupTaskSwitch = "--startup-task";

    /// <summary>
    /// A quiet launch shows no toolbar and no tour, only the tray icon and the global shortcuts (the Mac's login item). A
    /// launch that names a board is never quiet: opening it starts drawing.
    /// </summary>
    public bool Quiet => StartupTask && Board is null;

    /// <summary>
    /// Reads the arguments: the board is the first argument that is not a switch, as a full path from the launch's own
    /// folder (kept as it is when Windows cannot make it full, so opening it reports why).
    /// </summary>
    public static LaunchArguments Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? board = null;
        bool startupTask = false;
        foreach (string argument in args)
        {
            if (string.Equals(argument, StartupTaskSwitch, StringComparison.OrdinalIgnoreCase))
            {
                startupTask = true;
                continue;
            }
            if (board is not null || argument.Length == 0 || argument.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }
            try
            {
                board = Path.GetFullPath(argument);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                board = argument;
            }
        }
        return new LaunchArguments(board, startupTask);
    }
}
