using System.Text;
using System.Text.Json.Nodes;
using Annotator.Core;

namespace Annotator.Conformance.Tests;

/// <summary>
/// Section <c>boards</c>: valid board files are read, written back with random IDs renamed, and compared with the
/// bytes the Mac wrote (an equal model is required, WIN-CORE-02; equal bytes are the goal); invalid files and the
/// recipes for limits too large to commit must fail with the exact message.
/// </summary>
public class BoardsTests
{
    public static TheoryData<string> ValidCases => CorpusCases.Of("boards/valid/index.jsonl");

    [Theory]
    [MemberData(nameof(ValidCases))]
    public void ValidBoardsReadAndWriteAsOnTheMac(string name)
    {
        var record = CorpusCases.Record(name);
        OpenDifferences.Check(CorpusCases.Id(name), () =>
        {
            var input = Corpus.Bytes("boards/valid/" + record["file"]!.GetValue<string>());
            var expected = Corpus.Bytes("boards/valid/" + record["written"]!.GetValue<string>());
            var fresh = new FreshIds(InputText(input));
            var declared = AnnotatorJson.Decode<DrawingFile>(input).SchemaVersion;
            var document = DrawingFile.Read(input);
            var output = new DrawingFile(new CanvasDocument(fresh.Rename(document.Elements))).Data();
            var written = AnnotatorJson.Decode<DrawingFile>(output).SchemaVersion;
            var actual = record.DeepClone().AsObject();
            actual["declaredSchema"] = declared;
            actual["elementCount"] = document.Elements.Count;
            actual["writtenEqualsInput"] = output.AsSpan().SequenceEqual(input);
            actual["writtenSchema"] = written;
            JsonDiff.Equal(record, actual, NumberRule.Exact, "record");

            // The model must be equal; the bytes are the goal of the Windows codecs.
            var mac = DrawingFile.Read(expected);
            var windows = DrawingFile.Read(output);
            Assert.True(mac.Elements.Equals(windows.Elements), "The written board reads back as a different model than the Mac's.");
            Assert.Equal(Encoding.UTF8.GetString(expected), Encoding.UTF8.GetString(output));
        });
    }

    public static TheoryData<string> InvalidCases => CorpusCases.Of("boards/invalid/index.jsonl");

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void InvalidBoardsFailWithTheMacMessage(string name)
    {
        var record = CorpusCases.Record(name);
        OpenDifferences.Check(CorpusCases.Id(name), () =>
        {
            JsonObject outcome;
            if (record["file"] is { } file)
            {
                var input = Corpus.Bytes("boards/invalid/" + file.GetValue<string>());
                outcome = Outcome.Of(() => { _ = DrawingFile.Read(input); return null; });
            }
            else
            {
                outcome = Outcome.Of(() => { Recipe(record["name"]!.GetValue<string>()); return null; });
            }
            Assert.True(outcome.ContainsKey("error"), "The invalid board opened without an error.");
            JsonDiff.Equal(record["error"], outcome["error"], NumberRule.Exact, "error");
        });
    }

    /// <summary>The exporter's recipes for limits too large to commit (apps/macos/Tools/Conformance/BoardsInvalid.swift).</summary>
    private static void Recipe(string name)
    {
        switch (name)
        {
            case "file-over-128-mb":
                _ = DrawingFile.Read(new byte[128 * 1_024 * 1_024 + 1]);
                break;
            case "text-over-1-mb":
                _ = new CanvasDocument([Fixture.Element(90, Tool.Text, new Rect(0, 0, 40, 20), text: new string('a', 1_000_001))]);
                break;
            case "label-over-1-mb":
                _ = new CanvasDocument([Broken(91, Tool.Rectangle, (ref Element element) => element.Label = new ShapeLabel(new string('a', 1_000_001)))]);
                break;
            case "image-over-32-mb":
            {
                var image = new byte[32 * 1_024 * 1_024 + 1];
                ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
                signature.CopyTo(image);
                _ = new CanvasDocument([Broken(92, Tool.Image, (ref Element element) => element.ImagePng = ValueList.Create<byte>(image))]);
                break;
            }
            default:
                throw new InvalidDataException($"Unknown recipe {name}.");
        }
    }

    /// <summary>The exporter's <c>broken</c>: an 80 x 60 element at (10, 10); lines and strokes get two points.</summary>
    private static Element Broken(long number, Tool tool, ElementMutation change)
    {
        var element = Fixture.Element(number, tool, new Rect(10, 10, 80, 60));
        if (tool.IsFreehand || tool.IsConnector || tool == Tool.Ruler)
        {
            element.Points = [new Point(10, 10), new Point(90, 70)];
        }
        change(ref element);
        return element;
    }

    /// <summary>The input as text, as the exporter reads it to keep the UUIDs it holds: UTF-8, or UTF-16 with its mark.</summary>
    internal static string InputText(byte[] input)
    {
        if (input.Length >= 2 && input[0] == 0xFF && input[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(input, 2, input.Length - 2);
        }
        if (input.Length >= 2 && input[0] == 0xFE && input[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(input, 2, input.Length - 2);
        }
        return Encoding.UTF8.GetString(input);
    }
}
