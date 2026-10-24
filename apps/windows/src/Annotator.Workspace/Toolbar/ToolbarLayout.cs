using Annotator.Core;

namespace Annotator.Workspace.Toolbar;

/// <summary>Port of ToolbarPropertiesMode in ToolbarView.swift: what the toolbar shows below or beside the tools.</summary>
public enum ToolbarPropertiesMode
{
    ToolsOnly,
    Quick,
    Full,
}

public static class ToolbarPropertiesModeExtensions
{
    extension(ToolbarPropertiesMode mode)
    {
        /// <summary>The Mac's raw value, also the Properties button's accessibility value.</summary>
        public string RawValue => mode switch
        {
            ToolbarPropertiesMode.ToolsOnly => "Tools only",
            ToolbarPropertiesMode.Quick => "Quick controls",
            ToolbarPropertiesMode.Full => "Full properties",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

        /// <summary>The Properties button's cycle: tools only, quick controls, full properties.</summary>
        public ToolbarPropertiesMode Next => mode switch
        {
            ToolbarPropertiesMode.ToolsOnly => ToolbarPropertiesMode.Quick,
            ToolbarPropertiesMode.Quick => ToolbarPropertiesMode.Full,
            _ => ToolbarPropertiesMode.ToolsOnly,
        };
    }

    extension(ToolbarPropertiesMode)
    {
        /// <summary>
        /// Mac ToolbarPropertiesMode(model:): tools only when quick preferences are collapsed, quick controls when the person chose
        /// them (the inspector unpinned), else full properties, which is the default.
        /// </summary>
        public static ToolbarPropertiesMode From(in WorkspacePreferences preferences) =>
            preferences.QuickPreferencesExpanded == false ? ToolbarPropertiesMode.ToolsOnly
            : preferences.InspectorPinned == false ? ToolbarPropertiesMode.Quick
            : ToolbarPropertiesMode.Full;
    }
}

/// <summary>Port of ToolbarMetrics in ToolbarToolLayout.swift: sizes in points for the two toolbar densities.</summary>
public readonly record struct ToolbarMetrics(ToolbarSize Size)
{
    public bool Minimal => Size == ToolbarSize.Minimal;

    public double ButtonWidth => Minimal ? 34 : 44;

    public double ButtonHeight => Minimal ? 36 : 48;

    public double GlyphSize => Minimal ? 17 : 19;

    public bool ShowsKeyLabels => !Minimal;

    public double FamilyGap => Minimal ? 9 : 11;

    public double DividerPadding => Minimal ? 2 : 3;

    public double Padding => Minimal ? 8 : 12;

    public double Spacing => Minimal ? 4 : 6;

    public double CornerRadius => Minimal ? 13 : 15;

    public (double Width, double Height) ActionSize => Minimal ? (22, 24) : (24, 26);

    public double BoardWidth => Minimal ? 34 : 44;

    public double ModeWidth => Minimal ? 46 : 60;

    public double RegularReserve => Minimal ? 316 : 392;

    public double CompactReserve => Minimal ? 316 : 332;

    public double RailReserve => RegularReserve - 16;

    public double CompactRailReserve => Minimal ? 150 : 214;

    public double PropertiesWidth => Minimal ? 640 : 700;

    public double MinimumWidth => Minimal ? 300 : 400;

    public double CompactThreshold => Minimal ? 420 : 700;

    public double VerticalRailWidth => ButtonWidth + Padding * 2;

    public double VerticalToolPitch => Minimal ? 39 : 51;

    public double VerticalReserve => Minimal ? 364 : 374;
}

/// <summary>Port of ToolbarToolLayout.swift: tool families, widths and the visible part of the tool rail.</summary>
public static class ToolbarToolLayout
{
    public const double ButtonWidth = 44;

    public static double HorizontalWidth(ReadOnlySpan<ShortcutCommand> items, double available, bool propertiesVisible,
                                         ToolbarSize size = ToolbarSize.Expanded)
    {
        var metrics = new ToolbarMetrics(size);
        double toolWidth = Width(items, size);
        double regular = toolWidth + metrics.RegularReserve;
        double desired = regular >= metrics.CompactThreshold ? regular : toolWidth + metrics.CompactReserve;
        return Math.Min(Math.Max(320, available), Math.Max(propertiesVisible ? metrics.PropertiesWidth : metrics.MinimumWidth, desired));
    }

    /// <summary>The button that stands for <paramref name="tool"/>. Effects share one button unless they have their own.</summary>
    public static Tool Identity(Tool tool, ReadOnlySpan<ShortcutCommand> items)
    {
        if (tool.IsProtection)
        {
            return items.Contains(ShortcutCommand.ForTool(tool)) ? tool : Tool.Cover;
        }
        if (tool.IsShape && !items.Contains(ShortcutCommand.ForTool(tool)) && items.Contains(ShortcutCommand.ForTool(Tool.Rectangle)))
        {
            return Tool.Rectangle;
        }
        return tool;
    }

    public static int Family(Tool tool)
    {
        if (tool.IsShape || tool.IsConnector)
        {
            return 1;
        }
        return tool switch
        {
            Tool.Select => 0,
            Tool.Pen or Tool.Text or Tool.Highlighter => 2,
            Tool.Eraser => 3,
            Tool.Cover or Tool.Blur or Tool.Pixelate or Tool.Distort => 4,
            _ => 5,
        };
    }

    public static int Family(ShortcutCommand item) => item.Tool is { } tool ? Family(tool) : 6;

    public static double Width(ReadOnlySpan<ShortcutCommand> items, ToolbarSize size = ToolbarSize.Expanded)
    {
        var metrics = new ToolbarMetrics(size);
        double width = 0;
        for (int index = 0; index < items.Length; index++)
        {
            double gap = index == 0 ? 0 : Family(items[index - 1]) == Family(items[index]) ? 2 : metrics.FamilyGap;
            width += gap + metrics.ButtonWidth;
        }
        return width;
    }

    /// <summary>The items that fit in <paramref name="width"/>, scrolled so the selected item stays visible.</summary>
    public static ShortcutCommand[] Visible(ReadOnlySpan<ShortcutCommand> items, ShortcutCommand selected, double width,
                                            ToolbarSize size = ToolbarSize.Expanded)
    {
        ShortcutCommand[] source = items.ToArray();
        ShortcutCommand[] Fit(int start)
        {
            var result = new List<ShortcutCommand>();
            for (int index = start; index < source.Length; index++)
            {
                result.Add(source[index]);
                if (Width(result.ToArray(), size) > width)
                {
                    result.RemoveAt(result.Count - 1);
                    break;
                }
            }
            return [.. result];
        }

        ShortcutCommand[] first = Fit(0);
        int selectedIndex = Array.IndexOf(source, selected);
        if (selectedIndex < 0 || Array.IndexOf(first, selected) >= 0)
        {
            return first;
        }
        int begin = Math.Max(0, selectedIndex - first.Length + 1);
        while (begin < selectedIndex)
        {
            ShortcutCommand[] result = Fit(begin);
            if (Array.IndexOf(result, selected) >= 0)
            {
                return result;
            }
            begin++;
        }
        return Fit(selectedIndex);
    }
}

/// <summary>
/// Port of ToolbarPlacement.swift in y-down coordinates: the Mac's top edge (maxY) is the smallest y here.
/// Frames and work areas share one coordinate space, usually a display's points.
/// </summary>
public static class ToolbarPlacement
{
    /// <summary>
    /// Keeps a dragged toolbar inside the work area. A toolbar taller than the work area keeps its top edge in it, as the
    /// Mac's keeps its top below the menu bar.
    /// </summary>
    public static Rect ConstrainedDragFrame(Rect frame, Rect workArea)
    {
        double x = Math.Max(workArea.X, Math.Min(frame.X, workArea.MaxX - frame.Width));
        double y = Math.Max(workArea.Y, Math.Min(workArea.MaxY - frame.Height, frame.Y));
        return frame with { X = x, Y = y };
    }

    /// <summary>
    /// Mac resizeToolbar's clamp for a freely placed toolbar that changed size: inside the work area, and a toolbar taller
    /// than the work area keeps its bottom edge in it.
    /// </summary>
    public static Rect ConstrainedResizedFrame(Rect frame, Rect workArea)
    {
        double x = Math.Max(workArea.X, Math.Min(frame.X, workArea.MaxX - frame.Width));
        double y = Math.Min(workArea.MaxY - frame.Height, Math.Max(frame.Y, workArea.Y));
        return frame with { X = x, Y = y };
    }

    /// <summary>The edge a point touches, within <paramref name="threshold"/>, or null; ties go to top, bottom, left, then right.</summary>
    public static ToolbarDockEdge? DockEdge(Point point, Rect frame, double threshold = 1)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || frame.Width <= 0 || frame.Height <= 0)
        {
            return null;
        }
        var outer = new Rect(frame.X - threshold, frame.Y - threshold, frame.Width + threshold * 2, frame.Height + threshold * 2);
        if (!(point.X >= outer.X && point.X <= outer.MaxX && point.Y >= outer.Y && point.Y <= outer.MaxY))
        {
            return null;
        }
        ToolbarDockEdge nearest = ToolbarDockEdge.Top;
        double distance = Math.Abs(point.Y - frame.Y);
        Closer(ToolbarDockEdge.Bottom, Math.Abs(point.Y - frame.MaxY), ref nearest, ref distance);
        Closer(ToolbarDockEdge.Left, Math.Abs(point.X - frame.X), ref nearest, ref distance);
        Closer(ToolbarDockEdge.Right, Math.Abs(point.X - frame.MaxX), ref nearest, ref distance);
        return distance <= threshold ? nearest : null;

        static void Closer(ToolbarDockEdge edge, double candidate, ref ToolbarDockEdge nearest, ref double distance)
        {
            if (candidate < distance)
            {
                nearest = edge;
                distance = candidate;
            }
        }
    }

    /// <summary>
    /// The docked frame: centered along the edge, inset by <paramref name="inset"/>. Horizontal toolbars sit at
    /// the top unless the edge is Bottom; vertical ones at the right unless the edge is Left.
    /// </summary>
    public static Rect Frame(double width, double height, Rect workArea, ToolbarOrientation orientation, ToolbarDockEdge edge,
                             double inset = 12)
    {
        var safe = new Rect(workArea.X + inset, workArea.Y + inset, workArea.Width - inset * 2, workArea.Height - inset * 2);
        width = Math.Min(width, safe.Width);
        height = Math.Min(height, safe.Height);
        double x = safe.X + safe.Width / 2 - width / 2;
        double y = safe.Y + safe.Height / 2 - height / 2;
        if (orientation == ToolbarOrientation.Vertical)
        {
            x = edge == ToolbarDockEdge.Left ? safe.X : safe.MaxX - width;
        }
        else
        {
            y = edge == ToolbarDockEdge.Bottom ? safe.MaxY - height : safe.Y;
        }
        return new Rect(Math.Floor(x), Math.Floor(y), width, height);
    }
}
