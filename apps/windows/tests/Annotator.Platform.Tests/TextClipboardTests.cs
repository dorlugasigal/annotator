using Annotator.Platform.Text;

namespace Annotator.Platform.Tests;

/// <summary>
/// The text editor's clipboard line ends. The tests never touch the real clipboard: the person using this computer may
/// have something on it.
/// </summary>
public sealed class TextClipboardTests
{
    [Theory]
    [InlineData("One line", "One line")]
    [InlineData("One\nTwo\n", "One\r\nTwo\r\n")]
    [InlineData("\n\n", "\r\n\r\n")]
    [InlineData("Already\r\nWindows", "Already\r\nWindows")]
    public void CopiedLinesEndWithCarriageReturnAndLineFeed(string text, string expected) =>
        Assert.Equal(expected, TextClipboard.WindowsLineEnds(text));

    [Theory]
    [InlineData("One\r\nTwo", "One\nTwo")]
    [InlineData("One\rTwo\r", "One\nTwo\n")]
    [InlineData("\r\n\r\n", "\n\n")]
    [InlineData("One\nTwo", "One\nTwo")]
    [InlineData("\u05E9\u05DC\u05D5\u05DD \U0001F600", "\u05E9\u05DC\u05D5\u05DD \U0001F600")]
    public void PastedLinesEndWithLineFeed(string text, string expected) =>
        Assert.Equal(expected, TextClipboard.EditorLineEnds(text));
}
