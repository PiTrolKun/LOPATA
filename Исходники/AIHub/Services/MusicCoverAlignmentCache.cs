using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

internal static class MusicCoverAlignmentCache
{
    private sealed record Entry(string AudioHash, string LyricsHash, string MatcherVersion, MusicLyricsAlignmentResult Result);

    internal static async Task<MusicLyricsAlignmentResult> ReadAsync(string audio, string lyrics, string folder, Action<string> log, CancellationToken token)
    {
        Directory.CreateDirectory(folder);
        var audioHash = await MusicAudioTaskFiles.HashAsync(audio, token); var lyricsHash = MusicTimedLyrics.HashLyrics(lyrics);
        var path = Path.Combine(folder, "alignment-cache.json"); var sidecar = path + ".sha256";
        if (File.Exists(path) && File.Exists(sidecar) && new FileInfo(path).Length < 8_000_000
            && await File.ReadAllTextAsync(sidecar, token) == await MusicAudioTaskFiles.HashAsync(path, token)) {
            var cached = JsonSerializer.Deserialize<Entry>(await File.ReadAllTextAsync(path, token));
            if (cached is not null && cached.AudioHash == audioHash && cached.LyricsHash == lyricsHash && cached.MatcherVersion == MusicLyricsAlignmentService.ProviderVersion) {
                log("Reuse verified phrase timings."); return cached.Result;
            }
        }
        var aligner = new MusicLyricsAlignmentService(); aligner.Log += log;
        var result = await aligner.AlignAsync(audio, lyrics, folder, token);
        if (audioHash != await MusicAudioTaskFiles.HashAsync(audio, token)) throw new InvalidDataException("Music.Transform.InputChanged");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new Entry(audioHash, lyricsHash, MusicLyricsAlignmentService.ProviderVersion, result)), token);
            File.Move(temporary, path, true);
            await File.WriteAllTextAsync(sidecar, await MusicAudioTaskFiles.HashAsync(path, token), token);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return result;
    }
}
