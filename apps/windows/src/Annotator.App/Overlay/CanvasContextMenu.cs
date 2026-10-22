using Annotator.App.Native;
using Annotator.Workspace.Canvas;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.App.Overlay;

/// <summary>The canvas context menu (Mac DrawingCanvas.menu(for:)) as a native popup menu with the Mac's items and names.</summary>
internal static class CanvasContextMenu
{
    /// <summary>
    /// Shows <paramref name="menu"/> at <paramref name="anchor"/> (physical pixels) for the overlay
    /// <paramref name="owner"/> and returns the chosen command, or null when the menu closed without one.
    /// </summary>
    public static unsafe CanvasMenuCommand? Show(HWND owner, System.Drawing.Point anchor, CanvasMenu menu)
    {
        HMENU handle = PInvoke.CreatePopupMenu();
        if (handle.IsNull)
        {
            throw CallbackErrors.LastError("CreatePopupMenu (canvas)");
        }
        try
        {
            for (int index = 0; index < menu.Items.Length; index++)
            {
                CanvasMenuItem item = menu.Items[index];
                bool appended = item.Command is { } command
                    ? PInvoke.AppendMenu(handle, item.IsEnabled ? MENU_ITEM_FLAGS.MF_STRING : MENU_ITEM_FLAGS.MF_STRING | MENU_ITEM_FLAGS.MF_GRAYED,
                                         (nuint)index + 1, item.Title)
                    : PInvoke.AppendMenu(handle, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, (string?)null);
                if (!appended)
                {
                    throw CallbackErrors.LastError("AppendMenu (canvas)");
                }
            }
            const TRACK_POPUP_MENU_FLAGS flags = TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON
                | TRACK_POPUP_MENU_FLAGS.TPM_LEFTALIGN | TRACK_POPUP_MENU_FLAGS.TPM_TOPALIGN;
            int chosen = PInvoke.TrackPopupMenuEx(handle, (uint)flags, anchor.X, anchor.Y, owner, null);
            PInvoke.PostMessage(owner, PInvoke.WM_NULL, default, default);
            return chosen > 0 && chosen <= menu.Items.Length ? menu.Items[chosen - 1].Command : null;
        }
        finally
        {
            PInvoke.DestroyMenu(handle);
        }
    }
}
