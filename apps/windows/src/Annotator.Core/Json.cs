// JSON support shared by every Codable port. Swift's synthesized Codable has no source file; this file and
// JsonCodecs.cs reproduce what JSONEncoder and JSONDecoder do for the AnnotationCore types, as the Mac
// conformance corpus records it (reference/conformance/platform/json-*.jsonl):
// - keys are the Swift property names; nil optionals are omitted; keys are written in sorted order
//   (Swift's .sortedKeys order for these keys, which board and saved-shape files use); see SwiftJsonWriter;
// - UUIDs are written in uppercase and read in either case; Data is standard Base64;
// - input is UTF-8 (a byte order mark is skipped) or UTF-16 in either byte order, with or without a mark, or
//   UTF-32 big endian without a mark; a UTF-32 little endian mark reads as UTF-16, which then fails;
// - the whole input is checked for JSON syntax first (JsonScanner: trailing commas allowed, 512 levels), then
//   values decode in Swift declaration order, so the first error reported matches the Mac;
// - errors carry Swift's case, coding path, expected type or key and debug description; numbers that do not fit
//   their type fail for the whole document, as Foundation reports them;
// - unknown keys are ignored and the first of duplicate keys wins; dictionary keys that are canonically
//   equivalent count as duplicates.
using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Annotator.Core;

/// <summary>The cases of Swift's `DecodingError`.</summary>
public enum DecodingErrorKind
{
    TypeMismatch,
    ValueNotFound,
    KeyNotFound,
    DataCorrupted,
}

/// <summary>Foundation's underlying <c>NSError</c> for a JSON syntax problem: its domain, code and debug description.</summary>
public sealed record JsonReaderDetail(string Domain, int Code, string DebugDescription);

/// <summary>
/// Swift `DecodingError`. The message is the Foundation `localizedDescription` the Mac shows:
/// missing keys and null values read "…because it is missing", everything else "…isn’t in the correct format".
/// The other properties are developer detail, like Swift's `DecodingError.Context`; they are not for people.
/// </summary>
public sealed class DecodingError : Exception
{
    public const string ReadCorruptMessage = "The data couldn\u2019t be read because it isn\u2019t in the correct format.";
    public const string ValueNotFoundMessage = "The data couldn\u2019t be read because it is missing.";
    internal const string InvalidJsonDescription = "The given data was not valid JSON.";

    internal DecodingError(DecodingErrorKind kind, string debugDescription, string? expectedType = null, string? key = null,
                           JsonReaderDetail? underlying = null, ImmutableArray<object> codingPath = default)
        : base(kind is DecodingErrorKind.KeyNotFound or DecodingErrorKind.ValueNotFound ? ValueNotFoundMessage : ReadCorruptMessage)
    {
        Kind = kind;
        DebugDescription = debugDescription;
        ExpectedType = expectedType;
        Key = key;
        Underlying = underlying;
        CodingPath = codingPath.IsDefault ? [] : codingPath;
    }

    public DecodingErrorKind Kind { get; }

    /// <summary>Swift's `DecodingError.Context.debugDescription`.</summary>
    public string DebugDescription { get; }

    /// <summary>Swift's coding path: key names (strings) and array positions (integers), outermost first.</summary>
    public ImmutableArray<object> CodingPath { get; }

    /// <summary>The Swift type named by `typeMismatch` and `valueNotFound`, for example "Int" or "Array&lt;Any&gt;".</summary>
    public string? ExpectedType { get; }

    /// <summary>The missing key of `keyNotFound`.</summary>
    public string? Key { get; }

    /// <summary>Foundation's JSON reader error under "The given data was not valid JSON.", when there is one.</summary>
    public JsonReaderDetail? Underlying { get; }

    /// <summary>A JSON syntax problem or a number that does not fit its type: Foundation reports these for the whole document.</summary>
    internal static DecodingError InvalidJson(string detail) =>
        new(DecodingErrorKind.DataCorrupted, InvalidJsonDescription, underlying: new JsonReaderDetail("NSCocoaErrorDomain", 3840, detail));

    internal static DecodingError KeyNotFound(string name) =>
        new(DecodingErrorKind.KeyNotFound,
            $"No value associated with key CodingKeys(stringValue: \"{name}\", intValue: nil) (\"{name}\").", key: name);

    /// <summary>The same error one level further out: <paramref name="component"/> is a key name or an array index.</summary>
    internal DecodingError Within(object component) => Underlying is not null
        ? this
        : new DecodingError(Kind, DebugDescription, ExpectedType, Key, Underlying, CodingPath.Insert(0, component));
}

/// <summary>Swift `EncodingError.invalidValue`, raised for NaN or infinite numbers.</summary>
public sealed class EncodingError : Exception
{
    public const string InvalidValueMessage = "The data couldn\u2019t be written because it isn\u2019t in the correct format.";

    /// <summary>Foundation's error domain and code for an invalid value (NSCocoaErrorDomain 4866).</summary>
    public const string Domain = "NSCocoaErrorDomain";
    public const int Code = 4866;

    internal EncodingError(string debugDescription)
        : base(InvalidValueMessage)
    {
        DebugDescription = debugDescription;
    }

    public string DebugDescription { get; }
}

internal delegate T JsonRead<out T>(ref Utf8JsonReader reader);

internal delegate void JsonWrite<T>(SwiftJsonWriter writer, in T value);

/// <summary>The keys of one Swift type in declaration order, with their UTF-8 forms.</summary>
internal sealed class JsonKeys
{
    public JsonKeys(params string[] names)
    {
        Names = names;
        Utf8 = names.Select(name => Encoding.UTF8.GetBytes(name)).ToArray();
    }

    public string[] Names { get; }

    public byte[][] Utf8 { get; }

    public int IndexOf(ReadOnlySpan<byte> name)
    {
        for (var index = 0; index < Utf8.Length; index++)
        {
            if (name.SequenceEqual(Utf8[index]))
            {
                return index;
            }
        }
        return -1;
    }
}

/// <summary>Decoding state of one keyed container: which keys were seen and the first error for each key.</summary>
internal struct KeyedState
{
    private ulong seen;
    private DecodingError?[]? errors;

    /// <summary>True when the value of this key should be decoded; unknown and repeated keys are skipped.</summary>
    public bool Claim(int key, ref Utf8JsonReader reader)
    {
        if (key < 0 || (seen & (1UL << key)) != 0)
        {
            reader.Skip();
            return false;
        }
        seen |= 1UL << key;
        return true;
    }

    /// <summary>Decodes a value; an error is kept for <see cref="Finish"/> and the rest of the value is skipped.</summary>
    public T? Read<T>(ref Utf8JsonReader reader, int key, int keyCount, JsonRead<T> read)
    {
        var checkpoint = reader;
        try
        {
            return read(ref reader);
        }
        catch (DecodingError error)
        {
            errors ??= new DecodingError?[keyCount];
            errors[key] = error;
            reader = checkpoint;
            reader.Skip();
            return default;
        }
    }

    /// <summary>Throws the first problem in declaration order, as the Swift decoder would meet it.</summary>
    public readonly void Finish(JsonKeys keys, ulong requiredKeys)
    {
        for (var index = 0; index < keys.Names.Length; index++)
        {
            if (errors?[index] is { } error)
            {
                throw error.Within(keys.Names[index]);
            }
            if ((seen & (1UL << index)) == 0 && (requiredKeys & (1UL << index)) != 0)
            {
                throw DecodingError.KeyNotFound(keys.Names[index]);
            }
        }
    }
}

internal static class Json
{
    private const string KeyedType = "Dictionary<String, Any>";
    private const string UnkeyedType = "Array<Any>";

    // The scanner has already limited nesting to 512 levels.
    private static readonly JsonReaderOptions ReaderOptions = new() { MaxDepth = 1_024, AllowTrailingCommas = true };

    public static T Decode<T>(ReadOnlySpan<byte> data, JsonRead<T> read)
    {
        var utf8 = Utf8Text(data);
        JsonScanner.Validate(utf8);
        try
        {
            var reader = new Utf8JsonReader(utf8, ReaderOptions);
            reader.Read();
            return read(ref reader);
        }
        catch (JsonException error)
        {
            throw DecodingError.InvalidJson(error.Message);
        }
    }

    public static byte[] Encode<T>(in T value, JsonWrite<T> write, bool escapeSlashes = true)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(new SwiftJsonWriter(buffer, escapeSlashes), value);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Foundation's text encoding detection: a UTF-8 mark is skipped; FE FF and FF FE mark UTF-16 (FF FE 00 00 too,
    /// so a UTF-32 little endian mark reads as UTF-16); 00 00 FE FF marks UTF-32 big endian; without a mark, zero
    /// bytes in the first four give UTF-32 or UTF-16 in either byte order; anything else is UTF-8.
    /// </summary>
    internal static ReadOnlySpan<byte> Utf8Text(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return data[3..];
        }
        Encoding? encoding = null;
        var skip = 0;
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
        {
            (encoding, skip) = (new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true), 2);
        }
        else if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
        {
            (encoding, skip) = (new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true), 2);
        }
        else if (data.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xFE, 0xFF]))
        {
            (encoding, skip) = (new UTF32Encoding(bigEndian: true, byteOrderMark: false, throwOnInvalidCharacters: true), 4);
        }
        else if (data.Length >= 4)
        {
            encoding = (data[0], data[1], data[2], data[3]) switch
            {
                (0, 0, 0, _) => new UTF32Encoding(bigEndian: true, byteOrderMark: false, throwOnInvalidCharacters: true),
                (_, 0, 0, 0) => new UTF32Encoding(bigEndian: false, byteOrderMark: false, throwOnInvalidCharacters: true),
                (0, _, 0, _) => new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true),
                (_, 0, _, 0) => new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true),
                _ => null,
            };
        }
        else if (data.Length >= 2)
        {
            encoding = (data[0], data[1]) switch
            {
                (0, _) => new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true),
                (_, 0) => new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true),
                _ => null,
            };
        }
        if (encoding is null)
        {
            return data;
        }
        try
        {
            return Encoding.UTF8.GetBytes(encoding.GetString(data[skip..]));
        }
        catch (DecoderFallbackException)
        {
            throw DecodingError.InvalidJson("Unable to convert data to a string around line 1, column 1.");
        }
    }

    public static bool IsNull(ref Utf8JsonReader reader) => reader.TokenType == JsonTokenType.Null;

    /// <summary>A keyed container (a Swift struct or dictionary).</summary>
    public static void ExpectObject(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            return;
        }
        throw reader.TokenType == JsonTokenType.Null
            ? new DecodingError(DecodingErrorKind.ValueNotFound, "Cannot get keyed decoding container -- found null value instead",
                                KeyedType)
            : new DecodingError(DecodingErrorKind.TypeMismatch,
                                $"Expected to decode {KeyedType} but found {Describe(reader.TokenType)} instead.", KeyedType);
    }

    /// <summary>An unkeyed container (a Swift array).</summary>
    public static void ExpectArray(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            return;
        }
        throw reader.TokenType == JsonTokenType.Null
            ? new DecodingError(DecodingErrorKind.ValueNotFound, "Cannot get unkeyed decoding container -- found null value instead",
                                UnkeyedType)
            : new DecodingError(DecodingErrorKind.TypeMismatch,
                                $"Expected to decode {UnkeyedType} but found {Describe(reader.TokenType)} instead.", UnkeyedType);
    }

    /// <summary>Moves to the next key and its value. Returns false at the end of the object; otherwise the key index or -1.</summary>
    public static bool NextProperty(ref Utf8JsonReader reader, JsonKeys keys, out int key)
    {
        reader.Read();
        if (reader.TokenType == JsonTokenType.EndObject)
        {
            key = -1;
            return false;
        }
        key = MatchKey(ref reader, keys);
        reader.Read();
        return true;
    }

    /// <summary>A single value of the wrong JSON type, or null where a value is required.</summary>
    public static DecodingError Unexpected(ref Utf8JsonReader reader, string type) => reader.TokenType == JsonTokenType.Null
        ? new DecodingError(DecodingErrorKind.ValueNotFound, $"Cannot get value of type {type} -- found null value instead", type)
        : new DecodingError(DecodingErrorKind.TypeMismatch, $"Expected to decode {type} but found {Describe(reader.TokenType)} instead.", type);

    public static DecodingError Corrupted(string description) => new(DecodingErrorKind.DataCorrupted, description);

    public static double ReadDouble(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.Number)
        {
            throw Unexpected(ref reader, "Double");
        }
        return ParseDouble(reader.ValueSpan);
    }

    /// <summary>Swift `Int` (64-bit). Integral values written with a fraction or exponent are accepted, like Swift.</summary>
    public static long ReadInt(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.Number)
        {
            throw Unexpected(ref reader, "Int");
        }
        var literal = reader.ValueSpan;
        if (IsIntegerLiteral(literal))
        {
            return long.TryParse(literal, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw NotRepresentable(literal);
        }
        var number = ParseDouble(literal);
        return number == Math.Floor(number) && number >= -9_223_372_036_854_775_808.0 && number < 9_223_372_036_854_775_808.0
            ? (long)number
            : throw NotRepresentable(literal);
    }

    public static uint ReadUInt32(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.Number)
        {
            throw Unexpected(ref reader, "UInt32");
        }
        var value = ReadUnsigned(reader.ValueSpan, uint.MaxValue);
        return (uint)value;
    }

    public static ulong ReadUInt64(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.Number)
        {
            throw Unexpected(ref reader, "UInt64");
        }
        return ReadUnsigned(reader.ValueSpan, ulong.MaxValue);
    }

    public static bool ReadBool(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.True => true,
        JsonTokenType.False => false,
        _ => throw Unexpected(ref reader, "Bool"),
    };

    public static string ReadString(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw Unexpected(ref reader, "String");
        }
        return reader.GetString()!;
    }

    /// <summary>Swift `UUID` decodes a string first, then parses it.</summary>
    public static Guid ReadUuid(ref Utf8JsonReader reader)
    {
        var text = ReadString(ref reader);
        return SwiftText.TryParseUuid(text, out var id)
            ? id
            : throw Corrupted("Attempted to decode UUID from invalid UUID string.");
    }

    /// <summary>Swift `Data` with the default Base64 strategy decodes a string first.</summary>
    public static ValueList<byte> ReadData(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw Unexpected(ref reader, "String");
        }
        return reader.TryGetBytesFromBase64(out var bytes)
            ? ValueList<byte>.Adopt(bytes)
            : throw Corrupted("Encountered Data is not valid Base64.");
    }

    /// <summary>A Swift `String` raw-value enum: a string first, then a known raw value.</summary>
    public static TEnum ReadEnum<TEnum>(ref Utf8JsonReader reader, RawValueMap<TEnum> map, string type) where TEnum : struct, Enum
    {
        var raw = ReadString(ref reader);
        return map.Parse(raw) ?? throw Corrupted($"Cannot initialize {type} from invalid String value {raw}");
    }

    public static ValueList<T> ReadArray<T>(ref Utf8JsonReader reader, JsonRead<T> read)
    {
        ExpectArray(ref reader);
        var items = new List<T>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            try
            {
                items.Add(read(ref reader));
            }
            catch (DecodingError error)
            {
                throw error.Within(items.Count);
            }
        }
        return ValueList.CreateRange(items);
    }

    public static ElementList ReadElements(ref Utf8JsonReader reader, JsonRead<Element> read)
    {
        ExpectArray(ref reader);
        var items = new List<Element>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            try
            {
                items.Add(read(ref reader));
            }
            catch (DecodingError error)
            {
                throw error.Within(items.Count);
            }
        }
        return ElementList.CreateRange(items);
    }

    /// <summary>
    /// Swift `[String: T]`. The first of duplicate keys wins, and keys that are canonically equivalent count as
    /// duplicates, because Swift compares strings that way.
    /// </summary>
    public static ImmutableSortedDictionary<string, T> ReadDictionary<T>(ref Utf8JsonReader reader, JsonRead<T> read)
    {
        ExpectObject(ref reader);
        var builder = ImmutableSortedDictionary.CreateBuilder<string, T>(SwiftText.Comparer);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var key = reader.GetString()!;
            reader.Read();
            if (builder.ContainsKey(key))
            {
                reader.Skip();
                continue;
            }
            try
            {
                builder.Add(key, read(ref reader));
            }
            catch (DecodingError error)
            {
                throw error.Within(key);
            }
        }
        return builder.ToImmutable();
    }

    public static void WriteDouble(SwiftJsonWriter writer, ReadOnlySpan<byte> name, double value)
    {
        writer.WritePropertyName(name);
        WriteDoubleValue(writer, value);
    }

    public static void WriteDoubleValue(SwiftJsonWriter writer, double value)
    {
        if (!double.IsFinite(value))
        {
            throw new EncodingError($"Unable to encode Double.{(double.IsNaN(value) ? "nan" : value > 0 ? "infinity" : "-infinity")} directly in JSON.");
        }
        writer.WriteNumberValue(value);
    }

    public static void WriteUuid(SwiftJsonWriter writer, ReadOnlySpan<byte> name, Guid id)
    {
        writer.WritePropertyName(name);
        WriteUuidValue(writer, id);
    }

    public static void WriteUuidValue(SwiftJsonWriter writer, Guid id)
    {
        Span<char> text = stackalloc char[36];
        id.TryFormat(text, out _, "D");
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is >= 'a' and <= 'f')
            {
                text[index] = (char)(text[index] - 32);
            }
        }
        writer.WriteStringValue(text);
    }

    /// <summary>A finite Swift `Double` from a JSON number; overflow and underflow to zero do not fit.</summary>
    internal static double ParseDouble(ReadOnlySpan<byte> literal)
    {
        var value = double.Parse(literal, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (!double.IsFinite(value) || (value == 0 && HasNonZeroDigit(literal)))
        {
            throw NotRepresentable(literal);
        }
        return value;
    }

    private static ulong ReadUnsigned(ReadOnlySpan<byte> literal, ulong maximum)
    {
        if (IsIntegerLiteral(literal))
        {
            if (literal[0] == (byte)'-')
            {
                return HasNonZeroDigit(literal) ? throw NotRepresentable(literal) : 0UL;
            }
            return ulong.TryParse(literal, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value <= maximum
                ? value
                : throw NotRepresentable(literal);
        }
        var number = ParseDouble(literal);
        return number == Math.Floor(number) && number >= 0 && number < (double)maximum + 1
            ? (ulong)number
            : throw NotRepresentable(literal);
    }

    private static DecodingError NotRepresentable(ReadOnlySpan<byte> literal) =>
        DecodingError.InvalidJson($"Number {Encoding.UTF8.GetString(literal)} is not representable in Swift.");

    private static bool IsIntegerLiteral(ReadOnlySpan<byte> literal) => literal.IndexOfAny((byte)'.', (byte)'e', (byte)'E') < 0;

    /// <summary>Whether the significand (the part before any exponent) has a digit other than zero.</summary>
    private static bool HasNonZeroDigit(ReadOnlySpan<byte> literal)
    {
        foreach (var value in literal)
        {
            if (value is (byte)'e' or (byte)'E')
            {
                return false;
            }
            if (value is >= (byte)'1' and <= (byte)'9')
            {
                return true;
            }
        }
        return false;
    }

    private static int MatchKey(ref Utf8JsonReader reader, JsonKeys keys)
    {
        if (!reader.ValueIsEscaped)
        {
            return keys.IndexOf(reader.ValueSpan);
        }
        Span<byte> buffer = stackalloc byte[256];
        if (reader.ValueSpan.Length > buffer.Length)
        {
            return -1;
        }
        var length = reader.CopyString(buffer);
        return keys.IndexOf(buffer[..length]);
    }

    private static string Describe(JsonTokenType token) => token switch
    {
        JsonTokenType.StartObject => "a dictionary",
        JsonTokenType.StartArray => "an array",
        JsonTokenType.String => "a string",
        JsonTokenType.Number => "number",
        JsonTokenType.True or JsonTokenType.False => "bool",
        JsonTokenType.Null => "null",
        _ => token.ToString(),
    };
}
