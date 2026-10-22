using System.Collections.Frozen;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Annotator.Core;

/// <summary>Swift standard library arithmetic that differs from the .NET defaults.</summary>
internal static class SwiftMath
{
    // Swift.min(x, y) is `y < x ? y : x` and Swift.max(x, y) is `y >= x ? y : x`.
    // Math.Min and Math.Max treat NaN and signed zeros differently, so ported code uses these.
    public static double Min(double x, double y) => y < x ? y : x;

    public static double Max(double x, double y) => y >= x ? y : x;

    public static int Min(int x, int y) => y < x ? y : x;

    public static int Max(int x, int y) => y >= x ? y : x;
}

/// <summary>Swift String and CharacterSet behaviour used by validation.</summary>
internal static class SwiftText
{
    /// <summary>`text.utf8.count`.</summary>
    public static int Utf8Count(string text) => Encoding.UTF8.GetByteCount(text);

    /// <summary>`text.unicodeScalars.count`.</summary>
    public static int ScalarCount(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
        {
            count++;
        }
        return count;
    }

    /// <summary>`text.count`: extended grapheme clusters.</summary>
    public static int CharacterCount(string text) => text.Length == 0 ? 0 : new StringInfo(text).LengthInTextElements;

    /// <summary>`CharacterSet.alphanumerics`: general categories L*, M* and N*.</summary>
    public static bool IsAlphanumeric(Rune rune) => Rune.GetUnicodeCategory(rune) switch
    {
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
            or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
            or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber => true,
        _ => false,
    };

    /// <summary>`CharacterSet.letters`: general categories L* and M*.</summary>
    public static bool IsLetter(Rune rune) => Rune.GetUnicodeCategory(rune) switch
    {
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
            or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark => true,
        _ => false,
    };

    /// <summary>`CharacterSet.controlCharacters`: general categories Cc and Cf.</summary>
    public static bool IsControl(Rune rune) => Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format;

    /// <summary>`CharacterSet.newlines`: U+000A to U+000D, U+0085, U+2028 and U+2029.</summary>
    public static bool IsNewline(Rune rune) => rune.Value is (>= 0x0A and <= 0x0D) or 0x85 or 0x2028 or 0x2029;

    /// <summary>
    /// `CharacterSet.whitespacesAndNewlines`: the newlines, U+0009, U+0020, U+00A0, U+1680, U+2000 to U+200B (zero
    /// width space included, as Core Foundation keeps it), U+202F, U+205F and U+3000 (corpus `platform/strings.jsonl`).
    /// </summary>
    public static bool IsWhitespaceOrNewline(Rune rune) =>
        IsNewline(rune) || rune.Value is 0x09 or 0x20 or 0xA0 or 0x1680 or (>= 0x2000 and <= 0x200B) or 0x202F or 0x205F or 0x3000;

    /// <summary>`trimmingCharacters(in: .whitespacesAndNewlines)`.</summary>
    public static string TrimWhitespacesAndNewlines(string text)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && Rune.DecodeFromUtf16(text.AsSpan(start, end - start), out var rune, out var used) == System.Buffers.OperationStatus.Done
               && IsWhitespaceOrNewline(rune))
        {
            start += used;
        }
        while (end > start && Rune.DecodeLastFromUtf16(text.AsSpan(start, end - start), out var rune, out var used) == System.Buffers.OperationStatus.Done
               && IsWhitespaceOrNewline(rune))
        {
            end -= used;
        }
        return start == 0 && end == text.Length ? text : text[start..end];
    }

    /// <summary>`lowercased()`: the full Unicode lowercase mapping of every scalar, without context (no final sigma).</summary>
    public static string Lowercased(string text) => MapCase(text, upper: false);

    /// <summary>`uppercased()`: the full Unicode uppercase mapping of every scalar, for example "ß" to "SS".</summary>
    public static string Uppercased(string text) => MapCase(text, upper: true);

    /// <summary>Swift `a == b`: canonical equivalence ("é" equals "e" + U+0301, U+212A KELVIN SIGN equals "K").</summary>
    public static bool Equals(string? a, string? b)
    {
        if (ReferenceEquals(a, b) || string.Equals(a, b, StringComparison.Ordinal))
        {
            return true;
        }
        if (a is null || b is null || (SwiftUnicode.IsTriviallyNfc(a) && SwiftUnicode.IsTriviallyNfc(b)))
        {
            return false;
        }
        return string.Equals(SwiftUnicode.Nfc(a), SwiftUnicode.Nfc(b), StringComparison.Ordinal);
    }

    /// <summary>A hash that agrees with <see cref="Equals(string?, string?)"/>.</summary>
    public static int GetHashCode(string text) => StringComparer.Ordinal.GetHashCode(SwiftUnicode.Nfc(text));

    /// <summary>
    /// Swift `a &lt; b`: Unicode scalar order of the canonically composed (NFC) forms; 0 for canonically equivalent
    /// strings.
    /// </summary>
    public static int Compare(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return 0;
        }
        return CompareScalars(SwiftUnicode.Nfc(a), SwiftUnicode.Nfc(b));
    }

    /// <summary>Swift string semantics for sorted dictionaries, sets and sorting.</summary>
    public static SwiftStringComparer Comparer { get; } = new();

    /// <summary>Unicode scalar order (UTF-16 ordinal order with surrogates moved above U+FFFF).</summary>
    private static int CompareScalars(string a, string b)
    {
        var length = Math.Min(a.Length, b.Length);
        for (var index = 0; index < length; index++)
        {
            int x = a[index], y = b[index];
            if (x == y)
            {
                continue;
            }
            if (x >= 0xD800 && y >= 0xD800)
            {
                x = x >= 0xE000 ? x - 0x800 : x + 0x2000;
                y = y >= 0xE000 ? y - 0x800 : y + 0x2000;
            }
            return x - y;
        }
        return a.Length - b.Length;
    }

    private static string MapCase(string text, bool upper)
    {
        var builder = new StringBuilder(text.Length);
        Span<char> buffer = stackalloc char[2];
        foreach (var rune in text.EnumerateRunes())
        {
            var keys = upper ? SwiftUnicodeData.SpecialUpperKeys : SwiftUnicodeData.SpecialLowerKeys;
            var position = rune.Value < 0xDF ? -1 : keys.BinarySearch(rune.Value);
            if (position >= 0)
            {
                var packed = (upper ? SwiftUnicodeData.SpecialUpperValues : SwiftUnicodeData.SpecialLowerValues)[position];
                for (; packed != 0; packed >>= 21)
                {
                    var length = new Rune((int)(packed & 0x1F_FFFF)).EncodeToUtf16(buffer);
                    builder.Append(buffer[..length]);
                }
                continue;
            }
            var mapped = upper ? Rune.ToUpperInvariant(rune) : Rune.ToLowerInvariant(rune);
            builder.Append(buffer[..mapped.EncodeToUtf16(buffer)]);
        }
        return builder.ToString();
    }

    /// <summary>The uppercase `uuidString` form: 8-4-4-4-12 hexadecimal digits.</summary>
    public static string UuidString(Guid id) => id.ToString("D").ToUpperInvariant();

    /// <summary>`UUID(uuidString:)`: exactly 36 characters, hyphens at 8, 13, 18 and 23, hex digits in either case.</summary>
    public static bool TryParseUuid(string text, out Guid id)
    {
        id = Guid.Empty;
        if (text.Length != 36)
        {
            return false;
        }
        for (var index = 0; index < 36; index++)
        {
            var character = text[index];
            var hyphen = index is 8 or 13 or 18 or 23;
            if (hyphen ? character != '-' : !char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }
        return Guid.TryParseExact(text, "D", out id);
    }
}

/// <summary>Swift string equality (canonical equivalence) and ordering (scalars of the NFC form) for collections.</summary>
internal sealed class SwiftStringComparer : IComparer<string>, IEqualityComparer<string>
{
    public int Compare(string? x, string? y) => x is null ? (y is null ? 0 : -1) : y is null ? 1 : SwiftText.Compare(x, y);

    public bool Equals(string? x, string? y) => SwiftText.Equals(x, y);

    public int GetHashCode(string obj) => SwiftText.GetHashCode(obj);
}

/// <summary>Maps a Swift `String` raw-value enum to C#. Enum members are declared in Swift order with values 0..n-1.</summary>
internal sealed class RawValueMap<TEnum> where TEnum : struct, Enum
{
    private readonly string[] rawValues;
    private readonly FrozenDictionary<string, TEnum> cases;

    public RawValueMap(params string[] rawValues)
    {
        this.rawValues = rawValues;
        var all = new TEnum[rawValues.Length];
        var map = new Dictionary<string, TEnum>(rawValues.Length, StringComparer.Ordinal);
        for (var index = 0; index < rawValues.Length; index++)
        {
            all[index] = Unsafe.BitCast<int, TEnum>(index);
            map.Add(rawValues[index], all[index]);
        }
        AllCases = ValueList.Create<TEnum>(all);
        cases = map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public ValueList<TEnum> AllCases { get; }

    public string RawValue(TEnum value)
    {
        var index = Unsafe.BitCast<TEnum, int>(value);
        return (uint)index < (uint)rawValues.Length
            ? rawValues[index]
            : throw new ArgumentOutOfRangeException(nameof(value), value, "The value is not a case of this enumeration.");
    }

    public TEnum? Parse(string rawValue) => cases.TryGetValue(rawValue, out var value) ? value : null;
}
