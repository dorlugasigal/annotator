using System.Xml.Linq;

namespace Annotator.Platform.Tests;

/// <summary>
/// The Store identity in Package.appxmanifest (WIN-DST-01, D-7): the Partner Center values copied on 2026-10-20. The Store
/// requires the reserved name as the package and Start menu display name; the tile label and startup task keep the brand.
/// </summary>
public sealed class PackageManifestTests
{
    private static readonly XNamespace Foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
    private static readonly XNamespace Uap5 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/5";

    [Fact]
    public void TheManifestCarriesThePartnerCenterIdentityAndTheReservedName()
    {
        XElement package = XDocument.Load(StartupTaskTests.PackageManifest()).Root!;
        XElement identity = package.Element(Foundation + "Identity")!;
        Assert.Equal("DorLugasi-Gal.AnnotatorScreenAnnotation", identity.Attribute("Name")!.Value);
        Assert.Equal("CN=9BC8A074-AAD1-4E16-8AC2-B68940F537CD", identity.Attribute("Publisher")!.Value);
        XElement properties = package.Element(Foundation + "Properties")!;
        Assert.Equal("Annotator – Screen Annotation", properties.Element(Foundation + "DisplayName")!.Value);
        Assert.Equal("Dor Lugasi-Gal", properties.Element(Foundation + "PublisherDisplayName")!.Value);
        XElement visuals = package.Descendants(Uap + "VisualElements").Single();
        Assert.Equal("Annotator – Screen Annotation", visuals.Attribute("DisplayName")!.Value);
        Assert.Equal("Annotator", visuals.Element(Uap + "DefaultTile")!.Attribute("ShortName")!.Value);
        Assert.Equal("Annotator", package.Descendants(Uap5 + "StartupTask").Single().Attribute("DisplayName")!.Value);
        Assert.DoesNotContain("PLACEHOLDER", File.ReadAllText(StartupTaskTests.PackageManifest()), StringComparison.Ordinal);
    }
}
