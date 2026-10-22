using System.Buffers;
using System.Globalization;
using System.Text;

namespace Annotator.Core;

/// <summary>
/// Writes JSON byte for byte as Foundation's <c>JSONEncoder</c> writes the app's files: no whitespace, keys in the
/// order the codecs give (the Swift <c>.sortedKeys</c> order), strings that escape <c>"</c>, <c>\</c> and <c>/</c>
/// (left alone with <c>.withoutEscapingSlashes</c>), <c>\b \f \n \r \t</c> and every other control character as a
/// lowercase <c>\u00xx</c>, and keep all other characters, DEL and U+2028 included, as UTF-8. Numbers use Swift's
/// shortest form (<see cref="SwiftNumberFormat"/>). Corpus: <c>platform/json-encoding.jsonl</c>.
/// </summary>
internal sealed class SwiftJsonWriter
{
    private readonly ArrayBufferWriter<byte> output;
    private readonly bool escapeSlashes;
    private bool separate;
    private bool afterName;

    public SwiftJsonWriter(ArrayBufferWriter<byte> output, bool escapeSlashes = true)
    {
        this.output = output;
        this.escapeSlashes = escapeSlashes;
    }

    public void WriteStartObject()
    {
        Separator();
        Raw((byte)'{');
        separate = false;
    }

    public void WriteEndObject()
    {
        Raw((byte)'}');
        separate = true;
    }

    public void WriteStartArray()
    {
        Separator();
        Raw((byte)'[');
        separate = false;
    }

    public void WriteEndArray()
    {
        Raw((byte)']');
        separate = true;
    }

    public void WritePropertyName(ReadOnlySpan<byte> utf8Name)
    {
        Separator();
        Quoted(utf8Name);
        Raw((byte)':');
        afterName = true;
    }

    public void WritePropertyName(string name)
    {
        Separator();
        Quoted(name.AsSpan());
        Raw((byte)':');
        afterName = true;
    }

    public void WriteStringValue(ReadOnlySpan<char> value)
    {
        Separator();
        Quoted(value);
        separate = true;
    }

    public void WriteString(ReadOnlySpan<byte> utf8Name, ReadOnlySpan<char> value)
    {
        WritePropertyName(utf8Name);
        WriteStringValue(value);
    }

    public void WriteString(string name, ReadOnlySpan<char> value)
    {
        WritePropertyName(name);
        WriteStringValue(value);
    }

    public void WriteNumberValue(long value)
    {
        Separator();
        Ascii(value.ToString(CultureInfo.InvariantCulture));
        separate = true;
    }

    public void WriteNumberValue(ulong value)
    {
        Separator();
        Ascii(value.ToString(CultureInfo.InvariantCulture));
        separate = true;
    }

    /// <summary>A finite double in Swift's form; callers refuse NaN and infinity first.</summary>
    public void WriteNumberValue(double value)
    {
        Separator();
        Ascii(SwiftNumberFormat.Format(value));
        separate = true;
    }

    public void WriteNumber(ReadOnlySpan<byte> utf8Name, long value)
    {
        WritePropertyName(utf8Name);
        WriteNumberValue(value);
    }

    public void WriteNumber(ReadOnlySpan<byte> utf8Name, ulong value)
    {
        WritePropertyName(utf8Name);
        WriteNumberValue(value);
    }

    public void WriteBooleanValue(bool value)
    {
        Separator();
        Ascii(value ? "true" : "false");
        separate = true;
    }

    public void WriteBoolean(ReadOnlySpan<byte> utf8Name, bool value)
    {
        WritePropertyName(utf8Name);
        WriteBooleanValue(value);
    }

    public void WriteNullValue()
    {
        Separator();
        Ascii("null");
        separate = true;
    }

    /// <summary>Swift <c>Data</c>: standard Base64 with padding.</summary>
    public void WriteBase64String(ReadOnlySpan<byte> utf8Name, ReadOnlySpan<byte> bytes)
    {
        WritePropertyName(utf8Name);
        WriteStringValue(Convert.ToBase64String(bytes));
    }

    private void Separator()
    {
        if (afterName)
        {
            afterName = false;
            return;
        }
        if (separate)
        {
            Raw((byte)',');
        }
    }

    private void Raw(byte value)
    {
        output.GetSpan(1)[0] = value;
        output.Advance(1);
    }

    private void Ascii(string text)
    {
        var span = output.GetSpan(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            span[index] = (byte)text[index];
        }
        output.Advance(text.Length);
    }

    private void Quoted(ReadOnlySpan<char> text)
    {
        Raw((byte)'"');
        Span<byte> buffer = stackalloc byte[4];
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value < 0x80)
            {
                Escaped((byte)rune.Value);
                continue;
            }
            var length = rune.EncodeToUtf8(buffer);
            buffer[..length].CopyTo(output.GetSpan(length));
            output.Advance(length);
        }
        Raw((byte)'"');
    }

    private void Quoted(ReadOnlySpan<byte> utf8)
    {
        Raw((byte)'"');
        foreach (var value in utf8)
        {
            if (value < 0x80)
            {
                Escaped(value);
            }
            else
            {
                Raw(value);
            }
        }
        Raw((byte)'"');
    }

    private void Escaped(byte value)
    {
        switch (value)
        {
            case (byte)'"': Ascii("\\\""); break;
            case (byte)'\\': Ascii("\\\\"); break;
            case (byte)'/' when escapeSlashes: Ascii("\\/"); break;
            case 0x08: Ascii("\\b"); break;
            case 0x0C: Ascii("\\f"); break;
            case (byte)'\n': Ascii("\\n"); break;
            case (byte)'\r': Ascii("\\r"); break;
            case (byte)'\t': Ascii("\\t"); break;
            case < 0x20: Ascii("\\u00" + value.ToString("x2", CultureInfo.InvariantCulture)); break;
            default: Raw(value); break;
        }
    }
}

/// <summary>
/// Swift's <c>Double.description</c> as <c>JSONEncoder</c> writes it: the shortest digits that read back as the same
/// double; a whole number up to 2^53 has no exponent and no ".0"; anything larger, and anything below 1e-4, uses an
/// exponent with a sign and at least two digits (<c>1e+16</c>, <c>1e-07</c>, <c>9.007199254740994e+15</c>).
/// </summary>
internal static class SwiftNumberFormat
{
    private const double TwoToThe53 = 9_007_199_254_740_992.0;

    public static string Format(double value)
    {
        if (value == 0)
        {
            return double.IsNegative(value) ? "-0" : "0";
        }
        // .NET's "R" gives the same shortest round-trip digits; only the layout differs from Swift's.
        var shortest = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var exponentAt = shortest.IndexOf('E', StringComparison.Ordinal);
        var exponent = exponentAt < 0 ? 0 : int.Parse(shortest.AsSpan(exponentAt + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var mantissa = exponentAt < 0 ? shortest : shortest[..exponentAt];
        var dot = mantissa.IndexOf('.', StringComparison.Ordinal);
        var integerDigits = dot < 0 ? mantissa.Length : dot;
        var digits = dot < 0 ? mantissa : string.Concat(mantissa.AsSpan(0, dot), mantissa.AsSpan(dot + 1));
        // The value is 0.digits x 10^point; drop leading and trailing zeros.
        var point = integerDigits + exponent;
        var leading = 0;
        while (leading < digits.Length - 1 && digits[leading] == '0')
        {
            leading++;
        }
        digits = digits[leading..].TrimEnd('0');
        point -= leading;
        var text = new StringBuilder(32);
        if (value < 0)
        {
            text.Append('-');
        }
        if (Math.Abs(value) > TwoToThe53 || point - 1 < -4)
        {
            text.Append(digits[0]);
            if (digits.Length > 1)
            {
                text.Append('.').Append(digits, 1, digits.Length - 1);
            }
            var scientific = point - 1;
            text.Append('e').Append(scientific < 0 ? '-' : '+')
                .Append(Math.Abs(scientific).ToString("00", CultureInfo.InvariantCulture));
        }
        else if (point <= 0)
        {
            text.Append("0.").Append('0', -point).Append(digits);
        }
        else if (point >= digits.Length)
        {
            text.Append(digits).Append('0', point - digits.Length);
        }
        else
        {
            text.Append(digits, 0, point).Append('.').Append(digits, point, digits.Length - point);
        }
        return text.ToString();
    }
}
