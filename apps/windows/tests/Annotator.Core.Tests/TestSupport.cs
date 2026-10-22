using System.Text;
using System.Text.Json.Nodes;

namespace Annotator.Core.Tests;

/// <summary>Helpers that stand in for Swift Testing's `#expect(throws:)` and `JSONSerialization`.</summary>
internal static class TestSupport
{
    public static void AssertCanvasError(CanvasError expected, Action action)
    {
        var error = Assert.Throws<CanvasError>(action);
        Assert.Equal(expected, error);
        Assert.Equal(expected.Message, error.Message);
    }

    public static void AssertShapeError(ShapeLibraryError expected, Action action)
    {
        var error = Assert.Throws<ShapeLibraryError>(action);
        Assert.Equal(expected, error);
    }

    public static async Task AssertShapeErrorAsync(ShapeLibraryError expected, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<ShapeLibraryError>(action);
        Assert.Equal(expected, error);
    }

    public static JsonObject JsonObjectOf(byte[] data) => JsonNode.Parse(data)!.AsObject();

    public static byte[] Bytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());

    public static JsonObject JsonObjectOf(IEnumerable<KeyValuePair<string, string>> dictionary)
    {
        var result = new JsonObject();
        foreach (var (key, value) in dictionary)
        {
            result[key] = value;
        }
        return result;
    }

    public static ValueList<string> Strings(params string[] items) => ValueList.Create<string>(items);

    public static byte[] Repeated(byte value, int count)
    {
        var bytes = new byte[count];
        Array.Fill(bytes, value);
        return bytes;
    }

    /// <summary>A folder under the test output folder, so tests never write to the system temporary folder.</summary>
    public static string ScratchDirectory(string prefix)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "scratch", $"{prefix}-{Guid.NewGuid()}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    public static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
