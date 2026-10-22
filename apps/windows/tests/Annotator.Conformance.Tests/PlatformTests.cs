using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;

namespace Annotator.Conformance.Tests;

/// <summary>
/// Section <c>platform</c>: Swift memory layout and history bytes, Foundation JSON reading and writing, Swift string
/// equality and case mapping, Core Graphics paths, rectangle math, hatch positions and libm samples. Exact, except libm
/// values within 1 unit in the last place.
/// </summary>
public class PlatformTests
{
    // Layout and history budget

    [Fact]
    public void ElementEstimatesAndHistoryBytesMatchTheMac()
    {
        var layout = Corpus.Json("platform/layout.json");
        var stride = layout["memoryLayout"]!["Element"]!["stride"]!.GetValue<long>();
        Assert.Equal(Element.MinimumEstimatedBytes, Math.Max(512, stride));

        var gradientStyle = new Style { StrokeGradient = GradientPreset.Spectrum.Gradient, Fill = new RGBA(0.2, 0.4, 0.6, 0.5), FillGradient = GradientPreset.Sunset.Gradient };
        var label = new ShapeLabel("Label \u00FC", gradientStyle, followsStroke: true);
        var png = SmallPng();
        (string Name, Element Element)[] samples =
        [
            ("empty rectangle", Fixture.Element(1, Tool.Rectangle, new Rect(0, 0, 10, 10))),
            ("pen with 100 points and pressures", Fixture.Element(2, Tool.Pen,
                points: Enumerable.Range(0, 100).Select(index => new Point(index, index % 7)).ToArray(),
                configure: (ref Element element) => element.Pressures = ValueList.Create<double>(Enumerable.Repeat(0.5, 100).ToArray()))),
            ("text with multibyte characters", Fixture.Element(3, Tool.Text, new Rect(0, 0, 80, 30), text: "h\u00E9llo w\u00F6rld \u2713")),
            ("image", Fixture.Element(4, Tool.Image, new Rect(0, 0, 4, 3), configure: (ref Element element) => element.ImagePng = ValueList.Create<byte>(png))),
            ("rectangle with gradient label", Fixture.Element(5, Tool.Rectangle, new Rect(0, 0, 50, 40), style: gradientStyle,
                configure: (ref Element element) => element.Label = label)),
        ];
        var actual = new JsonArray();
        foreach (var (name, element) in samples)
        {
            actual.Add(new JsonObject
            {
                ["estimatedBytes"] = element.EstimatedBytes,
                ["imageBytes"] = element.ImagePng?.Count ?? 0,
                ["labelEstimatedBytes"] = element.Label is { } owned ? JsonValue.Create(owned.EstimatedBytes) : null,
                ["name"] = name,
                ["paintEstimatedBytes"] = element.Style.PaintEstimatedBytes,
                ["points"] = element.Points.Count,
                ["pressures"] = element.Pressures.Count,
                ["textUTF8"] = Encoding.UTF8.GetByteCount(element.Text),
            });
        }
        JsonDiff.Equal(layout["estimatedBytes"], actual, NumberRule.Exact, "estimatedBytes");

        var history = new CanvasDocument();
        var bytes = new JsonArray(JsonValue.Create(history.HistoryBytes));
        foreach (var (_, element) in samples)
        {
            history.Add(element);
            bytes.Add(history.HistoryBytes);
        }
        JsonDiff.Equal(layout["historyBytesAfterEachAdd"], bytes, NumberRule.Exact, "historyBytesAfterEachAdd");
    }

    /// <summary>The exporter's 4 x 3 fixture PNG, as the Mac encoded it into the image board.</summary>
    internal static byte[] SmallPng()
    {
        var board = JsonNode.Parse(Corpus.Bytes("boards/valid/image.json"))!;
        return Convert.FromBase64String(board["elements"]![0]!["imagePNG"]!.GetValue<string>());
    }

    // JSON decoding

    public static TheoryData<string> DecodingCases => CorpusCases.Of("platform/json-decoding.jsonl");

    [Theory]
    [MemberData(nameof(DecodingCases))]
    public void BoardDecodingMatchesFoundation(string name)
    {
        var record = CorpusCases.Record(name);
        OpenDifferences.Check(CorpusCases.Id(name), () =>
        {
            var input = Outcome.Input(record["input"]!);
            var decoded = Outcome.Of(() =>
            {
                var file = AnnotatorJson.Decode<DrawingFile>(input);
                return new JsonObject
                {
                    ["canonical"] = Outcome.Canonical(file),
                    ["elementCount"] = file.Elements.Count,
                    ["schemaVersion"] = file.SchemaVersion,
                };
            });
            var read = Outcome.Of(() => Encoding.UTF8.GetString(new DrawingFile(DrawingFile.Read(input)).Data()));
            JsonDiff.Equal(record["decode"], decoded, NumberRule.Exact, "decode");
            JsonDiff.Equal(record["read"], read, NumberRule.Exact, "read");
        });
    }

    public static TheoryData<string> ValueCases => CorpusCases.Of("platform/json-decoding-values.jsonl");

    [Theory]
    [MemberData(nameof(ValueCases))]
    public void ValueDecodingMatchesFoundation(string name)
    {
        var record = CorpusCases.Record(name);
        OpenDifferences.Check(CorpusCases.Id(name), () =>
        {
            var input = Outcome.Input(record["input"]!);
            var target = record["target"]!.GetValue<string>();
            var decoded = Outcome.Of(() => target switch
            {
                "Dictionary<String, Int>" => Canonical(Json.Decode(input, (ref Utf8JsonReader reader) => Json.ReadDictionary(ref reader, Json.ReadInt)),
                    (writer, dictionary) =>
                    {
                        writer.WriteStartObject();
                        foreach (var (key, value) in dictionary)
                        {
                            writer.WritePropertyName(key);
                            writer.WriteNumberValue(value);
                        }
                        writer.WriteEndObject();
                    }),
                "Array<Int>" => Canonical(Json.Decode(input, (ref Utf8JsonReader reader) => Json.ReadArray(ref reader, Json.ReadInt)),
                    (writer, items) => WriteArray(writer, items, item => writer.WriteNumberValue(item))),
                "Array<UInt64>" => Canonical(Json.Decode(input, (ref Utf8JsonReader reader) => Json.ReadArray(ref reader, Json.ReadUInt64)),
                    (writer, items) => WriteArray(writer, items, item => writer.WriteNumberValue(item))),
                "Array<Double>" => Canonical(Json.Decode(input, (ref Utf8JsonReader reader) => Json.ReadArray(ref reader, Json.ReadDouble)),
                    (writer, items) => WriteArray(writer, items, item => Json.WriteDoubleValue(writer, item))),
                "Array<String>" => Canonical(Json.Decode(input, (ref Utf8JsonReader reader) => Json.ReadArray(ref reader, Json.ReadString)),
                    (writer, items) => WriteArray(writer, items, item => writer.WriteStringValue(item))),
                "Array<Bool>" => Canonical(Json.Decode(input, (ref Utf8JsonReader reader) => Json.ReadArray(ref reader, Json.ReadBool)),
                    (writer, items) => WriteArray(writer, items, item => writer.WriteBooleanValue(item))),
                "Array<Optional<Int>>" => Canonical(Json.Decode(input, (ref Utf8JsonReader reader) =>
                        Json.ReadArray<long?>(ref reader, (ref Utf8JsonReader item) => Json.IsNull(ref item) ? null : Json.ReadInt(ref item))),
                    (writer, items) => WriteArray(writer, items, item =>
                    {
                        if (item is { } value)
                        {
                            writer.WriteNumberValue(value);
                        }
                        else
                        {
                            writer.WriteNullValue();
                        }
                    })),
                _ => throw new InvalidDataException($"Unknown target {target}."),
            });
            JsonDiff.Equal(record["decode"], decoded, NumberRule.Exact, "decode");
        });
    }

    private static JsonNode Canonical<T>(T value, Action<SwiftJsonWriter, T> write)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        write(new SwiftJsonWriter(buffer, escapeSlashes: false), value);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteArray<T>(SwiftJsonWriter writer, ValueList<T> items, Action<T> write)
    {
        writer.WriteStartArray();
        foreach (var item in items)
        {
            write(item);
        }
        writer.WriteEndArray();
    }

    // JSON encoding

    public static TheoryData<string> EncodingCases => CorpusCases.Of("platform/json-encoding.jsonl", record =>
        record["kind"]!.GetValue<string>() + " " + (record["bits"] ?? record["value"] ?? record["hex"] ?? record["written"])!.ToJsonString());

    [Theory]
    [MemberData(nameof(EncodingCases))]
    public void ValuesAreWrittenAsTheMacWritesThem(string name)
    {
        var record = CorpusCases.Record(name);
        OpenDifferences.Check(CorpusCases.Id(name), () =>
        {
            var written = record["kind"]!.GetValue<string>() switch
            {
                "Double" => App(writer => WriteArray(writer, [BitConverter.Int64BitsToDouble(unchecked((long)ulong.Parse(
                    record["bits"]!.GetValue<string>(), NumberStyles.HexNumber, CultureInfo.InvariantCulture)))], value => Json.WriteDoubleValue(writer, value))),
                "Int" => App(writer => WriteArray(writer, [record["value"]!.GetValue<long>()], value => writer.WriteNumberValue(value))),
                "UInt64" => App(writer => WriteArray(writer, [record["value"]!.GetValue<ulong>()], value => writer.WriteNumberValue(value))),
                "Bool" => App(writer => WriteArray(writer, [record["written"]!["ok"]!.GetValue<string>().Contains("true", StringComparison.Ordinal)],
                    value => writer.WriteBooleanValue(value))),
                "String" => App(writer => WriteArray(writer, [StringValue(record)], value => writer.WriteStringValue(value))),
                "UUID" => App(writer => WriteArray(writer, [Guid.Parse(record["value"]!.GetValue<string>())], value => Json.WriteUuidValue(writer, value))),
                "Data" => App(writer =>
                {
                    writer.WriteStartArray();
                    writer.WriteStringValue(Convert.ToBase64String(Convert.FromHexString(record["hex"]!.GetValue<string>())));
                    writer.WriteEndArray();
                }),
                "sorted keys" => App(writer =>
                {
                    var keys = record["keys"]!.AsArray().Select(key => key!.GetValue<string>()).ToArray();
                    var dictionary = System.Collections.Immutable.ImmutableSortedDictionary.CreateRange(SwiftText.Comparer,
                        keys.Select((key, index) => KeyValuePair.Create(key, (long)index)));
                    writer.WriteStartObject();
                    foreach (var (key, value) in dictionary)
                    {
                        writer.WritePropertyName(key);
                        writer.WriteNumberValue(value);
                    }
                    writer.WriteEndObject();
                }),
                "nil optionals are omitted" => Outcome.Of(() => Encoding.UTF8.GetString(AnnotatorJson.Encode(new Style { HeadScale = null }))),
                var kind => throw new InvalidDataException($"Unknown kind {kind}."),
            };
            JsonDiff.Equal(record["written"], written, NumberRule.Exact, "written");
        });
    }

    private static string StringValue(JsonObject record)
    {
        var value = record["value"]!.GetValue<string>();
        Assert.Equal(record["utf8Hex"]!.GetValue<string>(), Convert.ToHexStringLower(Encoding.UTF8.GetBytes(value)));
        return value;
    }

    /// <summary>The app's encoder (.sortedKeys only, so slashes are escaped).</summary>
    private static JsonObject App(Action<SwiftJsonWriter> write) => Outcome.Of(() =>
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        write(new SwiftJsonWriter(buffer));
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    });

    // Strings

    public static TheoryData<string> StringCases => CorpusCases.Of("platform/strings.jsonl", record =>
        string.Join(" ", record["scalars"]!.AsArray().Select(scalar => scalar!.GetValue<string>())));

    [Theory]
    [MemberData(nameof(StringCases))]
    public void StringsBehaveAsSwiftStrings(string name)
    {
        var record = CorpusCases.Record(name);
        OpenDifferences.Check(CorpusCases.Id(name), () =>
        {
            var value = record["value"]!.GetValue<string>();
            string Flags(Func<Rune, bool> member) => string.Concat(value.EnumerateRunes().Select(rune => member(rune) ? '1' : '0'));
            var lower = SwiftText.Lowercased(value);
            var upper = SwiftText.Uppercased(value);
            var actual = new JsonObject
            {
                ["alphanumerics"] = Flags(SwiftText.IsAlphanumeric),
                ["characterCount"] = SwiftText.CharacterCount(value),
                ["controlCharacters"] = Flags(SwiftText.IsControl),
                ["equalsLowercased"] = SwiftText.Equals(value, lower),
                ["equalsUppercased"] = SwiftText.Equals(value, upper),
                ["letters"] = Flags(SwiftText.IsLetter),
                ["lowercased"] = lower,
                ["newlines"] = Flags(SwiftText.IsNewline),
                ["scalarCount"] = SwiftText.ScalarCount(value),
                ["scalars"] = new JsonArray(value.EnumerateRunes().Select(rune => (JsonNode)$"U+{rune.Value:X4}").ToArray()),
                ["trimmedWhitespaceAndNewlines"] = SwiftText.TrimWhitespacesAndNewlines(value),
                ["uppercased"] = upper,
                ["utf16Count"] = value.Length,
                ["utf8Count"] = SwiftText.Utf8Count(value),
                ["value"] = value,
                ["whitespacesAndNewlines"] = Flags(SwiftText.IsWhitespaceOrNewline),
            };
            JsonDiff.Equal(record, actual, NumberRule.Exact);
        });
    }

    public static TheoryData<string> ComparisonCases => CorpusCases.Of("platform/string-comparison.jsonl", record =>
        record["leftUTF8Hex"]!.GetValue<string>() + " " + record["rightUTF8Hex"]!.GetValue<string>());

    [Theory]
    [MemberData(nameof(ComparisonCases))]
    public void StringsCompareAsSwiftStrings(string name)
    {
        var record = CorpusCases.Record(name);
        OpenDifferences.Check(CorpusCases.Id(name), () =>
        {
            var left = record["left"]!.GetValue<string>();
            var right = record["right"]!.GetValue<string>();
            var actual = record.DeepClone().AsObject();
            actual["equal"] = SwiftText.Equals(left, right);
            actual["less"] = SwiftText.Compare(left, right) < 0;
            actual["greater"] = SwiftText.Compare(left, right) > 0;
            JsonDiff.Equal(record, actual, NumberRule.Exact);
        });
    }

    // Core Graphics paths

    public static TheoryData<string> PathCases => CorpusCases.Of("platform/cgpath.jsonl");

    [Theory]
    [MemberData(nameof(PathCases))]
    public void PathsHaveTheCoreGraphicsElements(string name)
    {
        var record = CorpusCases.Record(name);
        JsonObject Built()
        {
            var builder = new PathBuilder();
            foreach (var op in record["ops"]!.AsArray())
            {
                Apply(builder, op!.AsArray());
            }
            return Shapes.PathSummary(builder.ToPathData());
        }
        OpenDifferences.Check(CorpusCases.Id(name),
            () => JsonDiff.Equal(record["path"], Built(), NumberRule.Exact),
            () => JsonDiff.Equal(record["path"], Built(), NumberRule.Within(1e-12)));
    }

    private static void Apply(PathBuilder builder, JsonArray op)
    {
        switch (op[0]!.GetValue<string>())
        {
            case "move": builder.MoveTo(Shapes.Point(op[1])); break;
            case "line": builder.LineTo(Shapes.Point(op[1])); break;
            case "quad": builder.QuadTo(Shapes.Point(op[1]), Shapes.Point(op[2])); break;
            case "cubic": builder.CubicTo(Shapes.Point(op[1]), Shapes.Point(op[2]), Shapes.Point(op[3])); break;
            case "close": builder.Close(); break;
            case "addRect": builder.AddRect(Shapes.Rect(op[1])); break;
            case "addEllipse": builder.AddEllipse(Shapes.Rect(op[1])); break;
            case "addRoundedRect": builder.AddRoundedRect(Shapes.Rect(op[1]), Number(op[2]), Number(op[3])); break;
            case "addLines": builder.AddLines(op[1]!.AsArray().Select(Shapes.Point).ToArray()); break;
            case "addArc":
                builder.AddArc(Shapes.Point(op[1]), Number(op[2]), Number(op[3]), Number(op[4]), op[5]!.GetValue<bool>());
                break;
            case "addRelativeArc": builder.AddRelativeArc(Shapes.Point(op[1]), Number(op[2]), Number(op[3]), Number(op[4])); break;
            case "addArcTangent": builder.AddArcToPoint(Shapes.Point(op[1]), Shapes.Point(op[2]), Number(op[3])); break;
            case "addPath":
            {
                var inner = new PathBuilder();
                foreach (var nested in op[1]!.AsArray())
                {
                    Apply(inner, nested!.AsArray());
                }
                builder.AddPath(inner.ToPathData(), Shapes.Transform(op[2]));
                break;
            }
            case "CGPath.rect": AddTransformed(builder, new PathBuilder().AddRect(Shapes.Rect(op[1])), op[2]); break;
            case "CGPath.ellipse": AddTransformed(builder, new PathBuilder().AddEllipse(Shapes.Rect(op[1])), op[2]); break;
            case "CGPath.roundedRect" when op[4] is { } transform:
                builder.AddRoundedRect(Shapes.Rect(op[1]), Number(op[2]), Number(op[3]), Shapes.Transform(transform));
                break;
            case "CGPath.roundedRect":
                builder.AddRoundedRect(Shapes.Rect(op[1]), Number(op[2]), Number(op[3]));
                break;
            case var other:
                throw new InvalidDataException($"Unknown path operation {other}.");
        }
    }

    private static void AddTransformed(PathBuilder builder, PathBuilder shape, JsonNode? transform)
    {
        var path = shape.ToPathData();
        builder.AddPath(transform is null ? path : path.Transformed(Shapes.Transform(transform)));
    }

    private static double Number(JsonNode? node) => node!.GetValue<double>();

    // Rectangle math

    public static TheoryData<string> RectCases => CorpusCases.Of("platform/rect.jsonl", record => string.Join(",", record.Select(entry => entry.Key)));

    [Theory]
    [MemberData(nameof(RectCases))]
    public void RectanglesFollowCoreGraphics(string name)
    {
        var record = CorpusCases.Record(name);
        OpenDifferences.Check(CorpusCases.Id(name), () =>
        {
            var actual = new JsonObject();
            if (record.ContainsKey("applying"))
            {
                var rect = Shapes.OptionalRect(record["rect"]);
                var transform = Shapes.Transform(record["transform"]);
                actual["applying"] = Shapes.Json(rect is { } value ? transform.TransformBounds(value) : null);
                actual["rect"] = record["rect"]!.DeepClone();
                actual["transform"] = record["transform"]!.DeepClone();
            }
            else if (record.ContainsKey("union"))
            {
                Rect? a = Shapes.OptionalRect(record["a"]), b = Shapes.OptionalRect(record["b"]);
                actual["a"] = record["a"]!.DeepClone();
                actual["b"] = record["b"]!.DeepClone();
                actual["containsRect"] = RectMath.Contains(a, b);
                actual["intersection"] = Shapes.Json(a is { } first && b is { } second ? RectMath.Intersection(first, second) : null);
                actual["intersects"] = a is { } x && b is { } y && RectMath.Intersects(x, y);
                actual["union"] = Shapes.Json(RectMath.Union(a, b));
            }
            else
            {
                var rect = Shapes.OptionalRect(record["rect"]);
                actual["insetBy(-2.5,-2.5)"] = Shapes.Json(rect is { } r1 ? RectMath.Inset(r1, -2.5, -2.5) : null);
                actual["insetBy(6,6)"] = Shapes.Json(rect is { } r2 ? RectMath.Inset(r2, 6, 6) : null);
                actual["integral"] = Shapes.Json(rect is { } r3 ? RectMath.Integral(r3) : null);
                actual["isEmpty"] = RectMath.IsEmpty(rect);
                actual["isInfinite"] = rect is { } r4 && Shapes.IsInfinite(r4);
                actual["isNull"] = rect is null;
                actual["maxX"] = rect is { } r5 ? RectMath.Standardized(r5).MaxX : (JsonNode)"+Infinity";
                actual["midX"] = rect is { } r6 ? RectMath.Standardized(r6).Center.X : (JsonNode)"+Infinity";
                actual["minX"] = rect is { } r7 ? RectMath.Standardized(r7).X : (JsonNode)"+Infinity";
                actual["rect"] = record["rect"]!.DeepClone();
                actual["standardized"] = Shapes.Json(rect is { } r8 ? RectMath.Standardized(r8) : null);
            }
            JsonDiff.Equal(record, actual, NumberRule.Exact);
        });
    }

    // Hatch positions

    public static TheoryData<string> HatchCases => CorpusCases.Of("platform/hatch.jsonl", record => record["frame"]!.ToJsonString());

    [Theory]
    [MemberData(nameof(HatchCases))]
    public void HatchPositionsUseAFusedMultiplyAdd(string name)
    {
        var record = CorpusCases.Record(name);
        OpenDifferences.Check(CorpusCases.Id(name), () =>
        {
            var frame = Shapes.Rect(record["frame"]);
            var width = record["width"]!.GetValue<double>();
            var stride = new HatchStride(frame, width);
            var positions = new List<double>();
            while (stride.MoveNext())
            {
                positions.Add(stride.Current);
            }
            var probe = new HatchStride(frame, width);
            var actual = record.DeepClone().AsObject();
            actual["count"] = positions.Count;
            actual["end"] = probe.End;
            actual["first"] = new JsonArray(positions.Take(64).Select(value => (JsonNode)value).ToArray());
            actual["last"] = new JsonArray(positions.TakeLast(64).Select(value => (JsonNode)value).ToArray());
            actual["spacing"] = probe.Spacing;
            actual["start"] = probe.Start;
            JsonDiff.Equal(record, actual, NumberRule.Exact);
        });
    }

    // libm samples

    public static TheoryData<string> MathCases => CorpusCases.Of("platform/math.jsonl", record =>
        string.Join(",", record.Where(entry => entry.Key is "x" or "y").Select(entry => entry.Value!.ToJsonString())));

    [Theory]
    [MemberData(nameof(MathCases))]
    public void MathMatchesTheMacWithinOneUlp(string name)
    {
        var record = CorpusCases.Record(name);
        OpenDifferences.Check(CorpusCases.Id(name),
            () => JsonDiff.Equal(record, MathRecord(record), NumberRule.Ulps(1)),
            () => JsonDiff.Equal(record, MathRecord(record), NumberRule.Ulps(2)));
    }

    private static JsonObject MathRecord(JsonObject record)
    {
        var actual = new JsonObject();
        var x = record["x"]!.GetValue<double>();
        if (record.ContainsKey("atan2"))
        {
            var y = record["y"]!.GetValue<double>();
            actual["atan2"] = Shapes.Number(Math.Atan2(y, x));
            actual["hypot"] = Shapes.Number(double.Hypot(x, y));
            actual["x"] = x;
            actual["y"] = y;
            return actual;
        }
        if (record.ContainsKey("sin"))
        {
            actual["cos"] = Shapes.Number(Math.Cos(x));
            actual["exp"] = Shapes.Number(Math.Exp(x));
            actual["log"] = x > 0 ? Shapes.Number(Math.Log(x)) : null;
            actual["pow(x,2.4)"] = x >= 0 ? Shapes.Number(Math.Pow(x, 2.4)) : null;
            actual["sin"] = Shapes.Number(Math.Sin(x));
            actual["sqrt"] = x >= 0 ? Shapes.Number(Math.Sqrt(x)) : null;
            actual["tan"] = Shapes.Number(Math.Tan(x));
        }
        actual["rounded"] = Math.Round(x, MidpointRounding.AwayFromZero);
        actual["roundedToEven"] = Math.Round(x, MidpointRounding.ToEven);
        actual["x"] = x;
        return actual;
    }
}
