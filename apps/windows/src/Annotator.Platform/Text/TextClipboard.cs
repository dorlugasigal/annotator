using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Memory;
using Windows.Win32.System.Ole;

namespace Annotator.Platform.Text;

/// <summary>
/// Plain text on the Windows clipboard (CF_UNICODETEXT) for the canvas text editor's Cut, Copy and Paste (WIN-INP-09).
/// The editor's lines end with LF and the clipboard's with CR LF. Use it on the editor's thread, with one of that
/// thread's windows as the clipboard owner.
/// </summary>
public static unsafe class TextClipboard
{
    /// <summary>What the person reads when another app holds the clipboard.</summary>
    public const string BusyMessage = "Another app is using the clipboard. Try again.";

    /// <summary>What the person reads when the copied text is too large to paste.</summary>
    public const string TooLargeMessage = "The copied text is too large to paste. Copy less text and try again.";

    /// <summary>The largest text Paste takes, in UTF-16 code units: far more than any annotation needs.</summary>
    public const int MaximumLength = 1_000_000;

    private const int OpenAttempts = 5;

    /// <summary>The clipboard's text with LF line ends, or null when it holds no text.</summary>
    /// <exception cref="InvalidOperationException">Another app holds the clipboard, or the text is too large.</exception>
    public static string? GetText(nint owner)
    {
        if (!PInvoke.IsClipboardFormatAvailable((uint)CLIPBOARD_FORMAT.CF_UNICODETEXT))
        {
            return null;
        }
        Open(owner);
        try
        {
            HANDLE handle = PInvoke.GetClipboardData((uint)CLIPBOARD_FORMAT.CF_UNICODETEXT);
            if (handle.IsNull)
            {
                return null;
            }
            var memory = new HGLOBAL((void*)handle.Value);
            nuint bytes = PInvoke.GlobalSize(memory);
            void* source = PInvoke.GlobalLock(memory);
            if (source is null)
            {
                throw new InvalidOperationException(BusyMessage);
            }
            try
            {
                // Clipboard memory can be larger than its text, which ends at the first NUL.
                var text = new ReadOnlySpan<char>(source, (int)Math.Min(bytes / sizeof(char), (nuint)MaximumLength * 2 + 2));
                int end = text.IndexOf('\0');
                text = end >= 0 ? text[..end] : text;
                string result = EditorLineEnds(text);
                return result.Length > MaximumLength ? throw new InvalidOperationException(TooLargeMessage) : result;
            }
            finally
            {
                PInvoke.GlobalUnlock(memory);
            }
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    /// <summary>Puts <paramref name="text"/> on the clipboard as the only thing there, with CR LF line ends.</summary>
    /// <exception cref="InvalidOperationException">Another app holds the clipboard or it did not take the text.</exception>
    public static void SetText(nint owner, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string windows = WindowsLineEnds(text);
        Open(owner);
        try
        {
            if (!PInvoke.EmptyClipboard())
            {
                throw new InvalidOperationException(BusyMessage);
            }
            nuint bytes = (nuint)(windows.Length + 1) * sizeof(char);
            HGLOBAL memory = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, bytes);
            if (memory.IsNull)
            {
                throw new InvalidOperationException(BusyMessage);
            }
            void* target = PInvoke.GlobalLock(memory);
            if (target is null)
            {
                PInvoke.GlobalFree(memory);
                throw new InvalidOperationException(BusyMessage);
            }
            var destination = new Span<char>(target, windows.Length + 1);
            windows.AsSpan().CopyTo(destination);
            destination[^1] = '\0';
            PInvoke.GlobalUnlock(memory);
            if (PInvoke.SetClipboardData((uint)CLIPBOARD_FORMAT.CF_UNICODETEXT, new HANDLE((nint)memory.Value)).IsNull)
            {
                // The clipboard did not take the memory, so it is still ours to free.
                PInvoke.GlobalFree(memory);
                throw new InvalidOperationException(BusyMessage);
            }
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    /// <summary>The editor's text as Windows apps expect it on the clipboard: every line ends with CR LF.</summary>
    public static string WindowsLineEnds(string text)
    {
        if (!text.Contains('\n'))
        {
            return text;
        }
        var result = new StringBuilder(text.Length + 16);
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (character == '\n' && (index == 0 || text[index - 1] != '\r'))
            {
                result.Append('\r');
            }
            result.Append(character);
        }
        return result.ToString();
    }

    /// <summary>Text from Windows as the editor keeps it: CR LF and a lone CR become LF.</summary>
    public static string EditorLineEnds(ReadOnlySpan<char> text)
    {
        if (!text.Contains('\r'))
        {
            return text.ToString();
        }
        var result = new StringBuilder(text.Length);
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (character == '\r')
            {
                result.Append('\n');
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }
                continue;
            }
            result.Append(character);
        }
        return result.ToString();
    }

    /// <summary>Opens the clipboard; another app can hold it for a moment, so this tries a few times.</summary>
    private static void Open(nint owner)
    {
        for (int attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (PInvoke.OpenClipboard(new HWND(owner)))
            {
                return;
            }
            Thread.Sleep(10);
        }
        throw new InvalidOperationException(BusyMessage);
    }
}
