using Annotator.Core;
using Annotator.Workspace.Shortcuts;

namespace Annotator.Workspace.Settings;

/// <summary>
/// Port of GlobalShortcutKind in ToolbarShortcutSettings.swift: the keys that work in any app, in the Mac's order. They
/// need Ctrl, Alt or Win on Windows (WIN-INP-04).
/// </summary>
public enum GlobalShortcutKind
{
    Draw,
    ShowHide,
    HideOnly,
    LiveZoom,
    BreakTimer,
}

public static class GlobalShortcutKindExtensions
{
    extension(GlobalShortcutKind)
    {
        public static IReadOnlyList<GlobalShortcutKind> AllCases =>
            [GlobalShortcutKind.Draw, GlobalShortcutKind.ShowHide, GlobalShortcutKind.HideOnly, GlobalShortcutKind.LiveZoom,
             GlobalShortcutKind.BreakTimer];

        /// <summary>The kind of a workspace action.</summary>
        public static GlobalShortcutKind For(GlobalShortcutAction action) => action switch
        {
            GlobalShortcutAction.ResumeDrawing => GlobalShortcutKind.Draw,
            GlobalShortcutAction.ShowHide => GlobalShortcutKind.ShowHide,
            GlobalShortcutAction.HideOnly => GlobalShortcutKind.HideOnly,
            GlobalShortcutAction.LiveZoom => GlobalShortcutKind.LiveZoom,
            GlobalShortcutAction.BreakTimer => GlobalShortcutKind.BreakTimer,
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    extension(GlobalShortcutKind kind)
    {
        /// <summary>The Mac's raw value, part of the identifier "global-shortcut-&lt;raw value&gt;" (WIN-PAR-04).</summary>
        public string RawValue => kind switch
        {
            GlobalShortcutKind.Draw => "draw",
            GlobalShortcutKind.ShowHide => "showHide",
            GlobalShortcutKind.HideOnly => "hideOnly",
            GlobalShortcutKind.LiveZoom => "liveZoom",
            GlobalShortcutKind.BreakTimer => "breakTimer",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        public GlobalShortcutAction Action => kind switch
        {
            GlobalShortcutKind.Draw => GlobalShortcutAction.ResumeDrawing,
            GlobalShortcutKind.ShowHide => GlobalShortcutAction.ShowHide,
            GlobalShortcutKind.HideOnly => GlobalShortcutAction.HideOnly,
            GlobalShortcutKind.LiveZoom => GlobalShortcutAction.LiveZoom,
            GlobalShortcutKind.BreakTimer => GlobalShortcutAction.BreakTimer,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        public string Title => kind switch
        {
            GlobalShortcutKind.Draw => "Draw",
            GlobalShortcutKind.ShowHide => "Show / hide",
            GlobalShortcutKind.HideOnly => "Hide only",
            GlobalShortcutKind.LiveZoom => "Live zoom",
            GlobalShortcutKind.BreakTimer => "Break timer",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        public string Detail => kind switch
        {
            GlobalShortcutKind.Draw => "Start drawing, or go back to drawing from your apps.",
            GlobalShortcutKind.ShowHide => "Show or hide your drawings and the toolbar.",
            GlobalShortcutKind.HideOnly => "Hide drawings and the toolbar. Covers hide too.",
            GlobalShortcutKind.LiveZoom => "Start or stop full-screen zoom.",
            GlobalShortcutKind.BreakTimer => "Start or end a break countdown.",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        /// <summary>The Mac's SF Symbol name; Windows maps it through the icon table (D-6).</summary>
        public string Symbol => kind switch
        {
            GlobalShortcutKind.Draw => "pencil.tip",
            GlobalShortcutKind.ShowHide => "pencil.tip.crop.circle",
            GlobalShortcutKind.HideOnly => "eye.slash",
            GlobalShortcutKind.LiveZoom => "plus.magnifyingglass",
            GlobalShortcutKind.BreakTimer => "timer",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        /// <summary>Mac value(in:): the shortcut the kind uses now, with the Windows defaults (D-5).</summary>
        public ActivationShortcut Value(in WorkspacePreferences preferences) => WindowsShortcuts.Shortcut(kind.Action, preferences);
    }
}

/// <summary>
/// Shortcuts that Windows keeps for itself (WIN-INP-04), from Microsoft's list of Windows keyboard shortcuts: the Windows
/// key with any letter, the taskbar's Windows key and number shortcuts, and the documented Windows key combinations with
/// Shift, Ctrl or Alt. Windows takes these keys before any app, so a shortcut there could never work.
/// </summary>
public static class ReservedShortcuts
{
    private static readonly HashSet<(ChordModifiers Modifiers, char Key)> s_reserved = Build();

    /// <summary>True when Windows keeps <paramref name="chord"/> for itself.</summary>
    public static bool IsReserved(ShortcutChord chord)
    {
        if (!chord.Modifiers.HasFlag(ChordModifiers.Win) || chord.Key.Length != 1)
        {
            return false;
        }
        char key = char.ToUpperInvariant(chord.Key[0]);
        // The taskbar's shortcuts use the number row; keypad digits carry their own virtual key.
        if (key is >= '0' and <= '9' && chord.VirtualKey is null)
        {
            ChordModifiers others = chord.Modifiers & ~ChordModifiers.Win;
            return others is ChordModifiers.None or ChordModifiers.Shift or ChordModifiers.Ctrl or ChordModifiers.Alt
                or (ChordModifiers.Ctrl | ChordModifiers.Shift);
        }
        return s_reserved.Contains((chord.Modifiers, key));
    }

    private static HashSet<(ChordModifiers, char)> Build()
    {
        var reserved = new HashSet<(ChordModifiers, char)>();
        for (char letter = 'A'; letter <= 'Z'; letter++)
        {
            reserved.Add((ChordModifiers.Win, letter));
        }
        void Add(ChordModifiers others, string letters)
        {
            foreach (char letter in letters)
            {
                reserved.Add((ChordModifiers.Win | others, letter));
            }
        }
        Add(ChordModifiers.Shift, "MRSV");
        Add(ChordModifiers.Ctrl, "CDFMNOQSV");
        Add(ChordModifiers.Alt, "BDGKRT");
        Add(ChordModifiers.Ctrl | ChordModifiers.Shift, "B");
        return reserved;
    }
}
