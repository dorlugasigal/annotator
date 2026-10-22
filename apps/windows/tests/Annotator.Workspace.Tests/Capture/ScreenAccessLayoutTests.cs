using Annotator.Workspace.Capture;

namespace Annotator.Workspace.Tests.Capture;

/// <summary>
/// The explanation's place: above Windows' question, which Windows centers in the work area (530 by 186 points measured on
/// build 26200, 1015,603 in a 2,560 by 1,392 work area at 100%).
/// </summary>
public sealed class ScreenAccessLayoutTests
{
    [Fact]
    public void EndsAboveWindowsQuestionAsMeasured()
    {
        // The explanation measured 476 by 222 pixels at 100%; the packaged test found it at 1042,344.
        (int x, int y) = ScreenAccessLayout.Origin(0, 0, 2560, 1392, 476, 222, 1.0);

        Assert.Equal((1042, 344), (x, y));
        Assert.True(y + 222 <= QuestionTop(1392, 186, 1.0) - 20);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void StaysClearOfATwoLineQuestionAtEveryScale(double scale)
    {
        int height = (int)Math.Round(222 * scale);
        (_, int y) = ScreenAccessLayout.Origin(0, 0, 2560, 1392, (int)Math.Round(476 * scale), height, scale);

        Assert.True(y + height <= QuestionTop(1392, 216, scale), $"bottom {y + height} at scale {scale}");
        Assert.True(y >= 0);
    }

    [Fact]
    public void CentersAcrossTheToolbarsDisplay()
    {
        (int x, int y) = ScreenAccessLayout.Origin(2560, 40, 1920, 1040, 476, 222, 1.0);

        Assert.Equal(2560 + (1920 - 476) / 2, x);
        Assert.Equal(40 + 520 - 130 - 222, y);
    }

    [Fact]
    public void ShortWorkAreaPutsItAtTheTop()
    {
        (_, int y) = ScreenAccessLayout.Origin(0, 30, 1024, 500, 476, 222, 1.0);

        Assert.Equal(30, y);
    }

    /// <summary>The top of Windows' question of <paramref name="height"/> points, centered in a work area that starts at 0.</summary>
    private static double QuestionTop(int workHeight, double height, double scale) => workHeight / 2.0 - height * scale / 2;
}
