using System.Globalization;
using System.IO;
using System.Text.Json;
using AIHub.Services.VectorGraphics;

namespace AIHub.Services.VideoEditor;

internal static class VideoImport
{
    public static async Task<VideoAsset> ReadAsync(string path, string work, CancellationToken token)
    {
        path = Path.GetFullPath(path); var file = new FileInfo(path); if (!file.Exists) throw new FileNotFoundException("VideoEditor.MissingSource", path);
        Directory.CreateDirectory(work);
        var asset = new VideoAsset { Path = path, Name = Path.GetFileName(path), Length = file.Length, LastWriteUtc = file.LastWriteTimeUtc };
        var ext = file.Extension.ToLowerInvariant();
        if (ext == ".svg")
        {
            var svg = await File.ReadAllTextAsync(path, token); var doc = VectorSvg.Parse(svg);
            var box = ((string?)doc.Root!.Attribute("viewBox"))?.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
            asset.Width = 1024; asset.Height = 1024;
            if (box?.Length == 4 && double.TryParse(box[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var w) && double.TryParse(box[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var h) && w > 0 && h > 0)
            { asset.Width = (int)Math.Clamp(w, 16, 16384); asset.Height = (int)Math.Clamp(h, 16, 16384); }
            asset.Kind = "svg"; asset.Models = AIHub.Services.VectorEditor.VectorEditorSvg.Facts([svg], DateTimeOffset.UtcNow).Models.ToList(); return asset;
        }
        using var probe = await VideoRuntime.ProbeAsync(path, token);
        var streams = probe.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        MediaStreamChoice Choice(JsonElement stream) {
            var index = stream.GetProperty("index").GetInt32(); var lang = "und"; var name = "";
            if (stream.TryGetProperty("tags", out var tags)) { if (tags.TryGetProperty("language", out var l)) lang = l.GetString() ?? "und"; if (tags.TryGetProperty("title", out var title)) name = title.GetString() ?? ""; }
            bool Flag(string flag) => stream.TryGetProperty("disposition", out var d) && d.TryGetProperty(flag, out var f) && f.GetInt32() == 1;
            return new(index, lang + " · " + name + " (#" + index + ")", lang, Flag("default"), Flag("forced"), stream.GetProperty("codec_name").GetString() ?? "unknown");
        }
        asset.AudioStreams = streams.Where(s => s.GetProperty("codec_type").GetString() == "audio").Select(Choice).ToList();
        asset.SubtitleStreams = streams.Where(s => s.GetProperty("codec_type").GetString() == "subtitle").Select(Choice).ToList();
        var v = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "video" && (!s.TryGetProperty("disposition", out var d) || !d.TryGetProperty("attached_pic", out var ap) || ap.GetInt32() == 0));
        var a = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "audio" && s.TryGetProperty("disposition", out var d) && d.GetProperty("default").GetInt32() == 1);
        if (a.ValueKind == JsonValueKind.Undefined) a = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "audio");
        if (v.ValueKind == JsonValueKind.Undefined && a.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("VideoEditor.UnsupportedMedia");
        asset.Kind = ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp" ? "image" : v.ValueKind != JsonValueKind.Undefined ? "video" : "audio";
        if (v.ValueKind != JsonValueKind.Undefined) { asset.VideoStream = v.GetProperty("index").GetInt32(); asset.Width = v.GetProperty("width").GetInt32(); asset.Height = v.GetProperty("height").GetInt32(); }
        if (a.ValueKind != JsonValueKind.Undefined) asset.AudioStream = a.GetProperty("index").GetInt32();
        double seconds = 0;
        if (probe.RootElement.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var duration)) double.TryParse(duration.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
        asset.Duration = seconds;
        if (asset.Kind is "video" or "audio" && asset.Duration <= 0) throw new InvalidDataException("VideoEditor.UnsupportedMedia");
        return asset;
    }
    public static void Insert(VideoProject p, VideoAsset asset, double position)
    {
        if (!p.Assets.Any(a => a.Id == asset.Id)) p.Assets.Add(asset);
        var kind = asset.Kind == "audio" ? "audio" : "video";
        var track = p.Tracks.FirstOrDefault(t => t.Kind == kind && !t.Locked && !t.Clips.Any(c => c.Start < position + Math.Max(1, asset.Duration) && c.Start + c.Duration > position));
        if (track is null) { track = new VideoTrack { Kind = kind, Name = kind + " " + (p.Tracks.Count(t => t.Kind == kind) + 1) }; p.Tracks.Add(track); }
        var duration = asset.Kind is "image" or "svg" ? 5 : asset.Duration;
        var link = Guid.NewGuid().ToString("N");
        track.Clips.Add(new() { AssetId = asset.Id, Start = position, Duration = duration, LinkId = asset.Kind == "video" && asset.AudioStream >= 0 ? link : null });
        if (asset.Kind == "video" && asset.AudioStream >= 0)
        {
            var audio = p.Tracks.FirstOrDefault(t => t.Kind == "audio" && !t.Locked && !t.Clips.Any(c => c.Start < position + duration && c.Start + c.Duration > position));
            if (audio is null) { audio = new() { Kind = "audio", Name = "Audio " + (p.Tracks.Count(t => t.Kind == "audio") + 1) }; p.Tracks.Add(audio); }
            audio.Clips.Add(new() { AssetId = asset.Id, Start = position, Duration = duration, LinkId = link });
        }
        VideoSeries.Ensure(p);
    }
    public static async Task<string> ThumbnailAsync(VideoAsset a, string work, CancellationToken token)
    {
        var path = Path.Combine(work, a.Id + ".png"); if (File.Exists(path)) return path;
        if (a.Kind == "svg") await VectorRenderer.RenderFrameAsync(await File.ReadAllTextAsync(a.Path, token), path, 320, 240, token);
        else if (a.Kind != "audio") await VideoRuntime.RunAsync(false, ["-v", "error", "-n", "-i", a.Path, "-map", "0:" + a.VideoStream, "-frames:v", "1", "-vf", "scale=320:-1", path], token);
        return path;
    }
}
