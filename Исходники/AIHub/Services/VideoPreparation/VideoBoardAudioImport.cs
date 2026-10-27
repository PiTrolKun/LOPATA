using System.Globalization;
using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.VideoEditor;
using Lopata.Updates;

namespace AIHub.Services.VideoPreparation;

public sealed record VideoBoardAudioImport(VideoBoardAudio Audio, string Path)
{
    public static async Task<VideoBoardAudioImport> ImportAsync(string source, string directory, CancellationToken token)
    {
        source = System.IO.Path.GetFullPath(source); SafeUpdatePath.RejectLinks(source); SafeUpdatePath.RejectLinks(directory);
        var extension = System.IO.Path.GetExtension(source).ToLowerInvariant();
        if (extension is not (".wav" or ".mp3" or ".flac" or ".ogg" or ".opus" or ".m4a" or ".aac")) throw new InvalidDataException("VideoPrep.BoardAudioInvalid");
        using var audit = ComponentAudit.CaptureOperation(); await VideoRuntime.PrepareAsync(token);
        using var original = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        using var probe = await VideoRuntime.ProbeAsync(source, token); var root = probe.RootElement;
        if (!root.GetProperty("streams").EnumerateArray().Any(s => s.TryGetProperty("codec_type", out var t) && t.GetString() == "audio")
            || !root.GetProperty("format").TryGetProperty("duration", out var duration)
            || !double.TryParse(duration.GetString(), CultureInfo.InvariantCulture, out var seconds) || !double.IsFinite(seconds) || seconds <= 0)
            throw new InvalidDataException("VideoPrep.BoardAudioInvalid");
        var role = "video-board/audio/" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(directory);
        var destination = System.IO.Path.Combine(directory, role.Split('/')[^1] + extension);
        try
        {
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
            { await original.CopyToAsync(output, token); output.Flush(true); }
            var audio = new VideoBoardAudio { Role = role, Name = System.IO.Path.GetFileName(source), Seconds = seconds };
            if (original.Length <= 512L * 1024 * 1024 && extension is ".wav" or ".mp3" or ".flac" or ".opus")
            {
                try
                {
                    var timing = await MusicLyricsTimingMetadata.ReadAsync(destination, token);
                    if (timing is not null)
                    {
                        var hash = await MusicLyricsTimingMetadata.AudioPayloadHashAsync(destination, token);
                        if (string.Equals(hash, timing.AudioPayloadHash, StringComparison.OrdinalIgnoreCase)) audio.TimedLyrics = timing;
                        else audio.MetadataNote = "BoardTimingWrongAudio";
                    }
                }
                catch (Exception e) when (e is InvalidDataException or JsonException or NotSupportedException) { audio.MetadataNote = "BoardTimingUnreadable"; }
            }
            token.ThrowIfCancellationRequested(); return new(audio, destination);
        }
        catch { if (File.Exists(destination)) File.Delete(destination); throw; }
    }
}
