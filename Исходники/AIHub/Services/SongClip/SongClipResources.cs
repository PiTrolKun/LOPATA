using System.Globalization;
using System.IO;
using AIHub.Services.VideoEditor;

namespace AIHub.Services.SongClip;

internal static class SongClipResources
{
    internal static async Task CheckH3AudioAsync(string song, CancellationToken token)
    {
        using var probe = await VideoRuntime.ProbeAsync(song, token);
        var format = probe.RootElement.GetProperty("format");
        if (!format.TryGetProperty("duration", out var field)
            || !double.TryParse(field.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || !SongClipVisualProtocol.CanRepresentDuration(seconds, 24))
            throw new InvalidDataException("SongClip.FrameCountLimit");

        // Decoded chunks, joined stereo samples, tensor and the padded conditioning copy.
        var requiredAudioBytes = (long)Math.Ceiling(seconds * 32_000) * 2 * sizeof(float) * 4;
        var (available, _) = RuntimeResourceDiagnostics.ReadPhysicalMemory();
        if (available > 0 && available < requiredAudioBytes)
            throw new InvalidDataException("SongClip.AudioWorkspaceMemory");
    }

    internal static void CheckMuxSpace(string source, string destination)
    {
        var root = Path.GetPathRoot(destination);
        if (root is not { Length: > 1 } || root[1] != ':') return;
        var staging = new FileInfo(destination);
        var replaceableBytes = staging.Exists ? staging.Length : 0;
        if (new DriveInfo(root).AvailableFreeSpace < new FileInfo(source).Length - replaceableBytes)
            throw new IOException("SongClip.MuxDiskSpace");
    }

    internal static void SaveElapsed(string path, double seconds)
    {
        try { SongClipStore.Atomic(path, seconds); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
