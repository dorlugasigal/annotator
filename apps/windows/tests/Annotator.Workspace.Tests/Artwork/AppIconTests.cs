using System.Buffers.Binary;
using Annotator.Workspace.Artwork;
using Annotator.Workspace.Tests.Settings;

namespace Annotator.Workspace.Tests.Artwork;

/// <summary>
/// The Windows app's icons (WIN-DST-01, plan Phase 6): drawn from the Mac's icon geometry and output colors
/// (apps/macos/Artwork/main.swift), and the committed MSIX logos and Annotator.ico are exactly what the artwork draws today.
/// </summary>
public sealed class AppIconTests
{
    private static string MacIconFolder => Path.GetDirectoryName(SettingsLogicTests.RepositoryFile(
        "apps", "macos", "Annotator", "Assets.xcassets", "AppIcon.appiconset", "Contents.json"))!;

    private static string AppProject => Path.GetDirectoryName(SettingsLogicTests.RepositoryFile(
        "apps", "windows", "src", "Annotator.App", "Annotator.App.csproj"))!;

    [Fact]
    public void TheTileMarkAndDotHaveTheMacIconColors()
    {
        (int size, _, byte[] mac) = PngDecoder.Decode(File.ReadAllBytes(Path.Combine(MacIconFolder, "icon_512x512@2x.png")));
        byte[] windows = AppIconArtwork.RenderRgba(size);
        // Inside the tile, on a stroke of the mark and at the dot's center; and a transparent corner.
        foreach ((int x, int y) in new[] { (512, 900), (140, 140), (520, 300), (768, 256), (10, 10) })
        {
            int index = (y * size + x) * 4;
            Assert.Equal(mac.AsSpan(index, 4).ToArray(), windows.AsSpan(index, 4).ToArray());
        }
    }

    [Theory]
    // The mean premultiplied difference per channel, out of 255. The Mac's AppKit draws the mark through a cached image, so
    // its small sizes are softer than the Windows logos, which draw the same vector at each size; flat areas are equal.
    [InlineData("icon_512x512@2x.png", 0.15)]
    [InlineData("icon_512x512@1x.png", 0.16)]
    [InlineData("icon_256x256@1x.png", 0.16)]
    [InlineData("icon_128x128@1x.png", 0.22)]
    [InlineData("icon_32x32@2x.png", 2.0)]
    [InlineData("icon_32x32@1x.png", 5.0)]
    [InlineData("icon_16x16@1x.png", 10.0)]
    public void EveryMacIconSizeIsDrawnTheSameWay(string file, double meanLimit)
    {
        (int size, int height, byte[] mac) = PngDecoder.Decode(File.ReadAllBytes(Path.Combine(MacIconFolder, file)));
        Assert.Equal(size, height);
        byte[] windows = AppIconArtwork.RenderRgba(size);
        double sum = 0;
        int silhouette = 0;
        for (int pixel = 0; pixel < size * size; pixel++)
        {
            int alphaMac = mac[pixel * 4 + 3], alphaWindows = windows[pixel * 4 + 3];
            silhouette = Math.Max(silhouette, Math.Abs(alphaMac - alphaWindows));
            sum += Math.Abs(alphaMac - alphaWindows);
            for (int channel = 0; channel < 3; channel++)
            {
                sum += Math.Abs(mac[pixel * 4 + channel] * alphaMac / 255.0 - windows[pixel * 4 + channel] * alphaWindows / 255.0);
            }
        }
        Assert.True(sum / (size * size * 4) <= meanLimit, $"{file}: mean difference {sum / (size * size * 4):F3} is above {meanLimit}.");
        // The rounded tile's outline is the same shape: its edge pixels' coverage stays within a few levels.
        Assert.True(silhouette <= 40, $"{file}: the tile's outline differs by {silhouette} levels of alpha.");
    }

    [Fact]
    public void TheCommittedPackageLogosMatchTheArtwork()
    {
        string folder = Path.Combine(AppProject, "Assets", "Package");
        var expected = AppIconAssets.PackageLogos.Select(asset => asset.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var committed = Directory.GetFiles(folder, "*.png").Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(expected.SetEquals(committed!),
            "Assets/Package differs from AppIconAssets. Run: dotnet run --project apps/windows/tools/Annotator.IconExport -- apps/windows/src/Annotator.App");
        foreach (AppIconAssets.Asset asset in AppIconAssets.PackageLogos)
        {
            (int width, int height, byte[] pixels) = PngDecoder.Decode(File.ReadAllBytes(Path.Combine(folder, asset.FileName)));
            Assert.Equal((asset.Width, asset.Height), (width, height));
            Assert.True(AppIconAssets.Render(asset).AsSpan().SequenceEqual(pixels),
                $"{asset.FileName} is out of date. Run apps/windows/tools/Annotator.IconExport.");
        }
    }

    [Fact]
    public void LogoNamesCarryTheQualifiersWindowsLooksFor()
    {
        IReadOnlyList<AppIconAssets.Asset> logos = AppIconAssets.PackageLogos;
        foreach (string logo in new[] { "Square44x44Logo", "StoreLogo", "SmallTile", "Square150x150Logo", "Wide310x150Logo", "LargeTile" })
        {
            Assert.Equal(AppIconAssets.Scales.Count, logos.Count(asset => asset.FileName.StartsWith(logo + ".scale-", StringComparison.Ordinal)));
        }
        Assert.Contains(logos, asset => asset is { FileName: "Square44x44Logo.scale-125.png", Width: 55, Height: 55 });
        Assert.Contains(logos, asset => asset is { FileName: "SmallTile.scale-125.png", Width: 89, Height: 89 });
        Assert.Contains(logos, asset => asset is { FileName: "Wide310x150Logo.scale-150.png", Width: 465, Height: 225 });
        Assert.Contains(logos, asset => asset is { FileName: "StoreLogo.scale-400.png", Width: 200, Height: 200 });
        foreach (int size in AppIconAssets.TargetSizes)
        {
            Assert.Single(logos, asset => asset.FileName == $"Square44x44Logo.targetsize-{size}_altform-unplated.png" && asset.Width == size);
            Assert.Single(logos, asset => asset.FileName == $"Square44x44Logo.targetsize-{size}_altform-lightunplated.png" && asset.Width == size);
        }
        // Tiles keep whole-pixel margins around an icon two thirds of their short side.
        Assert.All(logos, asset =>
        {
            Assert.Equal(0, (asset.Width - asset.IconSize) % 2);
            Assert.Equal(0, (asset.Height - asset.IconSize) % 2);
            Assert.InRange(asset.IconSize, Math.Min(asset.Width, asset.Height) * 0.6, Math.Min(asset.Width, asset.Height));
        });
    }

    [Fact]
    public void TheCommittedExecutableIconMatchesTheArtwork()
    {
        byte[] ico = File.ReadAllBytes(Path.Combine(AppProject, "Assets", "Annotator.ico"));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(ico));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2)));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));
        Assert.Equal(AppIconAssets.TargetSizes.Count, count);
        for (int index = 0; index < count; index++)
        {
            ReadOnlySpan<byte> entry = ico.AsSpan(6 + 16 * index, 16);
            int size = entry[0] == 0 ? 256 : entry[0];
            Assert.Equal(AppIconAssets.TargetSizes[index], size);
            Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]));
            byte[] image = ico.AsSpan(BinaryPrimitives.ReadInt32LittleEndian(entry[12..]), BinaryPrimitives.ReadInt32LittleEndian(entry[8..])).ToArray();
            byte[] pixels = size < IconFiles.IcoPngThreshold ? DecodeBitmap(image, size) : PngDecoder.Decode(image).Rgba;
            Assert.True(AppIconArtwork.RenderRgba(size).AsSpan().SequenceEqual(pixels),
                $"The {size}-pixel image in Assets/Annotator.ico is out of date. Run apps/windows/tools/Annotator.IconExport.");
        }
    }

    [Fact]
    public void PngFilesRoundTrip()
    {
        byte[] pixels = AppIconAssets.Render(AppIconAssets.PackageLogos.First(asset => asset.FileName == "Wide310x150Logo.scale-100.png"));
        byte[] png = IconFiles.EncodePng(pixels, 310, 150);
        (int width, int height, byte[] decoded) = PngDecoder.Decode(png);
        Assert.Equal((310, 150), (width, height));
        Assert.Equal(pixels, decoded);
    }

    /// <summary>A 32-bit ICO bitmap: BITMAPINFOHEADER, BGRA rows bottom first, then the AND mask.</summary>
    private static byte[] DecodeBitmap(byte[] image, int size)
    {
        Assert.Equal(40, BinaryPrimitives.ReadInt32LittleEndian(image));
        Assert.Equal(size, BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(4)));
        Assert.Equal(size * 2, BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(8)));
        int maskStride = (size + 31) / 32 * 4;
        Assert.Equal(40 + size * size * 4 + maskStride * size, image.Length);
        var rgba = new byte[size * size * 4];
        for (int row = 0; row < size; row++)
        {
            for (int column = 0; column < size; column++)
            {
                int from = 40 + ((size - 1 - row) * size + column) * 4;
                int to = (row * size + column) * 4;
                rgba[to] = image[from + 2];
                rgba[to + 1] = image[from + 1];
                rgba[to + 2] = image[from];
                rgba[to + 3] = image[from + 3];
                bool masked = (image[40 + size * size * 4 + (size - 1 - row) * maskStride + column / 8] & (0x80 >> (column % 8))) != 0;
                Assert.Equal(rgba[to + 3] == 0, masked);
            }
        }
        return rgba;
    }
}
