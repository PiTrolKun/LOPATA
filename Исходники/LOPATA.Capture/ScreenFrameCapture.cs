using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIHub.Services;

/// <summary>One bounded WGC frame through the Windows COM ABI.
/// Native objects belong to one MTA worker and are released before it returns.</summary>
public sealed partial class ScreenFrameCapture
{
    public Task<BitmapSource> CaptureAsync(nint handle, bool window, bool cursor, CancellationToken token, bool softwareDevice = false) =>
        Task.Run(() =>
        {
            try { return Capture(handle, window, cursor, token, softwareDevice); }
            catch (COMException error) when (!softwareDevice && error.HResult is unchecked((int)0x8007000E) or unchecked((int)0x887A0005) or unchecked((int)0x887A0007))
            {
                // Free the failed hardware session first, then try a software D3D device.
                // The capture path never pauses or unloads the model to make room.
                return Capture(handle, window, cursor, token, true);
            }
        }, token);

    private static BitmapSource Capture(nint target, bool window, bool cursor, CancellationToken token, bool softwareDevice)
    {
        Marshal.ThrowExceptionForHR(RoInitialize(1));
        nint item = 0, device = 0, context = 0, dxgi = 0, directDevice = 0;
        nint factory = 0, pool = 0, session = 0, options = 0, frame = 0;
        try
        {
            token.ThrowIfCancellationRequested();
            factory = Factory("Windows.Graphics.Capture.GraphicsCaptureItem", new("3628e81b-3cac-4c60-b7f4-23ce0e0c3356"));
            Marshal.ThrowExceptionForHR(Method<CreateItem>(factory, window ? 3 : 4)(factory, target,
                new("79c3f95b-31f7-4ec2-a464-632ef5d30760"), out item));
            Release(ref factory);
            Marshal.ThrowExceptionForHR(Method<GetSize>(item, 7)(item, out var size));
            ValidateSize(size);
            var hr = D3D11CreateDevice(0, softwareDevice ? 5 : 1, 0, 0x20, 0, 0, 7, out device, out _, out context);
            if (hr < 0)
            {
                Release(ref context); Release(ref device);
                Marshal.ThrowExceptionForHR(D3D11CreateDevice(0, 5, 0, 0x20, 0, 0, 7, out device, out _, out context));
            }
            dxgi = Query(device, new("54ec77fa-1377-44e6-8c32-88fd5f44c84c"));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out directDevice));
            factory = Factory("Windows.Graphics.Capture.Direct3D11CaptureFramePool", new("589b103f-6bbc-5df5-a991-02e28b3b66d5"));
            // B8G8R8A8UIntNormalized, two buffers, no UI dispatcher dependency.
            Marshal.ThrowExceptionForHR(Method<CreatePool>(factory, 6)(factory, directDevice, 87, 2, size, out pool));
            Marshal.ThrowExceptionForHR(Method<CreateSession>(pool, 10)(pool, item, out session));
            options = Query(session, new("2c39ae40-7d2e-5044-804e-8b6799d4cf9e"));
            Marshal.ThrowExceptionForHR(Method<SetBoolean>(options, 7)(options, cursor ? (byte)1 : (byte)0));
            Marshal.ThrowExceptionForHR(Method<Call>(session, 6)(session));
            var deadline = Stopwatch.StartNew();
            while (frame == 0)
            {
                token.ThrowIfCancellationRequested();
                if (deadline.Elapsed.TotalSeconds >= 8) throw new TimeoutException("Windows did not provide a capture frame within 8 seconds.");
                Marshal.ThrowExceptionForHR(Method<GetObject>(pool, 7)(pool, out frame));
                if (frame == 0) Thread.Sleep(10);
            }
            return ReadPixels(frame, device, context, token);
        }
        finally
        {
            Close(frame); Close(session); Close(pool);
            Release(ref frame); Release(ref options); Release(ref session); Release(ref pool);
            Release(ref factory); Release(ref directDevice); Release(ref dxgi);
            Release(ref context); Release(ref device); Release(ref item);
            RoUninitialize();
        }
    }

    private static BitmapSource ReadPixels(nint frame, nint device, nint context, CancellationToken token)
    {
        nint surface = 0, access = 0, texture = 0, staging = 0;
        var mapped = false;
        try
        {
            Marshal.ThrowExceptionForHR(Method<GetSize>(frame, 8)(frame, out var content));
            Marshal.ThrowExceptionForHR(Method<GetObject>(frame, 6)(frame, out surface));
            access = Query(surface, new("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1"));
            Marshal.ThrowExceptionForHR(Method<GetInterface>(access, 3)(access,
                new("6f15aaf2-d208-4e89-9ab4-489535d34f9c"), out texture));
            Method<GetTextureDescription>(texture, 10)(texture, out var description);
            var size = new Size { Width = Math.Min(content.Width, checked((int)description.Width)), Height = Math.Min(content.Height, checked((int)description.Height)) };
            ValidateSize(size);
            description.Usage = 3; description.BindFlags = 0; description.CpuAccessFlags = 0x20000; description.MiscFlags = 0;
            Marshal.ThrowExceptionForHR(Method<CreateTexture>(device, 5)(device, in description, 0, out staging));
            Method<CopyResource>(context, 47)(context, staging, texture);
            MappedResource data;
            var deadline = Stopwatch.StartNew();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                // Poll the GPU staging copy with a deadline rather than an unbounded blocking Map.
                var hr = Method<MapResource>(context, 14)(context, staging, 0, 1, 0x100000, out data);
                if (hr != unchecked((int)0x887A000A)) { Marshal.ThrowExceptionForHR(hr); mapped = true; break; }
                if (deadline.Elapsed.TotalSeconds >= 8) throw new TimeoutException("Windows could not copy the capture frame within 8 seconds.");
                Thread.Sleep(10);
            }
            var stride = checked(size.Width * 4);
            var pixels = new byte[checked(stride * size.Height)];
            for (var y = 0; y < size.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                Marshal.Copy(data.Data + checked((nint)((long)y * data.RowPitch)), pixels, y * stride, stride);
            }
            var result = BitmapSource.Create(size.Width, size.Height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
            result.Freeze(); return result;
        }
        finally
        {
            if (mapped) Method<UnmapResource>(context, 15)(context, staging, 0);
            Release(ref staging); Release(ref texture); Release(ref access); Release(ref surface);
        }
    }

    private static void ValidateSize(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0 || (long)size.Width * size.Height > 64_000_000)
            throw new InvalidOperationException("Capture dimensions are unavailable or exceed the 64 megapixel safety limit.");
    }
    private static T Method<T>(nint instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * nint.Size));
    private static nint Query(nint instance, Guid iid)
    { Marshal.ThrowExceptionForHR(Marshal.QueryInterface(instance, in iid, out var result)); return result; }
    private static void Release(ref nint value) { if (value != 0) { Marshal.Release(value); value = 0; } }
    private static void Close(nint instance)
    {
        if (instance == 0) return;
        var iid = new Guid("30d5a829-7fa4-4026-83bb-d75bae4ea99e");
        if (Marshal.QueryInterface(instance, in iid, out var close) >= 0)
        { try { Method<Call>(close, 6)(close); } finally { Marshal.Release(close); } }
    }
    private static nint Factory(string className, Guid iid)
    {
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out var name));
        try { Marshal.ThrowExceptionForHR(RoGetActivationFactory(name, in iid, out var result)); return result; }
        finally { WindowsDeleteString(name); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Size { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct TextureDescription
    { public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct MappedResource { public nint Data; public uint RowPitch, DepthPitch; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateItem(nint self, nint target, in Guid iid, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetSize(nint self, out Size result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreatePool(nint self, nint device, int format, int buffers, Size size, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateSession(nint self, nint item, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetBoolean(nint self, byte value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Call(nint self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetObject(nint self, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetInterface(nint self, in Guid iid, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GetTextureDescription(nint self, out TextureDescription result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateTexture(nint self, in TextureDescription description, nint initialData, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CopyResource(nint self, nint destination, nint source);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int MapResource(nint self, nint resource, uint subresource, uint map, uint flags, out MappedResource result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void UnmapResource(nint self, nint resource, uint subresource);
    [DllImport("combase.dll")] private static extern int RoInitialize(uint type);
    [DllImport("combase.dll")] private static extern void RoUninitialize();
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string value, int length, out nint result);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint name, in Guid iid, out nint factory);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgi, out nint result);
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(nint adapter, int driver, nint software, uint flags,
        nint levels, uint count, uint sdk, out nint device, out int level, out nint context);
}
