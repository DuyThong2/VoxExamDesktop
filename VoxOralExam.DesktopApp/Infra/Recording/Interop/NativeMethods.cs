using System.Runtime.InteropServices;

namespace VoxOralExam.DesktopApp.Infra.Recording.Interop;

internal static class NativeMethods
{
    private const uint MonitorDefaultToPrimary = 0x00000001;

    public static nint GetPrimaryMonitorHandle() =>
        MonitorFromWindow(GetDesktopWindow(), MonitorDefaultToPrimary);

    public static nint GetActivationFactory(string runtimeClassName, Guid iid)
    {
        var hr = WindowsCreateString(runtimeClassName, (uint)runtimeClassName.Length, out var hstring);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            hr = RoGetActivationFactory(hstring, ref iid, out var factory);
            Marshal.ThrowExceptionForHR(hr);
            return factory;
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(
        nint activatableClassId,
        [In] ref Guid iid,
        out nint factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string source,
        uint length,
        out nint hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(nint hstring);
}
