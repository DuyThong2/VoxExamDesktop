using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace VoxOralExam.DesktopApp.Infra.Recording.Interop;

[ComImport]
[Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirect3DDxgiInterfaceAccess
{
    nint GetInterface([In] ref Guid iid);
}

internal static class Direct3D11Interop
{
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        nint dxgiDevice,
        out nint graphicsDevice);

    public static (ID3D11Device Device, IDirect3DDevice WinRtDevice) CreateSharedDevice()
    {
        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            flags,
            null,
            out var device).CheckError();

        var winrtDevice = CreateDirect3DDeviceFromD3D11Device(device!);
        return (device!, winrtDevice);
    }

    private static IDirect3DDevice CreateDirect3DDeviceFromD3D11Device(
        ID3D11Device d3dDevice)
    {
        using var dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice>();
        var hr = CreateDirect3D11DeviceFromDXGIDevice(
            dxgiDevice.NativePointer,
            out var deviceHandle);
        Marshal.ThrowExceptionForHR(hr);

        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(deviceHandle);
        }
        finally
        {
            Marshal.Release(deviceHandle);
        }
    }

    /// <summary>
    /// Reads a GPU texture back to CPU memory as tightly-packed BGRA bytes (4 bytes/pixel, no row
    /// padding) -- staging textures commonly have a RowPitch larger than width*4 for driver
    /// alignment, so this copies row by row rather than one contiguous block.
    /// </summary>
    public static unsafe byte[] ReadBackBgra(
        ID3D11Device device,
        object contextLock,
        ID3D11Texture2D texture,
        int width,
        int height)
    {
        var description = texture.Description;
        description.Usage = ResourceUsage.Staging;
        description.BindFlags = BindFlags.None;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        description.MiscFlags = ResourceOptionFlags.None;

        var buffer = new byte[width * height * 4];
        using var staging = device.CreateTexture2D(description);

        lock (contextLock)
        {
            device.ImmediateContext.CopyResource(staging, texture);
            var mapped = device.ImmediateContext.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var rowBytes = width * 4;
                var source = (byte*)mapped.DataPointer;
                for (var y = 0; y < height; y++)
                {
                    new ReadOnlySpan<byte>(source + (long)y * mapped.RowPitch, rowBytes)
                        .CopyTo(buffer.AsSpan(y * rowBytes, rowBytes));
                }
            }
            finally
            {
                device.ImmediateContext.Unmap(staging, 0);
            }
        }

        return buffer;
    }

    public static ID3D11Texture2D GetTexture2D(IDirect3DSurface surface)
    {
        var winrtObject = (IWinRTObject)surface;
        var accessIid = typeof(IDirect3DDxgiInterfaceAccess).GUID;
        using var accessRef = winrtObject.NativeObject
            .As<WinRT.Interop.IUnknownVftbl>(accessIid);
        var access = (IDirect3DDxgiInterfaceAccess)
            Marshal.GetObjectForIUnknown(accessRef.ThisPtr);
        try
        {
            var textureIid = typeof(ID3D11Texture2D).GUID;
            var texturePointer = access.GetInterface(ref textureIid);
            return new ID3D11Texture2D(texturePointer);
        }
        finally
        {
            Marshal.ReleaseComObject(access);
        }
    }
}
