// Port of apps/macos/Sources/AnnotationCore/InkShortcuts.swift.
using System.Collections.Frozen;

namespace Annotator.Core;

/// <summary>A key that changes the ink color while drawing.</summary>
public readonly record struct InkShortcut(string Name, RGBA Color)
{
    /// <summary>The reserved ink keys. Tools and buttons cannot use them.</summary>
    public static FrozenDictionary<string, InkShortcut> Choices { get; } = new Dictionary<string, InkShortcut>(StringComparer.Ordinal)
    {
        ["r"] = new("Red", Palette.Coral),
        ["g"] = new("Green", new RGBA(0.28, 0.69, 0.4)),
        ["b"] = new("Blue", Palette.Blue),
        ["y"] = new("Yellow", Palette.Yellow),
        ["o"] = new("Orange", new RGBA(1, 0.66, 0.23)),
        ["p"] = new("Pink", new RGBA(0.89, 0.26, 0.4)),
        ["c"] = new("Cyan", new RGBA(0, 0.6, 0.7)),
        ["w"] = new("White", Palette.White),
        ["k"] = new("Black", new RGBA(0, 0, 0)),
    }.ToFrozenDictionary(StringComparer.Ordinal);
}
