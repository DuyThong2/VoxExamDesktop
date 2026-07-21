using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using WinRT;

namespace VoxOralExam.DesktopApp.Infra.Recording.Interop;

[ComImport]
[Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsCaptureItemInterop
{
    [PreserveSig]
    int CreateForWindow(nint window, ref Guid iid, out nint result);

    [PreserveSig]
    int CreateForMonitor(nint monitor, ref Guid iid, out nint result);
}

internal static class GraphicsCaptureInterop
{
    private static readonly Guid GraphicsCaptureItemIid =
        new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    public static GraphicsCaptureItem CreateItemForMonitor(nint monitor)
    {
        var interopIid = typeof(IGraphicsCaptureItemInterop).GUID;
        var factoryPointer = NativeMethods.GetActivationFactory(
            "Windows.Graphics.Capture.GraphicsCaptureItem",
            interopIid);

        try
        {
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPointer);
            try
            {
                var itemIid = GraphicsCaptureItemIid;
                var hr = interop.CreateForMonitor(monitor, ref itemIid, out var itemPointer);
                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    return MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPointer);
                }
                finally
                {
                    Marshal.Release(itemPointer);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(interop);
            }
        }
        finally
        {
            Marshal.Release(factoryPointer);
        }
    }
}
