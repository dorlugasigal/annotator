using Annotator.Workspace.Artwork;

// Writes Assets/Package/*.png and Assets/Annotator.ico into the app project given as the only argument, removing logos that
// the list no longer has. Run from the repository: dotnet run --project apps/windows/tools/Annotator.IconExport --
// apps/windows/src/Annotator.App
if (args.Length != 1 || !File.Exists(Path.Combine(args[0], "Annotator.App.csproj")))
{
    Console.Error.WriteLine("Usage: Annotator.IconExport <folder of Annotator.App.csproj>");
    return 2;
}
string project = Path.GetFullPath(args[0]);
string folder = Path.Combine(project, AppIconAssets.PackageFolder.Replace('/', Path.DirectorySeparatorChar));
Directory.CreateDirectory(folder);
var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (AppIconAssets.Asset asset in AppIconAssets.PackageLogos)
{
    byte[] png = IconFiles.EncodePng(AppIconAssets.Render(asset), asset.Width, asset.Height);
    File.WriteAllBytes(Path.Combine(folder, asset.FileName), png);
    written.Add(asset.FileName);
}
foreach (string stale in Directory.EnumerateFiles(folder, "*.png").Where(path => !written.Contains(Path.GetFileName(path))))
{
    File.Delete(stale);
    Console.WriteLine($"removed {Path.GetFileName(stale)}");
}
string ico = Path.Combine(project, AppIconAssets.IcoFile.Replace('/', Path.DirectorySeparatorChar));
File.WriteAllBytes(ico, AppIconAssets.RenderIco());
Console.WriteLine($"wrote {written.Count} logos to {folder} and {ico}");
return 0;
