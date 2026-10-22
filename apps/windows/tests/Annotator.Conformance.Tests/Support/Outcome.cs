using System.Text;
using System.Text.Json.Nodes;
using Annotator.Core;

namespace Annotator.Conformance.Tests;

/// <summary>
/// The exporter's <c>outcome</c> and <c>ErrorRecord.describe</c> (apps/macos/Tools/Conformance/Corpus.swift): <c>{"ok": value}</c>
/// or <c>{"error": {...}}</c> with the message people see, the error type and case, and Foundation's decoding detail.
/// </summary>
internal static class Outcome
{
    public static JsonObject Of(Func<JsonNode?> body)
    {
        try
        {
            return new JsonObject { ["ok"] = body() };
        }
        catch (Exception error) when (error is CanvasError or ShapeLibraryError or DecodingError or EncodingError)
        {
            return new JsonObject { ["error"] = Describe(error) };
        }
    }

    public static JsonObject Describe(Exception error)
    {
        var fields = new JsonObject { ["message"] = error.Message };
        switch (error)
        {
            case CanvasError canvas:
                fields["type"] = "CanvasError";
                fields["case"] = CaseName(canvas.Kind.ToString());
                break;
            case ShapeLibraryError library:
                fields["type"] = "ShapeLibraryError";
                fields["case"] = CaseName(library.Kind.ToString());
                break;
            case DecodingError decoding:
                fields["type"] = "DecodingError";
                fields["case"] = CaseName(decoding.Kind.ToString());
                if (decoding.ExpectedType is { } expected)
                {
                    fields["expectedType"] = expected;
                }
                if (decoding.Key is { } key)
                {
                    fields["key"] = key;
                }
                fields["codingPath"] = new JsonArray(decoding.CodingPath
                    .Select(component => component is int index ? JsonValue.Create(index) : (JsonNode)JsonValue.Create((string)component))
                    .ToArray());
                fields["debugDescription"] = decoding.DebugDescription;
                if (decoding.Underlying is { } underlying)
                {
                    fields["underlying"] = new JsonObject
                    {
                        ["code"] = underlying.Code,
                        ["debugDescription"] = underlying.DebugDescription,
                        ["domain"] = underlying.Domain,
                    };
                }
                break;
            case EncodingError:
                fields["type"] = "EncodingError";
                fields["domain"] = EncodingError.Domain;
                fields["code"] = EncodingError.Code;
                break;
        }
        return fields;
    }

    /// <summary>A Swift enum case name from the C# enum member name.</summary>
    public static string CaseName(string member) => char.ToLowerInvariant(member[0]) + member[1..];

    /// <summary>A Codable value as the exporter embeds it: its JSON written by the app's encoder, as a node.</summary>
    public static JsonNode Value<T>(T value) => JsonNode.Parse(AnnotatorJson.Encode(value))!;

    /// <summary>A string written by the exporter's own encoder (.sortedKeys and .withoutEscapingSlashes).</summary>
    public static string Canonical<T>(T value) => Encoding.UTF8.GetString(AnnotatorJson.Encode(value, escapeSlashes: false));

    /// <summary>The input bytes of a record: <c>{"utf8": text}</c> or <c>{"base64": data}</c>.</summary>
    public static byte[] Input(JsonNode input) => input["utf8"] is { } text
        ? Encoding.UTF8.GetBytes(text.GetValue<string>())
        : Convert.FromBase64String(input["base64"]!.GetValue<string>());
}
