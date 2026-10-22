// Port of apps/macos/Sources/AnnotationCore/PresentationSettings.swift.
namespace Annotator.Core;

/// <summary>Swift `PresentationSettings.Mode`.</summary>
public enum PresentationMode { Off, Lens, Screen }

public static class PresentationModeExtensions
{
    internal static readonly RawValueMap<PresentationMode> RawValues = new("off", "lens", "screen");

    extension(PresentationMode mode)
    {
        public string RawValue => RawValues.RawValue(mode);
    }

    extension(PresentationMode)
    {
        public static PresentationMode? FromRawValue(string rawValue) => RawValues.Parse(rawValue);
    }
}

/// <summary>Presentation zoom state. It shares undo with drawings but is never saved in board files.</summary>
public readonly record struct PresentationSettings(PresentationMode Mode = PresentationMode.Off, double Zoom = 2)
{
    public PresentationSettings()
        : this(PresentationMode.Off, 2)
    {
    }

    public void Validate()
    {
        if (!double.IsFinite(Zoom) || !(Zoom >= 1 && Zoom <= 16))
        {
            throw CanvasError.InvalidGeometry;
        }
    }

    public bool Equals(PresentationSettings other) => Mode == other.Mode && Zoom == other.Zoom;

    public override int GetHashCode() => HashCode.Combine(Mode, Zoom);
}
