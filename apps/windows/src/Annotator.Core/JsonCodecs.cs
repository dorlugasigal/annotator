// The JSON forms of the Codable AnnotationCore types (Swift synthesized Codable). See Json.cs for the rules.
// Keys are listed in Swift declaration order for decoding; writers emit them in sorted order.
using System.Collections.Immutable;
using System.Text.Json;

namespace Annotator.Core;

/// <summary>
/// The C# form of `JSONEncoder().encode(value)` and `JSONDecoder().decode(T.self, from:)` for the Codable
/// AnnotationCore types: Point, Rect, RGBA, ColorGradient, Anchor, Style, ShapeLabel, Element, DrawingFile,
/// SavedShape, ActivationShortcut, CursorHighlightSettings and WorkspacePreferences. These do not validate;
/// use the types' Data() and Read() methods for files.
/// </summary>
public static class AnnotatorJson
{
    public static byte[] Encode<T>(T value) => Encode(value, escapeSlashes: true);

    /// <summary>
    /// With <paramref name="escapeSlashes"/> false, slashes stay as they are, like Swift's <c>.withoutEscapingSlashes</c>
    /// (the conformance corpus's own encoder).
    /// </summary>
    internal static byte[] Encode<T>(T value, bool escapeSlashes) => value switch
    {
        Point point => Json.Encode(point, Codecs.WritePoint, escapeSlashes),
        Rect rect => Json.Encode(rect, Codecs.WriteRect, escapeSlashes),
        RGBA color => Json.Encode(color, Codecs.WriteRgba, escapeSlashes),
        ColorGradient gradient => Json.Encode(gradient, Codecs.WriteGradient, escapeSlashes),
        Anchor anchor => Json.Encode(anchor, Codecs.WriteAnchor, escapeSlashes),
        Style style => Json.Encode(style, Codecs.WriteStyle, escapeSlashes),
        ShapeLabel label => Json.Encode(label, Codecs.WriteLabel, escapeSlashes),
        Element element => Json.Encode(element, Codecs.WriteElement, escapeSlashes),
        DrawingFile file => Json.Encode(file, Codecs.WriteDrawingFile, escapeSlashes),
        SavedShape shape => Json.Encode(shape, Codecs.WriteSavedShape, escapeSlashes),
        ActivationShortcut shortcut => Json.Encode(shortcut, Codecs.WriteShortcut, escapeSlashes),
        CursorHighlightSettings settings => Json.Encode(settings, Codecs.WriteCursorHighlight, escapeSlashes),
        BreakTimerSettings settings => Json.Encode(settings, Codecs.WriteBreakTimer, escapeSlashes),
        WorkspacePreferences preferences => Json.Encode(preferences, Codecs.WritePreferences, escapeSlashes),
        _ => throw new NotSupportedException($"{typeof(T).Name} is not an AnnotationCore Codable type."),
    };

    public static T Decode<T>(ReadOnlySpan<byte> json)
    {
        object value = typeof(T) switch
        {
            var type when type == typeof(Point) => Json.Decode(json, Codecs.ReadPoint),
            var type when type == typeof(Rect) => Json.Decode(json, Codecs.ReadRect),
            var type when type == typeof(RGBA) => Json.Decode(json, Codecs.ReadRgba),
            var type when type == typeof(ColorGradient) => Json.Decode(json, Codecs.ReadGradient),
            var type when type == typeof(Anchor) => Json.Decode(json, Codecs.ReadAnchor),
            var type when type == typeof(Style) => Json.Decode(json, Codecs.ReadStyle),
            var type when type == typeof(ShapeLabel) => Json.Decode(json, Codecs.ReadLabel),
            var type when type == typeof(Element) => Json.Decode(json, Codecs.ReadElement),
            var type when type == typeof(DrawingFile) => Json.Decode(json, Codecs.ReadDrawingFile),
            var type when type == typeof(SavedShape) => Json.Decode(json, Codecs.ReadSavedShape),
            var type when type == typeof(ActivationShortcut) => Json.Decode(json, Codecs.ReadShortcut),
            var type when type == typeof(CursorHighlightSettings) => Json.Decode(json, Codecs.ReadCursorHighlight),
            var type when type == typeof(BreakTimerSettings) => Json.Decode(json, Codecs.ReadBreakTimer),
            var type when type == typeof(WorkspacePreferences) => Json.Decode(json, Codecs.ReadPreferences),
            _ => throw new NotSupportedException($"{typeof(T).Name} is not an AnnotationCore Codable type."),
        };
        return (T)value;
    }
}

internal static class Codecs
{
    private static readonly JsonKeys PointKeys = new("x", "y");
    private static readonly JsonKeys RectKeys = new("x", "y", "width", "height");
    private static readonly JsonKeys RgbaKeys = new("red", "green", "blue", "alpha");
    private static readonly JsonKeys GradientKeys = new("colors", "angle");
    private static readonly JsonKeys AnchorKeys = new("elementID", "relative");
    private static readonly JsonKeys StyleKeys = new(
        "color", "fill", "width", "opacity", "pattern", "fillPattern", "roughness", "cornerRadius", "fontSize", "fontName",
        "bold", "alignment", "curved", "startHead", "endHead", "pressure", "headScale", "effectAmount", "gridRows",
        "gridColumns", "negativeAxes", "axisTickCount", "strokeGradient", "fillGradient");
    private static readonly JsonKeys LabelKeys = new("text", "style", "followsStroke");
    private static readonly JsonKeys ElementKeys = new(
        "id", "tool", "frame", "points", "pressures", "text", "style", "rotation", "groupID", "locked", "startAnchor",
        "endAnchor", "seed", "revision", "geometryToken", "appendEpoch", "imagePNG", "label");
    private static readonly JsonKeys DrawingFileKeys = new("schemaVersion", "elements");
    private static readonly JsonKeys SavedShapeKeys = new("schemaVersion", "id", "name", "drawing");
    private static readonly JsonKeys ShortcutKeys = new("keyCode", "control", "option", "command", "shift", "character");
    private static readonly JsonKeys CursorKeys = new(
        "shape", "visibility", "color", "diameter", "borderWidth", "showClicks", "glowRadius", "borderStyle", "clickEffect");
    private static readonly JsonKeys PreferenceKeys = new(
        "schemaVersion", "toolStyles", "toolShortcuts", "shortcutVersion", "activationShortcut", "smartDraw",
        "spotlightRadius", "hideShortcut", "zoomShortcut", "resumeShortcut", "toolbarOrientation", "toolbarDockEdge",
        "lastCoverMode", "toolbarTools", "showMenuBarIcon", "cursorStyle", "quickPreferencesExpanded", "inspectorPinned",
        "animateVisibility", "visibilityAnimationStyle", "theme", "showShortcutHints", "cursorHighlight",
        "commandShortcuts", "toolbarSize", "shareToolStyles", "toolbarItems", "breakTimer", "breakShortcut",
        "hiddenToolbarControls", "toolbarCollapseDelay", "expandedPropertyChoices");
    private static readonly JsonKeys BreakTimerKeys = new(
        "minutes", "title", "background", "palette", "showEndTime", "chime", "countOver", "allDisplays", "lockWhenTouched");

    // Point

    public static Point ReadPoint(ref Utf8JsonReader reader)
    {
        Json.ExpectObject(ref reader);
        var state = new KeyedState();
        double x = 0, y = 0;
        while (Json.NextProperty(ref reader, PointKeys, out var key))
        {
            if (!state.Claim(key, ref reader))
            {
                continue;
            }
            switch (key)
            {
                case 0: x = state.Read(ref reader, key, 2, Json.ReadDouble); break;
                case 1: y = state.Read(ref reader, key, 2, Json.ReadDouble); break;
            }
        }
        state.Finish(PointKeys, 0b11);
        return new Point(x, y);
    }

    public static void WritePoint(SwiftJsonWriter writer, in Point value)
    {
        writer.WriteStartObject();
        Json.WriteDouble(writer, "x"u8, value.X);
        Json.WriteDouble(writer, "y"u8, value.Y);
        writer.WriteEndObject();
    }

    // Rect

    public static Rect ReadRect(ref Utf8JsonReader reader)
    {
        Json.ExpectObject(ref reader);
        var state = new KeyedState();
        double x = 0, y = 0, width = 0, height = 0;
        while (Json.NextProperty(ref reader, RectKeys, out var key))
        {
            if (!state.Claim(key, ref reader))
            {
                continue;
            }
            switch (key)
            {
                case 0: x = state.Read(ref reader, key, 4, Json.ReadDouble); break;
                case 1: y = state.Read(ref reader, key, 4, Json.ReadDouble); break;
                case 2: width = state.Read(ref reader, key, 4, Json.ReadDouble); break;
                case 3: height = state.Read(ref reader, key, 4, Json.ReadDouble); break;
            }
        }
        state.Finish(RectKeys, 0b1111);
        return new Rect(x, y, width, height);
    }

    public static void WriteRect(SwiftJsonWriter writer, in Rect value)
    {
        writer.WriteStartObject();
        Json.WriteDouble(writer, "height"u8, value.Height);
        Json.WriteDouble(writer, "width"u8, value.Width);
        Json.WriteDouble(writer, "x"u8, value.X);
        Json.WriteDouble(writer, "y"u8, value.Y);
        writer.WriteEndObject();
    }

    // RGBA

    public static RGBA ReadRgba(ref Utf8JsonReader reader)
    {
        Json.ExpectObject(ref reader);
        var state = new KeyedState();
        double red = 0, green = 0, blue = 0, alpha = 0;
        while (Json.NextProperty(ref reader, RgbaKeys, out var key))
        {
            if (!state.Claim(key, ref reader))
            {
                continue;
            }
            switch (key)
            {
                case 0: red = state.Read(ref reader, key, 4, Json.ReadDouble); break;
                case 1: green = state.Read(ref reader, key, 4, Json.ReadDouble); break;
                case 2: blue = state.Read(ref reader, key, 4, Json.ReadDouble); break;
                case 3: alpha = state.Read(ref reader, key, 4, Json.ReadDouble); break;
            }
        }
        state.Finish(RgbaKeys, 0b1111);
        return new RGBA(red, green, blue, alpha);
    }

    public static void WriteRgba(SwiftJsonWriter writer, in RGBA value)
    {
        writer.WriteStartObject();
        Json.WriteDouble(writer, "alpha"u8, value.Alpha);
        Json.WriteDouble(writer, "blue"u8, value.Blue);
        Json.WriteDouble(writer, "green"u8, value.Green);
        Json.WriteDouble(writer, "red"u8, value.Red);
        writer.WriteEndObject();
    }

    // ColorGradient

    public static ColorGradient ReadGradient(ref Utf8JsonReader reader)
    {
        Json.ExpectObject(ref reader);
        var state = new KeyedState();
        ValueList<RGBA> colors = default;
        double angle = 0;
        while (Json.NextProperty(ref reader, GradientKeys, out var key))
        {
            if (!state.Claim(key, ref reader))
            {
                continue;
            }
            switch (key)
            {
                case 0: colors = state.Read(ref reader, key, 2, ReadRgbaArray); break;
                case 1: angle = state.Read(ref reader, key, 2, Json.ReadDouble); break;
            }
        }
        state.Finish(GradientKeys, 0b11);
        return new ColorGradient(colors, angle);
    }

    public static void WriteGradient(SwiftJsonWriter writer, in ColorGradient value)
    {
        writer.WriteStartObject();
        Json.WriteDouble(writer, "angle"u8, value.Angle);
        writer.WritePropertyName("colors"u8);
        writer.WriteStartArray();
        foreach (ref readonly var color in value.Colors)
        {
            WriteRgba(writer, color);
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    // Anchor

    public static Anchor ReadAnchor(ref Utf8JsonReader reader)
    {
        Json.ExpectObject(ref reader);
        var state = new KeyedState();
        Guid elementId = default;
        Point relative = default;
        while (Json.NextProperty(ref reader, AnchorKeys, out var key))
        {
            if (!state.Claim(key, ref reader))
            {
                continue;
            }
            switch (key)
            {
                case 0: elementId = state.Read(ref reader, key, 2, Json.ReadUuid); break;
                case 1: relative = state.Read(ref reader, key, 2, ReadPoint); break;
            }
        }
        state.Finish(AnchorKeys, 0b11);
        return new Anchor(elementId, relative);
    }

    public static void WriteAnchor(SwiftJsonWriter writer, in Anchor value)
    {
        writer.WriteStartObject();
        Json.WriteUuid(writer, "elementID"u8, value.ElementId);
        writer.WritePropertyName("relative"u8);
        WritePoint(writer, value.Relative);
        writer.WriteEndObject();
    }

    // Style

    public static Style ReadStyle(ref Utf8JsonReader reader)
    {
        Json.ExpectObject(ref reader);
        var state = new KeyedState();
        var style = new Style();
        const int count = 24;
        while (Json.NextProperty(ref reader, StyleKeys, out var key))
        {
            if (!state.Claim(key, ref reader))
            {
                continue;
            }
            var isNull = Json.IsNull(ref reader);
            switch (key)
            {
                case 0: style.Color = state.Read(ref reader, key, count, ReadRgba); break;
                case 1: style.Fill = isNull ? null : state.Read(ref reader, key, count, ReadRgba); break;
                case 2: style.Width = state.Read(ref reader, key, count, Json.ReadDouble); break;
                case 3: style.Opacity = state.Read(ref reader, key, count, Json.ReadDouble); break;
                case 4: style.Pattern = state.Read(ref reader, key, count, ReadStrokePattern); break;
                case 5: style.FillPattern = state.Read(ref reader, key, count, ReadFillPattern); break;
                case 6: style.Roughness = state.Read(ref reader, key, count, Json.ReadDouble); break;
                case 7: style.CornerRadius = state.Read(ref reader, key, count, Json.ReadDouble); break;
                case 8: style.FontSize = state.Read(ref reader, key, count, Json.ReadDouble); break;
                case 9: style.FontName = state.Read(ref reader, key, count, Json.ReadString) ?? ""; break;
                case 10: style.Bold = state.Read(ref reader, key, count, Json.ReadBool); break;
                case 11: style.Alignment = state.Read(ref reader, key, count, ReadAlignment); break;
                case 12: style.Curved = state.Read(ref reader, key, count, Json.ReadBool); break;
                case 13: style.StartHead = state.Read(ref reader, key, count, ReadArrowHead); break;
                case 14: style.EndHead = state.Read(ref reader, key, count, ReadArrowHead); break;
                case 15: style.Pressure = state.Read(ref reader, key, count, Json.ReadBool); break;
                case 16: style.HeadScale = isNull ? null : state.Read(ref reader, key, count, Json.ReadDouble); break;
                case 17: style.EffectAmount = isNull ? null : state.Read(ref reader, key, count, Json.ReadDouble); break;
                case 18: style.GridRows = isNull ? null : state.Read(ref reader, key, count, Json.ReadInt); break;
                case 19: style.GridColumns = isNull ? null : state.Read(ref reader, key, count, Json.ReadInt); break;
                case 20: style.NegativeAxes = isNull ? null : state.Read(ref reader, key, count, Json.ReadBool); break;
                case 21: style.AxisTickCount = isNull ? null : state.Read(ref reader, key, count, Json.ReadInt); break;
                case 22: style.StrokeGradient = isNull ? null : state.Read(ref reader, key, count, ReadGradient); break;
                case 23: style.FillGradient = isNull ? null : state.Read(ref reader, key, count, ReadGradient); break;
            }
        }
        state.Finish(StyleKeys, 0b1111_1111_1111_1101);
        return style;
    }

    public static void WriteStyle(SwiftJsonWriter writer, in Style value)
    {
        writer.WriteStartObject();
        writer.WriteString("alignment"u8, value.Alignment.RawValue);
        if (value.AxisTickCount is { } ticks)
        {
            writer.WriteNumber("axisTickCount"u8, ticks);
        }
        writer.WriteBoolean("bold"u8, value.Bold);
        writer.WritePropertyName("color"u8);
        WriteRgba(writer, value.Color);
        Json.WriteDouble(writer, "cornerRadius"u8, value.CornerRadius);
        writer.WriteBoolean("curved"u8, value.Curved);
        if (value.EffectAmount is { } amount)
        {
            Json.WriteDouble(writer, "effectAmount"u8, amount);
        }
        writer.WriteString("endHead"u8, value.EndHead.RawValue);
        if (value.Fill is { } fill)
        {
            writer.WritePropertyName("fill"u8);
            WriteRgba(writer, fill);
        }
        if (value.FillGradient is { } fillGradient)
        {
            writer.WritePropertyName("fillGradient"u8);
            WriteGradient(writer, fillGradient);
        }
        writer.WriteString("fillPattern"u8, value.FillPattern.RawValue);
        writer.WriteString("fontName"u8, value.FontName);
        Json.WriteDouble(writer, "fontSize"u8, value.FontSize);
        if (value.GridColumns is { } columns)
        {
            writer.WriteNumber("gridColumns"u8, columns);
        }
        if (value.GridRows is { } rows)
        {
            writer.WriteNumber("gridRows"u8, rows);
        }
        if (value.HeadScale is { } headScale)
        {
            Json.WriteDouble(writer, "headScale"u8, headScale);
        }
        if (value.NegativeAxes is { } negativeAxes)
        {
            writer.WriteBoolean("negativeAxes"u8, negativeAxes);
        }
        Json.WriteDouble(writer, "opacity"u8, value.Opacity);
        writer.WriteString("pattern"u8, value.Pattern.RawValue);
        writer.WriteBoolean("pressure"u8, value.Pressure);
        Json.WriteDouble(writer, "roughness"u8, value.Roughness);
        writer.WriteString("startHead"u8, value.StartHead.RawValue);
        if (value.StrokeGradient is { } strokeGradient)
        {
            writer.WritePropertyName("strokeGradient"u8);
            WriteGradient(writer, strokeGradient);
        }
        Json.WriteDouble(writer, "width"u8, value.Width);
        writer.WriteEndObject();
    }

    // ShapeLabel

    public static ShapeLabel ReadLabel(ref Utf8JsonReader reader)
    {
        Json.ExpectObject(ref reader);
        var state = new KeyedState();
        var text = "";
        var style = new Style();
        bool? followsStroke = null;
        while (Json.NextProperty(ref reader, LabelKeys, out var key))
        {
            if (!state.Claim(key, ref reader))
            {
                continue;
            }
            switch (key)
            {
                case 0: text = state.Read(ref reader, key, 3, Json.ReadString) ?? ""; break;
                case 1: style = state.Read(ref reader, key, 3, ReadStyle); break;
                case 2: followsStroke = Json.IsNull(ref reader) ? null : state.Read(ref reader, key, 3, Json.ReadBool); break;
            }
        }
        state.Finish(LabelKeys, 0b011);
        return new ShapeLabel(text, style, followsStroke);
    }

    public static void WriteLabel(SwiftJsonWriter writer, in ShapeLabel value)
    {
        writer.WriteStartObject();
        if (value.FollowsStroke is { } followsStroke)
        {
            writer.WriteBoolean("followsStroke"u8, followsStroke);
        }
        writer.WritePropertyName("style"u8);
        WriteStyle(writer, value.Style);
        writer.WriteString("text"u8, value.Text);
        writer.WriteEndObject();
    }

    // Element

    public static Element ReadElement(ref Utf8JsonReader reader)
    {
        Json.ExpectObject(ref reader);
        var state = new KeyedState();
        const int count = 18;
        var element = default(Element);
        element.Text = "";
        element.Style = new Style();
        while (Json.NextProperty(ref reader, ElementKeys, out var key))
        {
            if (!state.Claim(key, ref reader))
            {
                continue;
            }
            var isNull = Json.IsNull(ref reader);
            switch (key)
            {
                case 0: element.Id = state.Read(ref reader, key, count, Json.ReadUuid); break;
                case 1: element.Tool = state.Read(ref reader, key, count, ReadTool); break;
                case 2: element.Frame = state.Read(ref reader, key, count, ReadRect); break;
                case 3: element.Points = state.Read(ref reader, key, count, ReadPointArray); break;
                case 4: element.Pressures = state.Read(ref reader, key, count, ReadDoubleArray); break;
                case 5: element.Text = state.Read(ref reader, key, count, Json.ReadString) ?? ""; break;
                case 6: element.Style = state.Read(ref reader, key, count, ReadStyle); break;
                case 7: element.Rotation = state.Read(ref reader, key, count, Json.ReadDouble); break;
                case 8: element.GroupId = isNull ? null : state.Read(ref reader, key, count, Json.ReadUuid); break;
                case 9: element.Locked = state.Read(ref reader, key, count, Json.ReadBool); break;
                case 10: element.StartAnchor = isNull ? null : state.Read(ref reader, key, count, ReadAnchor); break;
                case 11: element.EndAnchor = isNull ? null : state.Read(ref reader, key, count, ReadAnchor); break;
                case 12: element.Seed = state.Read(ref reader, key, count, Json.ReadUInt64); break;
                case 13: element.Revision = state.Read(ref reader, key, count, Json.ReadUInt64); break;
                case 14: element.GeometryToken = state.Read(ref reader, key, count, Json.ReadUuid); break;
                case 15: element.AppendEpoch = state.Read(ref reader, key, count, Json.ReadUuid); break;
                case 16: element.ImagePng = isNull ? null : state.Read(ref reader, key, count, Json.ReadData); break;
                case 17: element.Label = isNull ? null : state.Read(ref reader, key, count, ReadLabel); break;
            }
        }
        state.Finish(ElementKeys, 0b1111_0010_1111_1111);
        return element;
    }

    public static void WriteElement(SwiftJsonWriter writer, in Element value)
    {
        writer.WriteStartObject();
        Json.WriteUuid(writer, "appendEpoch"u8, value.AppendEpoch);
        if (value.EndAnchor is { } endAnchor)
        {
            writer.WritePropertyName("endAnchor"u8);
            WriteAnchor(writer, endAnchor);
        }
        writer.WritePropertyName("frame"u8);
        WriteRect(writer, value.Frame);
        Json.WriteUuid(writer, "geometryToken"u8, value.GeometryToken);
        if (value.GroupId is { } group)
        {
            Json.WriteUuid(writer, "groupID"u8, group);
        }
        Json.WriteUuid(writer, "id"u8, value.Id);
        if (value.ImagePng is { } image)
        {
            writer.WriteBase64String("imagePNG"u8, image.AsSpan());
        }
        if (value.Label is { } label)
        {
            writer.WritePropertyName("label"u8);
            WriteLabel(writer, label);
        }
        writer.WriteBoolean("locked"u8, value.Locked);
        writer.WritePropertyName("points"u8);
        writer.WriteStartArray();
        foreach (ref readonly var point in value.Points)
        {
            WritePoint(writer, point);
        }
        writer.WriteEndArray();
        writer.WritePropertyName("pressures"u8);
        writer.WriteStartArray();
        foreach (var pressure in value.Pressures.AsSpan())
        {
            Json.WriteDoubleValue(writer, pressure);
        }
        writer.WriteEndArray();
        writer.WriteNumber("revision"u8, value.Revision);
        Json.WriteDouble(writer, "rotation"u8, value.Rotation);
        writer.WriteNumber("seed"u8, value.Seed);
        if (value.StartAnchor is { } startAnchor)
        {
            writer.WritePropertyName("startAnchor"u8);
            WriteAnchor(writer, startAnchor);
        }
        writer.WritePropertyName("style"u8);
        WriteStyle(writer, value.Style);
        writer.WriteString("text"u8, value.Text);
        writer.WriteString("tool"u8, value.Tool.RawValue);
        writer.WriteEndObject();
    }

    // DrawingFile

    public static DrawingFile ReadDrawingFile(ref Utf8JsonReader reader)
    {
        Json.ExpectObject(ref reader);
        var state = new KeyedState();
        long schemaVersion = 0;
        ElementList elements = default;
        while (Json.NextProperty(ref reader, DrawingFileKeys, out var key))
        {
            if (!state.Claim(key, ref reader))
            {
                continue;
            }
            switch (key)
            {
                case 0: schemaVersion = state.Read(ref reader, key, 2, Json.ReadInt); break;
                case 1: elements = state.Read(ref reader, key, 2, ReadElementArray); break;
            }
        }
        state.Finish(DrawingFileKeys, 0b11);
        return new DrawingFile(schemaVersion, elements);
    }

    public static void WriteDrawingFile(SwiftJsonWriter writer, in DrawingFile value)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("elements"u8);
        writer.WriteStartArray();
        foreach (ref readonly var element in value.Elements)
        {
            WriteElement(writer, element);
        }
        writer.WriteEndArray();
        writer.WriteNumber("schemaVersion"u8, value.SchemaVersion);
        writer.WriteEndObject();
    }

    // SavedShape

    public static SavedShape ReadSavedShape(ref Utf8JsonReader reader)
    {
        Json.ExpectObject(ref reader);
        var state = new KeyedState();
        long schemaVersion = 0;
        Guid id = default;
        var name = "";
        DrawingFile drawing = default;
        while (Json.NextProperty(ref reader, SavedShapeKeys, out var key))
        {
            if (!state.Claim(key, ref reader))
            {
                continue;
            }
            switch (key)
            {
                case 0: schemaVersion = state.Read(ref reader, key, 4, Json.ReadInt); break;
                case 1: id = state.Read(ref reader, key, 4, Json.ReadUuid); break;
                case 2: name = state.Read(ref reader, key, 4, Json.ReadString) ?? ""; break;
                case 3: drawing = state.Read(ref reader, key, 4, ReadDrawingFile); break;
            }
        }
        state.Finish(SavedShapeKeys, 0b1111);
        return new SavedShape(schemaVersion, id, name, drawing);
    }

    public static void WriteSavedShape(SwiftJsonWriter writer, in SavedShape value)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("drawing"u8);
        WriteDrawingFile(writer, value.Drawing);
        Json.WriteUuid(writer, "id"u8, value.Id);
        writer.WriteString("name"u8, value.Name);
        writer.WriteNumber("schemaVersion"u8, value.SchemaVersion);
        writer.WriteEndObject();
    }

    // ActivationShortcut

    public static ActivationShortcut ReadShortcut(ref Utf8JsonReader reader)
    {
        Json.ExpectObject(ref reader);
        var state = new KeyedState();
        var shortcut = new ActivationShortcut(0, "");
        while (Json.NextProperty(ref reader, ShortcutKeys, out var key))
        {
            if (!state.Claim(key, ref reader))
            {
                continue;
            }
            switch (key)
            {
                case 0: shortcut.KeyCode = state.Read(ref reader, key, 6, Json.ReadUInt32); break;
                case 1: shortcut.Control = state.Read(ref reader, key, 6, Json.ReadBool); break;
                case 2: shortcut.Option = state.Read(ref reader, key, 6, Json.ReadBool); break;
                case 3: shortcut.Command = state.Read(ref reader, key, 6, Json.ReadBool); break;
                case 4: shortcut.Shift = state.Read(ref reader, key, 6, Json.ReadBool); break;
                case 5: shortcut.Character = state.Read(ref reader, key, 6, Json.ReadString) ?? ""; break;
            }
        }
        state.Finish(ShortcutKeys, 0b11_1111);
        return shortcut;
    }

    public static void WriteShortcut(SwiftJsonWriter writer, in ActivationShortcut value)
    {
        writer.WriteStartObject();
        writer.WriteString("character"u8, value.Character);
        writer.WriteBoolean("command"u8, value.Command);
        writer.WriteBoolean("control"u8, value.Control);
        writer.WriteNumber("keyCode"u8, value.KeyCode);
        writer.WriteBoolean("option"u8, value.Option);
        writer.WriteBoolean("shift"u8, value.Shift);
        writer.WriteEndObject();
    }

    // CursorHighlightSettings

    public static CursorHighlightSettings ReadCursorHighlight(ref Utf8JsonReader reader)
    {
        Json.ExpectObject(ref reader);
        var state = new KeyedState();
        var settings = new CursorHighlightSettings();
        while (Json.NextProperty(ref reader, CursorKeys, out var key))
        {
            if (!state.Claim(key, ref reader))
            {
                continue;
            }
            var isNull = Json.IsNull(ref reader);
            switch (key)
            {
                case 0: settings.Shape = state.Read(ref reader, key, 9, ReadCursorShape); break;
                case 1: settings.Visibility = state.Read(ref reader, key, 9, ReadCursorVisibility); break;
                case 2: settings.Color = state.Read(ref reader, key, 9, ReadRgba); break;
                case 3: settings.Diameter = state.Read(ref reader, key, 9, Json.ReadDouble); break;
                case 4: settings.BorderWidth = state.Read(ref reader, key, 9, Json.ReadDouble); break;
                case 5: settings.ShowClicks = state.Read(ref reader, key, 9, Json.ReadBool); break;
                case 6: settings.GlowRadius = isNull ? null : state.Read(ref reader, key, 9, Json.ReadDouble); break;
                case 7: settings.BorderStyle = isNull ? null : state.Read(ref reader, key, 9, ReadStrokePattern); break;
                case 8: settings.ClickEffect = isNull ? null : state.Read(ref reader, key, 9, ReadClickEffect); break;
            }
        }
        state.Finish(CursorKeys, 0b11_1111);
        return settings;
    }

    public static void WriteCursorHighlight(SwiftJsonWriter writer, in CursorHighlightSettings value)
    {
        writer.WriteStartObject();
        if (value.BorderStyle is { } borderStyle)
        {
            writer.WriteString("borderStyle"u8, borderStyle.RawValue);
        }
        Json.WriteDouble(writer, "borderWidth"u8, value.BorderWidth);
        if (value.ClickEffect is { } clickEffect)
        {
            writer.WriteString("clickEffect"u8, clickEffect.RawValue);
        }
        writer.WritePropertyName("color"u8);
        WriteRgba(writer, value.Color);
        Json.WriteDouble(writer, "diameter"u8, value.Diameter);
        if (value.GlowRadius is { } glow)
        {
            Json.WriteDouble(writer, "glowRadius"u8, glow);
        }
        writer.WriteString("shape"u8, value.Shape.RawValue);
        writer.WriteBoolean("showClicks"u8, value.ShowClicks);
        writer.WriteString("visibility"u8, value.Visibility.RawValue);
        writer.WriteEndObject();
    }

    // BreakTimerSettings

    /// <summary>Swift's custom decoder reads every key with <c>decodeIfPresent</c>, so none is required.</summary>
    public static BreakTimerSettings ReadBreakTimer(ref Utf8JsonReader reader)
    {
        Json.ExpectObject(ref reader);
        var state = new KeyedState();
        const int count = 9;
        var settings = new BreakTimerSettings();
        while (Json.NextProperty(ref reader, BreakTimerKeys, out var key))
        {
            if (!state.Claim(key, ref reader))
            {
                continue;
            }
            if (Json.IsNull(ref reader))
            {
                reader.Skip();
                continue;
            }
            switch (key)
            {
                case 0: settings.Minutes = state.Read(ref reader, key, count, Json.ReadInt); break;
                case 1: settings.Title = state.Read(ref reader, key, count, Json.ReadString) ?? BreakTimerSettings.DefaultTitle; break;
                case 2: settings.Background = state.Read(ref reader, key, count, ReadBreakBackground); break;
                case 3: settings.Palette = state.Read(ref reader, key, count, ReadBreakPalette); break;
                case 4: settings.ShowEndTime = state.Read(ref reader, key, count, Json.ReadBool); break;
                case 5: settings.Chime = state.Read(ref reader, key, count, Json.ReadBool); break;
                case 6: settings.CountOver = state.Read(ref reader, key, count, Json.ReadBool); break;
                case 7: settings.AllDisplays = state.Read(ref reader, key, count, Json.ReadBool); break;
                case 8: settings.LockWhenTouched = state.Read(ref reader, key, count, Json.ReadBool); break;
            }
        }
        state.Finish(BreakTimerKeys, 0);
        return settings;
    }

    public static void WriteBreakTimer(SwiftJsonWriter writer, in BreakTimerSettings value)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("allDisplays"u8, value.AllDisplays);
        writer.WriteString("background"u8, value.Background.RawValue);
        writer.WriteBoolean("chime"u8, value.Chime);
        writer.WriteBoolean("countOver"u8, value.CountOver);
        writer.WriteBoolean("lockWhenTouched"u8, value.LockWhenTouched);
        writer.WriteNumber("minutes"u8, value.Minutes);
        writer.WriteString("palette"u8, value.Palette.RawValue);
        writer.WriteBoolean("showEndTime"u8, value.ShowEndTime);
        writer.WriteString("title"u8, value.Title);
        writer.WriteEndObject();
    }

    // WorkspacePreferences

    public static WorkspacePreferences ReadPreferences(ref Utf8JsonReader reader)
    {
        Json.ExpectObject(ref reader);
        var state = new KeyedState();
        const int count = 31;
        // Swift decodes a missing optional as nil, even when the property has a default value.
        var preferences = new WorkspacePreferences { ShortcutVersion = null };
        while (Json.NextProperty(ref reader, PreferenceKeys, out var key))
        {
            if (!state.Claim(key, ref reader))
            {
                continue;
            }
            var isNull = Json.IsNull(ref reader);
            switch (key)
            {
                case 0: preferences.SchemaVersion = state.Read(ref reader, key, count, Json.ReadInt); break;
                case 1: preferences.ToolStyles = state.Read(ref reader, key, count, ReadStyleDictionary) ?? WorkspacePreferences.EmptyStyles; break;
                case 2: preferences.ToolShortcuts = state.Read(ref reader, key, count, ReadStringDictionary) ?? WorkspacePreferences.EmptyStrings; break;
                case 3: preferences.ShortcutVersion = isNull ? null : state.Read(ref reader, key, count, Json.ReadInt); break;
                case 4: preferences.ActivationShortcut = state.Read(ref reader, key, count, ReadShortcut); break;
                case 5: preferences.SmartDraw = state.Read(ref reader, key, count, Json.ReadBool); break;
                case 6: preferences.SpotlightRadius = state.Read(ref reader, key, count, Json.ReadDouble); break;
                case 7: preferences.HideShortcut = isNull ? null : state.Read(ref reader, key, count, ReadShortcut); break;
                case 8: preferences.ZoomShortcut = isNull ? null : state.Read(ref reader, key, count, ReadShortcut); break;
                case 9: preferences.ResumeShortcut = isNull ? null : state.Read(ref reader, key, count, ReadShortcut); break;
                case 10: preferences.ToolbarOrientation = isNull ? null : state.Read(ref reader, key, count, ReadOrientation); break;
                case 11: preferences.ToolbarDockEdge = isNull ? null : state.Read(ref reader, key, count, ReadDockEdge); break;
                case 12: preferences.LastCoverMode = isNull ? null : state.Read(ref reader, key, count, ReadTool); break;
                case 13: preferences.ToolbarTools = isNull ? null : state.Read(ref reader, key, count, ReadStringArray); break;
                case 14: preferences.ShowMenuBarIcon = isNull ? null : state.Read(ref reader, key, count, Json.ReadBool); break;
                case 15: preferences.CursorStyle = isNull ? null : state.Read(ref reader, key, count, ReadCursorStyle); break;
                case 16: preferences.QuickPreferencesExpanded = isNull ? null : state.Read(ref reader, key, count, Json.ReadBool); break;
                case 17: preferences.InspectorPinned = isNull ? null : state.Read(ref reader, key, count, Json.ReadBool); break;
                case 18: preferences.AnimateVisibility = isNull ? null : state.Read(ref reader, key, count, Json.ReadBool); break;
                case 19: preferences.VisibilityAnimationStyle = isNull ? null : state.Read(ref reader, key, count, ReadAnimationStyle); break;
                case 20: preferences.Theme = isNull ? null : state.Read(ref reader, key, count, ReadTheme); break;
                case 21: preferences.ShowShortcutHints = isNull ? null : state.Read(ref reader, key, count, Json.ReadBool); break;
                case 22: preferences.CursorHighlight = isNull ? null : state.Read(ref reader, key, count, ReadCursorHighlight); break;
                case 23: preferences.CommandShortcuts = isNull ? null : state.Read(ref reader, key, count, ReadStringDictionary); break;
                case 24: preferences.ToolbarSize = isNull ? null : state.Read(ref reader, key, count, ReadToolbarSize); break;
                case 25: preferences.ShareToolStyles = isNull ? null : state.Read(ref reader, key, count, Json.ReadBool); break;
                case 26: preferences.ToolbarItems = isNull ? null : state.Read(ref reader, key, count, ReadStringArray); break;
                case 27: preferences.BreakTimer = isNull ? null : state.Read(ref reader, key, count, ReadBreakTimer); break;
                case 28: preferences.BreakShortcut = isNull ? null : state.Read(ref reader, key, count, ReadShortcut); break;
                case 29: preferences.HiddenToolbarControls = isNull ? null : state.Read(ref reader, key, count, ReadStringArray); break;
                case 30: preferences.ToolbarCollapseDelay = isNull ? null : state.Read(ref reader, key, count, Json.ReadDouble); break;
                case 31: preferences.ExpandedPropertyChoices = isNull ? null : state.Read(ref reader, key, count, ReadStringArray); break;
            }
        }
        state.Finish(PreferenceKeys, 0b111_0111);
        return preferences;
    }

    public static void WritePreferences(SwiftJsonWriter writer, in WorkspacePreferences value)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("activationShortcut"u8);
        WriteShortcut(writer, value.ActivationShortcut);
        WriteOptional(writer, "animateVisibility"u8, value.AnimateVisibility);
        WriteOptional(writer, "breakShortcut"u8, value.BreakShortcut);
        if (value.BreakTimer is { } breakTimer)
        {
            writer.WritePropertyName("breakTimer"u8);
            WriteBreakTimer(writer, breakTimer);
        }
        if (value.CommandShortcuts is { } commandShortcuts)
        {
            writer.WritePropertyName("commandShortcuts"u8);
            WriteStringDictionary(writer, commandShortcuts);
        }
        if (value.CursorHighlight is { } cursorHighlight)
        {
            writer.WritePropertyName("cursorHighlight"u8);
            WriteCursorHighlight(writer, cursorHighlight);
        }
        if (value.CursorStyle is { } cursorStyle)
        {
            writer.WriteString("cursorStyle"u8, cursorStyle.RawValue);
        }
        WriteOptional(writer, "expandedPropertyChoices"u8, value.ExpandedPropertyChoices);
        WriteOptional(writer, "hiddenToolbarControls"u8, value.HiddenToolbarControls);
        WriteOptional(writer, "hideShortcut"u8, value.HideShortcut);
        WriteOptional(writer, "inspectorPinned"u8, value.InspectorPinned);
        if (value.LastCoverMode is { } lastCoverMode)
        {
            writer.WriteString("lastCoverMode"u8, lastCoverMode.RawValue);
        }
        WriteOptional(writer, "quickPreferencesExpanded"u8, value.QuickPreferencesExpanded);
        WriteOptional(writer, "resumeShortcut"u8, value.ResumeShortcut);
        writer.WriteNumber("schemaVersion"u8, value.SchemaVersion);
        WriteOptional(writer, "shareToolStyles"u8, value.ShareToolStyles);
        if (value.ShortcutVersion is { } shortcutVersion)
        {
            writer.WriteNumber("shortcutVersion"u8, shortcutVersion);
        }
        WriteOptional(writer, "showMenuBarIcon"u8, value.ShowMenuBarIcon);
        WriteOptional(writer, "showShortcutHints"u8, value.ShowShortcutHints);
        writer.WriteBoolean("smartDraw"u8, value.SmartDraw);
        Json.WriteDouble(writer, "spotlightRadius"u8, value.SpotlightRadius);
        if (value.Theme is { } theme)
        {
            writer.WriteString("theme"u8, theme.RawValue);
        }
        writer.WritePropertyName("toolShortcuts"u8);
        WriteStringDictionary(writer, value.ToolShortcuts);
        writer.WritePropertyName("toolStyles"u8);
        writer.WriteStartObject();
        foreach (var (tool, style) in value.ToolStyles)
        {
            writer.WritePropertyName(tool);
            WriteStyle(writer, style);
        }
        writer.WriteEndObject();
        if (value.ToolbarCollapseDelay is { } collapseDelay)
        {
            Json.WriteDouble(writer, "toolbarCollapseDelay"u8, collapseDelay);
        }
        if (value.ToolbarDockEdge is { } dockEdge)
        {
            writer.WriteString("toolbarDockEdge"u8, dockEdge.RawValue);
        }
        WriteOptional(writer, "toolbarItems"u8, value.ToolbarItems);
        if (value.ToolbarOrientation is { } orientation)
        {
            writer.WriteString("toolbarOrientation"u8, orientation.RawValue);
        }
        if (value.ToolbarSize is { } toolbarSize)
        {
            writer.WriteString("toolbarSize"u8, toolbarSize.RawValue);
        }
        WriteOptional(writer, "toolbarTools"u8, value.ToolbarTools);
        if (value.VisibilityAnimationStyle is { } animationStyle)
        {
            writer.WriteString("visibilityAnimationStyle"u8, animationStyle.RawValue);
        }
        WriteOptional(writer, "zoomShortcut"u8, value.ZoomShortcut);
        writer.WriteEndObject();
    }

    // Collections and enums

    private static ValueList<RGBA> ReadRgbaArray(ref Utf8JsonReader reader) => Json.ReadArray(ref reader, ReadRgba);

    private static ValueList<Point> ReadPointArray(ref Utf8JsonReader reader) => Json.ReadArray(ref reader, ReadPoint);

    private static ValueList<double> ReadDoubleArray(ref Utf8JsonReader reader) => Json.ReadArray(ref reader, Json.ReadDouble);

    private static ValueList<string> ReadStringArray(ref Utf8JsonReader reader) => Json.ReadArray(ref reader, Json.ReadString);

    private static ElementList ReadElementArray(ref Utf8JsonReader reader) => Json.ReadElements(ref reader, ReadElement);

    private static ImmutableSortedDictionary<string, Style> ReadStyleDictionary(ref Utf8JsonReader reader) =>
        Json.ReadDictionary(ref reader, ReadStyle);

    private static ImmutableSortedDictionary<string, string> ReadStringDictionary(ref Utf8JsonReader reader) =>
        Json.ReadDictionary(ref reader, Json.ReadString);

    private static Tool ReadTool(ref Utf8JsonReader reader) => Json.ReadEnum(ref reader, ToolExtensions.RawValues, "Tool");

    private static StrokePattern ReadStrokePattern(ref Utf8JsonReader reader) =>
        Json.ReadEnum(ref reader, StrokePatternExtensions.RawValues, "StrokePattern");

    private static FillPattern ReadFillPattern(ref Utf8JsonReader reader) =>
        Json.ReadEnum(ref reader, FillPatternExtensions.RawValues, "FillPattern");

    private static TextAlignment ReadAlignment(ref Utf8JsonReader reader) =>
        Json.ReadEnum(ref reader, TextAlignmentExtensions.RawValues, "TextAlignment");

    private static ArrowHead ReadArrowHead(ref Utf8JsonReader reader) =>
        Json.ReadEnum(ref reader, ArrowHeadExtensions.RawValues, "ArrowHead");

    private static CursorHighlightShape ReadCursorShape(ref Utf8JsonReader reader) =>
        Json.ReadEnum(ref reader, CursorHighlightShapeExtensions.RawValues, "Shape");

    private static CursorHighlightVisibility ReadCursorVisibility(ref Utf8JsonReader reader) =>
        Json.ReadEnum(ref reader, CursorHighlightVisibilityExtensions.RawValues, "Visibility");

    private static CursorClickEffect ReadClickEffect(ref Utf8JsonReader reader) =>
        Json.ReadEnum(ref reader, CursorClickEffectExtensions.RawValues, "ClickEffect");

    private static ToolbarOrientation ReadOrientation(ref Utf8JsonReader reader) =>
        Json.ReadEnum(ref reader, ToolbarOrientationExtensions.RawValues, "ToolbarOrientation");

    private static ToolbarDockEdge ReadDockEdge(ref Utf8JsonReader reader) =>
        Json.ReadEnum(ref reader, ToolbarDockEdgeExtensions.RawValues, "ToolbarDockEdge");

    private static VisibilityAnimationStyle ReadAnimationStyle(ref Utf8JsonReader reader) =>
        Json.ReadEnum(ref reader, VisibilityAnimationStyleExtensions.RawValues, "VisibilityAnimationStyle");

    private static AppTheme ReadTheme(ref Utf8JsonReader reader) => Json.ReadEnum(ref reader, AppThemeExtensions.RawValues, "AppTheme");

    private static ToolCursorStyle ReadCursorStyle(ref Utf8JsonReader reader) =>
        Json.ReadEnum(ref reader, ToolCursorStyleExtensions.RawValues, "ToolCursorStyle");

    private static BreakBackground ReadBreakBackground(ref Utf8JsonReader reader) =>
        Json.ReadEnum(ref reader, BreakBackgroundExtensions.RawValues, "Background");

    private static BreakPalette ReadBreakPalette(ref Utf8JsonReader reader) =>
        Json.ReadEnum(ref reader, BreakPaletteExtensions.RawValues, "Palette");

    private static ToolbarSize ReadToolbarSize(ref Utf8JsonReader reader) =>
        Json.ReadEnum(ref reader, ToolbarSizeExtensions.RawValues, "ToolbarSize");

    private static void WriteStringDictionary(SwiftJsonWriter writer, ImmutableSortedDictionary<string, string> dictionary)
    {
        writer.WriteStartObject();
        foreach (var (key, value) in dictionary)
        {
            writer.WriteString(key, value);
        }
        writer.WriteEndObject();
    }

    private static void WriteOptional(SwiftJsonWriter writer, ReadOnlySpan<byte> name, bool? value)
    {
        if (value is { } flag)
        {
            writer.WriteBoolean(name, flag);
        }
    }

    private static void WriteOptional(SwiftJsonWriter writer, ReadOnlySpan<byte> name, ActivationShortcut? value)
    {
        if (value is { } shortcut)
        {
            writer.WritePropertyName(name);
            WriteShortcut(writer, shortcut);
        }
    }

    private static void WriteOptional(SwiftJsonWriter writer, ReadOnlySpan<byte> name, ValueList<string>? value)
    {
        if (value is { } items)
        {
            writer.WritePropertyName(name);
            writer.WriteStartArray();
            foreach (var item in items)
            {
                writer.WriteStringValue(item);
            }
            writer.WriteEndArray();
        }
    }
}
