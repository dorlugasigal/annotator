namespace Annotator.Workspace.Packaging;

/// <summary>
/// What a launch of Annotator.exe asks for (WIN-SYS-01, WIN-SYS-02, WIN-SYS-04): the board file to open, named by
/// "Annotator.exe board.annotator" or by the package's file type association ("%1"), and whether the package's startup
/// task started the app at sign-in, which passes <see cref="StartupTaskSwitch"/>, or Windows started it again after an
/// update the Microsoft Store installed by itself (<see cref="AfterUpdateSwitch"/>).
/// </summary>
public sealed record LaunchArguments(string? Board, bool StartupTask, bool AfterUpdate = false)
{
    /// <summary>The argument the startup task passes (uap10:Parameters in Package.appxmanifest).</summary>
    public const string StartupTaskSwitch = "--startup-task";

    /// <summary>
    /// The argument Windows passes when it starts Annotator again after closing it for an update the Microsoft Store
    /// installed by itself (WIN-SYS-03). Annotator comes back as it does at sign-in, without covering the screen.
    /// </summary>
    public const string AfterUpdateSwitch = "--after-update";

    /// <summary>
    /// A quiet launch shows no toolbar and no tutorial, only the tray icon and the global shortcuts (the Mac's login item). A
    /// launch that names a board is never quiet: opening it starts drawing.
    /// </summary>
    public bool Quiet => (StartupTask || AfterUpdate) && Board is null;

    /// <summary>
    /// Reads the arguments: the board is the first argument that is not a switch, as a full path from the launch's own
    /// folder (kept as it is when Windows cannot make it full, so opening it reports why).
    /// </summary>
    public static LaunchArguments Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? board = null;
        bool startupTask = false;
        bool afterUpdate = false;
        foreach (string argument in args)
        {
            if (string.Equals(argument, StartupTaskSwitch, StringComparison.OrdinalIgnoreCase))
            {
                startupTask = true;
                continue;
            }
            if (string.Equals(argument, AfterUpdateSwitch, StringComparison.OrdinalIgnoreCase))
            {
                afterUpdate = true;
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
        return new LaunchArguments(board, startupTask, afterUpdate);
    }
}
