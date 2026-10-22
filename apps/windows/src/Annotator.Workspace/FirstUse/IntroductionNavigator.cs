namespace Annotator.Workspace.FirstUse;

/// <summary>The tour's pages (Mac IntroductionPage): draw, color, focus and an optional setup page.</summary>
public enum IntroductionPage
{
    Draw,
    Color,
    Focus,
    Setup,
}

public static class IntroductionPageExtensions
{
    public static IReadOnlyList<IntroductionPage> All { get; } = [IntroductionPage.Draw, IntroductionPage.Color, IntroductionPage.Focus, IntroductionPage.Setup];

    extension(IntroductionPage page)
    {
        /// <summary>The step's name: its button label and help.</summary>
        public string Title => page switch
        {
            IntroductionPage.Draw => "Make a point",
            IntroductionPage.Color => "Make it yours",
            IntroductionPage.Focus => "Lead the way",
            IntroductionPage.Setup => "A little setup",
            _ => throw new ArgumentOutOfRangeException(nameof(page)),
        };

        /// <summary>Seconds for the page's illustration to play once. Nothing loops and nothing advances the page.</summary>
        public double StoryDuration => page switch
        {
            IntroductionPage.Draw => 4.8,
            IntroductionPage.Color => 3.6,
            IntroductionPage.Focus => 4.2,
            IntroductionPage.Setup => 1.1,
            _ => throw new ArgumentOutOfRangeException(nameof(page)),
        };

        /// <summary>What the illustration shows, for screen readers; null for the setup page, which has none.</summary>
        public string? IllustrationDescription => page switch
        {
            IntroductionPage.Draw =>
                "A pen circles a bar on a chart. The pointer clicks a button, and the circle stays. Then the drawing hides and comes back.",
            IntroductionPage.Color =>
                "The palette switches to a gradient. A rounded outline draws itself, a light fill fades in and the label \"Make your point\" appears.",
            IntroductionPage.Focus =>
                "The pointer moves to a small price. A ring appears around it, the rest of the screen dims and a zoom lens makes the price bigger.",
            IntroductionPage.Setup => null,
            _ => throw new ArgumentOutOfRangeException(nameof(page)),
        };

        /// <summary>"Step 1 of 4": the step button's value.</summary>
        public string StepValue => $"Step {(int)page + 1} of {All.Count}";
    }
}

/// <summary>Which way the tour moved: forward pages enter from the right, Back from the left.</summary>
public enum IntroductionDirection
{
    Forward,
    Backward,
}

/// <summary>One time a page's story started: with motion, or as its final still.</summary>
public readonly record struct IntroductionPlayback(IntroductionPage Page, bool Animated);

/// <summary>
/// Port of the Mac's IntroductionNavigator: the page, the direction of the last move, and the playback token that restarts
/// a page's story. Pages change only through explicit navigation; a story never advances the page. Use it on one thread.
/// </summary>
public sealed class IntroductionNavigator
{
    private const int History = 24;
    private readonly List<IntroductionPlayback> _playbacks = [];
    private readonly List<IntroductionPage> _finished = [];

    /// <summary>Raised after the page, the presentation or the playback token changes.</summary>
    public event Action? Changed;

    public IntroductionPage Page { get; private set; } = IntroductionPage.Draw;

    /// <summary>The page shown before the last move, so a transition knows what leaves.</summary>
    public IntroductionPage PreviousPage { get; private set; } = IntroductionPage.Draw;

    /// <summary>Read by the page transition when a page is inserted or removed, so Back always moves right.</summary>
    public IntroductionDirection Direction { get; private set; } = IntroductionDirection.Forward;

    /// <summary>Changes for every replay and every presentation change; each value starts the page's story again.</summary>
    public int PlaybackToken { get; private set; }

    public bool IsPresented { get; private set; }

    /// <summary>Freezes every illustration at one moment. Used for design review frames and tests.</summary>
    public double? ReviewProgress { get; set; }

    /// <summary>Stretches story time, for tests; 1 is real time.</summary>
    public double StoryTimeScale { get; set; } = 1;

    public bool CanGoBack => (int)Page > 0;

    public bool CanGoForward => (int)Page < IntroductionPageExtensions.All.Count - 1;

    /// <summary>The last 24 stories that started, oldest first.</summary>
    public IReadOnlyList<IntroductionPlayback> Playbacks => _playbacks;

    /// <summary>The last 24 stories that finished, oldest first.</summary>
    public IReadOnlyList<IntroductionPage> FinishedPlaybacks => _finished;

    public void Select(IntroductionPage target)
    {
        if (target == Page)
        {
            return;
        }
        Direction = target > Page ? IntroductionDirection.Forward : IntroductionDirection.Backward;
        PreviousPage = Page;
        Page = target;
        Changed?.Invoke();
    }

    /// <summary>One page in <paramref name="direction"/>; false at the first or last page.</summary>
    public bool Move(IntroductionDirection direction)
    {
        int target = (int)Page + (direction == IntroductionDirection.Forward ? 1 : -1);
        if (target < 0 || target >= IntroductionPageExtensions.All.Count)
        {
            return false;
        }
        Select((IntroductionPage)target);
        return true;
    }

    /// <summary>Plays the page's story again, while the tour shows.</summary>
    public void Replay()
    {
        if (!IsPresented)
        {
            return;
        }
        PlaybackToken++;
        Changed?.Invoke();
    }

    /// <summary>Hiding the window restarts the story in its final state, which also ends any running animation.</summary>
    public void SetPresented(bool visible)
    {
        if (visible == IsPresented)
        {
            return;
        }
        IsPresented = visible;
        PlaybackToken++;
        Changed?.Invoke();
    }

    public void NotePlayback(IntroductionPlayback playback)
    {
        _playbacks.Add(playback);
        if (_playbacks.Count > History)
        {
            _playbacks.RemoveRange(0, _playbacks.Count - History);
        }
    }

    public void NoteFinished(IntroductionPage page)
    {
        _finished.Add(page);
        if (_finished.Count > History)
        {
            _finished.RemoveRange(0, _finished.Count - History);
        }
    }
}
