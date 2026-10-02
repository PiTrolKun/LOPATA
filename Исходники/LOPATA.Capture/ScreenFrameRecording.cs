using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace AIHub.Services;

public sealed partial class ScreenFrameCapture
{
    /// <summary>A persistent, two-buffer session. A static desktop reuses the last CPU frame.</summary>
    public Task RecordAsync(nint target, bool window, bool cursor, int fps,
        Action<BitmapSource> receive, CancellationToken token, bool softwareDevice = false) => Task.Run(() =>
    {
        try { Record(target, window, cursor, fps, receive, token, softwareDevice); }
        catch (COMException error) when (!softwareDevice && error.HResult is unchecked((int)0x8007000E) or unchecked((int)0x887A0005) or unchecked((int)0x887A0007))
        { Record(target, window, cursor, fps, receive, token, true); }
    }, token);

    private static void Record(nint target, bool window, bool cursor, int fps, Action<BitmapSource> receive,
        CancellationToken token, bool softwareDevice)
    {
        Marshal.ThrowExceptionForHR(RoInitialize(1));
        nint item = 0, device = 0, context = 0, dxgi = 0, directDevice = 0;
        nint factory = 0, pool = 0, session = 0, options = 0;
        try
        {
            factory = Factory("Windows.Graphics.Capture.GraphicsCaptureItem", new("3628e81b-3cac-4c60-b7f4-23ce0e0c3356"));
            Marshal.ThrowExceptionForHR(Method<CreateItem>(factory, window ? 3 : 4)(factory, target,
                new("79c3f95b-31f7-4ec2-a464-632ef5d30760"), out item)); Release(ref factory);
            Marshal.ThrowExceptionForHR(Method<GetSize>(item, 7)(item, out var size)); ValidateSize(size);
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(0, softwareDevice ? 5 : 1, 0, 0x20, 0, 0, 7, out device, out _, out context));
            dxgi = Query(device, new("54ec77fa-1377-44e6-8c32-88fd5f44c84c"));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out directDevice));
            factory = Factory("Windows.Graphics.Capture.Direct3D11CaptureFramePool", new("589b103f-6bbc-5df5-a991-02e28b3b66d5"));
            Marshal.ThrowExceptionForHR(Method<CreatePool>(factory, 6)(factory, directDevice, 87, 2, size, out pool));
            Marshal.ThrowExceptionForHR(Method<CreateSession>(pool, 10)(pool, item, out session));
            options = Query(session, new("2c39ae40-7d2e-5044-804e-8b6799d4cf9e"));
            Marshal.ThrowExceptionForHR(Method<SetBoolean>(options, 7)(options, cursor ? (byte)1 : (byte)0));
            Marshal.ThrowExceptionForHR(Method<Call>(session, 6)(session));
            BitmapSource? last = null; var clock = Stopwatch.StartNew(); double due = 0;
            while (!token.IsCancellationRequested)
            {
                var remaining = due - clock.Elapsed.TotalMilliseconds;
                if (remaining > 0) { if (token.WaitHandle.WaitOne((int)Math.Min(remaining + 1, 20))) break; continue; }
                nint frame = 0;
                try
                {
                    Marshal.ThrowExceptionForHR(Method<GetObject>(pool, 7)(pool, out frame));
                    if (frame != 0) last = ReadPixels(frame, device, context, token);
                }
                finally { Close(frame); Release(ref frame); }
                if (last is null)
                {
                    if (clock.Elapsed.TotalSeconds >= 8) throw new TimeoutException("Windows did not provide a recording frame within 8 seconds.");
                    token.WaitHandle.WaitOne(10); continue;
                }
                // Resizing the target is an explicit partial recording, never a silent crop.
                if (last.PixelWidth != size.Width || last.PixelHeight != size.Height)
                    throw new InvalidOperationException("The captured window or monitor changed size during recording.");
                receive(last);
                due += 1000d / fps;
                if (due < clock.Elapsed.TotalMilliseconds - 1000d / fps) due = clock.Elapsed.TotalMilliseconds;
            }
        }
        finally
        {
            Close(session); Close(pool); Release(ref options); Release(ref session); Release(ref pool);
            Release(ref factory); Release(ref directDevice); Release(ref dxgi); Release(ref context); Release(ref device); Release(ref item);
            RoUninitialize();
        }
    }
}
