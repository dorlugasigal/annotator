// Port of apps/macos/Sources/AnnotationCore/BreakTimer.swift (specification 014).
// Swift nests Background and Palette inside BreakTimerSettings; C# would clash with the property names,
// so they are top-level enums with the same raw values.
using System.Globalization;
using System.Text;

namespace Annotator.Core;

/// <summary>Swift `BreakTimerSettings.Background`.</summary>
public enum BreakBackground { Moving, Blur, Plain }

/// <summary>Swift `BreakTimerSettings.Palette`.</summary>
public enum BreakPalette { Forest, Ocean, Sunset, Berry, Graphite }

public static class BreakBackgroundExtensions
{
    internal static readonly RawValueMap<BreakBackground> RawValues = new("moving", "blur", "plain");

    extension(BreakBackground background)
    {
        public string RawValue => RawValues.RawValue(background);

        public string Label => background switch
        {
            BreakBackground.Moving => "Moving colors",
            BreakBackground.Blur => "Blurred desktop",
            BreakBackground.Plain => "Plain",
            _ => throw new ArgumentOutOfRangeException(nameof(background)),
        };
    }

    extension(BreakBackground)
    {
        public static ValueList<BreakBackground> AllCases => RawValues.AllCases;

        public static BreakBackground? FromRawValue(string rawValue) => RawValues.Parse(rawValue);
    }
}

public static class BreakPaletteExtensions
{
    internal static readonly RawValueMap<BreakPalette> RawValues = new("forest", "ocean", "sunset", "berry", "graphite");

    extension(BreakPalette palette)
    {
        public string RawValue => RawValues.RawValue(palette);

        /// <summary>Swift `rawValue.capitalized`.</summary>
        public string Label => palette switch
        {
            BreakPalette.Forest => "Forest",
            BreakPalette.Ocean => "Ocean",
            BreakPalette.Sunset => "Sunset",
            BreakPalette.Berry => "Berry",
            BreakPalette.Graphite => "Graphite",
            _ => throw new ArgumentOutOfRangeException(nameof(palette)),
        };
    }

    extension(BreakPalette)
    {
        public static ValueList<BreakPalette> AllCases => RawValues.AllCases;

        public static BreakPalette? FromRawValue(string rawValue) => RawValues.Parse(rawValue);
    }
}

/// <summary>
/// Swift `BreakTimerSettings`: saved choices for the break screen. A missing key reads as its default, so profiles
/// from older versions and partial profiles keep working. Stored properties are public fields (see Element.cs).
/// </summary>
public struct BreakTimerSettings : IEquatable<BreakTimerSettings>
{
    public const int MinimumMinutes = 1;
    public const int MaximumMinutes = 180;
    public const int TitleLimit = 60;
    public const string DefaultTitle = "Coffee break";

    public long Minutes = 10;
    public string Title = DefaultTitle;
    public BreakBackground Background = BreakBackground.Moving;
    public BreakPalette Palette = BreakPalette.Forest;
    public bool ShowEndTime = true;
    public bool Chime = true;
    public bool CountOver = true;
    public bool AllDisplays = true;
    public bool LockWhenTouched = true;

    public BreakTimerSettings()
    {
    }

    public static IReadOnlyList<int> QuickMinutes { get; } = [5, 10, 15, 30];

    public readonly void Validate()
    {
        if (!(Minutes >= MinimumMinutes && Minutes <= MaximumMinutes))
        {
            throw CanvasError.InvalidDocument("A break lasts 1 to 180 minutes.");
        }
        if (!IsValidTitle(Title))
        {
            throw CanvasError.InvalidDocument("The break title can have up to 60 characters on one line.");
        }
    }

    public static bool IsValidTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        if (SwiftText.CharacterCount(title) > TitleLimit)
        {
            return false;
        }
        foreach (var rune in title.EnumerateRunes())
        {
            if (IsLineBreakOrControl(rune))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Keeps what can be kept of typed text: one line, no control characters, at most 60 characters.</summary>
    public static string CleanTitle(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var cleaned = new StringBuilder(text.Length);
        var count = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext() && count < TitleLimit)
        {
            var element = (string)elements.Current;
            var newline = false;
            var control = false;
            foreach (var rune in element.EnumerateRunes())
            {
                newline |= SwiftText.IsNewline(rune);
                control |= IsLineBreakOrControl(rune);
            }
            if (newline)
            {
                cleaned.Append(' ');
                count++;
            }
            else if (!control)
            {
                cleaned.Append(element);
                count++;
            }
        }
        return cleaned.ToString();
    }

    /// <summary>Emoji joiners are format characters, not control characters, so they stay allowed.</summary>
    private static bool IsLineBreakOrControl(Rune rune) =>
        SwiftText.IsNewline(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control;

    public readonly bool Equals(BreakTimerSettings other) =>
        Minutes == other.Minutes && SwiftText.Equals(Title, other.Title) && Background == other.Background
        && Palette == other.Palette && ShowEndTime == other.ShowEndTime && Chime == other.Chime
        && CountOver == other.CountOver && AllDisplays == other.AllDisplays && LockWhenTouched == other.LockWhenTouched;

    public override readonly bool Equals(object? obj) => obj is BreakTimerSettings other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(Minutes, SwiftText.GetHashCode(Title), Background, Palette);

    public static bool operator ==(BreakTimerSettings left, BreakTimerSettings right) => left.Equals(right);

    public static bool operator !=(BreakTimerSettings left, BreakTimerSettings right) => !left.Equals(right);
}

/// <summary>Swift `BreakCountdown`: the time left in a break. It follows the wall clock, so sleep and slow frames never make it drift.</summary>
public struct BreakCountdown : IEquatable<BreakCountdown>
{
    public const double MaximumSeconds = BreakTimerSettings.MaximumMinutes * 60;

    private DateTimeOffset end;
    private double? pausedRemaining;

    public BreakCountdown(long minutes, DateTimeOffset start)
    {
        if (!(minutes >= BreakTimerSettings.MinimumMinutes && minutes <= BreakTimerSettings.MaximumMinutes))
        {
            throw CanvasError.InvalidDocument("A break lasts 1 to 180 minutes.");
        }
        Total = minutes * 60;
        end = start.AddSeconds(Total);
    }

    /// <summary>The length the ring measures against. It grows when time is added.</summary>
    public double Total { get; private set; }

    public readonly bool IsPaused => pausedRemaining is not null;

    /// <summary>Seconds left; negative once time is up.</summary>
    public readonly double Remaining(DateTimeOffset now) => pausedRemaining ?? (end - now).TotalSeconds;

    public readonly bool IsTimeUp(DateTimeOffset now) => Remaining(now) <= 0;

    /// <summary>The part of the ring still filled, from 1 at the start to 0 at time up.</summary>
    public readonly double Fraction(DateTimeOffset now) => Total > 0 ? Math.Clamp(Remaining(now) / Total, 0, 1) : 0;

    /// <summary>When the break ends if it runs from now on. A paused break moves its end later as time passes.</summary>
    public readonly DateTimeOffset EndDate(DateTimeOffset now) => now.AddSeconds(Remaining(now));

    public void Pause(DateTimeOffset now)
    {
        if (pausedRemaining is null && Remaining(now) > 0)
        {
            pausedRemaining = Remaining(now);
        }
    }

    public void Resume(DateTimeOffset now)
    {
        if (pausedRemaining is { } left)
        {
            end = now.AddSeconds(left);
            pausedRemaining = null;
        }
    }

    public void TogglePause(DateTimeOffset now)
    {
        if (IsPaused)
        {
            Resume(now);
        }
        else
        {
            Pause(now);
        }
    }

    /// <summary>
    /// Adds or removes whole minutes. Removing stops at zero, which counts as time up. Adding after time is up starts a
    /// fresh countdown, so the ring fills again. Once time is up, removing does nothing and the time over keeps counting.
    /// </summary>
    public void Adjust(int minutes, DateTimeOffset now)
    {
        if (!(minutes > 0 || Remaining(now) > 0))
        {
            return;
        }
        var current = Math.Max(Remaining(now), 0);
        var next = Math.Min(Math.Max(current + minutes * 60.0, 0), MaximumSeconds);
        if (current == 0 && next > 0)
        {
            Total = next;
        }
        else if (minutes > 0)
        {
            Total = Math.Min(Math.Max(Total + minutes * 60.0, next), MaximumSeconds);
        }
        else
        {
            Total = Math.Max(Total, next);
        }
        if (pausedRemaining is not null)
        {
            pausedRemaining = next;
            if (next == 0)
            {
                pausedRemaining = null;
                end = now;
            }
        }
        else
        {
            end = now.AddSeconds(next);
        }
    }

    /// <summary>The countdown text: <c>9:59</c>, <c>1:05:00</c>, or the time over as <c>+1:23</c> when <paramref name="countOver"/> is on.</summary>
    public readonly string Label(DateTimeOffset now, bool countOver)
    {
        var left = Remaining(now);
        if (!double.IsFinite(left))
        {
            return "0:00";
        }
        if (left > 0)
        {
            return Clock((long)Math.Ceiling(left));
        }
        var over = (long)Math.Floor(-left);
        return countOver && over > 0 ? "+" + Clock(over) : "0:00";
    }

    /// <summary>Seconds until <see cref="Label"/> shows a different value.</summary>
    public readonly double SecondsUntilLabelChanges(DateTimeOffset now)
    {
        var left = Remaining(now);
        if (!double.IsFinite(left) || IsPaused)
        {
            return 1;
        }
        var step = left > 0 ? left - Math.Floor(left) : Math.Ceiling(-left) - -left;
        return step > 0.000_1 ? step : 1;
    }

    public static string Clock(long seconds)
    {
        var value = Math.Max(seconds, 0);
        long hours = value / 3_600, minutes = value % 3_600 / 60, rest = value % 60;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{rest:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{rest:00}");
    }

    /// <summary>Words for Narrator, such as "1 hour 5 minutes" or "45 seconds".</summary>
    public static string Spoken(long seconds)
    {
        var value = Math.Max(seconds, 0);
        long hours = value / 3_600, minutes = value % 3_600 / 60, rest = value % 60;
        var parts = new List<string>(3);
        void Unit(long count, string name)
        {
            if (count > 0)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{count} {name}{(count == 1 ? "" : "s")}"));
            }
        }
        Unit(hours, "hour");
        Unit(minutes, "minute");
        Unit(rest, "second");
        return parts.Count == 0 ? "0 seconds" : string.Join(' ', parts);
    }

    public readonly bool Equals(BreakCountdown other) =>
        end == other.end && pausedRemaining == other.pausedRemaining && Total == other.Total;

    public override readonly bool Equals(object? obj) => obj is BreakCountdown other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(end, pausedRemaining, Total);

    public static bool operator ==(BreakCountdown left, BreakCountdown right) => left.Equals(right);

    public static bool operator !=(BreakCountdown left, BreakCountdown right) => !left.Equals(right);
}
