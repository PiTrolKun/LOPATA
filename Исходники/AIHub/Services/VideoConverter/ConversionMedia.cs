using System.Globalization;
using System.IO;
using System.Text.Json;
using AIHub.Services.VideoEditor;
using AIHub.Services.VideoUpscale;

namespace AIHub.Services.VideoConverter;

internal sealed record ConversionTrack(int Index, string Type, string Codec, long? Bitrate, int Channels,
    int SampleRate, double? Duration, double Start, string? Language, string? Title, string Disposition)
{
    internal bool IsArtwork => Type == "video" && Disposition.Split('+').Contains("attached_pic");
    internal bool IsPicture => Type == "video" && !IsArtwork;
}

internal sealed record ConversionMedia(long Bytes, double Duration, int Width, int Height, int Rotation,
    string Sar, double Fps, string Pixel, string Format, ConversionTrack Video, ConversionTrack[] Tracks,
    IReadOnlyDictionary<string, string> Color, int Chapters, bool DynamicHdr, OutputProvenance? Provenance,
    string? ConversionTag)
{
    internal string? Comment { get; init; }
    internal bool Turned => Math.Abs(Rotation) == 90;
    internal int OrientedWidth => Turned ? Height : Width;
    internal int OrientedHeight => Turned ? Width : Height;
    internal string OrientedSar => Turned && Sar.Split(':') is [var x, var y] ? $"{y}:{x}" : Sar;

    internal static async Task<ConversionMedia> ProbeAsync(string path, CancellationToken token)
    {
        using var result = JsonDocument.Parse(await UpscaleFfmpeg.RunAsync(true,
            ["-v", "error", "-show_streams", "-show_format", "-show_chapters", "-of", "json", path], token));
        var root = result.RootElement;
        var format = root.GetProperty("format");
        var streams = root.GetProperty("streams").EnumerateArray().ToArray();
        var tracks = streams.Select(ReadTrack).ToArray();
        var video = tracks.FirstOrDefault(t => t.IsPicture) ?? throw new InvalidDataException("В файле нет видеодорожки.");
        var stream = streams.First(s => Int(s, "index") == video.Index);
        var width = Int(stream, "width");
        var height = Int(stream, "height");
        var duration = Number(format, "duration") ?? video.Duration ?? 0;
        var fps = Ratio(Value(stream, "avg_frame_rate"));
        if (fps == 0) fps = Ratio(Value(stream, "r_frame_rate"));
        if (width <= 0 || height <= 0 || duration <= 0 || fps is <= 0 or > 1000)
            throw new InvalidDataException("Не удалось определить разрешение, длительность или частоту кадров.");
        var sar = Value(stream, "sample_aspect_ratio");
        var rotation = 0;
        var dynamicHdr = false;
        if (stream.TryGetProperty("side_data_list", out var side))
            foreach (var item in side.EnumerateArray())
            {
                if (Number(item, "rotation") is double angle) rotation = (int)Math.Round(angle % 360);
                var type = Value(item, "side_data_type");
                dynamicHdr |= type.Contains("DOVI", StringComparison.OrdinalIgnoreCase)
                    || type.Contains("Dynamic HDR", StringComparison.OrdinalIgnoreCase);
            }
        if (rotation > 180) rotation -= 360;
        if (rotation < -180) rotation += 360;
        var color = new[] { "color_range", "color_space", "color_transfer", "color_primaries", "chroma_location" }
            .Select(key => (key, value: Value(stream, key)))
            .Where(pair => pair.value.Length > 0 && pair.value is not ("unknown" or "unspecified" or "reserved"))
            .ToDictionary(pair => pair.key, pair => pair.value, StringComparer.Ordinal);
        // The libaom decoder doesn't expose AV1 chroma siting in the stream metadata.
        // AV1CodecConfigurationRecord stores it in the last two bits of its third byte.
        if (video.Codec == "av1" && !color.ContainsKey("chroma_location") &&
            await ReadAv1ChromaAsync(path, video.Index, token) is { } position)
            color.Add("chroma_location", position);
        return new(new FileInfo(path).Length, duration, width, height, rotation,
            Ratio(sar) > 0 ? sar : "1:1", fps, Value(stream, "pix_fmt"), Value(format, "format_name"), video, tracks,
            color, root.TryGetProperty("chapters", out var chapters) ? chapters.GetArrayLength() : 0, dynamicHdr,
            VideoImportProvenance.Read(root), VideoImportProvenance.ReadTag(format, "LOPATA_CONVERSION"))
        { Comment = VideoImportProvenance.UserComment(format) };
    }

    private static async Task<string?> ReadAv1ChromaAsync(string path, int index, CancellationToken token)
    {
        using var result = JsonDocument.Parse(await UpscaleFfmpeg.RunAsync(true,
            ["-v", "error", "-select_streams", index.ToString(CultureInfo.InvariantCulture),
                "-show_entries", "stream=extradata", "-show_data", "-of", "json", path], token));
        var streams = result.RootElement.GetProperty("streams");
        return streams.GetArrayLength() == 1 ? Av1Chroma(Value(streams[0], "extradata")) : null;
    }

    private static string? Av1Chroma(string data)
    {
        const string prefix = "00000000: ";
        var line = data.AsSpan().TrimStart();
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var header = line[prefix.Length..];
        if (header.Length < 9 || !header.StartsWith("81", StringComparison.Ordinal) || header[4] != ' '
            || !byte.TryParse(header.Slice(5, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var flags)
            || (flags & 0x0c) != 0x0c)
            return null;
        return (flags & 3) switch { 1 => "left", 2 => "topleft", _ => null };
    }

    private static ConversionTrack ReadTrack(JsonElement s)
    {
        var flags = s.TryGetProperty("disposition", out var disposition)
            ? string.Join('+', disposition.EnumerateObject().Where(p => p.Value.TryGetInt32(out var set) && set == 1)
                .Select(p => p.Name).Order(StringComparer.Ordinal))
            : "";
        var seconds = Number(s, "duration");
        if (seconds is null && Tag(s, "DURATION") is { } stamp)
        {
            // Matroska stores nanoseconds. TimeSpan accepts seven places.
            var point = stamp.IndexOf('.');
            if (point >= 0 && stamp.Length > point + 8) stamp = stamp[..(point + 8)];
            if (TimeSpan.TryParse(stamp, CultureInfo.InvariantCulture, out var parsed))
                seconds = Math.Max(0, parsed.TotalSeconds - (Number(s, "start_time") ?? 0));
        }
        return new(Int(s, "index"), Value(s, "codec_type"), Value(s, "codec_name"), (long?)Number(s, "bit_rate"),
            Int(s, "channels"), Int(s, "sample_rate"), seconds, Number(s, "start_time") ?? 0,
            Tag(s, "language"), Tag(s, "title") ?? Tag(s, "handler_name"), flags);
    }

    internal static double Ratio(string? text)
    {
        if (text is null) return 0;
        var parts = text.Split([':', '/']);
        return parts is [var num, var den]
            && double.TryParse(num, CultureInfo.InvariantCulture, out var a)
            && double.TryParse(den, CultureInfo.InvariantCulture, out var b) && b > 0 && a > 0 ? a / b : 0;
    }

    private static string? Tag(JsonElement e, string name) =>
        e.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object
            ? tags.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value is
                { ValueKind: JsonValueKind.String } value ? value.GetString() : null
            : null;
    private static string Value(JsonElement e, string key) => e.TryGetProperty(key, out var value) ? value.ToString() : "";
    private static int Int(JsonElement e, string key) => int.TryParse(Value(e, key), CultureInfo.InvariantCulture, out var n) ? n : 0;
    private static double? Number(JsonElement e, string key) =>
        double.TryParse(Value(e, key), CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null;
}
