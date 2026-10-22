using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Platform;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Export;
using Annotator.Rendering.Imaging;
using Annotator.Rendering.Text;
using Annotator.Workspace;
using Annotator.Workspace.Files;
using Microsoft.UI.Dispatching;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Annotator.App.Files;

/// <summary>
/// Mac insertImage's decoding for pasted and inserted images: the first frame with its orientation applied, within the
/// raster limits (RasterAsset.decode), stored again as a PNG of its pixels (PNGExporter.encode). Safe on any thread.
/// </summary>
internal static class ImageImport
{
    /// <exception cref="RenderFailure">The data is not an image Windows can read, or it is larger than the limits.</exception>
    public static ClipboardImage Prepare(ReadOnlySpan<byte> encoded)
    {
        RasterImage image = RasterAsset.Decode(encoded);
        return new ClipboardImage(ValueList.Create<byte>(RasterAsset.EncodePng(image)), image.Width, image.Height);
    }
}

/// <summary>
/// The Mac's openBoard, saveBoard, exportPNG and importScreenshot with the Windows file dialogs (WIN-SYS-04, spec 005):
/// Open board..., Save board..., Export PNG... and Insert image. Each starts on a fresh turn of the app thread, so the menu,
/// flyout or key that asked has finished. While a dialog is open the canvases take no input and its owner, the toolbar, is
/// disabled, as the Mac's panels are modal; afterwards the keyboard goes back to the canvas, or to the window that had it.
/// Reading, checking, encoding and writing files run on a worker thread; the results come back to the workspace on the app
/// thread, and every failure reaches the error window with the Mac's words. Use it on the app thread.
/// </summary>
internal sealed class FileCommands
{
    private static readonly FileType BoardType = new("Annotator board (*.annotator)", "*.annotator");
    private static readonly FileType BoardOrJsonType = new("Annotator board (*.annotator, *.json)", "*.annotator;*.json");
    private static readonly FileType PngType = new("PNG image (*.png)", "*.png");
    private static readonly FileType ImageType = new("Image (PNG, JPEG, HEIC or TIFF)", "*.png;*.jpg;*.jpeg;*.heic;*.heif;*.tif;*.tiff");

    private readonly WorkspaceModel _model;
    private readonly DispatcherQueue _queue;
    private readonly Func<HWND> _owner;
    private readonly Func<HWND, HWND> _focusTarget;
    private readonly Action _inputChanged;
    private readonly Guid? _dialogState;
    private readonly List<Task> _pending = [];
    private bool _quitting;

    /// <param name="owner">The window the dialogs belong to: the toolbar, in the topmost band above the canvases.</param>
    /// <param name="focusTarget">Where focus goes after a dialog, given the window that was active before it opened.</param>
    /// <param name="inputChanged">Called when a dialog opens or closes, so the canvases stop or resume taking input.</param>
    /// <param name="dialogState">Keeps the dialogs' remembered folder apart from the app's (the live check); null shares it.</param>
    public FileCommands(WorkspaceModel model, DispatcherQueue queue, Func<HWND> owner, Func<HWND, HWND> focusTarget, Action inputChanged,
                        Guid? dialogState = null)
    {
        _model = model;
        _queue = queue;
        _owner = owner;
        _focusTarget = focusTarget;
        _inputChanged = inputChanged;
        _dialogState = dialogState;
    }

    /// <summary>True while a file dialog is open: the canvases take no input (the Mac's modal panels).</summary>
    public bool DialogOpen { get; private set; }

    /// <summary>Quit: closes an open dialog as cancelled, so nothing is saved or opened after Quit.</summary>
    public void CancelDialog()
    {
        _quitting = true;
        if (DialogOpen)
        {
            FileDialog.CancelShown();
        }
    }

    /// <summary>Mac saveBoard(): Save board... (Ctrl+S on a canvas, the tray and the Tools window's File menu).</summary>
    public void SaveBoard() => Later(() =>
    {
        if (_model.PrepareBoardSave() is not { } request)
        {
            return;
        }
        string? path = Choose(new FileDialogRequest(Save: true, [BoardType])
        {
            FileName = request.SuggestedName,
            DefaultExtension = BoardFiles.Extension,
            Note = request.Message,
        });
        if (path is null || _model.BoardToSave() is not { } document)
        {
            return;
        }
        Run(() => BoardFiles.Save(path, document), _model.BoardSaved, _model.ReportFailure);
    });

    /// <summary>Mac openBoard(): Open board... (Ctrl+O on a canvas, the tray and the Tools window's File menu).</summary>
    public void OpenBoard() => Later(() =>
    {
        if (Choose(new FileDialogRequest(Save: false, [BoardOrJsonType])) is { } path)
        {
            OpenBoardFile(path);
        }
    });

    /// <summary>
    /// Mac openBoard(from:): reads and checks the board on a worker thread, then replaces the editing target's board with it,
    /// or says why it could not and keeps the current drawing. Also opens a board named on the command line, even while a
    /// dialog is open.
    /// </summary>
    public void OpenBoard(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _queue.TryEnqueue(() =>
        {
            if (!_quitting)
            {
                OpenBoardFile(path);
            }
        });
    }

    /// <summary>Mac exportPNG(): Export PNG... (the tray and the Tools window's File menu).</summary>
    public void ExportPng() => Later(() =>
    {
        if (!_model.PrepareExport())
        {
            return;
        }
        string? path = Choose(new FileDialogRequest(Save: true, [PngType])
        {
            FileName = PngExport.SuggestedName,
            DefaultExtension = "png",
        });
        if (path is null || _model.ExportRequest() is not { } export)
        {
            return;
        }
        Run(() => AtomicFile.Write(path, Render(export)), _model.PngExported, _model.ReportFailure);
    });

    /// <summary>Mac importScreenshot(): Insert image (the Tools window's Extras).</summary>
    public void InsertImage() => Later(() =>
    {
        string? path = Choose(new FileDialogRequest(Save: false, [ImageType])
        {
            Title = "Insert image into desktop drawing",
            OkLabel = "Insert",
        });
        if (path is null)
        {
            return;
        }
        ClipboardImage? image = null;
        Run(() => image = ImageImport.Prepare(ImageFiles.Read(path)), () =>
        {
            try
            {
                _model.InsertImage(image!);
            }
            catch (Exception error) when (IsFailure(error))
            {
                _model.ReportFailure(error);
            }
        }, _model.ReportFailure);
    });

    /// <summary>
    /// Mac PNGExporter.data on a WARP device that exists only while it draws, so no graphics driver stays loaded and the
    /// overlays' device is never shared. A device failure is the Mac's contextUnavailable.
    /// </summary>
    internal static byte[] Render(PngExport export)
    {
        try
        {
            using GraphicsDevices devices = GraphicsDevices.Create(new GraphicsDeviceOptions { UseWarp = true });
            using var measurer = new DirectWriteTextMeasurer();
            using var exporter = new PngExporter(devices, measurer);
            return exporter.Data(export.Elements, export.Size, export.Scale, export.Background);
        }
        catch (NativeCallException error)
        {
            StartupLog.Note("export", error.ToString().ReplaceLineEndings(" | "));
            throw new RenderFailure(RenderFailureKind.ContextUnavailable);
        }
    }

    private void OpenBoardFile(string path)
    {
        CanvasDocument? document = null;
        Run(() => document = BoardFiles.Load(path, RasterAsset.Check), () => _model.OpenBoard(document!.Value), _model.ReportBoardOpenFailure);
    }

    /// <summary>Runs <paramref name="action"/> on a fresh turn of the app thread, unless a dialog is already open or Quit began.</summary>
    private void Later(Action action) => _queue.TryEnqueue(() =>
    {
        if (!DialogOpen && !_quitting)
        {
            action();
        }
    });

    /// <summary>
    /// Shows a file dialog over the toolbar while the canvases take no input, then gives the keyboard back. Returns the chosen
    /// path, or null when the person cancelled or Windows could not show the dialog, which is reported.
    /// </summary>
    private string? Choose(FileDialogRequest request)
    {
        HWND before = PInvoke.GetForegroundWindow();
        DialogOpen = true;
        _inputChanged();
        try
        {
            string? chosen = FileDialog.Show(_owner(), _dialogState is { } state ? request with { StateId = state } : request);
            return _quitting ? null : chosen;
        }
        catch (NativeCallException error)
        {
            StartupLog.Note("file-dialog", error.ToString().ReplaceLineEndings(" | "));
            _model.Report($"The file dialog could not open: {error.Message} Try again.");
            return null;
        }
        finally
        {
            DialogOpen = false;
            if (!_quitting)
            {
                _inputChanged();
                HWND target = _focusTarget(before);
                if (!target.IsNull && PInvoke.IsWindow(target) && PInvoke.GetForegroundWindow() != target)
                {
                    PInvoke.SetForegroundWindow(target);
                }
            }
        }
    }

    /// <summary>Runs file work on a worker thread, then <paramref name="done"/> or <paramref name="failed"/> on the app thread.</summary>
    private void Run(Action work, Action done, Action<Exception> failed)
    {
        Task task = RunAsync(work, done, failed);
        _pending.Add(task);
        _ = task.ContinueWith(finished => _pending.Remove(finished), CancellationToken.None, TaskContinuationOptions.None,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static async Task RunAsync(Action work, Action done, Action<Exception> failed)
    {
        try
        {
            await Task.Run(work);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Every failure reaches the error window: files, formats, images and graphics devices, and anything else.
            failed(error);
            return;
        }
        done();
    }

    /// <summary>
    /// Waits for files that are being read or written, so Quit never cuts a save short (a cut save would still keep the earlier
    /// file, as saves are atomic).
    /// </summary>
    public Task FinishAsync() => Task.WhenAll([.. _pending]);

    /// <summary>Failures that the person can act on: files, formats, images and graphics devices.</summary>
    private static bool IsFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or System.Security.SecurityException or CanvasError or DecodingError
            or RenderFailure or NativeCallException or WorkspaceFailure;
}
