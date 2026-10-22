using System.Text.Json.Nodes;
using Annotator.Core.Motion;
using Annotator.Rendering.Imaging;

namespace Annotator.RenderHarness;

/// <summary>
/// The zoom-screen and zoom-lens comparisons. Frames are compared as half-resolution gray images (720 x 450, 2 area points
/// per pixel). Screen zoom shows the desktop around the pointer p: an area point x shows the desktop's point p + (x - p) / z.
/// With p known (the pointer rests during the ramps) the magnification z of each frame is the factor whose mapping of the
/// unzoomed frame best matches it (mean absolute gray difference, each sample capped at 40 so the pointer and the toolbar
/// count little); while the pointer moves, p is found the same way with z known. The lens shows the 240 / z points around
/// the pointer in a 240-point window 28 points right of and above it; its opacity in a frame is the share of the way from
/// the frame without the lens to the frame with it.
/// </summary>
internal static partial class LiveCompare
{
    private const int GrayWidth = Width / 2, GrayHeight = Height / 2;

    /// <summary>The sample content starts 130 points right of and 60 points below the area's corner.</summary>
    private static (double X, double Y) Design(double x, double y) => (130 + x, 60 + y);

    /// <summary>Half-resolution gray frames of a whole video, with the frames the sheets need.</summary>
    private static (List<byte[]> Gray, Dictionary<int, RasterImage> Kept) ScanGray(string video, HashSet<int> keep)
    {
        var gray = new List<byte[]>();
        var kept = new Dictionary<int, RasterImage>();
        Scan(video, (index, frame) =>
        {
            var half = new byte[GrayWidth * GrayHeight];
            for (var y = 0; y < GrayHeight; y++)
            {
                for (var x = 0; x < GrayWidth; x++)
                {
                    var sum = 0;
                    for (var dy = 0; dy < 2; dy++)
                    {
                        var offset = ((y * 2 + dy) * Width + x * 2) * 4;
                        sum += frame[offset] * 29 + frame[offset + 1] * 150 + frame[offset + 2] * 77
                               + frame[offset + 4] * 29 + frame[offset + 5] * 150 + frame[offset + 6] * 77;
                    }
                    half[y * GrayWidth + x] = (byte)(sum / (4 * 256));
                }
            }
            gray.Add(half);
            if (keep.Contains(index))
            {
                kept[index] = Image(frame);
            }
        });
        return (gray, kept);
    }

    private static double Sample(byte[] image, double x, double y)
    {
        // x and y in area points.
        x = x / 2 - 0.5;
        y = y / 2 - 0.5;
        if (x < 0 || y < 0 || x > GrayWidth - 1 || y > GrayHeight - 1)
        {
            return double.NaN;
        }
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, GrayWidth - 1), y1 = Math.Min(y0 + 1, GrayHeight - 1);
        double fx = x - x0, fy = y - y0;
        double top = image[y0 * GrayWidth + x0] * (1 - fx) + image[y0 * GrayWidth + x1] * fx;
        double bottom = image[y1 * GrayWidth + x0] * (1 - fx) + image[y1 * GrayWidth + x1] * fx;
        return top * (1 - fy) + bottom * fy;
    }

    private static double At(byte[] image, double x, double y) => image[Math.Clamp((int)(y / 2), 0, GrayHeight - 1) * GrayWidth + Math.Clamp((int)(x / 2), 0, GrayWidth - 1)];

    /// <summary>
    /// How well <paramref name="frame"/> shows <paramref name="unzoomed"/> magnified <paramref name="zoom"/> times around
    /// <paramref name="pointer"/>: the mean capped difference over a grid of area points above the toolbar.
    /// </summary>
    private static double ZoomMismatch(byte[] frame, byte[] unzoomed, (double X, double Y) pointer, double zoom, int step = 8, int bottom = 740)
    {
        double sum = 0;
        var count = 0;
        for (var y = 4; y < bottom; y += step)
        {
            for (var x = 4; x < Width; x += step)
            {
                var source = Sample(unzoomed, pointer.X + (x - pointer.X) / zoom, pointer.Y + (y - pointer.Y) / zoom);
                if (double.IsNaN(source))
                {
                    continue;
                }
                sum += Math.Min(40, Math.Abs(At(frame, x, y) - source));
                count++;
            }
        }
        return count == 0 ? double.MaxValue : sum / count;
    }

    /// <summary>The magnification of <paramref name="frame"/> around <paramref name="pointer"/>, from 1x to 4x, and its mismatch.</summary>
    private static (double Zoom, double Mismatch) EstimateZoom(byte[] frame, byte[] unzoomed, (double X, double Y) pointer)
    {
        (double Zoom, double Mismatch) best = (1, double.MaxValue);
        for (var zoom = 1.0; zoom <= 4.0001; zoom += 0.02)
        {
            var mismatch = ZoomMismatch(frame, unzoomed, pointer, zoom, 12);
            if (mismatch < best.Mismatch)
            {
                best = (zoom, mismatch);
            }
        }
        var coarse = best.Zoom;
        best = (coarse, double.MaxValue);
        for (var zoom = Math.Max(1, coarse - 0.03); zoom <= coarse + 0.03; zoom += 0.002)
        {
            var mismatch = ZoomMismatch(frame, unzoomed, pointer, zoom);
            if (mismatch < best.Mismatch)
            {
                best = (zoom, mismatch);
            }
        }
        return best;
    }

    /// <summary>The point that <paramref name="frame"/> magnifies <paramref name="zoom"/> times, searched near <paramref name="guess"/>.</summary>
    private static ((double X, double Y) Anchor, double Mismatch) EstimateAnchor(byte[] frame, byte[] unzoomed, double zoom, (double X, double Y) guess)
    {
        var best = (Anchor: guess, Mismatch: double.MaxValue);
        foreach (var (range, step, grid) in new[] { (60.0, 4.0, 16), (4.0, 0.5, 8) })
        {
            var center = best.Anchor;
            for (var dy = -range; dy <= range; dy += step)
            {
                for (var dx = -range; dx <= range; dx += step)
                {
                    var anchor = (center.X + dx, center.Y + dy);
                    var mismatch = ZoomMismatch(frame, unzoomed, anchor, zoom, grid);
                    if (mismatch < best.Mismatch)
                    {
                        best = (anchor, mismatch);
                    }
                }
            }
        }
        return best;
    }

    /// <summary>
    /// Fits a ramp of <paramref name="duration"/> seconds on <paramref name="curve"/> to measured progress (0 to 1) by its
    /// start time: the start, the root mean square error, and the times the measured progress first passed 10% and 90%.
    /// </summary>
    private static JsonObject FitRamp(IReadOnlyList<(double Time, double Progress)> samples, CubicBezier curve, double duration)
    {
        var moving = samples.Where(sample => sample.Progress is > 0.02 and < 0.98).ToList();
        if (moving.Count == 0)
        {
            return new JsonObject { ["fitted"] = false };
        }
        double bestStart = 0, bestError = double.MaxValue;
        for (var start = moving[0].Time - duration; start <= moving[0].Time; start += 0.0005)
        {
            double sum = 0;
            foreach (var (time, progress) in samples)
            {
                var expected = curve.Progress(Math.Clamp((time - start) / duration, 0, 1));
                sum += (progress - expected) * (progress - expected);
            }
            var error = Math.Sqrt(sum / samples.Count);
            if (error < bestError)
            {
                (bestStart, bestError) = (start, error);
            }
        }
        double? Passed(double share) => samples.FirstOrDefault(sample => sample.Progress >= share) is { Time: var time } found && found != default ? time : null;
        return new JsonObject
        {
            ["fitted"] = true,
            ["start"] = Round(bestStart),
            ["rmsError"] = Round(bestError),
            ["passed10"] = Number(Passed(0.1)),
            ["passed90"] = Number(Passed(0.9)),
            ["expected10to90"] = Round(CurveTime(curve, 0.9) * duration - CurveTime(curve, 0.1) * duration),
            ["movingFrames"] = moving.Count,
        };
    }

    /// <summary>The share of a curve's time at which it reaches <paramref name="progress"/>.</summary>
    private static double CurveTime(CubicBezier curve, double progress)
    {
        double low = 0, high = 1;
        for (var step = 0; step < 40; step++)
        {
            var middle = (low + high) / 2;
            if (curve.Progress(middle) < progress)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }
        return (low + high) / 2;
    }

    /// <summary>
    /// zoom-screen: the magnification of every frame of the ramps and of zooming further (pointer at rest), fitted to
    /// MotionTiming's ramps (260 ms on the enter curve from the first zoomed frame, 200 ms on the standard curve out), and
    /// the point the view magnifies while the pointer moves, against the Windows take's pointer path.
    /// </summary>
    private static JsonObject ZoomScreen(Pair pair)
    {
        var (macKeep, windowsKeep) = SheetFrames(pair);
        var mac = ScanGray(pair.Mac.Video, macKeep);
        var windows = ScanGray(pair.Windows.Video, windowsKeep);
        Sheets(pair, mac.Kept, windows.Kept);
        var result = new JsonObject
        {
            ["expected"] = new JsonObject
            {
                ["zoomInSeconds"] = MotionTiming.ZoomInDuration,
                ["zoomOutSeconds"] = MotionTiming.ZoomOutDuration,
                ["zoomInCurve"] = "MotionCurves.Enter",
                ["zoomOutCurve"] = "MotionCurves.Standard",
            },
        };
        var start = Design(400, 96);
        var rest = Design(260, 470);
        foreach (var (name, recording, gray) in new[] { ("mac", pair.Mac, mac.Gray), ("windows", pair.Windows, windows.Gray) })
        {
            var zoomIn = recording.Segment("zoom in");
            var unzoomed = gray[Math.Max(0, Index(zoomIn.Start) - 1)];
            JsonArray Series((string Label, double Start, double End) segment, (double X, double Y) pointer, int after)
            {
                var series = new JsonArray();
                for (var index = Math.Max(0, Index(segment.Start) - 2); index <= Math.Min(gray.Count - 1, Index(segment.End) + after); index++)
                {
                    var (zoom, mismatch) = EstimateZoom(gray[index], unzoomed, pointer);
                    series.Add(new JsonArray(Round(index / (double)Rate - segment.Start), Round(zoom, 3), Round(mismatch, 2)));
                }
                return series;
            }
            var inSeries = Series(zoomIn, start, 8);
            var further = Series(recording.Segment("zoom further"), rest, 2);
            var outSeries = Series(recording.Segment("zoom out"), rest, 8);
            static List<(double, double)> Progress(JsonArray series, double from, double to) =>
                [.. series.Select(row => (row![0]!.GetValue<double>(), Math.Clamp((row[1]!.GetValue<double>() - from) / (to - from), -0.2, 1.2)))];
            var target = inSeries.Last()![1]!.GetValue<double>();
            var furtherZoom = further.Last()![1]!.GetValue<double>();
            // The anchor while the pointer moves: from the zoom-in pointer, frame by frame.
            var moves = recording.Segment("pointer moves while zoomed");
            var anchors = new JsonArray();
            var guess = start;
            for (var index = Index(moves.Start) - 6; index <= Math.Min(gray.Count - 1, Index(moves.End) + 6); index++)
            {
                var (anchor, mismatch) = EstimateAnchor(gray[index], unzoomed, target, guess);
                guess = anchor;
                anchors.Add(new JsonArray(Round(index / (double)Rate - moves.Start), Round(anchor.X, 1), Round(anchor.Y, 1), Round(mismatch, 2)));
            }
            var steps = new JsonArray();
            double? previous = null;
            foreach (var row in further)
            {
                var zoom = row![1]!.GetValue<double>();
                if (previous is { } last && zoom - last > 0.02)
                {
                    steps.Add(new JsonArray(row[0]!.GetValue<double>(), zoom));
                }
                previous = zoom;
            }
            result[name] = new JsonObject
            {
                ["zoomInTarget"] = Round(target, 3),
                ["zoomInFit"] = FitRamp(Progress(inSeries, 1, target), MotionCurves.Enter, MotionTiming.ZoomInDuration),
                ["zoomFurtherReached"] = Round(furtherZoom, 3),
                ["zoomFurtherSteps"] = steps,
                ["zoomOutFit"] = FitRamp(Progress(outSeries, furtherZoom, 1), MotionCurves.Standard, MotionTiming.ZoomOutDuration),
                ["zoomIn"] = inSeries,
                ["zoomFurther"] = further,
                ["zoomOut"] = outSeries,
                ["anchorsWhilePointerMoves"] = anchors,
            };
            Console.WriteLine($"    {name}: in {result[name]!["zoomInFit"]!.ToJsonString()} to {target:F3}; further to {furtherZoom:F3} in {steps.Count} steps; "
                              + $"out {result[name]!["zoomOutFit"]!.ToJsonString()}");
        }
        // How far the view's anchor was from the Windows take's pointer path: on Windows at the same time, and on the Mac from
        // the nearest point of that path (the Mac's pointer is the same seeded path at its own pace).
        var path = pair.Windows.Pointer;
        if (path.Length > 1)
        {
            var moves = pair.Windows.Segment("pointer moves while zoomed");
            (double, double) PointerAt(double time)
            {
                var found = path.LastOrDefault(point => point.Time <= time);
                return found == default ? (path[0].X, path[0].Y) : (found.X, found.Y);
            }
            var polyline = path.Where(point => point.Time >= moves.Start - 0.05 && point.Time <= moves.End + 0.05).Select(point => (point.X, point.Y)).ToArray();
            double Nearest((double X, double Y) anchor)
            {
                var best = double.MaxValue;
                for (var index = 1; index < polyline.Length; index++)
                {
                    var (ax, ay) = polyline[index - 1];
                    var (bx, by) = polyline[index];
                    double dx = bx - ax, dy = by - ay, length = dx * dx + dy * dy;
                    var t = length == 0 ? 0 : Math.Clamp(((anchor.X - ax) * dx + (anchor.Y - ay) * dy) / length, 0, 1);
                    best = Math.Min(best, Math.Sqrt(Math.Pow(ax + dx * t - anchor.X, 2) + Math.Pow(ay + dy * t - anchor.Y, 2)));
                }
                return best;
            }
            JsonObject Stats(IEnumerable<double> values)
            {
                var sorted = values.Order().ToArray();
                return sorted.Length == 0 ? new JsonObject() : new JsonObject
                {
                    ["frames"] = sorted.Length,
                    ["median"] = Round(sorted[sorted.Length / 2], 2),
                    ["p95"] = Round(sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * 0.95) - 1)], 2),
                    ["max"] = Round(sorted[^1], 2),
                };
            }
            var windowsAnchors = result["windows"]!["anchorsWhilePointerMoves"]!.AsArray()
                .Where(row => row![0]!.GetValue<double>() >= 0 && row[0]!.GetValue<double>() <= moves.End - moves.Start);
            var macMoves = pair.Mac.Segment("pointer moves while zoomed");
            var macAnchors = result["mac"]!["anchorsWhilePointerMoves"]!.AsArray()
                .Where(row => row![0]!.GetValue<double>() >= 0 && row[0]!.GetValue<double>() <= macMoves.End - macMoves.Start);
            result["anchorFromPointer"] = new JsonObject
            {
                ["windowsAtTheSameTime"] = Stats(windowsAnchors.Select(row =>
                {
                    var (x, y) = PointerAt(moves.Start + row![0]!.GetValue<double>());
                    return Math.Sqrt(Math.Pow(row[1]!.GetValue<double>() - x, 2) + Math.Pow(row[2]!.GetValue<double>() - y, 2));
                })),
                ["windowsFromThePath"] = Stats(windowsAnchors.Select(row => Nearest((row![1]!.GetValue<double>(), row[2]!.GetValue<double>())))),
                ["macFromThePath"] = Stats(macAnchors.Select(row => Nearest((row![1]!.GetValue<double>(), row[2]!.GetValue<double>())))),
            };
            Console.WriteLine($"    anchor from the pointer path (points): {result["anchorFromPointer"]!.ToJsonString()}");
        }
        return result;
    }

    /// <summary>
    /// zoom-lens: the lens's opacity in every frame of turning on and off (the 120 ms fade in on the fade curve, gone at
    /// once when turned off) and its magnification once shown.
    /// </summary>
    private static JsonObject ZoomLens(Pair pair)
    {
        var (macKeep, windowsKeep) = SheetFrames(pair);
        var mac = ScanGray(pair.Mac.Video, macKeep);
        var windows = ScanGray(pair.Windows.Video, windowsKeep);
        Sheets(pair, mac.Kept, windows.Kept);
        const double Size = 240, Offset = 28, Margin = 8;
        var result = new JsonObject
        {
            ["expected"] = new JsonObject { ["fadeInSeconds"] = 0.12, ["fadeCurve"] = "MotionCurves.Fade" },
        };
        foreach (var (name, recording, gray) in new[] { ("mac", pair.Mac, mac.Gray), ("windows", pair.Windows, windows.Gray) })
        {
            // The lens's interior in area points for the pointer at rest: 28 points right of it and its bottom 28 points above it.
            (double X0, double Y0, double X1, double Y1) Lens((double X, double Y) pointer) =>
                (pointer.X + Offset + Margin, Math.Max(0, pointer.Y - Offset - Size + Margin), pointer.X + Offset + Size - Margin, pointer.Y - Offset - Margin);
            JsonArray Opacity(byte[] without, byte[] with, (double X0, double Y0, double X1, double Y1) lens, int from, int to, double request)
            {
                var series = new JsonArray();
                for (var index = Math.Max(0, from); index <= Math.Min(gray.Count - 1, to); index++)
                {
                    double up = 0, down = 0;
                    for (var y = lens.Y0; y < lens.Y1; y += 2)
                    {
                        for (var x = lens.X0; x < lens.X1; x += 2)
                        {
                            double a = At(without, x, y), b = At(with, x, y), f = At(gray[index], x, y);
                            up += (f - a) * (b - a);
                            down += (b - a) * (b - a);
                        }
                    }
                    series.Add(new JsonArray(Round(index / (double)Rate - request), Round(down == 0 ? 0 : up / down, 3)));
                }
                return series;
            }
            var on = recording.Segment("lens on");
            var onLens = Lens(Design(180, 180));
            var before = gray[Math.Max(0, Index(on.Start) - 1)];
            var shown = gray[Math.Min(gray.Count - 1, Index(on.End) - 1)];
            var fadeIn = Opacity(before, shown, onLens, Index(on.Start) - 2, Index(on.End), on.Start);
            var off = recording.Segment("lens off");
            var offLens = Lens(Design(300, 560));
            var lastShown = gray[Math.Max(0, Index(off.Start) - 1)];
            var after = gray[Math.Min(gray.Count - 1, Index(off.End))];
            // The share of the lens that is still shown: from the frame without it (0) to the last frame with it (1).
            var fadeOut = Opacity(after, lastShown, offLens, Index(off.Start) - 2, Index(off.End), off.Start);
            var fit = FitRamp([.. fadeIn.Select(row => (row![0]!.GetValue<double>(), Math.Clamp(row[1]!.GetValue<double>(), -0.2, 1.2)))],
                              MotionCurves.Fade, 0.12);
            double? Gone() => fadeOut.FirstOrDefault(row => row![0]!.GetValue<double>() >= 0 && row[1]!.GetValue<double>() < 0.05) is { } row ? row[0]!.GetValue<double>() : null;
            double? FirstVisible() => fadeIn.FirstOrDefault(row => row![1]!.GetValue<double>() > 0.05) is { } row ? row[0]!.GetValue<double>() : null;
            // The lens's magnification once shown: its interior shows the pointer's surroundings p + (x - center) / z.
            var pointer = Design(180, 180);
            (double X, double Y) center = (pointer.X + Offset + Size / 2, pointer.Y - Offset - Size / 2);
            (double Zoom, double Mismatch) best = (1, double.MaxValue);
            for (var zoom = 1.0; zoom <= 6.0001; zoom += 0.01)
            {
                double sum = 0;
                var count = 0;
                for (var y = onLens.Y0; y < onLens.Y1; y += 3)
                {
                    for (var x = onLens.X0; x < onLens.X1; x += 3)
                    {
                        var source = Sample(before, pointer.X + (x - center.X) / zoom, pointer.Y + (y - center.Y) / zoom);
                        if (!double.IsNaN(source))
                        {
                            sum += Math.Min(40, Math.Abs(At(shown, x, y) - source));
                            count++;
                        }
                    }
                }
                if (count > 0 && sum / count < best.Mismatch)
                {
                    best = (zoom, sum / count);
                }
            }
            result[name] = new JsonObject
            {
                ["fadeInFit"] = fit,
                ["firstVisibleAfterRequest"] = Number(FirstVisible()),
                ["goneAfterRequest"] = Number(Gone()),
                ["lensMagnification"] = Round(best.Zoom, 2),
                ["lensMagnificationMismatch"] = Round(best.Mismatch, 2),
                ["fadeIn"] = fadeIn,
                ["fadeOut"] = fadeOut,
            };
            Console.WriteLine($"    {name}: first visible {FirstVisible():F3} s after the request, fade {fit.ToJsonString()}, gone {Gone():F3} s after off, "
                              + $"lens at {best.Zoom:F2}x");
        }
        return result;
    }
}
