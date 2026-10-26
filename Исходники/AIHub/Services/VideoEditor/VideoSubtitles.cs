using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AIHub.Services.VideoEditor;

internal static partial class VideoSubtitles
{
    public static NarrationSegment[] Cues(VideoProject project)
    {
        return project.Narration.Select(n => {
            var audio = project.Tracks.Where(t => t.Kind == "audio" && !t.Muted).SelectMany(t => t.Clips).FirstOrDefault(c => c.NarrationId == n.Id);
            // Keep long speech intact, but do not stretch the automatic text slot to its length.
            var start = audio?.Start ?? n.Start;
            var end = audio is null ? n.End : start + Math.Min(audio.Duration, audio.PlannedDuration ?? n.End - n.Start);
            if (audio is null && n.AudioAssetId is not null && n.SpeechOffset is {} offset && n.SpeechLength is {} length && project.Tracks.Where(t => t.Kind == "audio" && !t.Muted).SelectMany(t => t.Clips).FirstOrDefault(c => c.AssetId == n.AudioAssetId) is {} whole)
            { start = whole.Start + offset / whole.Speed; end = Math.Min(whole.Start + whole.Duration, start + Math.Min(length / whole.Speed, n.End - n.Start)); }
            if (n.SubtitleStart is {} manualStart && n.SubtitleEnd is {} manualEnd) { start = manualStart; end = manualEnd; }
            return new NarrationSegment { Id = n.Id, Start = start, End = end, Text = n.Text, Language = n.Language, SeriesNumbers = [.. n.SeriesNumbers],
                SubtitleTrackId = n.SubtitleTrackId, SubtitleTitle = n.SubtitleTitle, SubtitleDefault = n.SubtitleDefault, SubtitleForced = n.SubtitleForced };
        }).ToArray();
    }
    internal static void ResetRanges(VideoProject project, IEnumerable<string> ids)
    {
        var selected = ids.ToHashSet();
        foreach (var n in project.Narration.Where(n => selected.Contains(n.Id))) { n.SubtitleStart = null; n.SubtitleEnd = null; }
    }
    internal static void SetRange(VideoProject project, string id, double start, double end)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end - start < 1 / project.Fps) throw new InvalidDataException("VideoEditor.SubtitleInvalid");
        var n = project.Narration.Single(n => n.Id == id); n.SubtitleStart = start; n.SubtitleEnd = end;
    }
    public static string Srt(IEnumerable<NarrationSegment> segments, double from, double end)
    {
        var result = new StringBuilder(); var index = 0;
        foreach (var n in segments.OrderBy(n => n.Start))
        {
            if (n.End <= from || n.Start >= end || string.IsNullOrWhiteSpace(n.Text)) continue;
            result.Append(++index).Append('\n').Append(Stamp(Math.Max(n.Start, from) - from)).Append(" --> ").Append(Stamp(Math.Min(n.End, end) - from)).Append('\n');
            result.Append(n.Text.Replace("\r", "").Trim()).Append("\n\n");
        }
        return result.ToString();
    }
    private static string Stamp(double seconds) { var ms = (long)Math.Round(Math.Max(0, seconds) * 1000); return $"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00},{ms % 1000:000}"; }
    public static List<NarrationSegment> Parse(string srt, string language = "ru")
    {
        var result = new List<NarrationSegment>();
        foreach (Match m in Cue().Matches(srt.Replace("\r", "")))
        {
            double Time(string s) => TimeSpan.ParseExact(s.Replace(',', '.'), @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture).TotalSeconds;
            var start = Time(m.Groups[1].Value); var end = Time(m.Groups[2].Value); if (end <= start) throw new InvalidDataException("VideoEditor.SubtitleInvalid");
            result.Add(new() { Start = start, End = end, Text = m.Groups[3].Value.Trim(), Language = language });
        }
        if (result.Count == 0) throw new InvalidDataException("VideoEditor.SubtitleInvalid"); return result;
    }
    [GeneratedRegex(@"(?:^|\n)\d+\n(\d{2}:\d{2}:\d{2}[,.]\d{3})\s+-->\s+(\d{2}:\d{2}:\d{2}[,.]\d{3})[^\n]*\n([\s\S]*?)(?=\n\n|$)")]
    private static partial Regex Cue();
}
