namespace Annotator.Core.Tutorial;

/// <summary>One key of the tutorial's keyboard: its id, where its row starts it and how wide it is, in key units.</summary>
public readonly record struct TutorialKey(string Id, int Row, double X, double Width);

/// <summary>
/// The left part of a keyboard that the tutorial draws for shortcuts with modifiers and for Escape (Swift
/// <c>TutorialKeyboard</c>): the Escape row, the number row and the three letter rows to B and 5, and the bottom row of the
/// platform. Keys the shortcut uses light up. The keyboard carries on past its right edge, which fades out.
/// </summary>
public static class TutorialKeyboard
{
    /// <summary>A key unit, the gap between keys and a key's height, in points.</summary>
    public const double Unit = 22;
    public const double Gap = 3;
    public const double KeyHeight = 20;
    public const int Rows = 6;

    /// <summary>How much of the keyboard shows, in key units; keys past it are cut and the edge fades.</summary>
    public const double VisibleUnits = 6.5;

    /// <summary>The width that fades out at the right edge, in points.</summary>
    public const double FadeWidth = 36;

    public static double Width => VisibleUnits * Unit;

    public static double Height => Rows * KeyHeight + (Rows - 1) * Gap;

    /// <summary>The keys, row by row from the Escape row; <paramref name="mac"/> chooses the platform's bottom row.</summary>
    public static IReadOnlyList<TutorialKey> Keys(bool mac)
    {
        var keys = new List<TutorialKey>();
        void Row(int row, params (string Id, double Width)[] items)
        {
            double x = 0;
            foreach ((string id, double width) in items)
            {
                keys.Add(new TutorialKey(id, row, x, width));
                x += width;
            }
        }
        Row(0, ("esc", 1), ("f1", 1), ("f2", 1), ("f3", 1), ("f4", 1), ("f5", 1), ("f6", 1));
        Row(1, ("`", 1), ("1", 1), ("2", 1), ("3", 1), ("4", 1), ("5", 1), ("6", 1));
        Row(2, ("tab", 1.5), ("q", 1), ("w", 1), ("e", 1), ("r", 1), ("t", 1));
        Row(3, ("caps", 1.75), ("a", 1), ("s", 1), ("d", 1), ("f", 1), ("g", 1));
        Row(4, ("shift", 2.25), ("z", 1), ("x", 1), ("c", 1), ("v", 1), ("b", 1));
        if (mac)
        {
            Row(5, ("fn", 1), ("control", 1.25), ("option", 1.25), ("command", 1.5), ("space", 3));
        }
        else
        {
            Row(5, ("ctrl", 1.5), ("win", 1.25), ("alt", 1.25), ("space", 3));
        }
        return keys;
    }

    /// <summary>Where <paramref name="key"/> sits, in points from the keyboard's top-left corner.</summary>
    public static Rect Frame(TutorialKey key) =>
        new(key.X * Unit + Gap / 2, key.Row * (KeyHeight + Gap), key.Width * Unit - Gap, KeyHeight);

    /// <summary>
    /// The keyboard's key ids for a shortcut's keycaps: Windows' "Ctrl", "Shift", "Alt", "Win", "Esc" and characters, or the
    /// Mac's ⌃ ⇧ ⌥ ⌘ symbols, "esc" and characters. Null when a key is not on the keyboard.
    /// </summary>
    public static IReadOnlyList<string>? Ids(IEnumerable<string> keycaps, bool mac)
    {
        ArgumentNullException.ThrowIfNull(keycaps);
        IReadOnlyList<TutorialKey> keys = Keys(mac);
        var ids = new List<string>();
        foreach (string cap in keycaps)
        {
            IEnumerable<string> parts = mac && cap.Length > 1 && cap != "esc" && cap != "return"
                ? cap.Select(character => character.ToString())
                : [cap];
            foreach (string part in parts)
            {
                string id = part switch
                {
                    "Ctrl" => "ctrl",
                    "Shift" or "⇧" => "shift",
                    "Alt" => "alt",
                    "Win" => "win",
                    "Esc" or "esc" => "esc",
                    "⌃" => "control",
                    "⌥" => "option",
                    "⌘" => "command",
                    _ => part.ToLowerInvariant(),
                };
                if (!keys.Any(key => key.Id == id))
                {
                    return null;
                }
                ids.Add(id);
            }
        }
        return ids;
    }

    /// <summary>
    /// Whether the card shows the keyboard for these keys: a shortcut with a modifier, or Escape, whose keys are all on the
    /// keyboard. Single keys such as 7 or B show only in the line.
    /// </summary>
    public static bool Shows(IEnumerable<string> keycaps, bool mac)
    {
        if (Ids(keycaps, mac) is not { Count: > 0 } ids)
        {
            return false;
        }
        return ids.Any(id => id is "esc" or "ctrl" or "shift" or "alt" or "win" or "control" or "option" or "command");
    }
}
