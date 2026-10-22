using System.Text;

namespace Annotator.Core;

/// <summary>
/// Unicode canonical normalization (NFC, UAX #15) from <see cref="SwiftUnicodeData"/>, so Swift string equality and
/// ordering work the same under .NET's invariant globalization, where <c>string.Normalize</c> does nothing.
/// </summary>
internal static class SwiftUnicode
{
    private const int HangulBase = 0xAC00;
    private const int LeadBase = 0x1100;
    private const int VowelBase = 0x1161;
    private const int TrailBase = 0x11A7;
    private const int VowelCount = 21;
    private const int TrailCount = 28;
    private const int HangulCount = 11_172;

    /// <summary>True when no character can change under NFC: everything is below U+0300, where combining marks start.</summary>
    public static bool IsTriviallyNfc(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            if (character >= '\u0300')
            {
                return false;
            }
        }
        return true;
    }

    public static string Nfc(string text)
    {
        if (IsTriviallyNfc(text))
        {
            return text;
        }
        var scalars = Decomposed(text);
        Compose(scalars);
        return Build(scalars);
    }

    public static int CombiningClass(int scalar)
    {
        if (scalar < 0x300)
        {
            return 0;
        }
        var ranges = SwiftUnicodeData.CombiningClasses;
        int low = 0, high = ranges.Length / 3 - 1;
        while (low <= high)
        {
            var middle = (low + high) >> 1;
            if (scalar < ranges[middle * 3])
            {
                high = middle - 1;
            }
            else if (scalar > ranges[middle * 3 + 1])
            {
                low = middle + 1;
            }
            else
            {
                return ranges[middle * 3 + 2];
            }
        }
        return 0;
    }

    /// <summary>The full canonical decomposition in canonical order.</summary>
    private static List<int> Decomposed(string text)
    {
        var scalars = new List<int>(text.Length + 4);
        foreach (var rune in text.EnumerateRunes())
        {
            Decompose(rune.Value, scalars);
        }
        // Canonical ordering: a stable sort of each run of non-starters by combining class.
        for (var index = 1; index < scalars.Count; index++)
        {
            var current = scalars[index];
            var currentClass = CombiningClass(current);
            if (currentClass == 0)
            {
                continue;
            }
            var position = index;
            while (position > 0)
            {
                var previousClass = CombiningClass(scalars[position - 1]);
                if (previousClass <= currentClass)
                {
                    break;
                }
                scalars[position] = scalars[position - 1];
                position--;
            }
            scalars[position] = current;
        }
        return scalars;
    }

    private static void Decompose(int scalar, List<int> output)
    {
        if (scalar is >= HangulBase and < HangulBase + HangulCount)
        {
            var index = scalar - HangulBase;
            output.Add(LeadBase + index / (VowelCount * TrailCount));
            output.Add(VowelBase + index % (VowelCount * TrailCount) / TrailCount);
            if (index % TrailCount != 0)
            {
                output.Add(TrailBase + index % TrailCount);
            }
            return;
        }
        var position = scalar < 0xC0 ? -1 : SwiftUnicodeData.DecompositionKeys.BinarySearch(scalar);
        if (position < 0)
        {
            output.Add(scalar);
            return;
        }
        Decompose(SwiftUnicodeData.DecompositionFirst[position], output);
        var second = SwiftUnicodeData.DecompositionSecond[position];
        if (second != 0)
        {
            Decompose(second, output);
        }
    }

    /// <summary>Canonical composition of decomposed, canonically ordered scalars, in place.</summary>
    private static void Compose(List<int> scalars)
    {
        var written = 0;
        var starter = -1;
        var lastClass = 0;
        for (var index = 0; index < scalars.Count; index++)
        {
            var scalar = scalars[index];
            var combiningClass = CombiningClass(scalar);
            var adjacent = starter >= 0 && starter == written - 1;
            if (starter >= 0 && (adjacent || (lastClass != 0 && lastClass < combiningClass))
                && Composite(scalars[starter], scalar) is { } composite)
            {
                scalars[starter] = composite;
                continue;
            }
            if (combiningClass == 0)
            {
                starter = written;
            }
            lastClass = combiningClass;
            scalars[written++] = scalar;
        }
        scalars.RemoveRange(written, scalars.Count - written);
    }

    private static int? Composite(int first, int second)
    {
        if (first is >= LeadBase and < LeadBase + 19 && second is >= VowelBase and < VowelBase + VowelCount)
        {
            return HangulBase + ((first - LeadBase) * VowelCount + second - VowelBase) * TrailCount;
        }
        if (first is >= HangulBase and < HangulBase + HangulCount && (first - HangulBase) % TrailCount == 0
            && second is > TrailBase and < TrailBase + TrailCount)
        {
            return first + second - TrailBase;
        }
        var position = SwiftUnicodeData.CompositionPairs.BinarySearch(((long)first << 21) | (uint)second);
        return position < 0 ? null : SwiftUnicodeData.Compositions[position];
    }

    private static string Build(List<int> scalars)
    {
        var text = new StringBuilder(scalars.Count + 4);
        foreach (var scalar in scalars)
        {
            text.Append(new Rune(scalar).ToString());
        }
        return text.ToString();
    }
}
