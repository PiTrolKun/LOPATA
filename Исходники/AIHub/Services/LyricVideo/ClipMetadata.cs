using System.IO;
using System.Text.Json;

namespace AIHub.Services.LyricVideo;

internal static class ClipMetadata
{
    public static Dictionary<string, string> Create(ClipSession session, string operationId, ClipOptions options)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in session.SourceTags)
        {
            if (pair.Value.Length == 0) continue; // MP4 omits empty tags; do not claim they were written.
            if (pair.Key.Length > 100 || pair.Key.Length == 0 || pair.Key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or ':'))
                || pair.Value.Contains('\0')) continue;
            var key = pair.Key;
            if (key.Equals("encoder", StringComparison.OrdinalIgnoreCase) || key.Equals("encoded_by", StringComparison.OrdinalIgnoreCase)
                || key.Equals("creation_time", StringComparison.OrdinalIgnoreCase) || key.Equals(MusicLyricsTimingMetadata.JsonTag, StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("LOPATA_VIDEO", StringComparison.OrdinalIgnoreCase)) key = "LOPATA_SOURCE_" + key;
            result[key] = pair.Value;
        }
        result["title"] = session.Title;
        if (session.Artist.Length > 0) result["artist"] = session.Artist;
        result["encoded_by"] = "LOPATA " + session.Config.Version;
        result["creation_time"] = session.Provenance.ExportedUtc.ToString("O");
        result["LOPATA_VIDEO"] = session.Provenance.Json();
        result["LOPATA_VIDEO_OPERATION"] = operationId;
        result["LOPATA_VIDEO_OPTIONS"] = JsonSerializer.Serialize(options);
        if (result.Count > 256 || result.Sum(p => p.Value.Length) > 2 * 1024 * 1024)
            throw new InvalidDataException("LyricVideo.MetadataTooLarge");
        return result;
    }
    // FFmpeg loads each value from a UTF-8 file: even long lyrics/timing never hit Windows' command-line limit.
    public static async Task<string[]> ArgumentsAsync(Dictionary<string, string> tags, string directory, CancellationToken token)
    {
        Directory.CreateDirectory(directory); var args = new List<string>(); var i = 0;
        foreach (var pair in tags)
        {
            var path = Path.Combine(directory, (i++).ToString("000") + ".txt");
            await File.WriteAllTextAsync(path, pair.Key + "=" + pair.Value, new System.Text.UTF8Encoding(false), token);
            args.AddRange(["-/metadata", Path.GetFileName(path)]);
        }
        return args.ToArray();
    }
}
