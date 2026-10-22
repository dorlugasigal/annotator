using System.Collections.Immutable;

namespace Annotator.Workspace.Library;

/// <summary>Where the adaptive tile grid puts its columns (SwiftUI LazyVGrid with GridItem(.adaptive(minimum: 68, maximum: 96), spacing: 8)).</summary>
/// <param name="Count">How many columns fit.</param>
/// <param name="Width">Each column's width.</param>
public readonly record struct GridColumns(int Count, double Width)
{
    /// <summary>The left edge of column <paramref name="column"/> from the grid's left edge.</summary>
    public double X(int column) => column * (Width + ToolLibraryLayout.TileSpacing);
}

/// <summary>The tile grid of one section in its scroll area: columns, rows and whether it scrolls.</summary>
/// <param name="GridWidth">The grid's width: the scroll area less its 16-point trailing padding, and less the scroller when it scrolls.</param>
/// <param name="ContentHeight">The height of every row with the spacing between them.</param>
/// <param name="Scrolls">True when the rows are taller than the scroll area, which then shows the Mac's legacy scroller.</param>
public readonly record struct GridPlan(GridColumns Columns, int Rows, double GridWidth, double ContentHeight, bool Scrolls);

/// <summary>
/// The sizes of ToolLibraryPanel and ToolLibrary (ToolLibrary.swift) and ToolLibraryWindowController, in points: the panel's
/// padding and spacing, the scroll area's height for a section, the adaptive grid and the segmented picker's segments.
/// </summary>
public static class ToolLibraryLayout
{
    /// <summary>ToolLibraryPanel's default width, which the UX reference captures.</summary>
    public const double DefaultWidth = 368;

    /// <summary>ToolLibraryPanel's default maximumHeight.</summary>
    public const double DefaultMaximumHeight = 420;

    /// <summary>The window's first content size (ToolLibraryWindowController).</summary>
    public const double WindowWidth = 440;

    public const double WindowHeight = 500;

    /// <summary>The window's smallest content size (contentMinSize).</summary>
    public const double MinimumWidth = 368;

    public const double MinimumHeight = 260;

    /// <summary>The panel's padding on every side.</summary>
    public const double Padding = 16;

    /// <summary>The spacing between the panel's rows, and between the picker and the grid.</summary>
    public const double Spacing = 12;

    /// <summary>The header: the "Tools" headline beside the small Paste image button.</summary>
    public const double HeaderHeight = 20;

    public const double PickerHeight = 24;

    /// <summary>The footer's menu buttons.</summary>
    public const double FooterHeight = 16;

    /// <summary>The Focus section's Spotlight size row under the grid.</summary>
    public const double SpotlightRowHeight = 16;

    public const double TileHeight = 76;

    public const double TileSpacing = 8;

    public const double MinimumTileWidth = 68;

    public const double MaximumTileWidth = 96;

    /// <summary>The grid's trailing padding inside its scroll area.</summary>
    public const double GridTrailingPadding = 16;

    /// <summary>The legacy scroller of a grid that scrolls (.scrollIndicators(.visible)), from the reference frames.</summary>
    public const double ScrollerWidth = 17;

    /// <summary>The segments' widths at their natural size in SF Pro, from the UX reference (frames include the outer bezel).</summary>
    public static ImmutableArray<double> NaturalSegmentWidths { get; } = [57, 69, 60.5, 62, 89.5];

    /// <summary>The segmented control's frame reaches 1 point past the content width on each side, like an NSSegmentedControl.</summary>
    public const double SegmentBezel = 1;

    /// <summary>Mac ToolLibraryPanel: the scroll area's height for a panel of <paramref name="maximumHeight"/>.</summary>
    public static double ViewportHeight(double maximumHeight, ToolLibrarySection section) =>
        Math.Max(76, maximumHeight - 168 - (section == ToolLibrarySection.Focus ? 28 : 0));

    /// <summary>Mac ToolLibraryPanel: the content width, the panel width less its padding, at least 336.</summary>
    public static double ViewportWidth(double width) => Math.Max(336, width - Padding * 2);

    /// <summary>The tiles a section adds after its tools (Mac ToolLibrary.gridHeight's extra): five in Focus, one in Extras.</summary>
    public static int ExtraTiles(ToolLibrarySection section) => section switch
    {
        ToolLibrarySection.Focus => 5,
        ToolLibrarySection.Extras => 1,
        _ => 0,
    };

    /// <summary>
    /// Mac ToolLibrary.gridHeight: the scroll area's height for a tool section, from an estimate of four 76-point columns per
    /// 312 points, at least 160 and at most <paramref name="viewportHeight"/>.
    /// </summary>
    public static double GridHeight(ToolLibrarySection section, double viewportWidth, double viewportHeight)
    {
        int count = Math.Max(1, (int)((viewportWidth - 24) / 76));
        int rows = (section.Tools.Length + ExtraTiles(section) + count - 1) / count;
        return Math.Min(viewportHeight, Math.Max(160, rows * TileHeight + Math.Max(0, rows - 1) * TileSpacing));
    }

    /// <summary>
    /// SwiftUI's adaptive columns: as many 68-point columns with 8 points between them as fit, then widened to share the
    /// width, up to 96 points.
    /// </summary>
    public static GridColumns Columns(double width)
    {
        int count = Math.Max(1, (int)Math.Floor((width + TileSpacing) / (MinimumTileWidth + TileSpacing)));
        double column = Math.Min(MaximumTileWidth, (width - (count - 1) * TileSpacing) / count);
        return new GridColumns(count, Math.Max(0, column));
    }

    /// <summary>
    /// The grid of <paramref name="tiles"/> tiles in a scroll area <paramref name="viewportWidth"/> wide and
    /// <paramref name="viewportHeight"/> high. A grid taller than the area scrolls, and the scroller takes its width.
    /// </summary>
    public static GridPlan Grid(int tiles, double viewportWidth, double viewportHeight)
    {
        double width = Math.Max(0, viewportWidth - GridTrailingPadding);
        GridPlan plan = Plan(tiles, width, scrolls: false);
        return plan.ContentHeight > viewportHeight + 0.5 ? Plan(tiles, Math.Max(0, width - ScrollerWidth), scrolls: true) : plan;
    }

    /// <summary>The height of a panel that shows <paramref name="viewportHeight"/> points of its section at its ideal height.</summary>
    public static double IdealHeight(ToolLibrarySection section, double viewportHeight) =>
        Padding + HeaderHeight + Spacing + PickerHeight + Spacing + viewportHeight
        + (section == ToolLibrarySection.Focus ? Spacing + SpotlightRowHeight : 0)
        + Spacing + Spacing + 1 + Spacing + FooterHeight + Padding;

    /// <summary>
    /// The segmented picker's segments for a content width of <paramref name="available"/> points, as an NSSegmentedControl with
    /// equal segments lays them out: the widest natural width for every segment when they fit, centered; otherwise the
    /// narrow segments grow toward the widest until the control fills the width; below the natural widths they shrink in
    /// proportion. Frames include the bezel, so the control reaches 1 point past the width on each side.
    /// </summary>
    public static ImmutableArray<(double X, double Width)> Segments(double available)
    {
        ImmutableArray<double> natural = NaturalSegmentWidths;
        double target = available + SegmentBezel * 2;
        double widest = natural.Max();
        double total = natural.Sum();
        double[] widths;
        if (target >= widest * natural.Length)
        {
            widths = [.. natural.Select(_ => widest)];
        }
        else if (target <= total)
        {
            widths = [.. natural.Select(width => width * target / total)];
        }
        else
        {
            double level = Level(natural, target);
            widths = [.. natural.Select(width => Math.Max(width, level))];
        }
        double used = widths.Sum();
        double x = Padding - SegmentBezel + (target - used) / 2;
        var segments = ImmutableArray.CreateBuilder<(double, double)>(widths.Length);
        foreach (double width in widths)
        {
            segments.Add((x, width));
            x += width;
        }
        return segments.MoveToImmutable();
    }

    /// <summary>The width the narrow segments grow to so that every segment together is <paramref name="target"/> wide.</summary>
    private static double Level(ImmutableArray<double> natural, double target)
    {
        double[] sorted = [.. natural.Order()];
        for (int raised = 1; raised <= sorted.Length; raised++)
        {
            double rest = sorted.Skip(raised).Sum();
            double level = (target - rest) / raised;
            if (level <= (raised < sorted.Length ? sorted[raised] : double.PositiveInfinity) && level >= sorted[raised - 1])
            {
                return level;
            }
        }
        return sorted[^1];
    }

    private static GridPlan Plan(int tiles, double width, bool scrolls)
    {
        GridColumns columns = Columns(width);
        int rows = tiles == 0 ? 0 : (tiles + columns.Count - 1) / columns.Count;
        double height = rows == 0 ? 0 : rows * TileHeight + (rows - 1) * TileSpacing;
        return new GridPlan(columns, rows, width, height, scrolls);
    }
}
