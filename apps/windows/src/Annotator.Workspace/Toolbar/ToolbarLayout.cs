using Annotator.Core;

namespace Annotator.Workspace.Toolbar;

/// <summary>
/// Port of ToolbarPropertiesMode in ToolbarView.swift: whether a tool's properties show below or beside the tools.
/// Profiles from before, with quick controls, show them.
/// </summary>
public enum ToolbarPropertiesMode
{
    Shown,
    Hidden,
}

public static class ToolbarPropertiesModeExtensions
{
    extension(ToolbarPropertiesMode mode)
    {
        /// <summary>The Mac's raw value, also the Properties button's accessibility value.</summary>
        public string RawValue => mode switch
        {
            ToolbarPropertiesMode.Shown => "Shown",
            ToolbarPropertiesMode.Hidden => "Hidden",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

        /// <summary>The Properties button and key switch between the two.</summary>
        public ToolbarPropertiesMode Next =>
            mode == ToolbarPropertiesMode.Shown ? ToolbarPropertiesMode.Hidden : ToolbarPropertiesMode.Shown;
    }

    extension(ToolbarPropertiesMode)
    {
        /// <summary>Mac ToolbarPropertiesMode(model:): hidden when quick preferences are collapsed, else shown, the default.</summary>
        public static ToolbarPropertiesMode From(in WorkspacePreferences preferences) =>
            preferences.QuickPreferencesExpanded == false ? ToolbarPropertiesMode.Hidden : ToolbarPropertiesMode.Shown;
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

    /// <summary>
    /// Room for the one-row action strip, gaps and padding next to the tools: the padding, the gap after the tools, and
    /// each button with its gap (<see cref="HorizontalRoom"/>).
    /// </summary>
    public double RegularReserve => Minimal ? 307 : 383;

    /// <summary>The same room on a narrow screen, where only More tools, Board and Draw stay beside the tools.</summary>
    public double CompactReserve => Minimal ? 151 : 203;

    /// <summary>
    /// Full properties below a minimal rail keep the expanded toolbar's layout, drawn smaller by as much as the tool buttons,
    /// so a tool's properties stay on one row.
    /// </summary>
    public double PropertiesScale => Minimal ? 34.0 / 44.0 : 1;

    /// <summary>
    /// Below this screen width a toolbar that doesn't fit on one row moves its other buttons to a second row; on a wider
    /// screen it keeps one row and shows fewer tools.
    /// </summary>
    public double CompactThreshold => Minimal ? 420 : 700;

    public double VerticalRailWidth => ButtonWidth + Padding * 2;

    public double VerticalToolPitch => Minimal ? 39 : 51;

    /// <summary>Room for the rail's own buttons below the tools, with gaps and padding.</summary>
    public double VerticalReserve => Minimal ? 346 : 354;

    /// <summary>The width one of the toolbar's own controls takes in the horizontal row, with its gap.</summary>
    public double HorizontalRoom(ToolbarControl control) => control switch
    {
        ToolbarControl.Library => ButtonWidth + Spacing,
        ToolbarControl.Board => BoardWidth + Spacing,
        ToolbarControl.Mode => ModeWidth + Spacing,
        _ => ActionSize.Width + Spacing,
    };

    /// <summary>The divider before Board and Draw, which goes when both of them do.</summary>
    public double DividerRoom(IReadOnlySet<ToolbarControl> hidden) =>
        hidden.Contains(ToolbarControl.Board) && hidden.Contains(ToolbarControl.Mode) ? 1 + Spacing : 0;

    public double RegularReserveHiding(IReadOnlySet<ToolbarControl> hidden)
    {
        var room = 0.0;
        foreach (var control in hidden)
        {
            room += HorizontalRoom(control);
        }
        return RegularReserve - room - DividerRoom(hidden);
    }

    /// <summary>A narrow toolbar keeps More tools, Board and Draw beside the tools and moves the other buttons below them.</summary>
    public double CompactReserveHiding(IReadOnlySet<ToolbarControl> hidden) => CompactReserve - FirstRowRoom(hidden);

    /// <summary>The second row of a narrow toolbar: Undo, Redo and the other buttons, each with its gap, and the padding.</summary>
    public double SecondRowWidth(IReadOnlySet<ToolbarControl> hidden)
    {
        double width = Padding * 2;
        foreach (var control in ToolbarToolLayout.SecondRowControls)
        {
            if (!hidden.Contains(control))
            {
                width += HorizontalRoom(control);
            }
        }
        return width;
    }

    private double FirstRowRoom(IReadOnlySet<ToolbarControl> hidden)
    {
        var room = 0.0;
        foreach (var control in (ReadOnlySpan<ToolbarControl>)[ToolbarControl.Library, ToolbarControl.Board, ToolbarControl.Mode])
        {
            if (hidden.Contains(control))
            {
                room += HorizontalRoom(control);
            }
        }
        return room + DividerRoom(hidden);
    }

    /// <summary>The height one of the toolbar's own controls takes in the vertical rail, with its gap.</summary>
    public double VerticalRoom(ToolbarControl control, IReadOnlySet<ToolbarControl> hidden)
    {
        double gap = Minimal ? 3 : 5;
        return control switch
        {
            ToolbarControl.Library => ButtonHeight + gap,
            ToolbarControl.Board => (Minimal ? 30 : 36) + gap,
            ToolbarControl.Mode => 36 + gap,
            ToolbarControl.Properties or ToolbarControl.ClearAll or ToolbarControl.Settings or ToolbarControl.Hide
                => ActionSize.Height + gap,
            // Undo and Redo share a row in the expanded rail and stack in the minimal one.
            ToolbarControl.Undo or ToolbarControl.Redo => Minimal ? ActionSize.Height + 3
                : control == ToolbarControl.Redo && hidden.Contains(ToolbarControl.Undo) && hidden.Contains(ToolbarControl.Redo)
                    ? ActionSize.Height + gap : 0,
            _ => 0,
        };
    }

    public double VerticalReserveHiding(IReadOnlySet<ToolbarControl> hidden)
    {
        var room = 0.0;
        foreach (var control in hidden)
        {
            room += VerticalRoom(control, hidden);
        }
        return VerticalReserve - room;
    }
}

/// <summary>Port of ToolbarToolLayout.swift: tool families, widths and the visible part of the tool rail.</summary>
public static class ToolbarToolLayout
{
    public const double ButtonWidth = 44;

    /// <summary>The buttons a narrow toolbar moves below its tools.</summary>
    public static IReadOnlySet<ToolbarControl> SecondRowControls { get; } = new HashSet<ToolbarControl>
    {
        ToolbarControl.Undo, ToolbarControl.Redo, ToolbarControl.ClearAll, ToolbarControl.Properties, ToolbarControl.Settings,
        ToolbarControl.Hide,
    };

    private static readonly IReadOnlySet<ToolbarControl> NoneHidden = new HashSet<ToolbarControl>();

    /// <summary>
    /// The toolbar's width: exactly its tools and buttons on one row, so taking a button off leaves no gap. On a screen too
    /// narrow for that, the screen's width, with fewer tools showing. One row of full properties can make it wider
    /// (<see cref="GrownWidth"/>, Mac ToolbarWidthLayout).
    /// </summary>
    public static double HorizontalWidth(ReadOnlySpan<ShortcutCommand> items, double available,
                                         ToolbarSize size = ToolbarSize.Expanded, IReadOnlySet<ToolbarControl>? hidden = null)
    {
        hidden ??= NoneHidden;
        var metrics = new ToolbarMetrics(size);
        double toolWidth = Width(items, size);
        double limit = Math.Max(320, available);
        if (!UsesTwoRows(items, available, size, hidden))
        {
            return Math.Min(limit, toolWidth + metrics.RegularReserveHiding(hidden));
        }
        return Math.Min(limit, Math.Max(toolWidth + metrics.CompactReserveHiding(hidden), metrics.SecondRowWidth(hidden)));
    }

    /// <summary>Only a narrow screen, too narrow for one row, moves Undo, Redo and the other buttons below the tools.</summary>
    public static bool UsesTwoRows(ReadOnlySpan<ShortcutCommand> items, double available, ToolbarSize size = ToolbarSize.Expanded,
                                   IReadOnlySet<ToolbarControl>? hidden = null)
    {
        hidden ??= NoneHidden;
        var metrics = new ToolbarMetrics(size);
        double limit = Math.Max(320, available);
        if (hidden.IsSupersetOf(SecondRowControls) || limit >= metrics.CompactThreshold)
        {
            return false;
        }
        return Width(items, size) + metrics.RegularReserveHiding(hidden) > limit;
    }

    /// <summary>Mac ToolbarWidthLayout: a horizontal toolbar is as wide as its tools and buttons need, or wider when its
    /// content, such as one row of full properties, needs more room, up to <paramref name="limit"/>.</summary>
    public static double GrownWidth(double baseWidth, double limit, double idealContentWidth) =>
        double.IsFinite(idealContentWidth)
            ? Math.Min(Math.Max(baseWidth, Math.Ceiling(idealContentWidth)), Math.Max(baseWidth, limit))
            : baseWidth;

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
