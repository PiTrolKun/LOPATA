using System.Globalization;
using System.IO;
using System.Text.Json;

namespace AIHub.Services.SongClip;

internal static class H3MusicResultValidation
{
    internal static double Validate(JsonElement output, JsonElement source, SongClipSettings settings,
        string operationId, IReadOnlyList<OutputModelUse> expected, string scenario = "song-to-clip/h3_music_context")
    {
        var streams = output.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.SingleOrDefault(s => s.GetProperty("codec_type").GetString() == "video");
        var audio = streams.SingleOrDefault(s => s.GetProperty("codec_type").GetString() == "audio");
        var input = source.GetProperty("streams").EnumerateArray().FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "audio");
        if (video.ValueKind != JsonValueKind.Object || audio.ValueKind != JsonValueKind.Object || input.ValueKind != JsonValueKind.Object
            || video.GetProperty("width").GetInt32() != settings.Width || video.GetProperty("height").GetInt32() != settings.Height
            || video.GetProperty("avg_frame_rate").GetString() != "24/1")
            throw new InvalidDataException("Invalid H3 music video streams.");
        var seconds = Duration(input, source);
        if (Math.Abs(Duration(video, output) - seconds) > .1 || Math.Abs(Duration(audio, output) - seconds) > .1)
            throw new InvalidDataException("H3 music video does not cover the complete source song.");
        var tags = output.GetProperty("format").GetProperty("tags");
        string? Tag(string name) => tags.EnumerateObject().FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Value is { ValueKind: JsonValueKind.String } tag ? tag.GetString() : null;
        var facts = Tag("LOPATA_VIDEO") is { } value ? JsonSerializer.Deserialize<OutputProvenance>(value) : null;
        if (facts is null || facts.Program != "LOPATA" || facts.Scenario != scenario
            || facts.GenerationSeconds is not >= 0 || Tag("LOPATA_OPERATION") != operationId
            || expected.Any(e => !facts.Models.Any(m => m.Id == e.Id && m.Revision == e.Revision && m.Role == e.Role)))
            throw new InvalidDataException("Missing H3 music video provenance.");
        _ = facts.Json(); return Duration(video, output);
    }

    private static double Duration(JsonElement stream, JsonElement document)
    {
        var value = stream.TryGetProperty("duration", out var duration) ? duration : document.GetProperty("format").GetProperty("duration");
        if (!double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || !double.IsFinite(seconds) || seconds <= 0) throw new InvalidDataException("Invalid H3 music media duration.");
        return seconds;
    }
}
