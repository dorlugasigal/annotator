using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Rendering.Imaging;

namespace Annotator.Benchmarks;

/// <summary>The fixtures of apps/macos/Sources/PerformanceBenchmarks/Fixtures.swift, built the same way from the same seed.</summary>
internal static class Fixtures
{
    public const ulong Seed = 0xA660_2026;
    public const int Width = 1_920;
    public const int Height = 1_080;

    /// <summary>Swift Fixtures.id: a fixed UUID prefix and the index in the last six bytes.</summary>
    public static Guid Id(int index)
    {
        var n = (ulong)index;
        ReadOnlySpan<byte> bytes =
        [
            0xA6, 0x60, 0x20, 0x26, 0, 0, 0x40, 0, 0x80, 0,
            (byte)(n >> 40), (byte)(n >> 32), (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n,
        ];
        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>Swift Fixtures.elements: ten tools in turn, every fourth fillable shape filled, roughness 0, 2 and 4.</summary>
    public static Element[] Elements(int count, bool ellipsesOnly = false, double scaleX = 1, double scaleY = 1)
    {
        Tool[] tools = [Tool.Rectangle, Tool.Ellipse, Tool.Diamond, Tool.Arrow, Tool.Pen, Tool.Highlighter, Tool.Text, Tool.Counter, Tool.Grid, Tool.Axes];
        double[] ellipseRoughness = [2, 6, 12];
        var result = new Element[count];
        for (var index = 0; index < count; index++)
        {
            var tool = ellipsesOnly ? Tool.Ellipse : tools[index % tools.Length];
            double x = (index * 73 % 1_800 + 20) * scaleX, y = (index * 97 % 950 + 20) * scaleY;
            var style = new Style
            {
                Roughness = ellipsesOnly ? ellipseRoughness[index % 3] : index % 3 * 2,
                Width = 2,
                FontSize = 14,
            };
            if (tool.SupportsFill && index % 4 == 0)
            {
                style.Fill = new RGBA(0.2, 0.4, 0.8, 0.2);
            }
            ValueList<Point> points = default;
            if (tool.IsFreehand || tool.IsConnector)
            {
                var array = new Point[24];
                for (var step = 0; step < array.Length; step++)
                {
                    array[step] = new Point(x + step * 3, y + Math.Sin(step * 0.2) * 25);
                }
                points = ValueList.Create<Point>(array);
            }
            result[index] = new Element(tool, new Rect(x, y, 75, 55), points, tool == Tool.Counter ? $"{index % 99}" : $"Owned {index}", style, Id(index))
            {
                Seed = unchecked(Seed + (ulong)index),
                GeometryToken = Id(index + 1_000_000),
                AppendEpoch = Id(index + 2_000_000),
            };
        }
        return result;
    }

    /// <summary>Swift Fixtures.stroke: a pen stroke of <paramref name="count"/> samples, 12 sine periods across the bitmap.</summary>
    public static Element Stroke(int count, double scaleX = 1, double scaleY = 1)
    {
        var points = new Point[count];
        for (var index = 0; index < count; index++)
        {
            var t = (double)index / Math.Max(1, count - 1);
            points[index] = new Point((10 + t * 1_900) * scaleX, (540 + Math.Sin(t * Math.PI * 24) * 400) * scaleY);
        }
        return new Element(Tool.Pen, points: ValueList.Create<Point>(points), id: Id(900_000))
        {
            Seed = Seed,
            GeometryToken = Id(3_000_000),
            AppendEpoch = Id(4_000_000),
        };
    }

    /// <summary>Swift Fixtures.source: opaque 32 x 32 blocks whose red and green follow the block position.</summary>
    public static RasterImage Source(int width = Width, int height = Height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var row = pixels.AsSpan(y * stride, stride);
            var blockY = y / 32 * 32;
            for (var x = 0; x < width; x++)
            {
                var blockX = x / 32 * 32;
                row[x * 4] = 102;
                row[x * 4 + 1] = (byte)(blockY % 255);
                row[x * 4 + 2] = (byte)(blockX % 255);
                row[x * 4 + 3] = 255;
            }
        }
        return new RasterImage(width, height, stride, pixels);
    }

    public static byte[] Png(RasterImage image) => RasterAsset.EncodePng(image);

    /// <summary>Swift Fixtures.images: 256-point image elements at spread positions, all with the same PNG.</summary>
    public static Element[] Images(int count, byte[] data)
    {
        var png = ValueList.Create<byte>(data);
        var result = new Element[count];
        for (var index = 0; index < count; index++)
        {
            result[index] = new Element(Tool.Image, new Rect(index * 83 % 1_600, index * 61 % 800, 256, 256), id: Id(index + 800_000))
            {
                ImagePng = png,
            };
        }
        return result;
    }

    /// <summary>FNV-1a over the bytes, as Swift Fixtures.checksum (here over BGRA rows without padding).</summary>
    public static ulong Checksum(ReadOnlySpan<byte> bytes, ulong hash = 14695981039346656037)
    {
        foreach (var value in bytes)
        {
            hash = unchecked((hash ^ value) * 1099511628211);
        }
        return hash;
    }
}
