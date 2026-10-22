using Annotator.Platform.Capture;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace Annotator.Platform.Tests;

/// <summary>
/// Consent for programmatic, border-free capture (WIN-CAP-04). The test process is unpackaged, like the development app:
/// Windows asks nothing of it, so capture starts as before. A packaged Annotator reads both answers before capture starts
/// and fails closed at once without them; only the app asks, from a window in front.
/// </summary>
public sealed class CaptureAccessTests
{
    private const CaptureAccessStatus Allowed = CaptureAccessStatus.Allowed;
    private const CaptureAccessStatus NotAsked = CaptureAccessStatus.UserPromptRequired;
    private const CaptureAccessStatus Denied = CaptureAccessStatus.DeniedByUser;

    [Fact]
    public async Task AnUnpackagedProcessNeedsNoConsentAndNeverAsks()
    {
        Assert.Equal(CaptureConsent.NotNeeded, CaptureAccess.Check());
        CaptureAccess.Require();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(CaptureConsent.NotNeeded, await CaptureAccess.RequestAsync(TimeSpan.FromMinutes(1)));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "Nothing to ask, so nothing to wait for.");
        Assert.True(CaptureConsent.NotNeeded.Granted);
        Assert.Null(CaptureConsent.NotNeeded.Failure);
    }

    [Fact]
    public void CaptureStartsOnlyWithBothAnswers()
    {
        Assert.True(new CaptureConsent(Allowed, Allowed).Granted);
        Assert.Null(new CaptureConsent(Allowed, Allowed).Failure);
        Assert.False(new CaptureConsent(Allowed, NotAsked).Granted);
        Assert.False(new CaptureConsent(NotAsked, NotAsked).Granted);
        Assert.False(new CaptureConsent(Denied, Allowed).Granted);
        Assert.False(new CaptureConsent(Allowed, Denied).Granted);
    }

    [Fact]
    public void EachMissingAnswerFailsWithItsReason()
    {
        Assert.Equal(CaptureFailureKind.AccessNeeded, new CaptureConsent(NotAsked, NotAsked).Failure!.Kind);
        Assert.Equal(CaptureFailureKind.AccessNeeded, new CaptureConsent(Allowed, NotAsked).Failure!.Kind);
        Assert.Equal(CaptureFailureKind.AccessDenied, new CaptureConsent(Denied, NotAsked).Failure!.Kind);
        Assert.Equal(CaptureFailureKind.AccessDenied, new CaptureConsent(CaptureAccessStatus.DeniedBySystem, Allowed).Failure!.Kind);
        Assert.Equal(CaptureFailureKind.AccessDenied, new CaptureConsent(CaptureAccessStatus.NotDeclaredByApp, Allowed).Failure!.Kind);
        Assert.Equal(CaptureFailureKind.AccessDenied, new CaptureConsent(CaptureAccessStatus.Failed, Allowed).Failure!.Kind);
        Assert.Equal(CaptureFailureKind.AccessDenied, new CaptureConsent(CaptureAccessStatus.NoAnswer, NotAsked).Failure!.Kind);
        Assert.Equal(CaptureFailureKind.BorderRequired, new CaptureConsent(Allowed, Denied).Failure!.Kind);
        Assert.Equal(CaptureFailureKind.BorderRequired, new CaptureConsent(Allowed, CaptureAccessStatus.Failed).Failure!.Kind);
        Assert.Equal("Screen capture consent (screenshots Allowed, border DeniedByUser)", new CaptureConsent(Allowed, Denied).Failure!.Operation);
    }

    [Fact]
    public void SupportInfoNamesEveryStatus()
    {
        Assert.Equal("not needed", CaptureAccess.Describe(CaptureAccessStatus.NotNeeded));
        Assert.Equal("allowed", CaptureAccess.Describe(CaptureAccessStatus.Allowed));
        Assert.Equal("turned off in Windows Settings", CaptureAccess.Describe(CaptureAccessStatus.DeniedByUser));
        Assert.Equal("not allowed by Windows", CaptureAccess.Describe(CaptureAccessStatus.DeniedBySystem));
        Assert.Equal("not declared by the package", CaptureAccess.Describe(CaptureAccessStatus.NotDeclaredByApp));
        Assert.Equal("not asked yet", CaptureAccess.Describe(CaptureAccessStatus.UserPromptRequired));
        Assert.Equal("unknown", CaptureAccess.Describe(CaptureAccessStatus.NoAnswer));
        Assert.Equal("unknown", CaptureAccess.Describe(CaptureAccessStatus.Failed));
    }

    [Fact]
    public void SupportInfoNamesTheQuestionThatStopsCapture()
    {
        Assert.Equal("not needed", CaptureAccess.Describe(CaptureConsent.NotNeeded));
        Assert.Equal("allowed", CaptureAccess.Describe(new CaptureConsent(Allowed, Allowed)));
        Assert.Equal("not asked yet (screenshots and screen recording)", CaptureAccess.Describe(new CaptureConsent(NotAsked, NotAsked)));
        Assert.Equal("turned off in Windows Settings (screenshots and screen recording)", CaptureAccess.Describe(new CaptureConsent(Denied, Allowed)));
        Assert.Equal("not asked yet (screenshot border)", CaptureAccess.Describe(new CaptureConsent(Allowed, NotAsked)));
        Assert.Equal("turned off in Windows Settings (screenshot border)", CaptureAccess.Describe(new CaptureConsent(Allowed, Denied)));
    }

    [Fact]
    public void EveryWindowsAnswerHasATypedStatus()
    {
        Assert.Equal(CaptureAccessStatus.Allowed, CaptureAccess.Map(AppCapabilityAccessStatus.Allowed));
        Assert.Equal(CaptureAccessStatus.DeniedByUser, CaptureAccess.Map(AppCapabilityAccessStatus.DeniedByUser));
        Assert.Equal(CaptureAccessStatus.DeniedBySystem, CaptureAccess.Map(AppCapabilityAccessStatus.DeniedBySystem));
        Assert.Equal(CaptureAccessStatus.NotDeclaredByApp, CaptureAccess.Map(AppCapabilityAccessStatus.NotDeclaredByApp));
        Assert.Equal(CaptureAccessStatus.UserPromptRequired, CaptureAccess.Map(AppCapabilityAccessStatus.UserPromptRequired));
        Assert.Equal(CaptureAccessStatus.Failed, CaptureAccess.Map((AppCapabilityAccessStatus)42));
    }

    [Fact]
    public void EachQuestionHasItsWindowsSettingsPage()
    {
        // Privacy & security > Screenshots and screen recording, and > Screenshot borders (checked on build 26200).
        Assert.Equal("ms-settings:privacy-graphicscaptureprogrammatic", CaptureAccess.ScreenSettingsUri);
        Assert.Equal("ms-settings:privacy-graphicscapturewithoutborder", CaptureAccess.BorderSettingsUri);
    }

    [Fact]
    public void ThePackageDeclaresBothCaptureCapabilities()
    {
        string manifest = File.ReadAllText(StartupTaskTests.PackageManifest());
        Assert.Contains("<uap11:Capability Name=\"graphicsCaptureProgrammatic\" />", manifest, StringComparison.Ordinal);
        Assert.Contains("<uap11:Capability Name=\"graphicsCaptureWithoutBorder\" />", manifest, StringComparison.Ordinal);
    }
}
