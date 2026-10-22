using System.Runtime.InteropServices;
using Annotator.Platform;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;

namespace Annotator.App.Files;

/// <summary>One choice of the dialog's file type list: its name and its patterns, such as "*.png".</summary>
internal sealed record FileType(string Name, string Patterns);

/// <summary>
/// What an Open or Save dialog shows (the Mac's NSOpenPanel and NSSavePanel settings): the title, the OK button's word, the
/// suggested name, the file types, the extension a typed name gets, and a note, the Mac panel's message.
/// </summary>
internal sealed record FileDialogRequest(bool Save, IReadOnlyList<FileType> Types)
{
    public string? Title { get; init; }

    public string? OkLabel { get; init; }

    public string? FileName { get; init; }

    public string? DefaultExtension { get; init; }

    public string? Note { get; init; }

    /// <summary>
    /// Keeps the dialog's remembered folder, size and position apart from the app's other dialogs (the live check's dialogs);
    /// null shares Windows' state for the app.
    /// </summary>
    public Guid? StateId { get; init; }
}

/// <summary>
/// The Windows file dialogs (WIN-SYS-04): the common Open and Save dialogs, modal to their owner, in file-system paths only. A
/// Save dialog asks before it replaces a file and keeps the chosen type's extension, as the Mac's panels do. Use it on the app
/// thread, which is single-threaded for COM.
/// </summary>
internal static unsafe class FileDialog
{
    private static readonly Guid OpenDialogClass = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
    private static readonly Guid SaveDialogClass = new("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B");

    /// <summary>The Show result when the person cancels: HRESULT_FROM_WIN32(ERROR_CANCELLED).</summary>
    private const int Cancelled = unchecked((int)0x800704C7);

    /// <summary>The note's control in the dialog's customization area.</summary>
    private const uint NoteControl = 1;

    /// <summary>The dialog on screen, if any. Only the app thread uses it.</summary>
    private static IFileOpenDialog* s_shown;

    /// <summary>Closes the dialog on screen as if the person cancelled it, so Quit never takes its owner away under it.</summary>
    public static void CancelShown()
    {
        if (s_shown is not null)
        {
            s_shown->Close(new HRESULT(Cancelled));
        }
    }

    /// <summary>
    /// Shows the dialog over <paramref name="owner"/> until the person chooses a file or cancels, and returns the file's full
    /// path, or null when cancelled. Throws <see cref="NativeCallException"/> when Windows cannot show the dialog.
    /// </summary>
    public static string? Show(HWND owner, FileDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Guid clsid = request.Save ? SaveDialogClass : OpenDialogClass;
        Guid iid = request.Save ? IFileSaveDialog.IID_Guid : IFileOpenDialog.IID_Guid;
        void* raw;
        Check(PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, &raw), "CoCreateInstance (file dialog)");
        // IFileSaveDialog and IFileOpenDialog both start with IFileDialog's methods, in the same order.
        var dialog = (IFileOpenDialog*)raw;
        try
        {
            Configure(dialog, request);
            s_shown = dialog;
            HRESULT shown = dialog->Show(owner);
            s_shown = null;
            if (shown.Value == Cancelled)
            {
                return null;
            }
            Check(shown, "IFileDialog::Show");
            IShellItem* item = null;
            Check(dialog->GetResult(&item), "IFileDialog::GetResult");
            try
            {
                PWSTR path;
                Check(item->GetDisplayName(SIGDN.SIGDN_FILESYSPATH, &path), "IShellItem::GetDisplayName");
                try
                {
                    return new string(path.Value);
                }
                finally
                {
                    PInvoke.CoTaskMemFree(path.Value);
                }
            }
            finally
            {
                item->Release();
            }
        }
        finally
        {
            s_shown = null;
            dialog->Release();
        }
    }

    private static void Configure(IFileOpenDialog* dialog, FileDialogRequest request)
    {
        FILEOPENDIALOGOPTIONS options;
        Check(dialog->GetOptions(&options), "IFileDialog::GetOptions");
        options |= FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM | FILEOPENDIALOGOPTIONS.FOS_PATHMUSTEXIST;
        options |= request.Save
            ? FILEOPENDIALOGOPTIONS.FOS_OVERWRITEPROMPT | FILEOPENDIALOGOPTIONS.FOS_STRICTFILETYPES | FILEOPENDIALOGOPTIONS.FOS_NOREADONLYRETURN
            : FILEOPENDIALOGOPTIONS.FOS_FILEMUSTEXIST;
        Check(dialog->SetOptions(options), "IFileDialog::SetOptions");
        if (request.StateId is { } state)
        {
            Check(dialog->SetClientGuid(&state), "IFileDialog::SetClientGuid");
        }
        if (request.Types.Count > 0)
        {
            var specs = new COMDLG_FILTERSPEC[request.Types.Count];
            var pins = new List<GCHandle>(request.Types.Count * 2);
            try
            {
                for (int index = 0; index < specs.Length; index++)
                {
                    specs[index] = new COMDLG_FILTERSPEC
                    {
                        pszName = Pin(request.Types[index].Name, pins),
                        pszSpec = Pin(request.Types[index].Patterns, pins),
                    };
                }
                fixed (COMDLG_FILTERSPEC* types = specs)
                {
                    Check(dialog->SetFileTypes((uint)specs.Length, types), "IFileDialog::SetFileTypes");
                }
            }
            finally
            {
                foreach (GCHandle pin in pins)
                {
                    pin.Free();
                }
            }
        }
        if (request.DefaultExtension is { } extension)
        {
            fixed (char* text = extension)
            {
                Check(dialog->SetDefaultExtension(text), "IFileDialog::SetDefaultExtension");
            }
        }
        if (request.FileName is { } name)
        {
            fixed (char* text = name)
            {
                Check(dialog->SetFileName(text), "IFileDialog::SetFileName");
            }
        }
        if (request.Title is { } title)
        {
            fixed (char* text = title)
            {
                Check(dialog->SetTitle(text), "IFileDialog::SetTitle");
            }
        }
        if (request.OkLabel is { } label)
        {
            fixed (char* text = label)
            {
                Check(dialog->SetOkButtonLabel(text), "IFileDialog::SetOkButtonLabel");
            }
        }
        if (request.Note is { } note)
        {
            AddNote(dialog, note);
        }
    }

    /// <summary>The Mac panel's message as a line of text in the dialog's customization area.</summary>
    private static void AddNote(IFileOpenDialog* dialog, string note)
    {
        Guid iid = IFileDialogCustomize.IID_Guid;
        void* raw;
        Check(dialog->QueryInterface(&iid, &raw), "IFileDialog::QueryInterface (IFileDialogCustomize)");
        var customize = (IFileDialogCustomize*)raw;
        try
        {
            fixed (char* text = note)
            {
                Check(customize->AddText(NoteControl, text), "IFileDialogCustomize::AddText");
            }
        }
        finally
        {
            customize->Release();
        }
    }

    private static char* Pin(string text, List<GCHandle> pins)
    {
        var handle = GCHandle.Alloc(text, GCHandleType.Pinned);
        pins.Add(handle);
        return (char*)handle.AddrOfPinnedObject();
    }

    private static void Check(HRESULT result, string operation)
    {
        if (result.Failed)
        {
            throw new NativeCallException(operation, result.Value);
        }
    }
}
