using System.Runtime.InteropServices;
using Annotator.Core;
using Annotator.Platform;
using Annotator.Platform.Capture;
using Annotator.Platform.Windowing;
using Microsoft.UI.Dispatching;

namespace Annotator.App.Pickers;

/// <summary>
/// Pick from screen for the color popover: one <see cref="ScreenColorSampler"/> at a time, its result brought back to the
/// app thread. Nothing runs until a pick starts, and the sampler's thread, window and capture end with the pick.
/// </summary>
internal sealed class ScreenColorPickerHost(WindowRegistry registry, DispatcherQueue queue) : IScreenColorPicker
{
    /// <summary>What the popover shows when the screen cannot be read.</summary>
    public const string FailureMessage = "Pick from screen could not read the screen. Try again, or enter a hex color.";

    private ScreenColorSampler? _sampler;
    private Action<ScreenPickResult>? _completed;

    public bool IsPicking => _sampler is not null;

    public void Pick(Action<ScreenPickResult> completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        if (_sampler is not null)
        {
            return;
        }
        ScreenColorSampler? sampler = null;
        try
        {
            sampler = ScreenColorSampler.Start(registry, sample => queue.TryEnqueue(() => Finish(sampler!, sample)));
        }
        catch (Exception error) when (error is InvalidOperationException or NativeCallException or COMException)
        {
            StartupLog.Note("screen-color", error.ToString().ReplaceLineEndings(" | "));
            completed(new ScreenPickResult.Failed(FailureMessage));
            return;
        }
        _sampler = sampler;
        _completed = completed;
    }

    public void Cancel()
    {
        if (_sampler is { } sampler)
        {
            _sampler = null;
            _completed = null;
            sampler.Dispose();
        }
    }

    private void Finish(ScreenColorSampler sampler, ScreenColorSample sample)
    {
        if (!ReferenceEquals(_sampler, sampler) || _completed is not { } completed)
        {
            // Cancelled meanwhile; the sampler is already gone.
            return;
        }
        _sampler = null;
        _completed = null;
        sampler.Dispose();
        switch (sample)
        {
            case ScreenColorSample.Picked picked:
                completed(new ScreenPickResult.Picked(new RGBA(picked.Red / 255.0, picked.Green / 255.0, picked.Blue / 255.0)));
                break;
            case ScreenColorSample.Failed failed:
                StartupLog.Note("screen-color", $"{failed.Operation}: {failed.Error}".ReplaceLineEndings(" | "));
                completed(new ScreenPickResult.Failed(FailureMessage));
                break;
            default:
                completed(new ScreenPickResult.Cancelled());
                break;
        }
    }
}
