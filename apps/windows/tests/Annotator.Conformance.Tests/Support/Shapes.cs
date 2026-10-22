using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Annotator.Core;
using Annotator.Core.Geometry;

namespace Annotator.Conformance.Tests;

/// <summary>Corpus forms of points, rectangles, transforms and paths (apps/macos/Tools/Conformance/Corpus.swift).</summary>
internal static class Shapes
{
    private const double InfiniteOrigin = -8.988465674311579e+307;
    private const double InfiniteSize = 1.7976931348623157e+308;

    public static Point Point(JsonNode? node) => new(node![0]!.GetValue<double>(), node[1]!.GetValue<double>());

    public static JsonArray Json(Point point) => new(JsonValue.Create(point.X), JsonValue.Create(point.Y));

    public static Size Size(JsonNode? node) => new(node!["width"]!.GetValue<double>(), node["height"]!.GetValue<double>());

    public static JsonObject Json(Size size) => new() { ["height"] = size.Height, ["width"] = size.Width };

    public static Rect Rect(JsonNode? node) => OptionalRect(node) ?? throw new InvalidDataException("A rectangle is CGRect.null.");

    /// <summary>A rectangle, or null for <c>CGRect.null</c> (written with "+Infinity" coordinates).</summary>
    public static Rect? OptionalRect(JsonNode? node)
    {
        if (node!["x"]!.GetValueKind() == JsonValueKind.String)
        {
            return null;
        }
        return new Rect(node["x"]!.GetValue<double>(), node["y"]!.GetValue<double>(), node["width"]!.GetValue<double>(),
                        node["height"]!.GetValue<double>());
    }

    /// <summary>A rectangle as the exporter writes it; null is <c>CGRect.null</c>.</summary>
    public static JsonObject Json(Rect? rect) => rect is { } value
        ? new JsonObject { ["height"] = value.Height, ["width"] = value.Width, ["x"] = value.X, ["y"] = value.Y }
        : new JsonObject { ["height"] = 0, ["width"] = 0, ["x"] = "+Infinity", ["y"] = "+Infinity" };

    public static bool IsInfinite(Rect rect) =>
        rect.X == InfiniteOrigin && rect.Y == InfiniteOrigin && rect.Width == InfiniteSize && rect.Height == InfiniteSize;

    public static AffineTransform Transform(JsonNode? node)
    {
        var values = node!.AsArray().Select(value => value!.GetValue<double>()).ToArray();
        return new AffineTransform(values[0], values[1], values[2], values[3], values[4], values[5]);
    }

    public static JsonArray Elements(PathData path)
    {
        var elements = new JsonArray();
        foreach (var element in path)
        {
            var name = element.Verb switch
            {
                PathVerb.MoveTo => "move",
                PathVerb.LineTo => "line",
                PathVerb.QuadTo => "quad",
                PathVerb.CubicTo => "cubic",
                _ => "close",
            };
            if (element.Verb == PathVerb.Close)
            {
                elements.Add(new JsonArray("close"));
                continue;
            }
            var points = new JsonArray();
            foreach (var point in element.Points)
            {
                points.Add(Json(point));
            }
            elements.Add(new JsonArray(name, points));
        }
        return elements;
    }

    /// <summary>The exporter's <c>PathRecord.summary</c>.</summary>
    public static JsonObject PathSummary(PathData path) => new()
    {
        ["boundingBox"] = Json(path.Bounds),
        ["boundingBoxOfPath"] = Json(path.TightBounds),
        ["currentPoint"] = path.CurrentPoint is { } current ? Json(current) : null,
        ["elements"] = Elements(path),
        ["isEmpty"] = path.IsEmpty,
    };

    public static string Text(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A double as the exporter writes it: infinities and NaN become "+Infinity", "-Infinity" and "NaN".</summary>
    public static JsonNode Number(double value) => double.IsFinite(value)
        ? JsonValue.Create(value)
        : double.IsNaN(value) ? "NaN" : value > 0 ? "+Infinity" : "-Infinity";
}
