using Annotator.App.Shell;
using Annotator.Core;
using Annotator.Platform.Displays;
using Annotator.Platform.Windowing;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using CompositionTarget = Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop;

namespace Annotator.App.Breaks;

/// <summary>What one break window passes back to the break timer host.</summary>
internal sealed class BreakWindowInput
{
    /// <summary>A key went down, with whether Ctrl, Alt or Win is held; returns whether the break screen used it.</summary>
    public required Func<VirtualKey, bool, bool> KeyDown { get; init; }
    public required Action PointerMoved { get; init; }
    public required Action PointerPressed { get; init; }
    /// <summary>The window was activated (true) or lost activation (false).</summary>
    public required Action<bool> Activation { get; init; }
    /// <summary>Alt+F4 or the system's close command: the break screen decides what it means.</summary>
    public required Action CloseRequested { get; init; }
}

/// <summary>
/// One display's break screen (Mac BreakWindow, spec 014): a borderless, topmost window over the whole monitor, taskbar
/// included, with no taskbar button or Alt+Tab entry. It takes the keyboard so the break screen sees every key. It is an
/// ordinary readable window, so a meeting app that shares the whole display shows the timer.
/// </summary>
internal sealed partial class BreakWindow : Window
{
    private readonly WindowRegistration _registration;
    private readonly BreakWindowInput _input;
    private readonly ContentControl _focus;
    private bool _closing;
    private bool _focusWhenLoaded;

    public BreakWindow(WindowRegistry registry, DisplayInfo display, BreakTimerSettings settings, BreakFaceView view, BreakWindowInput input)
    {
        _input = input;
        View = view;
        Display = display;
        Title = "Annotator break timer";
        Hwnd = WindowChrome.Configure(this, noActivate: false);
        if (settings.Background == BreakBackground.Blur)
        {
            // Mac Blurred desktop: the system blurs what is behind the window; no screen pixels are read.
            SystemBackdrop = new BlurBackdrop();
        }
        _registration = registry.Register((nint)Hwnd, WindowLayer.Guide);
        // A control that holds the keyboard when no button has it, so key presses always reach the break screen.
        _focus = new ContentControl
        {
            Content = view.Root,
            IsTabStop = true,
            UseSystemFocusVisuals = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        _focus.PreviewKeyDown += OnPreviewKeyDown;
        _focus.Loaded += (_, _) =>
        {
            if (_focusWhenLoaded)
            {
                _focusWhenLoaded = false;
                TakeKeyboard();
            }
        };
        _focus.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, _) => _input.PointerMoved()), handledEventsToo: true);
        _focus.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => _input.PointerPressed()), handledEventsToo: true);
        _focus.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, _) => _input.PointerPressed()), handledEventsToo: true);
        Content = _focus;
        Activated += (_, args) => _input.Activation(args.WindowActivationState != WindowActivationState.Deactivated);
        AppWindow.Closing += (_, args) =>
        {
            if (!_closing)
            {
                args.Cancel = true;
                _input.CloseRequested();
            }
        };
        Closed += (_, _) => _registration.Dispose();
        AppWindow.MoveAndResize(new RectInt32(display.Bounds.Left, display.Bounds.Top, display.Bounds.Width, display.Bounds.Height));
    }

    public HWND Hwnd { get; }

    public BreakFaceView View { get; }

    public DisplayInfo Display { get; }

    /// <summary>Shows the window over everything; <paramref name="activate"/> also gives it the keyboard.</summary>
    public void Present(bool activate)
    {
        AppWindow.Show(activateWindow: activate);
        WindowChrome.KeepOnTop(Hwnd);
        if (activate)
        {
            PInvoke.SetForegroundWindow(Hwnd);
            TakeKeyboard();
        }
    }

    /// <summary>Puts the keyboard on the break screen, unless one of its buttons already has it.</summary>
    public void TakeKeyboard()
    {
        // Right after Show the content has no XamlRoot yet, and WinUI rejects a null root: focus once it has loaded.
        if (_focus.XamlRoot is not { } root)
        {
            _focusWhenLoaded = true;
            return;
        }
        if (FocusManager.GetFocusedElement(root) is not Button)
        {
            _focus.Focus(FocusState.Programmatic);
        }
    }

    public void Shutdown()
    {
        _closing = true;
        Close();
    }

    /// <summary>
    /// Every key goes to the break screen first. While locking is armed or arming, it takes all of them; without locking
    /// it uses Escape, Enter, Space, the arrows, plus and minus, and lets Tab, Space and Enter work on a focused button.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        bool command = Down(VirtualKey.Control) || Down(VirtualKey.Menu) || Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows);
        if (_input.KeyDown(args.Key, command))
        {
            args.Handled = true;
        }
    }

    private static bool Down(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

    /// <summary>
    /// The system's acrylic blur behind the window, kept active on every display: a window on a second display is never the
    /// active one, and the default backdrop turns solid on inactive windows.
    /// </summary>
    private sealed partial class BlurBackdrop : SystemBackdrop
    {
        private DesktopAcrylicController? _controller;

        protected override void OnTargetConnected(CompositionTarget connectedTarget, XamlRoot xamlRoot)
        {
            base.OnTargetConnected(connectedTarget, xamlRoot);
            _controller = new DesktopAcrylicController
            {
                Kind = DesktopAcrylicKind.Base,
                TintColor = Windows.UI.Color.FromArgb(255, 10, 14, 12),
                TintOpacity = 0.25f,
                LuminosityOpacity = 0.6f,
                FallbackColor = Windows.UI.Color.FromArgb(255, 10, 14, 12),
            };
            _controller.AddSystemBackdropTarget(connectedTarget);
            _controller.SetSystemBackdropConfiguration(new SystemBackdropConfiguration { IsInputActive = true, Theme = SystemBackdropTheme.Dark });
        }

        protected override void OnTargetDisconnected(CompositionTarget disconnectedTarget)
        {
            _controller?.RemoveSystemBackdropTarget(disconnectedTarget);
            _controller?.Dispose();
            _controller = null;
            base.OnTargetDisconnected(disconnectedTarget);
        }
    }
}
