using System.Globalization;
using System.Text.Unicode;

namespace Annotator.Core;

/// <summary>
/// Checks the syntax of a whole JSON document before decoding, as Foundation's <c>JSONDecoder</c> does, and reports
/// problems with Foundation's developer detail (<c>platform/json-decoding*.jsonl</c>): standard JSON plus trailing
/// commas in objects and arrays, at most 512 nested arrays and objects, strict UTF-8 and escapes. Locations count
/// lines and bytes from 1. A number is checked only when a valid separator follows it; otherwise the container
/// reports the character after it, as Foundation does.
/// </summary>
internal ref struct JsonScanner
{
    private const int MaximumDepth = 512;
    private readonly ReadOnlySpan<byte> json;
    private int position;

    private JsonScanner(ReadOnlySpan<byte> json)
    {
        this.json = json;
        position = 0;
    }

    public static void Validate(ReadOnlySpan<byte> json)
    {
        var scanner = new JsonScanner(json);
        scanner.Document();
    }

    private void Document()
    {
        SkipWhitespace();
        RequireMore();
        Value(0);
        SkipWhitespace();
        if (position < json.Length)
        {
            throw Unexpected(position, "after top-level value");
        }
    }

    private void Value(int depth)
    {
        switch (json[position])
        {
            case (byte)'{':
                Object(depth + 1);
                break;
            case (byte)'[':
                Array(depth + 1);
                break;
            case (byte)'"':
                String();
                break;
            case (byte)'t':
                Literal("true"u8);
                break;
            case (byte)'f':
                Literal("false"u8);
                break;
            case (byte)'n':
                Literal("null"u8);
                break;
            case (byte)'-' or (>= (byte)'0' and <= (byte)'9'):
                Number();
                break;
            default:
                throw Unexpected(position, null);
        }
    }

    private void Object(int depth)
    {
        if (depth > MaximumDepth)
        {
            // Foundation reports this two bytes past the opening bracket.
            throw Invalid($"Too many nested arrays or dictionaries around {Location(position + 2)}.");
        }
        position++;
        SkipWhitespace();
        RequireMore();
        if (json[position] == (byte)'}')
        {
            position++;
            return;
        }
        while (true)
        {
            if (json[position] != (byte)'"')
            {
                // Foundation reports a missing key one byte past the character it found.
                throw Invalid($"Unexpected character '{(char)json[position]}' around {Location(position + 1)}.");
            }
            String();
            SkipWhitespace();
            RequireMore();
            if (json[position] != (byte)':')
            {
                throw Unexpected(position, "in object");
            }
            position++;
            SkipWhitespace();
            RequireMore();
            Value(depth);
            SkipWhitespace();
            RequireMore();
            if (json[position] == (byte)',')
            {
                position++;
                SkipWhitespace();
                RequireMore();
                if (json[position] == (byte)'}')
                {
                    position++;
                    return;
                }
                continue;
            }
            if (json[position] == (byte)'}')
            {
                position++;
                return;
            }
            throw Unexpected(position, "in object");
        }
    }

    private void Array(int depth)
    {
        if (depth > MaximumDepth)
        {
            throw Invalid($"Too many nested arrays or dictionaries around {Location(position + 2)}.");
        }
        position++;
        SkipWhitespace();
        RequireMore();
        if (json[position] == (byte)']')
        {
            position++;
            return;
        }
        while (true)
        {
            Value(depth);
            SkipWhitespace();
            RequireMore();
            if (json[position] == (byte)',')
            {
                position++;
                SkipWhitespace();
                RequireMore();
                if (json[position] == (byte)']')
                {
                    position++;
                    return;
                }
                continue;
            }
            if (json[position] == (byte)']')
            {
                position++;
                return;
            }
            throw Unexpected(position, "in array");
        }
    }

    private void String()
    {
        position++;
        var contentStart = position;
        while (true)
        {
            RequireMore();
            var value = json[position];
            if (value == (byte)'"')
            {
                if (!Utf8.IsValid(json[contentStart..position]))
                {
                    throw Invalid($"Unable to convert data to a string around {Location(contentStart)}.");
                }
                position++;
                return;
            }
            if (value == (byte)'\\')
            {
                position++;
                RequireMore();
                Escape();
                continue;
            }
            if (value < 0x20)
            {
                throw Invalid($"Unescaped control character '0x{value.ToString("x", CultureInfo.InvariantCulture)}' around {Location(position)}.");
            }
            position++;
        }
    }

    /// <summary>Reads the escape after a backslash; <see cref="position"/> is on the escape letter.</summary>
    private void Escape()
    {
        var letter = json[position];
        switch (letter)
        {
            case (byte)'"' or (byte)'\\' or (byte)'/' or (byte)'b' or (byte)'f' or (byte)'n' or (byte)'r' or (byte)'t':
                position++;
                return;
            case (byte)'u':
                var digits = position + 1;
                var unit = Hex(digits);
                position = digits + 4;
                if (unit is >= 0xDC00 and <= 0xDFFF)
                {
                    throw Invalid($"Invalid unicode scalar value '0x{unit.ToString("x", CultureInfo.InvariantCulture)}' around {Location(digits)}.");
                }
                if (unit is >= 0xD800 and <= 0xDBFF)
                {
                    if (position + 6 > json.Length || json[position] != (byte)'\\' || json[position + 1] != (byte)'u'
                        || Hex(position + 2) is not (>= 0xDC00 and <= 0xDFFF))
                    {
                        throw Invalid($"Missing low code point in surrogate pair around {Location(position)}.");
                    }
                    position += 6;
                }
                return;
            default:
                throw Invalid($"Invalid escape sequence '{(char)letter}' around {Location(position)}.");
        }
    }

    private readonly int Hex(int start)
    {
        if (start + 4 > json.Length)
        {
            throw EndOfFile();
        }
        var unit = 0;
        for (var index = start; index < start + 4; index++)
        {
            int digit = json[index] switch
            {
                >= (byte)'0' and <= (byte)'9' => json[index] - '0',
                >= (byte)'a' and <= (byte)'f' => json[index] - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => json[index] - 'A' + 10,
                _ => -1,
            };
            if (digit < 0)
            {
                throw Invalid($"Invalid hex digit in unicode escape sequence around {Location(index)}.");
            }
            unit = unit * 16 + digit;
        }
        return unit;
    }

    private void Literal(ReadOnlySpan<byte> word)
    {
        for (var index = 0; index < word.Length; index++)
        {
            RequireMore();
            if (json[position] != word[index])
            {
                throw Unexpected(position, null);
            }
            position++;
        }
    }

    private void Number()
    {
        var start = position;
        while (position < json.Length && json[position] is (>= (byte)'0' and <= (byte)'9') or (byte)'-' or (byte)'+' or (byte)'.' or (byte)'e' or (byte)'E')
        {
            position++;
        }
        if (position == json.Length || json[position] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)',' or (byte)']' or (byte)'}')
        {
            CheckNumber(start, position);
        }
    }

    private readonly void CheckNumber(int start, int end)
    {
        var index = start;
        if (json[index] == (byte)'-')
        {
            index++;
        }
        if (index == end)
        {
            throw AtEndOfNumber(end);
        }
        if (json[index] == (byte)'0')
        {
            index++;
            if (index < end && IsDigit(json[index]))
            {
                throw Invalid($"Number with leading zero around {Location(index)}.");
            }
        }
        else if (IsDigit(json[index]))
        {
            index = Digits(index, end);
        }
        else
        {
            throw AfterInNumber(index);
        }
        if (index < end && json[index] == (byte)'.')
        {
            index = RequireDigits(index + 1, end);
        }
        if (index < end && json[index] is (byte)'e' or (byte)'E')
        {
            index++;
            if (index < end && json[index] is (byte)'+' or (byte)'-')
            {
                index++;
            }
            index = RequireDigits(index, end);
        }
        if (index < end)
        {
            throw AfterInNumber(index);
        }
    }

    private readonly int RequireDigits(int index, int end)
    {
        if (index == end)
        {
            throw AtEndOfNumber(end);
        }
        if (!IsDigit(json[index]))
        {
            throw AfterInNumber(index);
        }
        return Digits(index, end);
    }

    private readonly int Digits(int index, int end)
    {
        while (index < end && IsDigit(json[index]))
        {
            index++;
        }
        return index;
    }

    private static bool IsDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';

    private void SkipWhitespace()
    {
        while (position < json.Length && json[position] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r')
        {
            position++;
        }
    }

    private readonly void RequireMore()
    {
        if (position >= json.Length)
        {
            throw EndOfFile();
        }
    }

    private readonly DecodingError AtEndOfNumber(int end) =>
        Invalid($"Unexpected character '{(char)json[end - 1]}' at end of number around {Location(end - 1)}.");

    private readonly DecodingError AfterInNumber(int index) =>
        Invalid($"Unexpected character '{(char)json[index]}' after '{(char)json[index - 1]}' in number around {Location(index)}.");

    private readonly DecodingError Unexpected(int index, string? context) =>
        Invalid($"Unexpected character '{(char)json[index]}'{(context is null ? "" : " " + context)} around {Location(index)}.");

    private static DecodingError EndOfFile() => Invalid("Unexpected end of file");

    private static DecodingError Invalid(string detail) => DecodingError.InvalidJson(detail);

    private readonly string Location(int offset)
    {
        var line = 1;
        var lineStart = 0;
        var limit = Math.Min(offset, json.Length);
        for (var index = 0; index < limit; index++)
        {
            if (json[index] == (byte)'\n')
            {
                line++;
                lineStart = index + 1;
            }
        }
        return string.Create(CultureInfo.InvariantCulture, $"line {line}, column {offset - lineStart + 1}");
    }
}
