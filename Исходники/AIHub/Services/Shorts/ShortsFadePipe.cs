using System.Diagnostics;
using System.Globalization;
using System.IO;
using AIHub.Services.VideoEditor;
using AIHub.Services.VideoUpscale;

namespace AIHub.Services.Shorts;

internal static class ShortsFadePipe
{
    internal static string[] InputArguments =>
        ["-f", "rawvideo", "-pixel_format", "yuv420p", "-video_size", $"{ShortsJob.Width}x{ShortsJob.Height}",
            "-framerate", ShortsJob.Fps.ToString(CultureInfo.InvariantCulture), "-i", "pipe:0"];

    internal static async Task RenderAsync(string losslessVideo, string videoFilter, int frames,
        List<string> encodeArguments, CancellationToken token)
    {
        await UpscaleFfmpeg.PrepareAsync(token);
        await VideoRuntime.PrepareAsync(token);
        string[] decodeArguments = ["-nostdin", "-v", "error", "-xerror", "-i", losslessVideo, "-map", "0:v:0",
            "-an", "-vf", videoFilter, "-frames:v", frames.ToString(CultureInfo.InvariantCulture),
            "-c:v", "rawvideo", "-threads:v", "1", "-f", "rawvideo", "pipe:1"];

        var encodeStart = VideoRuntime.Info(false, encodeArguments);
        encodeStart.RedirectStandardInput = true;
        using var encoder = OwnedProcessRegistry.Shared.Start(encodeStart, "Shorts.FadeEncoder");
        try
        {
            using var decoder = OwnedProcessRegistry.Shared.Start(UpscaleFfmpeg.Info(false, decodeArguments), "Shorts.Ffv1Decoder");
            using var cancel = token.Register(() => { Stop(decoder); Stop(encoder); });

            var decodeErrors = decoder.StandardError.ReadToEndAsync();
            var encodeErrors = encoder.StandardError.ReadToEndAsync();
            var encodeOutput = encoder.StandardOutput.ReadToEndAsync();
            IOException? pipeFailure = null;
            try
            {
                await using var input = encoder.StandardInput.BaseStream;
                await decoder.StandardOutput.BaseStream.CopyToAsync(input, 256 * 1024, token);
            }
            catch (IOException error)
            {
                pipeFailure = error;
                Stop(decoder);
            }
            finally
            {
                await Task.WhenAll(decoder.WaitForExitAsync(), encoder.WaitForExitAsync(), encodeOutput);
            }

            token.ThrowIfCancellationRequested();
            if (decoder.ExitCode != 0 || encoder.ExitCode != 0 || pipeFailure is not null)
                throw new IOException($"VideoEditor.RuntimeFailed: FFV1 decode {decoder.ExitCode}: {await decodeErrors}\n" +
                    $"Shorts encode {encoder.ExitCode}: {await encodeErrors}", pipeFailure);
        }
        finally
        {
            Stop(encoder);
            await encoder.WaitForExitAsync();
        }
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
    }
}
