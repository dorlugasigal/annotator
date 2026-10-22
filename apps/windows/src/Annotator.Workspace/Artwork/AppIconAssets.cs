using System.Globalization;

namespace Annotator.Workspace.Artwork;

/// <summary>
/// Every icon file the Windows app ships, drawn by <see cref="AppIconArtwork"/>: the MSIX logos in
/// Assets/Package (each Windows scale, the app-list and taskbar target sizes in their unplated forms, the four tile
/// sizes and the Store logo) and the executable's Assets/Annotator.ico. apps/windows/tools/Annotator.IconExport writes
/// them; a test checks that the committed files still match.
/// </summary>
public static class AppIconAssets
{
    /// <summary>One file: its name in Assets/Package, its pixel size and the size of the icon centered in it.</summary>
    public sealed record Asset(string FileName, int Width, int Height, int IconSize);

    /// <summary>The package's logo folder, relative to the app project.</summary>
    public const string PackageFolder = "Assets/Package";

    /// <summary>The executable's icon, relative to the app project.</summary>
    public const string IcoFile = "Assets/Annotator.ico";

    /// <summary>The scales Windows picks from: 100%, 125%, 150%, 200% and 400%.</summary>
    public static IReadOnlyList<int> Scales { get; } = [100, 125, 150, 200, 400];

    /// <summary>Icon sizes in pixels for Start, the taskbar, Alt+Tab and File Explorer (Square44x44Logo target sizes and the ICO).</summary>
    public static IReadOnlyList<int> TargetSizes { get; } = [16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256];

    /// <summary>Every MSIX logo file, named with MRT qualifiers so Windows picks the right one.</summary>
    public static IReadOnlyList<Asset> PackageLogos { get; } = BuildPackageLogos();

    /// <summary>Renders <paramref name="asset"/> as straight-alpha RGBA rows: the icon centered on a transparent background.</summary>
    public static byte[] Render(Asset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return AppIconArtwork.RenderRgba(asset.Width, asset.Height, asset.IconSize,
            (asset.Width - asset.IconSize) / 2.0, (asset.Height - asset.IconSize) / 2.0);
    }

    /// <summary>The executable's icon with every target size.</summary>
    public static byte[] RenderIco() => IconFiles.EncodeIco(TargetSizes, AppIconArtwork.RenderRgba);

    private static List<Asset> BuildPackageLogos()
    {
        var logos = new List<Asset>();
        foreach (int scale in Scales)
        {
            string suffix = string.Create(CultureInfo.InvariantCulture, $".scale-{scale}.png");
            // App list and Store logos: the icon fills the square, with the 6% margin the artwork has.
            int list = Scaled(44, scale);
            logos.Add(new Asset("Square44x44Logo" + suffix, list, list, list));
            int store = Scaled(50, scale);
            logos.Add(new Asset("StoreLogo" + suffix, store, store, store));
            // Tiles: the icon two thirds of the tile's short side, centered.
            logos.Add(Tile("SmallTile" + suffix, Scaled(71, scale), Scaled(71, scale)));
            logos.Add(Tile("Square150x150Logo" + suffix, Scaled(150, scale), Scaled(150, scale)));
            logos.Add(Tile("Wide310x150Logo" + suffix, Scaled(310, scale), Scaled(150, scale)));
            logos.Add(Tile("LargeTile" + suffix, Scaled(310, scale), Scaled(310, scale)));
        }
        foreach (int size in TargetSizes)
        {
            // Windows 11 shows the unplated forms on Start, the taskbar and in Alt+Tab, in dark and light themes. The icon
            // carries its own dark tile, so all three forms are the same picture.
            string name = string.Create(CultureInfo.InvariantCulture, $"Square44x44Logo.targetsize-{size}");
            logos.Add(new Asset(name + ".png", size, size, size));
            logos.Add(new Asset(name + "_altform-unplated.png", size, size, size));
            logos.Add(new Asset(name + "_altform-lightunplated.png", size, size, size));
        }
        return logos;
    }

    /// <summary>A size at a Windows scale, rounded as Windows rounds asset sizes (71 at 125% is 89).</summary>
    private static int Scaled(int size, int scale) => (int)Math.Round(size * scale / 100.0, MidpointRounding.AwayFromZero);

    private static Asset Tile(string name, int width, int height)
    {
        int shortSide = Math.Min(width, height);
        int icon = (int)Math.Round(shortSide * 2 / 3.0, MidpointRounding.AwayFromZero);
        // Whole-pixel margins keep the tile's edges as sharp as the icon's own.
        if ((width - icon) % 2 != 0 || (height - icon) % 2 != 0)
        {
            icon--;
        }
        return new Asset(name, width, height, icon);
    }
}
