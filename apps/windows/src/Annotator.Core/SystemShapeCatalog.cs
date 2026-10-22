// Port of apps/macos/Sources/AnnotationCore/SystemShapeCatalog.swift (generated on the Mac by tools/artwork/generate-system-symbols.py).
namespace Annotator.Core;

public static class SystemShapeCatalog
{
    extension(Tool tool)
    {
        public double? SystemShapeAspectRatio => tool switch
        {
            Tool.DatabaseStack => 0.9,
            Tool.Server => 1,
            Tool.Messaging => 1.0002036,
            Tool.Web => 1,
            Tool.Mobile => 0.7,
            Tool.Desktop => 1.1111111,
            Tool.Laptop => 1.2515959,
            Tool.Document => 0.80000003,
            Tool.Configuration => 0.89726505,
            Tool.LoadBalancer => 1,
            Tool.ApiGateway => 1,
            Tool.Envelope => 1.25,
            Tool.Cart => 0.9985843,
            Tool.Bell => 0.89998725,
            _ => null,
        };
    }
}

public partial struct Element
{
    /// <summary>The frame of the drawn symbol: system-design shapes keep their aspect ratio inside the frame.</summary>
    public readonly Rect SymbolFrame
    {
        get
        {
            if (Tool.SystemShapeAspectRatio is not { } ratio)
            {
                return Frame;
            }
            var width = SwiftMath.Min(Frame.Width, Frame.Height * ratio);
            var height = SwiftMath.Min(Frame.Height, Frame.Width / ratio);
            return new Rect(Frame.Center.X - width / 2, Frame.Center.Y - height / 2, width, height);
        }
    }
}
