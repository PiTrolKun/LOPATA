using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.SongClip;

internal static class SongClipSourceMetadata
{
    internal static async Task<(string Lyrics,string Timings)> ReadAsync(string source,CancellationToken token)
    {
        await MusicAudioRuntime.Default.PrepareAsync(token);
        using var probe=JsonDocument.Parse(await MusicAudioRuntime.Default.RunAsync(true,
            ["-v","error","-show_entries","format_tags:stream=codec_type:stream_tags","-of","json",source],token));
        return Parse(probe.RootElement);
    }

    internal static (string Lyrics,string Timings) Parse(JsonElement probe)
    {
        var tags=AudioConversionMetadata.ProbeTags(probe);
        // ID3 USLT written by LOPATA is exposed by ffprobe as lyrics-xxx;
        // other languages/descriptors have the same lyrics-* form.
        var lyrics=tags.Where(tag=>!string.IsNullOrWhiteSpace(tag.Value)&&
                (tag.Key.Equals("lyrics",StringComparison.OrdinalIgnoreCase)
                ||tag.Key.StartsWith("lyrics-",StringComparison.OrdinalIgnoreCase)
                ||tag.Key.StartsWith("unsyncedlyrics",StringComparison.OrdinalIgnoreCase)))
            .OrderBy(tag=>tag.Key.Equals("lyrics",StringComparison.OrdinalIgnoreCase)?0:1)
            .Select(tag=>tag.Value).FirstOrDefault()??"";
        var timing=tags.GetValueOrDefault(MusicLyricsTimingMetadata.LrcTag)??"";
        if((lyrics.Length==0||timing.Length==0)&&tags.TryGetValue(MusicLyricsTimingMetadata.JsonTag,out var json))
        {
            try
            {
                var timed=JsonSerializer.Deserialize<MusicTimedLyrics>(json);
                if(timed is not null){timed.Validate();if(lyrics.Length==0)lyrics=timed.SourceLyrics;if(timing.Length==0)timing=MusicLyricsTimingMetadata.Lrc(timed);}
            }
            // An optional malformed timing tag must not discard readable text or LRC.
            catch(Exception error) when(error is JsonException or InvalidDataException) { }
        }
        return (lyrics,timing);
    }
}
