using System.Runtime.InteropServices;

namespace VoxOralExam.DesktopApp.Infra.Interop;

/// <summary>
/// Win32 entry points for controlling a window's standard close affordances.
/// </summary>
internal static class WindowNativeMethods
{
    public const int WmSysCommand = 0x0112;
    public const int WmInitMenu = 0x0116;
    public const int WmInitMenuPopup = 0x0117;

    /// <summary>
    /// The low 4 bits of WM_SYSCOMMAND's wParam carry mnemonic/accelerator detail, so callers must
    /// mask with 0xFFF0 before comparing against this.
    /// </summary>
    public const int ScClose = 0xF060;

    private const uint MfByCommand = 0x00000000;
    private const uint MfEnabled = 0x00000000;
    private const uint MfGrayed = 0x00000001;
    private const uint MfDisabled = 0x00000002;

    /// <summary>
    /// Greys the titlebar X and the system-menu Close item. Also makes DefWindowProc ignore Alt+F4,
    /// because Alt+F4 is delivered as SC_CLOSE through that same, now-disabled, menu item.
    /// </summary>
    public static void DisableCloseCommand(nint hwnd) =>
        SetCloseCommandState(hwnd, MfGrayed | MfDisabled);

    public static void EnableCloseCommand(nint hwnd) =>
        SetCloseCommandState(hwnd, MfEnabled);

    private static void SetCloseCommandState(nint hwnd, uint flags)
    {
        if (hwnd == nint.Zero)
        {
            return;
        }

        // bRevert:false -- passing true throws away the window's private copy of the system menu and
        // restores the default one, which would immediately un-grey the item we are disabling.
        var menu = GetSystemMenu(hwnd, bRevert: false);
        if (menu == nint.Zero)
        {
            return;
        }

        EnableMenuItem(menu, ScClose, MfByCommand | flags);

        // The caption buttons are painted from the system-menu state; nudge the frame so the X
        // changes appearance now instead of at the next unrelated non-client repaint.
        DrawMenuBar(hwnd);
    }

    [DllImport("user32.dll")]
    private static extern nint GetSystemMenu(nint hWnd, [MarshalAs(UnmanagedType.Bool)] bool bRevert);

    [DllImport("user32.dll")]
    private static extern int EnableMenuItem(nint hMenu, int uIDEnableItem, uint uEnable);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawMenuBar(nint hWnd);
}
