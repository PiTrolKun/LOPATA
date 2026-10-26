using System.IO;
using System.Text.Json;

namespace AIHub.Services.VideoEditor;

public sealed class VideoProject
{
    public int Schema { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Video";
    public string DefaultVoice { get; set; } = "kokoro-sveta";
    public string DefaultLanguage { get; set; } = "ru";
    public int DefaultRate { get; set; } = 100;
    public VideoVoiceOptions DefaultVoiceOptions { get; set; } = new();
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public double Fps { get; set; } = 30;
    public int NextSeriesNumber { get; set; } = 1;
    public bool SelectBySeries { get; set; }
    public bool LinkedSelectionEnabled { get; set; } = true;
    public VideoSeriesLayers SeriesLayers { get; set; } = VideoSeriesLayers.All;
    public bool StretchAudio { get; set; } = true;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<VideoAsset> Assets { get; set; } = [];
    public List<VideoAttachment> ImportedFiles { get; set; } = [];
    public List<VideoTrack> Tracks { get; set; } = [new() { Name = "Video 1", Kind = "video" }, new() { Name = "Audio 1", Kind = "audio" }];
    public List<NarrationSegment> Narration { get; set; } = [];
    public OutputProvenance Provenance { get; set; } = new() { Scenario = "video-editor/project" };
    public double Duration => Math.Max(1, Math.Max(Tracks.SelectMany(t => t.Clips).Select(c => c.Start + c.Duration).DefaultIfEmpty(0).Max(), Narration.Select(n => Math.Max(n.End, n.SubtitleEnd ?? n.End)).DefaultIfEmpty(0).Max()));
    [System.Text.Json.Serialization.JsonIgnore]
    internal string? PreviewDirectory { get; set; }
    public VideoProject Clone() { var copy = JsonSerializer.Deserialize<VideoProject>(JsonSerializer.Serialize(this))!; copy.PreviewDirectory = PreviewDirectory; return copy; }
    public VideoAsset Asset(string id) => Assets.Single(a => a.Id == id);
    public void Validate()
    {
        VideoVoiceProfiles.ValidateStored(DefaultVoiceOptions);
        foreach (var segment in Narration) VideoVoiceProfiles.ValidateStored(segment.VoiceOptions);
        if (Schema != 1 || !Guid.TryParseExact(Id, "N", out _) || Width is < 16 or > 16384 || Height is < 16 or > 16384
            || !double.IsFinite(Fps) || Fps is < 1 or > 240 || Assets.Count > 10000 || Tracks.Count > 256 || ImportedFiles.Count > 10000
            || ImportedFiles.Any(f => f is null || !Path.IsPathFullyQualified(f.Path) || f.Length < 0))
            throw new InvalidDataException("VideoEditor.InvalidProject");
        if (Assets.Select(a => a.Id).Distinct().Count() != Assets.Count || Tracks.Select(t => t.Id).Distinct().Count() != Tracks.Count
            || Tracks.SelectMany(t => t.Clips).Select(c => c.Id).Distinct().Count() != Tracks.Sum(t => t.Clips.Count)) throw new InvalidDataException("VideoEditor.InvalidProject");
        foreach (var a in Assets)
            if (!Guid.TryParseExact(a.Id, "N", out _) || !Path.IsPathFullyQualified(a.Path) || a.Kind is not ("video" or "audio" or "image" or "svg")
                || !double.IsFinite(a.Duration) || a.Duration < 0) throw new InvalidDataException("VideoEditor.InvalidProject");
        if (NextSeriesNumber < 1 || (SeriesLayers & ~VideoSeriesLayers.All) != 0) throw new InvalidDataException("VideoEditor.InvalidProject");
        foreach (var series in Tracks.SelectMany(t => t.Clips).Select(c => c.SeriesNumbers).Concat(Narration.Select(n => n.SeriesNumbers)))
            if (series is null || series.Count > 10000 || series.Any(n => n <= 0) || series.Distinct().Count() != series.Count) throw new InvalidDataException("VideoEditor.InvalidProject");
        foreach (var t in Tracks)
        {
            if (!Guid.TryParseExact(t.Id, "N", out _) || t.Kind is not ("audio" or "video") || !double.IsFinite(t.Volume) || t.Volume is < 0 or > 4) throw new InvalidDataException("VideoEditor.InvalidProject");
            foreach (var c in t.Clips)
            {
                var values = new[] { c.Start, c.SourceIn, c.Duration, c.Speed, c.Volume, c.X, c.Y, c.Scale, c.Rotation, c.Opacity, c.FadeIn, c.FadeOut, c.Brightness, c.Contrast, c.Saturation, c.CropLeft, c.CropTop, c.CropRight, c.CropBottom };
                if (!Guid.TryParseExact(c.Id, "N", out _) || values.Any(x => !double.IsFinite(x)) || c.Start < 0 || c.SourceIn < 0 || c.Duration <= 0 || c.Speed is < .0625 or > 16
                    || c.Volume is < 0 or > 4 || c.Scale is < .01 or > 20 || c.Opacity is < 0 or > 1 || c.FadeIn < 0 || c.FadeOut < 0
                    || c.CropLeft < 0 || c.CropTop < 0 || c.CropRight < 0 || c.CropBottom < 0 || c.CropLeft + c.CropRight >= 1 || c.CropTop + c.CropBottom >= 1
                    || c.Brightness is < -1 or > 1 || c.Contrast is < 0 or > 3 || c.Saturation is < 0 or > 3
                    || (c.AssetId is not null && !Assets.Any(a => a.Id == c.AssetId)) || (c.AssetId is null && c.Text is null)) throw new InvalidDataException("VideoEditor.InvalidProject");
                if (c.AssetId is not null && Asset(c.AssetId) is { Kind: "audio" or "video" } source && !c.Freeze
                    && c.SourceIn + c.Duration * c.Speed > source.Duration + .1) throw new InvalidDataException("VideoEditor.SourceRange");
            }
        }
        foreach (var n in Narration)
            if (!Guid.TryParseExact(n.Id, "N", out _) || !double.IsFinite(n.Start) || !double.IsFinite(n.End) || n.Start < 0 || n.End <= n.Start
                || n.SpeechOffset is {} offset && (!double.IsFinite(offset) || offset < 0) || n.SpeechLength is {} length && (!double.IsFinite(length) || length <= 0)
                || n.SubtitleStart.HasValue != n.SubtitleEnd.HasValue
                || n.SubtitleStart is {} ss && (!double.IsFinite(ss) || ss < 0)
                || n.SubtitleEnd is {} se && (!double.IsFinite(se) || se <= n.SubtitleStart)
                || n.Language.Length is < 1 or > 20 || n.Language.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') || n.Rate is < 50 or > 200)
                throw new InvalidDataException("VideoEditor.InvalidProject");
    }
}
public sealed class VideoAsset
{
    public VideoSpeechProfile? NarratorProfile { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "video";
    public double Duration { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int VideoStream { get; set; } = -1;
    public int AudioStream { get; set; } = -1;
    public long Length { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public string? Proxy { get; set; }
    public List<OutputModelUse> Models { get; set; } = [];
    public List<MediaStreamChoice> AudioStreams { get; set; } = [];
    public List<MediaStreamChoice> SubtitleStreams { get; set; } = [];
}
public sealed record VideoAttachment(string Path, string Name, long Length, DateTime LastWriteUtc);
public sealed class VideoTrack
{
    public bool BackgroundMusic { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "video";
    public double Volume { get; set; } = 1;
    public bool Muted { get; set; }
    public bool Hidden { get; set; }
    public bool Locked { get; set; }
    public List<VideoClip> Clips { get; set; } = [];
}
public sealed class VideoClip
{
    public bool GeneratedMusic { get; set; }
    public List<int> SeriesNumbers { get; set; } = [];
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? AssetId { get; set; }
    public string? Text { get; set; }
    public string? LinkId { get; set; }
    public string? NarrationId { get; set; }
    public double? PlannedDuration { get; set; }
    public double Start { get; set; }
    public double SourceIn { get; set; }
    public double Duration { get; set; } = 5;
    public double Speed { get; set; } = 1;
    public double Volume { get; set; } = 1;
    public double X { get; set; }
    public double Y { get; set; }
    public double Scale { get; set; } = 1;
    public double Rotation { get; set; }
    public double Opacity { get; set; } = 1;
    public double FadeIn { get; set; }
    public double FadeOut { get; set; }
    public double Brightness { get; set; }
    public double Contrast { get; set; } = 1;
    public double Saturation { get; set; } = 1;
    public double CropLeft { get; set; }
    public double CropTop { get; set; }
    public double CropRight { get; set; }
    public double CropBottom { get; set; }
    public bool Freeze { get; set; }
}
public sealed class NarrationSegment
{
    public List<int> SeriesNumbers { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public string? DisplayName { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string Caption => (DisplayName is {} name ? name + " · " : "") + TimeSpan.FromSeconds(DisplayStart ?? SubtitleStart ?? Start).ToString(@"hh\:mm\:ss") + " – " + TimeSpan.FromSeconds(DisplayEnd ?? SubtitleEnd ?? End).ToString(@"hh\:mm\:ss") + (string.IsNullOrWhiteSpace(Text) ? "" : " · " + Text);
    [System.Text.Json.Serialization.JsonIgnore]
    public double? DisplayStart { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public double? DisplayEnd { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public double Start { get; set; }
    public double End { get; set; }
    public double? SubtitleStart { get; set; }
    public double? SubtitleEnd { get; set; }
    public string Text { get; set; } = "";
    public string Language { get; set; } = "ru";
    public string SubtitleTrackId { get; set; } = "narration";
    public string SubtitleTitle { get; set; } = "Narration";
    public bool SubtitleDefault { get; set; }
    public bool SubtitleForced { get; set; }
    public string Provider { get; set; } = "kokoro-sveta";
    public int Rate { get; set; } = 100;
    public VideoVoiceOptions VoiceOptions { get; set; } = new();
    public bool Stale { get; set; }
    public string? AudioAssetId { get; set; }
    public double? SpokenDuration { get; set; }
    public double? SpeechOffset { get; set; }
    public double? SpeechLength { get; set; }
    public OutputModelUse[] Models { get; set; } = [];
}
public sealed record MediaStreamChoice(int Index, string Name, string Language, bool Default, bool Forced, string Codec);
