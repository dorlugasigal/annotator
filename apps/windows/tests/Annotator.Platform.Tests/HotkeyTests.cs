using Annotator.Platform.Hotkeys;
using Annotator.Platform.Threading;
using Windows.Win32;

namespace Annotator.Platform.Tests;

public sealed class HotkeyTests
{
    private const int VK_F13 = 0x7C;
    private const int VK_F24 = 0x87;
    private const HotkeyModifiers CtrlAltShift = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift;

    [Fact]
    public async Task RegistersAnUnusualChordAndReportsASecondRegistrationAsAlreadyUsed()
    {
        using DispatcherThread first = DispatcherThread.Start("Annotator test hotkeys 1");
        using DispatcherThread second = DispatcherThread.Start("Annotator test hotkeys 2");
        HotkeyService service = await first.Run(() => new HotkeyService());
        HotkeyService other = await second.Run(() => new HotkeyService());
        try
        {
            HotkeyRegistrationResult.Registered registered = await first.Run(() => RegisterFirstFreeChord(service));

            HotkeyRegistrationResult again = await first.Run(() => service.Register(registered.Chord));
            HotkeyRegistrationResult elsewhere = await second.Run(() => other.Register(registered.Chord));

            Assert.Equal(new HotkeyRegistrationResult.AlreadyUsed(registered.Chord), again);
            Assert.Equal(new HotkeyRegistrationResult.AlreadyUsed(registered.Chord), elsewhere);
            Assert.Equal(registered.Chord, Assert.Single(await first.Run(() => service.Registrations)).Value);

            // Disposing releases the chord, so another service can take it.
            await first.Run(service.Dispose);
            HotkeyRegistrationResult afterDispose = await second.Run(() => other.Register(registered.Chord));
            var taken = Assert.IsType<HotkeyRegistrationResult.Registered>(afterDispose);
            Assert.True(await second.Run(() => other.Unregister(taken.Id)));
            Assert.False(await second.Run(() => other.Unregister(taken.Id)));
        }
        finally
        {
            await first.Run(service.Dispose);
            await second.Run(other.Dispose);
        }
    }

    [Fact]
    public async Task RaisesPressedForItsOwnHotkeyMessages()
    {
        using DispatcherThread thread = DispatcherThread.Start("Annotator test hotkeys");
        HotkeyService service = await thread.Run(() => new HotkeyService());
        var pressed = new TaskCompletionSource<HotkeyPressedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            HotkeyRegistrationResult.Registered registered = await thread.Run(() =>
            {
                service.Pressed += (_, args) => pressed.TrySetResult(args);
                HotkeyRegistrationResult.Registered result = RegisterFirstFreeChord(service);

                // WM_HOTKEY for another id is ignored; then deliver the registered one.
                nint lParam = Win32.MakeLParam((int)result.Chord.Modifiers, result.Chord.VirtualKey);
                Assert.True(Win32.Post((nint)service.Handle, PInvoke.WM_HOTKEY, (nuint)(result.Id.Value + 100), lParam));
                Assert.True(Win32.Post((nint)service.Handle, PInvoke.WM_HOTKEY, (nuint)result.Id.Value, lParam));
                return result;
            });

            HotkeyPressedEventArgs args = await pressed.Task.WaitAsync(TestTimeouts.Work, TestContext.Current.CancellationToken);
            Assert.Equal(registered.Id, args.Id);
            Assert.Equal(registered.Chord, args.Chord);
        }
        finally
        {
            await thread.Run(service.Dispose);
        }
    }

    [Fact]
    public async Task ReplacingKeepsTheCurrentChordUntilTheNewOneIsRegistered()
    {
        using DispatcherThread first = DispatcherThread.Start("Annotator test hotkeys 1");
        using DispatcherThread second = DispatcherThread.Start("Annotator test hotkeys 2");
        HotkeyService service = await first.Run(() => new HotkeyService());
        HotkeyService other = await second.Run(() => new HotkeyService());
        try
        {
            HotkeyRegistrationResult.Registered current = await first.Run(() => RegisterFirstFreeChord(service));
            // Another app (here another window) owns a second unusual chord.
            HotkeyRegistrationResult.Registered taken = await second.Run(() => RegisterFirstFreeChord(other));

            HotkeyRegistrationResult refused = await first.Run(() => service.Replace(current.Id, taken.Chord));
            Assert.Equal(new HotkeyRegistrationResult.AlreadyUsed(taken.Chord), refused);
            HotkeyRegistrationResult invalid = await first.Run(() => service.Replace(current.Id, new HotkeyChord(CtrlAltShift, 0)));
            Assert.IsType<HotkeyRegistrationResult.Refused>(invalid);
            Assert.Equal(current.Chord, Assert.Single(await first.Run(() => service.Registrations)).Value);

            HotkeyRegistrationResult same = await first.Run(() => service.Replace(current.Id, current.Chord));
            Assert.Equal(current, same);

            // A free chord replaces the current one, which is then free for others.
            await second.Run(() => other.Unregister(taken.Id));
            var replaced = Assert.IsType<HotkeyRegistrationResult.Registered>(await first.Run(() => service.Replace(current.Id, taken.Chord)));
            Assert.Equal(taken.Chord, Assert.Single(await first.Run(() => service.Registrations)).Value);
            Assert.IsType<HotkeyRegistrationResult.Registered>(await second.Run(() => other.Register(current.Chord)));
            Assert.NotEqual(current.Id, replaced.Id);

            // Without a current registration, Replace registers.
            await first.Run(() => service.Unregister(replaced.Id));
            Assert.IsType<HotkeyRegistrationResult.Registered>(await first.Run(() => service.Replace(null, taken.Chord)));
        }
        finally
        {
            await first.Run(service.Dispose);
            await second.Run(other.Dispose);
        }
    }

    [Theory]
    [InlineData(HotkeyModifiers.Control, 0x00, HotkeyRefusal.InvalidKey)]
    [InlineData(HotkeyModifiers.Control, 0xFF, HotkeyRefusal.InvalidKey)]
    [InlineData(HotkeyModifiers.Control, 0x01, HotkeyRefusal.InvalidKey)]
    [InlineData(HotkeyModifiers.Control, 0x10, HotkeyRefusal.ModifierAsKey)]
    [InlineData(HotkeyModifiers.Alt, 0x5B, HotkeyRefusal.ModifierAsKey)]
    [InlineData((HotkeyModifiers)0x4000, VK_F13, HotkeyRefusal.InvalidModifiers)]
    public async Task RefusesChordsThatCannotBeShortcuts(HotkeyModifiers modifiers, int virtualKey, HotkeyRefusal reason)
    {
        using DispatcherThread thread = DispatcherThread.Start("Annotator test hotkeys");
        HotkeyRegistrationResult result = await thread.Run(() =>
        {
            using var service = new HotkeyService();
            return service.Register(new HotkeyChord(modifiers, virtualKey));
        });

        var chord = new HotkeyChord(modifiers, virtualKey);
        Assert.Equal(new HotkeyRegistrationResult.Refused(chord, reason, 0), result);
    }

    [Fact]
    public async Task RequiresItsOwnThread()
    {
        using DispatcherThread thread = DispatcherThread.Start("Annotator test hotkeys");
        HotkeyService service = await thread.Run(() => new HotkeyService());
        try
        {
            Assert.Throws<InvalidOperationException>(() => service.Register(new HotkeyChord(CtrlAltShift, VK_F13)));
        }
        finally
        {
            await thread.Run(service.Dispose);
        }
    }

    /// <summary>
    /// Registers Ctrl+Alt+Shift+F13, or the next function key up to F24 if another app on this
    /// machine already owns it. Never registers real defaults such as Ctrl+1 or Ctrl+2.
    /// </summary>
    private static HotkeyRegistrationResult.Registered RegisterFirstFreeChord(HotkeyService service)
    {
        for (int key = VK_F13; key <= VK_F24; key++)
        {
            HotkeyRegistrationResult result = service.Register(new HotkeyChord(CtrlAltShift, key));
            switch (result)
            {
                case HotkeyRegistrationResult.Registered registered:
                    return registered;
                case HotkeyRegistrationResult.AlreadyUsed:
                    continue;
                default:
                    Assert.Fail($"Unexpected result {result}.");
                    break;
            }
        }
        Assert.Fail("Ctrl+Alt+Shift+F13 to F24 are all taken by other apps on this machine.");
        return null!;
    }
}
