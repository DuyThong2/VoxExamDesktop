using System.Windows;
using System.Windows.Interop;

namespace VoxOralExam.DesktopApp.Infra.Interop;

/// <summary>
/// Greys out a window's titlebar X and neutralises the close affordances that never reach managed
/// code: the X click, Alt+F4 and the system-menu Close item all arrive as WM_SYSCOMMAND/SC_CLOSE and
/// are swallowed while <see cref="IsLocked"/> is true.
///
/// This is only half of the protection, and deliberately so. WM_CLOSE is NOT filtered here: WPF
/// turns an incoming WM_CLOSE into Window.Close(), and Window.Close() itself round-trips through
/// WM_CLOSE, so a hook that swallows it wedges every managed close as well -- including
/// Application.Shutdown(), which would leave the app unable to exit and blocking a Windows logoff
/// (measured: the process hangs). The remaining close paths -- taskbar "Close window", the Alt+Tab
/// preview X, an externally posted WM_CLOSE, and the app's own Window.Close() -- therefore all
/// surface as the Closing event, and it is the owning window's Closing handler that must cancel
/// them while the exam is locked. That split is what keeps Application.Shutdown() working: it closes
/// with ignoreCancel, so it passes the Closing guard even when a user-initiated close would not.
/// </summary>
public sealed class WindowCloseGuard : IDisposable
{
    private readonly Window _window;
    private HwndSource? _source;
    private bool _isLocked = true;
    private bool _disposed;

    public WindowCloseGuard(Window window)
    {
        _window = window;

        // SourceInitialized rather than Loaded: the HWND exists by then but nothing has painted yet,
        // so the X is already grey on the first frame instead of briefly looking clickable.
        var existingHandle = new WindowInteropHelper(window).Handle;
        if (existingHandle != nint.Zero)
        {
            AttachToSource(existingHandle);
        }
        else
        {
            _window.SourceInitialized += OnSourceInitialized;
        }

        // Maximize/restore transitions make Windows rebuild the standard system-menu item states,
        // silently re-enabling the X. ExamWindow starts maximized, so this fires in practice.
        _window.StateChanged += OnStateChanged;
    }

    public bool IsLocked
    {
        get => _isLocked;
        set
        {
            if (_isLocked == value)
            {
                return;
            }

            _isLocked = value;
            ApplySystemMenuState();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.SourceInitialized -= OnSourceInitialized;
        _window.StateChanged -= OnStateChanged;

        if (_source is not null)
        {
            _source.Disposed -= OnSourceDisposed;
            _source.RemoveHook(OnWndProc);
            _source = null;
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _window.SourceInitialized -= OnSourceInitialized;
        AttachToSource(new WindowInteropHelper(_window).Handle);
    }

    private void AttachToSource(nint handle)
    {
        _source = HwndSource.FromHwnd(handle);
        if (_source is null)
        {
            return;
        }

        // Hooks added here run before HwndSource's own message processing, so handled=true below
        // prevents WPF from ever translating WM_CLOSE into Window.Close().
        _source.AddHook(OnWndProc);
        _source.Disposed += OnSourceDisposed;
        ApplySystemMenuState();
    }

    private void OnSourceDisposed(object? sender, EventArgs e) => _source = null;

    private void OnStateChanged(object? sender, EventArgs e) => ApplySystemMenuState();

    private void ApplySystemMenuState()
    {
        var handle = _source?.Handle ?? nint.Zero;
        if (handle == nint.Zero)
        {
            return;
        }

        if (_isLocked)
        {
            WindowNativeMethods.DisableCloseCommand(handle);
        }
        else
        {
            WindowNativeMethods.EnableCloseCommand(handle);
        }
    }

    private nint OnWndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case WindowNativeMethods.WmInitMenu:
            case WindowNativeMethods.WmInitMenuPopup:
                // Re-assert right before the system menu is shown; cheap, and it closes the gap left
                // by any window-state/DPI/theme change that reset the item behind our back.
                ApplySystemMenuState();
                break;

            case WindowNativeMethods.WmSysCommand when _isLocked:
                if ((wParam.ToInt64() & 0xFFF0) == WindowNativeMethods.ScClose)
                {
                    handled = true;
                }

                break;

            // WM_CLOSE is intentionally not handled here -- see the class summary. It is allowed
            // through to WPF, which raises Closing, where the owning window cancels it while locked.
        }

        return nint.Zero;
    }
}
