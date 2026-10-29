// New for the Windows port: JSON written the way the Mac's JSONEncoder writes it (board and saved-shape files
// use .sortedKeys; numbers drop a trailing ".0"; UUIDs are uppercase; Data is Base64), plus refusal messages.
using System.Text;
using static Annotator.Core.Tests.TestSupport;

namespace Annotator.Core.Tests;

public class JsonCompatibilityTests
{
    private const string DefaultStyle =
        """{"alignment":"left","bold":false,"color":{"alpha":1,"blue":0.22,"green":0.27,"red":0.94},"cornerRadius":0,"curved":false,"endHead":"open","fillPattern":"solid","fontName":"system","fontSize":28,"opacity":1,"pattern":"solid","pressure":false,"roughness":0,"startHead":"none","width":3}""";

    private const string ArrowStyle =
        """{"alignment":"left","bold":false,"color":{"alpha":1,"blue":0.22,"green":0.27,"red":0.94},"cornerRadius":0,"curved":false,"endHead":"open","fillPattern":"solid","fontName":"system","fontSize":28,"headScale":0.75,"opacity":1,"pattern":"solid","pressure":false,"roughness":0,"startHead":"none","width":3}""";

    private const string LabelStyle =
        """{"alignment":"center","bold":false,"color":{"alpha":1,"blue":0.16,"green":0.15,"red":0.12},"cornerRadius":0,"curved":false,"endHead":"open","fillPattern":"solid","fontName":"system","fontSize":28,"opacity":1,"pattern":"solid","pressure":false,"roughness":0,"startHead":"none","width":3}""";

    private const string RectangleJson =
        """{"appendEpoch":"0E7A2F44-5B8E-4C2B-9A43-6C1D0F5E2B11","frame":{"height":80,"width":120.5,"x":10,"y":-20.25},"geometryToken":"5D7C9B2A-1E3F-4A6B-8C9D-0E1F2A3B4C5D","id":"3F2504E0-4F89-41D3-9A0C-0305E82C3301","locked":false,"points":[],"pressures":[],"revision":3,"rotation":0,"seed":15934714562267240775,"style":"""
        + DefaultStyle + ""","text":"","tool":"rectangle"}""";

    private const string ArrowJson =
        """{"appendEpoch":"1B4E28BA-2FA1-11D2-883F-0016D3CCA427","frame":{"height":0,"width":169.5,"x":130.5,"y":19.75},"geometryToken":"2C5EA4C0-4067-11D9-8BAD-0002A5D5C51B","groupID":"9B2C4D6E-8F10-4A2B-BC3D-4E5F60718293","id":"8A6E0804-2BD0-4672-B79D-D97027F9071A","locked":true,"points":[{"x":130.5,"y":19.75},{"x":300,"y":19.75}],"pressures":[],"revision":0,"rotation":0,"seed":5356633016190504591,"startAnchor":{"elementID":"3F2504E0-4F89-41D3-9A0C-0305E82C3301","relative":{"x":1,"y":0.5}},"style":"""
        + ArrowStyle + ""","text":"","tool":"arrow"}""";

    private const string PenJson =
        """{"appendEpoch":"7D444840-9DC0-11D1-B245-5FFDCE74FAD2","frame":{"height":2,"width":2.5,"x":1,"y":2},"geometryToken":"E902893A-9D22-3C7E-A7B8-D6E313B71D9F","id":"6F9619FF-8B86-D011-B42D-00C04FC964FF","locked":false,"points":[{"x":1,"y":2},{"x":3.5,"y":4}],"pressures":[1,0.5],"revision":7,"rotation":0,"seed":10485578433175362915,"style":"""
        + DefaultStyle + ""","text":"Note","tool":"pen"}""";

    private const string BoardV1 = """{"elements":[""" + RectangleJson + "," + ArrowJson + "," + PenJson + """],"schemaVersion":1}""";

    private const string LabeledBoardV2 =
        """{"elements":[{"appendEpoch":"A1B2C3D4-E5F6-4789-8ABC-DEF012345678","frame":{"height":120,"width":160,"x":40,"y":50},"geometryToken":"B1C2D3E4-F5A6-4789-9ABC-DEF012345679","id":"C1D2E3F4-A5B6-4789-8ABC-DEF01234567A","label":{"style":"""
        + LabelStyle
        + ""","text":"Orders"},"locked":false,"points":[],"pressures":[],"revision":2,"rotation":0,"seed":42,"style":"""
        + DefaultStyle + ""","text":"","tool":"database"}],"schemaVersion":2}""";

    private const string GradientBoardV3 =
        """{"elements":[{"appendEpoch":"A1B2C3D4-E5F6-4789-8ABC-DEF012345678","frame":{"height":60,"width":100,"x":0,"y":0},"geometryToken":"B1C2D3E4-F5A6-4789-9ABC-DEF012345679","id":"C1D2E3F4-A5B6-4789-8ABC-DEF01234567A","label":{"followsStroke":true,"style":"""
        + LabelStyle
        + ""","text":"Follow me"},"locked":false,"points":[],"pressures":[],"revision":0,"rotation":0,"seed":42,"style":{"alignment":"left","bold":false,"color":{"alpha":1,"blue":0.67,"green":0.12,"red":0.96},"cornerRadius":0,"curved":false,"endHead":"open","fillPattern":"solid","fontName":"system","fontSize":28,"opacity":1,"pattern":"solid","pressure":false,"roughness":0,"startHead":"none","strokeGradient":{"angle":25,"colors":[{"alpha":1,"blue":0.67,"green":0.12,"red":0.96},{"alpha":1,"blue":0.39,"green":0.23,"red":1},{"alpha":1,"blue":0.2,"green":0.48,"red":1}]},"width":3},"text":"","tool":"rectangle"}],"schemaVersion":3}""";

    private const string ImageBoardV1 =
        """{"elements":[{"appendEpoch":"A1B2C3D4-E5F6-4789-8ABC-DEF012345678","frame":{"height":40,"width":30,"x":5,"y":6},"geometryToken":"B1C2D3E4-F5A6-4789-9ABC-DEF012345679","id":"C1D2E3F4-A5B6-4789-8ABC-DEF01234567A","imagePNG":"iVBORw0KGgoAAAAN","locked":false,"points":[],"pressures":[],"revision":0,"rotation":0,"seed":42,"style":"""
        + DefaultStyle + ""","text":"","tool":"image"}],"schemaVersion":1}""";

    private const string SavedShapeV1 =
        """{"drawing":{"elements":[""" + RectangleJson + """],"schemaVersion":1},"id":"C56A4180-65AA-42EC-A945-5FD21DEC0538","name":"Saved box","schemaVersion":1}""";

    // Preferences are written without .sortedKeys on the Mac, so key order varies; this one uses declaration order.
    private const string ProfileV1 =
        """{"schemaVersion":1,"toolStyles":{"pen":""" + DefaultStyle + """},"toolShortcuts":{"1":"select","2":"rectangle","3":"diamond","4":"ellipse","5":"arrow","6":"line","7":"pen","8":"text","9":"highlighter","0":"eraser","u":"blur","s":"cover","x":"pixelate","t":"distort","m":"ruler","n":"counter","i":"image","d":"database","q":"queue","h":"person","f":"grid","a":"axes"},"shortcutVersion":2,"activationShortcut":{"keyCode":19,"control":true,"option":false,"command":false,"shift":false,"character":"2"},"smartDraw":false,"spotlightRadius":130,"theme":"dark","cursorHighlight":{"shape":"ring","visibility":"moving","color":{"red":1,"green":0.82,"blue":0,"alpha":1},"diameter":36,"borderWidth":3,"showClicks":true,"glowRadius":4.5}}""";

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void BoardWrittenByTheMacDecodesAndReencodesByteForByte()
    {
        var document = DrawingFile.Read(Utf8(BoardV1));
        Assert.Equal(3, document.Elements.Count);
        var rectangle = document.Elements[0];
        Assert.Equal(Guid.Parse("3F2504E0-4F89-41D3-9A0C-0305E82C3301"), rectangle.Id);
        Assert.Equal(Tool.Rectangle, rectangle.Tool);
        Assert.Equal(new Rect(10, -20.25, 120.5, 80), rectangle.Frame);
        Assert.Equal(15934714562267240775UL, rectangle.Seed);
        Assert.Equal(3UL, rectangle.Revision);
        Assert.Equal(new Style(), rectangle.Style);
        var arrow = document.Elements[1];
        Assert.Equal(new Anchor(rectangle.Id, new Point(1, 0.5)), arrow.StartAnchor);
        Assert.Null(arrow.EndAnchor);
        Assert.Equal(Guid.Parse("9B2C4D6E-8F10-4A2B-BC3D-4E5F60718293"), arrow.GroupId);
        Assert.True(arrow.Locked);
        Assert.Equal(Style.Defaults(Tool.Arrow), arrow.Style);
        var pen = document.Elements[2];
        Assert.Equal(new[] { 1, 0.5 }, pen.Pressures);
        Assert.Equal("Note", pen.Text);
        Assert.Equal(BoardV1, Encoding.UTF8.GetString(new DrawingFile(document).Data()));
    }

    [Fact]
    public void SeedsMatchTheSwiftFormulaOverTheUppercaseUuid()
    {
        // Values computed independently: djb2 over the uppercase uuidString, wrapping at 2^64.
        Assert.Equal(15934714562267240775UL, Element.SeedFor(Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301")));
        Assert.Equal(5356633016190504591UL, Element.SeedFor(Guid.Parse("8A6E0804-2BD0-4672-B79D-D97027F9071A")));
        Assert.Equal(10485578433175362915UL, Element.SeedFor(Guid.Parse("6F9619FF-8B86-D011-B42D-00C04FC964FF")));
        Assert.Equal(13796826776842752249UL, Element.SeedFor(Guid.Empty));
        var element = new Element(Tool.Pen, id: Guid.Parse("6F9619FF-8B86-D011-B42D-00C04FC964FF"));
        Assert.Equal(10485578433175362915UL, element.Seed);
    }

    [Fact]
    public void LabeledAndGradientBoardsKeepTheirFormats()
    {
        var labeled = DrawingFile.Read(Utf8(LabeledBoardV2));
        Assert.Equal(new ShapeLabel("Orders"), labeled.Elements[0].Label);
        Assert.Equal(2, new DrawingFile(labeled).SchemaVersion);
        Assert.Equal(LabeledBoardV2, Encoding.UTF8.GetString(new DrawingFile(labeled).Data()));

        var gradient = DrawingFile.Read(Utf8(GradientBoardV3));
        var expected = new Style();
        expected.SetGradient(GradientPreset.Sunset.Gradient, fill: false);
        Assert.Equal(expected, gradient.Elements[0].Style);
        Assert.Equal(new ShapeLabel("Follow me", followsStroke: true), gradient.Elements[0].Label);
        Assert.Equal(3, new DrawingFile(gradient).SchemaVersion);
        Assert.Equal(GradientBoardV3, Encoding.UTF8.GetString(new DrawingFile(gradient).Data()));
    }

    [Fact]
    public void ImageDataIsBase64AndRoundTrips()
    {
        var document = DrawingFile.Read(Utf8(ImageBoardV1));
        var image = Assert.NotNull(document.Elements[0].ImagePng);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13 }, image.ToArray());
        Assert.Equal(ImageBoardV1, Encoding.UTF8.GetString(new DrawingFile(document).Data()));
        var broken = ImageBoardV1.Replace("iVBORw0KGgoAAAAN", "iVBORw0KGgoAAAA", StringComparison.Ordinal);
        var error = Assert.Throws<DecodingError>(() => DrawingFile.Read(Utf8(broken)));
        Assert.Equal(DecodingErrorKind.DataCorrupted, error.Kind);
    }

    [Fact]
    public void SavedShapeFileRoundTripsByteForByte()
    {
        var shape = SavedShape.Read(Utf8(SavedShapeV1));
        Assert.Equal("Saved box", shape.Name);
        Assert.Equal(Guid.Parse("C56A4180-65AA-42EC-A945-5FD21DEC0538"), shape.Id);
        Assert.Single(shape.Elements);
        Assert.Equal(SavedShapeV1, Encoding.UTF8.GetString(shape.Data()));
    }

    [Fact]
    public void ProfileWrittenByTheMacDecodesInAnyKeyOrder()
    {
        var profile = WorkspacePreferences.Read(Utf8(ProfileV1));
        var expected = new WorkspacePreferences();
        // Written before the laser pointer had its L key: a saved profile keeps its own keys.
        expected.ToolShortcuts = expected.ToolShortcuts.Remove("l");
        expected.ToolStyles = expected.ToolStyles.SetItem("pen", new Style());
        expected.Theme = AppTheme.Dark;
        var halo = new CursorHighlightSettings();
        halo.Visibility = CursorHighlightVisibility.Moving;
        halo.GlowRadius = 4.5;
        expected.CursorHighlight = halo;
        Assert.Equal(expected, profile);
        Assert.Equal(profile, WorkspacePreferences.Read(profile.Data()));
    }

    [Fact]
    public void NewerOrUnknownSchemasAreRefusedWithTheMacMessages()
    {
        var board = Assert.Throws<CanvasError>(() => DrawingFile.Read("""{"schemaVersion":4,"elements":[]}"""u8));
        Assert.Equal("Drawing format 4 is not supported by this version.", board.Message);
        Assert.Equal(CanvasError.UnsupportedSchema(0), Assert.Throws<CanvasError>(() => DrawingFile.Read("""{"schemaVersion":0,"elements":[]}"""u8)));

        var labelInV1 = Assert.Throws<CanvasError>(() => DrawingFile.Read(Utf8(LabeledBoardV2.Replace("\"schemaVersion\":2", "\"schemaVersion\":1", StringComparison.Ordinal))));
        Assert.Equal("The drawing could not be opened: Shape labels require board format 2.", labelInV1.Message);
        var gradientInV2 = Assert.Throws<CanvasError>(() => DrawingFile.Read(Utf8(GradientBoardV3.Replace("\"schemaVersion\":3", "\"schemaVersion\":2", StringComparison.Ordinal))));
        Assert.Equal("The drawing could not be opened: Gradients and matching labels need board format 3.", gradientInV2.Message);

        var profile = Assert.Throws<CanvasError>(() => WorkspacePreferences.Read(Utf8(ProfileV1.Replace("\"schemaVersion\":1", "\"schemaVersion\":3", StringComparison.Ordinal))));
        Assert.Equal("Drawing format 3 is not supported by this version.", profile.Message);
        var gradientProfile = ProfileV1.Replace("\"toolStyles\":{\"pen\":" + DefaultStyle,
            "\"toolStyles\":{\"pen\":" + DefaultStyle.Replace("\"width\":3", "\"strokeGradient\":{\"angle\":25,\"colors\":[{\"alpha\":1,\"blue\":0,\"green\":0,\"red\":1},{\"alpha\":1,\"blue\":1,\"green\":0,\"red\":0}]},\"width\":3", StringComparison.Ordinal),
            StringComparison.Ordinal);
        var oldProfile = Assert.Throws<CanvasError>(() => WorkspacePreferences.Read(Utf8(gradientProfile)));
        Assert.Equal("The drawing could not be opened: Gradient colors need a newer profile format.", oldProfile.Message);
        Assert.Equal(2, WorkspacePreferences.Read(Utf8(gradientProfile.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal))).SchemaVersion);

        var shape = Assert.Throws<ShapeLibraryError>(() => SavedShape.Read(Utf8(SavedShapeV1.Replace("\"name\":\"Saved box\",\"schemaVersion\":1", "\"name\":\"Saved box\",\"schemaVersion\":2", StringComparison.Ordinal))));
        Assert.Equal("My shapes could not read a saved shape. This format needs a newer version of Annotator.", shape.Message);
        var innerBoard = Assert.Throws<CanvasError>(() => SavedShape.Read(Utf8(SavedShapeV1.Replace("]," + "\"schemaVersion\":1},", "],\"schemaVersion\":4},", StringComparison.Ordinal))));
        Assert.Equal(CanvasError.UnsupportedSchema(4), innerBoard);
    }

    [Fact]
    public void DecodingErrorsUseTheFoundationMessagesInSwiftOrder()
    {
        var unknownTool = Assert.Throws<DecodingError>(() => DrawingFile.Read(Utf8(BoardV1.Replace("\"tool\":\"pen\"", "\"tool\":\"lasso\"", StringComparison.Ordinal))));
        Assert.Equal(DecodingErrorKind.DataCorrupted, unknownTool.Kind);
        Assert.Equal("The data couldn\u2019t be read because it isn\u2019t in the correct format.", unknownTool.Message);

        // Decoding finishes before the schema check, so a newer file without elements reports the missing key.
        var missing = Assert.Throws<DecodingError>(() => DrawingFile.Read("""{"schemaVersion":99}"""u8));
        Assert.Equal(DecodingErrorKind.KeyNotFound, missing.Kind);
        Assert.Equal("The data couldn\u2019t be read because it is missing.", missing.Message);

        var nullRequired = Assert.Throws<DecodingError>(() => DrawingFile.Read("""{"schemaVersion":null,"elements":[]}"""u8));
        Assert.Equal(DecodingErrorKind.ValueNotFound, nullRequired.Kind);

        // Keys are decoded in Swift declaration order: a missing "id" is reported before a bad "tool".
        var order = BoardV1.Replace("\"id\":\"6F9619FF-8B86-D011-B42D-00C04FC964FF\",", "", StringComparison.Ordinal)
            .Replace("\"tool\":\"pen\"", "\"tool\":5", StringComparison.Ordinal);
        Assert.Equal(DecodingErrorKind.KeyNotFound, Assert.Throws<DecodingError>(() => DrawingFile.Read(Utf8(order))).Kind);
        var typeFirst = BoardV1.Replace("\"id\":\"6F9619FF-8B86-D011-B42D-00C04FC964FF\"", "\"id\":7", StringComparison.Ordinal)
            .Replace("\"tool\":\"pen\",", "", StringComparison.Ordinal);
        Assert.Equal(DecodingErrorKind.TypeMismatch, Assert.Throws<DecodingError>(() => DrawingFile.Read(Utf8(typeFirst))).Kind);

        // Syntax is checked for the whole input first, like JSONDecoder.
        var syntax = Assert.Throws<DecodingError>(() => DrawingFile.Read("""{"elements":[] , "schemaVersion":1} trailing"""u8));
        Assert.Equal(DecodingErrorKind.DataCorrupted, syntax.Kind);
        Assert.Equal(DecodingErrorKind.DataCorrupted, Assert.Throws<DecodingError>(() => DrawingFile.Read("""{"elements":[}"""u8)).Kind);
        Assert.Equal(DecodingErrorKind.DataCorrupted, Assert.Throws<DecodingError>(() => DrawingFile.Read(""u8)).Kind);

        var missingStyleKey = Assert.Throws<DecodingError>(() => DrawingFile.Read(Utf8(BoardV1.Replace("\"fillPattern\":\"solid\",", "", StringComparison.Ordinal))));
        Assert.Equal(DecodingErrorKind.KeyNotFound, missingStyleKey.Kind);
        Assert.Equal(DecodingErrorKind.DataCorrupted, Assert.Throws<DecodingError>(() => DrawingFile.Read("""{"schemaVersion":1.5,"elements":[]}"""u8)).Kind);
        Assert.Equal(DecodingErrorKind.DataCorrupted, Assert.Throws<DecodingError>(() => DrawingFile.Read(Utf8(BoardV1.Replace("\"id\":\"6F9619FF-8B86-D011-B42D-00C04FC964FF\"", "\"id\":\"6F9619FF8B86D011B42D00C04FC964FF\"", StringComparison.Ordinal)))).Kind);
    }

    [Fact]
    public void DecodingAcceptsWhatTheSwiftDecoderAccepts()
    {
        // Lowercase UUIDs, integral numbers with fractions or exponents, unknown keys, a UTF-8 byte order mark,
        // null optionals and duplicate keys (the first one wins).
        var relaxed = BoardV1
            .Replace("3F2504E0-4F89-41D3-9A0C-0305E82C3301", "3f2504e0-4f89-41d3-9a0c-0305e82c3301", StringComparison.Ordinal)
            .Replace("\"frame\":{\"height\":80,", "\"frame\":{\"future\":[1,{\"a\":null}],\"height\":8.0e1,", StringComparison.Ordinal)
            .Replace("\"schemaVersion\":1}", "\"schemaVersion\":1.0,\"schemaVersion\":7}", StringComparison.Ordinal)
            .Replace("\"locked\":false,\"points\":[],", "\"label\":null,\"locked\":false,\"points\":[],", StringComparison.Ordinal);
        var withMark = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Utf8(relaxed)).ToArray();
        var document = DrawingFile.Read(withMark);
        Assert.Equal(DrawingFile.Read(Utf8(BoardV1)).Elements, document.Elements);
        Assert.Equal(BoardV1, Encoding.UTF8.GetString(new DrawingFile(document).Data()));
    }

    [Fact]
    public void NonFiniteNumbersAreNotWritten()
    {
        var file = new DrawingFile(new CanvasDocument([new Element(Tool.Rectangle, new Rect(0, 0, 10, 10))]));
        var element = file.Elements[0];
        element.Rotation = double.NaN;
        file.Elements = [element];
        var error = Assert.Throws<EncodingError>(() => file.Data());
        Assert.Equal("The data couldn\u2019t be written because it isn\u2019t in the correct format.", error.Message);
    }

    [Fact]
    public void WrittenIdentifiersAreUppercaseAndTheClipboardNameIsShared()
    {
        var element = new Element(Tool.Rectangle, new Rect(0, 0, 10, 10), id: Guid.Parse("c56a4180-65aa-42ec-a945-5fd21dec0538"));
        var text = Encoding.UTF8.GetString(new DrawingFile(new CanvasDocument([element])).Data());
        Assert.Contains("\"id\":\"C56A4180-65AA-42EC-A945-5FD21DEC0538\"", text, StringComparison.Ordinal);
        Assert.Equal("app.annotator.annotations", AnnotationClipboard.TypeName);
    }

    [Fact]
    public void CodableTypesRoundTripThroughAnnotatorJson()
    {
        Assert.Equal("""{"x":1.5,"y":-2}""", Encoding.UTF8.GetString(AnnotatorJson.Encode(new Point(1.5, -2))));
        Assert.Equal(new Rect(1, 2, 3, 4), AnnotatorJson.Decode<Rect>("""{"y":2,"x":1,"height":4,"width":3}"""u8));
        Assert.Equal("""{"character":"2","command":false,"control":true,"keyCode":19,"option":false,"shift":false}""",
            Encoding.UTF8.GetString(AnnotatorJson.Encode(ActivationShortcut.Standard)));
        var label = new ShapeLabel("A", followsStroke: false);
        Assert.Equal(label, AnnotatorJson.Decode<ShapeLabel>(AnnotatorJson.Encode(label)));
        var gradient = GradientPreset.Ocean.Gradient;
        Assert.Equal(gradient, AnnotatorJson.Decode<ColorGradient>(AnnotatorJson.Encode(gradient)));
        var element = new Element(Tool.Arrow, points: [new(0, 0), new(10, 0)]);
        Assert.Equal(element, AnnotatorJson.Decode<Element>(AnnotatorJson.Encode(element)));
        Assert.Throws<NotSupportedException>(() => AnnotatorJson.Encode("text"));
    }
}
