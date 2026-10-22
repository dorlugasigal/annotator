using Annotator.App.Breaks;
using Annotator.App.Shell;
using Annotator.Platform.Windowing;
using Annotator.Workspace.Breaks;
using Microsoft.UI.Xaml.Controls;

namespace Annotator.App.Reference;

/// <summary>
/// Development only (UX reference capture): the break screen's face on its own at the Mac capture's size, as
/// UXReferenceCaptureTests.test10BreakTimer renders <c>BreakScreenFace(state:)</c>. The face is still: its ring does
/// not drain, and its buttons do nothing.
/// </summary>
internal sealed class BreakReferenceWindow : PopoverWindow
{
    public BreakReferenceWindow(WindowRegistry registry, BreakFace face, double width, double height)
        : base(registry, "Break timer")
    {
        var view = new BreakFaceView(preview: false, new BreakFaceActions(), reduceMotion: false);
        view.Root.Width = width;
        view.Root.Height = height;
        view.Apply(face);
        view.Enter(animated: false);
        var root = new Grid();
        root.Children.Add(view.Root);
        Surface = root;
    }

    protected override void FocusFirst()
    {
    }
}
