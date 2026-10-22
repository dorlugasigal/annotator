using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Annotator.Core;

namespace Annotator.Conformance.Tests;

/// <summary>The exporter's fixed identities (apps/macos/Tools/Conformance/Corpus.swift, <c>Fixture</c>).</summary>
internal static class Fixture
{
    public const string Prefix = "00000000-0000-4000-8000-";

    public static Guid Id(long number) => Guid.Parse(Prefix + number.ToString("D12", CultureInfo.InvariantCulture));

    /// <summary>An element with a fixed identity, geometry token and append epoch, like Swift's <c>Fixture.element</c>.</summary>
    public static Element Element(long number, Tool tool, Rect frame = default, Point[]? points = null, string text = "",
                                  Style? style = null, ElementMutation? configure = null)
    {
        var element = new Element(tool, frame, points is null ? default : ValueList.Create<Point>(points), text,
                                  style ?? Core.Style.Defaults(tool), Id(number));
        element.GeometryToken = Id(100_000_000 + number);
        element.AppendEpoch = Id(200_000_000 + number);
        configure?.Invoke(ref element);
        return element;
    }

    public static Style Styled(Tool tool, StyleMutation change)
    {
        var style = Core.Style.Defaults(tool);
        change(ref style);
        return style;
    }

    public delegate void StyleMutation(ref Style style);
}

/// <summary>
/// The exporter's stand-ins for IDs the app makes at random (apps/macos/Tools/Conformance/Support.swift, <c>FreshIDs</c>):
/// <c>FFFFFFFF-0000-4000-8000-00000000000N</c> by first appearance, walking elements in document order and, in each,
/// id, groupID, startAnchor, endAnchor, geometryToken and appendEpoch. Fixture IDs and IDs in the input keep their value.
/// </summary>
internal sealed partial class FreshIds
{
    private const string Prefix = "FFFFFFFF-0000-4000-8000-";
    private readonly Dictionary<Guid, Guid> names = [];
    private readonly HashSet<Guid> kept = [];

    public FreshIds(string keepingUuidsIn = "") => Keep(keepingUuidsIn);

    public void Keep(string text)
    {
        foreach (Match match in UuidPattern().Matches(text))
        {
            kept.Add(Guid.Parse(match.Value));
        }
    }

    public Guid this[Guid id]
    {
        get
        {
            if (kept.Contains(id) || IsFixture(id))
            {
                return id;
            }
            if (names.TryGetValue(id, out var name))
            {
                return name;
            }
            name = Guid.Parse(Prefix + (names.Count + 1).ToString("D12", CultureInfo.InvariantCulture));
            names.Add(id, name);
            return name;
        }
    }

    /// <summary>An ID that must already have a stand-in.</summary>
    public Guid Known(Guid id)
    {
        if (kept.Contains(id) || IsFixture(id))
        {
            return id;
        }
        return names.TryGetValue(id, out var name) ? name : throw new InvalidOperationException("A random UUID appeared outside an element walk.");
    }

    public Element Rename(Element original)
    {
        var element = original;
        element.Id = this[element.Id];
        element.GroupId = element.GroupId is { } group ? this[group] : null;
        element.StartAnchor = element.StartAnchor is { } start ? start with { ElementId = this[start.ElementId] } : null;
        element.EndAnchor = element.EndAnchor is { } end ? end with { ElementId = this[end.ElementId] } : null;
        element.GeometryToken = this[element.GeometryToken];
        element.AppendEpoch = this[element.AppendEpoch];
        return element;
    }

    public ElementList Rename(ElementList elements) => ElementList.CreateRange(elements.Select(Rename));

    /// <summary>A Swift <c>Set&lt;UUID&gt;</c> as the exporter writes it: stand-ins, sorted.</summary>
    public JsonArray Set(IEnumerable<Guid> ids) =>
        new(ids.Select(id => SwiftText.UuidString(Known(id))).Order(StringComparer.Ordinal).Select(text => (JsonNode)text).ToArray());

    private static bool IsFixture(Guid id) => SwiftText.UuidString(id).StartsWith(Fixture.Prefix, StringComparison.Ordinal);

    [GeneratedRegex("[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}")]
    private static partial Regex UuidPattern();
}
