using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;

namespace Annotator.App.Settings;

/// <summary>
/// The Windows file dialogs (IFileSaveDialog and IFileOpenDialog) for Settings: Export profile..., Import profile..., Save
/// support info... and Save AI skill... (the Mac's NSSavePanel and NSOpenPanel). They are modal to the Settings window
/// and return the chosen path, or null when the person cancels.
/// </summary>
internal static unsafe class FileDialogs
{
    private const int Cancelled = unchecked((int)0x800704C7);

    /// <summary>One file type: its name and pattern, such as ("JSON", "*.json").</summary>
    public readonly record struct FileType(string Name, string Pattern);

    /// <summary>Asks where to save a file, suggesting <paramref name="fileName"/>.</summary>
    public static string? Save(nint owner, string fileName, FileType type, string extension)
    {
        // CLSID_FileSaveDialog; a literal because typeof(T).GUID needs reflection metadata under Native AOT.
        var clsid = new Guid(0xC0B4E2F3, 0xBA21, 0x4773, 0x8D, 0xBA, 0x33, 0x5E, 0xC9, 0x46, 0xEB, 0x8B);
        Guid iid = IFileSaveDialog.IID_Guid;
        var dialog = (IFileSaveDialog*)Create(clsid, iid, "FileSaveDialog");
        try
        {
            FILEOPENDIALOGOPTIONS options;
            Check(dialog->GetOptions(&options), "IFileSaveDialog.GetOptions");
            Check(dialog->SetOptions(options | FILEOPENDIALOGOPTIONS.FOS_OVERWRITEPROMPT | FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM
                | FILEOPENDIALOGOPTIONS.FOS_STRICTFILETYPES), "IFileSaveDialog.SetOptions");
            SetTypes((IFileDialog*)dialog, type);
            fixed (char* name = fileName)
            fixed (char* defaultExtension = extension)
            {
                Check(dialog->SetFileName(name), "IFileSaveDialog.SetFileName");
                Check(dialog->SetDefaultExtension(defaultExtension), "IFileSaveDialog.SetDefaultExtension");
            }
            return Show((IFileDialog*)dialog, owner);
        }
        finally
        {
            dialog->Release();
        }
    }

    /// <summary>Asks for a file to open.</summary>
    public static string? Open(nint owner, FileType type)
    {
        var dialog = CreateOpen();
        try
        {
            FILEOPENDIALOGOPTIONS options;
            Check(dialog->GetOptions(&options), "IFileOpenDialog.GetOptions");
            Check(dialog->SetOptions(options | FILEOPENDIALOGOPTIONS.FOS_FILEMUSTEXIST | FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM),
                "IFileOpenDialog.SetOptions");
            SetTypes((IFileDialog*)dialog, type);
            return Show((IFileDialog*)dialog, owner);
        }
        finally
        {
            dialog->Release();
        }
    }

    /// <summary>Asks for a folder, with the dialog's title and button named for the task (the Mac panel's title and prompt).</summary>
    public static string? Folder(nint owner, string title, string button)
    {
        var dialog = CreateOpen();
        try
        {
            FILEOPENDIALOGOPTIONS options;
            Check(dialog->GetOptions(&options), "IFileOpenDialog.GetOptions");
            Check(dialog->SetOptions(options | FILEOPENDIALOGOPTIONS.FOS_PICKFOLDERS | FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM
                | FILEOPENDIALOGOPTIONS.FOS_PATHMUSTEXIST), "IFileOpenDialog.SetOptions");
            fixed (char* text = title)
            fixed (char* label = button)
            {
                Check(dialog->SetTitle(text), "IFileOpenDialog.SetTitle");
                Check(dialog->SetOkButtonLabel(label), "IFileOpenDialog.SetOkButtonLabel");
            }
            return Show((IFileDialog*)dialog, owner);
        }
        finally
        {
            dialog->Release();
        }
    }

    private static IFileOpenDialog* CreateOpen()
    {
        // CLSID_FileOpenDialog.
        var clsid = new Guid(0xDC1C5A9C, 0xE88A, 0x4DDE, 0xA5, 0xA1, 0x60, 0xF8, 0x2A, 0x20, 0xAE, 0xF7);
        return (IFileOpenDialog*)Create(clsid, IFileOpenDialog.IID_Guid, "FileOpenDialog");
    }

    private static void* Create(Guid clsid, Guid iid, string name)
    {
        void* instance;
        HRESULT created = PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, &instance);
        if (created.Failed)
        {
            throw new Platform.NativeCallException($"CoCreateInstance({name})", created.Value);
        }
        return instance;
    }

    private static void SetTypes(IFileDialog* dialog, FileType type)
    {
        fixed (char* name = type.Name)
        fixed (char* pattern = type.Pattern)
        {
            var spec = new COMDLG_FILTERSPEC { pszName = name, pszSpec = pattern };
            Check(dialog->SetFileTypes(1, &spec), "IFileDialog.SetFileTypes");
        }
    }

    private static string? Show(IFileDialog* dialog, nint owner)
    {
        HRESULT shown = dialog->Show(new HWND(owner));
        if (shown.Value == Cancelled)
        {
            return null;
        }
        Check(shown, "IFileDialog.Show");
        IShellItem* item;
        Check(dialog->GetResult(&item), "IFileDialog.GetResult");
        try
        {
            PWSTR path;
            Check(item->GetDisplayName(SIGDN.SIGDN_FILESYSPATH, &path), "IShellItem.GetDisplayName");
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

    private static void Check(HRESULT result, string operation)
    {
        if (result.Failed)
        {
            throw new Platform.NativeCallException(operation, result.Value);
        }
    }
}
