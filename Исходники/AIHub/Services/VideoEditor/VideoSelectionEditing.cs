using System.IO;
using System.Text.Json;

namespace AIHub.Services.VideoEditor;

internal sealed record VideoClipboardClip(string TrackId, string Kind, VideoClip Clip);
internal sealed record VideoClipboard(VideoClipboardClip[] Clips, NarrationSegment[] Subtitles)
{
    internal bool Empty => Clips.Length == 0 && Subtitles.Length == 0;
    internal double Start => Clips.Select(c => c.Clip.Start).Concat(Subtitles.Select(n => n.SubtitleStart ?? n.Start)).DefaultIfEmpty(0).Min();
    internal double End => Clips.Select(c => c.Clip.Start + c.Clip.Duration).Concat(Subtitles.Select(n => n.SubtitleEnd ?? n.End)).DefaultIfEmpty(0).Max();
}

internal static class VideoSelectionEditing
{
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    internal static VideoClip[] Clips(VideoProject p, IEnumerable<VideoItemId> selection, bool requireUnlocked = true)
    {
        var ids = selection.Where(i => !i.Subtitle).Select(i => i.Id).ToHashSet();
        var all = p.Tracks.SelectMany(t => t.Clips).ToArray();
        var links = all.Where(c => !p.SelectBySeries && ids.Contains(c.Id) && c.LinkId is not null).Select(c => c.LinkId!).ToHashSet();
        var clips = all.Where(c => ids.Contains(c.Id) || c.LinkId is {} link && links.Contains(link)).ToArray();
        if (requireUnlocked && p.Tracks.Any(t => t.Locked && t.Clips.Any(clips.Contains))) throw new InvalidOperationException("VideoEditor.SelectionLocked");
        return clips;
    }
    private static NarrationSegment[] Subtitles(VideoProject p, IEnumerable<VideoItemId> selection)
    {
        var ids = selection.Where(i => i.Subtitle).Select(i => i.Id).ToHashSet(); return p.Narration.Where(n => ids.Contains(n.Id)).ToArray();
    }
    internal static double Move(VideoProject p, IReadOnlyCollection<VideoItemId> selection, double delta, string? targetTrack = null)
    {
        if (!double.IsFinite(delta)) throw new InvalidDataException("VideoEditor.InvalidProject");
        var clips = Clips(p, selection); var subtitles = Subtitles(p, selection);
        var cues = VideoSubtitles.Cues(p).Where(n => subtitles.Any(s => s.Id == n.Id)).ToArray();
        var sourceClips = clips.Where(c => c.AssetId is {} id && p.Asset(id).Kind == "video").ToArray();
        var followingPlans = subtitles.Where(n => sourceClips.Any(c => c.Start < n.End - .001 && c.Start + c.Duration > n.Start + .001)).ToArray();
        delta = Math.Max(delta, -clips.Select(c => c.Start).Concat(cues.Select(n => n.Start)).Concat(followingPlans.Select(n => n.Start)).DefaultIfEmpty(0).Min());
        foreach (var c in clips) c.Start += delta;
        foreach (var n in cues) VideoSubtitles.SetRange(p, n.Id, n.Start + delta, n.End + delta);
        foreach (var n in followingPlans) { n.Start += delta; n.End += delta; }
        if (selection.Count == 1 && !selection.First().Subtitle && targetTrack is not null) {
            var clip = clips.Single(c => c.Id == selection.First().Id); var owner = p.Tracks.Single(t => t.Clips.Contains(clip));
            if (p.Tracks.FirstOrDefault(t => t.Id == targetTrack) is { Locked: false } target && target.Kind == owner.Kind && target != owner) { owner.Clips.Remove(clip); target.Clips.Add(clip); }
        }
        return delta;
    }
    internal static void Delete(VideoProject p, IReadOnlyCollection<VideoItemId> selection)
    {
        var clips = Clips(p, selection); var subtitles = Subtitles(p, selection).Select(n => n.Id).ToHashSet();
        foreach (var t in p.Tracks) { t.Clips.RemoveAll(clips.Contains); foreach (var c in t.Clips.Where(c => c.NarrationId is {} n && subtitles.Contains(n))) c.NarrationId = null; }
        p.Narration.RemoveAll(n => subtitles.Contains(n.Id));
    }
    internal static VideoClipboard Capture(VideoProject p, IReadOnlyCollection<VideoItemId> selection)
    {
        VideoSeries.Ensure(p); var clips = Clips(p, selection, false); var subtitles = Subtitles(p, selection); var cues = VideoSubtitles.Cues(p).ToDictionary(n => n.Id);
        return new(p.Tracks.SelectMany(t => t.Clips.Where(clips.Contains).Select(c => new VideoClipboardClip(t.Id, t.Kind, Copy(c)))).ToArray(),
            subtitles.Select(n => { var copy = Copy(n); copy.SubtitleStart = cues[n.Id].Start; copy.SubtitleEnd = cues[n.Id].End; return copy; }).ToArray());
    }
    internal static VideoItemId[] Paste(VideoProject p, VideoClipboard bundle, double at)
    {
        if (bundle.Empty) return []; VideoSeries.Ensure(p); var delta = Math.Max(0, at) - bundle.Start; var series = new Dictionary<int, int>();
        var links = new Dictionary<string, string>(); var captions = bundle.Subtitles.ToDictionary(n => n.Id, _ => Guid.NewGuid().ToString("N")); var result = new List<VideoItemId>();
        foreach (var item in bundle.Clips) {
            var c = Copy(item.Clip); c.Id = Guid.NewGuid().ToString("N"); c.Start += delta;
            c.SeriesNumbers = VideoSeries.SplitNumbers(p, c.SeriesNumbers, series);
            if (c.LinkId is {} old) { if (!links.TryGetValue(old, out var link)) links[old] = link = Guid.NewGuid().ToString("N"); c.LinkId = link; }
            c.NarrationId = c.NarrationId is {} n && captions.TryGetValue(n, out var replacement) ? replacement : null;
            var track = p.Tracks.FirstOrDefault(t => t.Id == item.TrackId && !t.Locked) ?? p.Tracks.FirstOrDefault(t => t.Kind == item.Kind && !t.Locked);
            if (track is null) { track = new() { Kind = item.Kind, Name = item.Kind }; p.Tracks.Add(track); }
            track.Clips.Add(c); result.Add(new(c.Id));
        }
        foreach (var item in bundle.Subtitles) {
            var n = Copy(item); n.Id = captions[item.Id]; var duration = n.End - n.Start;
            n.SeriesNumbers = VideoSeries.SplitNumbers(p, n.SeriesNumbers, series);
            n.Start = Math.Max(0, n.Start + delta); n.End = n.Start + duration; n.SubtitleStart += delta; n.SubtitleEnd += delta;
            n.AudioAssetId = null; n.SpeechOffset = null; n.SpeechLength = null; n.SpokenDuration = null; n.Stale = false;
            p.Narration.Add(n); result.Add(new(n.Id, true));
        }
        return result.ToArray();
    }
    internal static void Split(VideoProject p, IReadOnlyCollection<VideoItemId> selection, double at, int? textCaret = null)
    {
        VideoSeries.Ensure(p); var series = new Dictionary<int, int>(); var clips = Clips(p, selection); var done = new HashSet<string>();
        var beforeCues = VideoSubtitles.Cues(p).ToDictionary(n => n.Id);
        foreach (var c in clips) {
            var linked = !p.SelectBySeries || c.LinkId is {} link && p.Tracks.SelectMany(t => t.Clips).Where(x => x.LinkId == link).All(clips.Contains);
            if (done.Add(linked ? c.LinkId ?? c.Id : c.Id)) VideoEditing.Split(p, c.Id, at, series, linked);
        }
        foreach (var n in Subtitles(p, selection).ToArray()) {
            var cue = beforeCues[n.Id];
            if (at <= cue.Start + 1 / p.Fps || at >= cue.End - 1 / p.Fps) continue;
            var right = Copy(n); right.Id = Guid.NewGuid().ToString("N");
            right.SeriesNumbers = VideoSeries.SplitNumbers(p, n.SeriesNumbers, series);
            var caret = textCaret ?? (int)Math.Round(n.Text.Length * (at - cue.Start) / (cue.End - cue.Start)); caret = Math.Clamp(caret, 0, n.Text.Length);
            if (textCaret is null) { var space = n.Text.IndexOf(' ', caret); if (space >= 0) caret = space; }
            right.Text = n.Text[caret..].Trim(); n.Text = n.Text[..caret].Trim();
            right.Start = at; right.End = cue.End; n.Start = cue.Start; n.End = at;
            right.SubtitleStart = at; right.SubtitleEnd = cue.End; n.SubtitleStart = cue.Start; n.SubtitleEnd = at;
            right.AudioAssetId = null; right.SpeechOffset = null; right.SpeechLength = null; right.SpokenDuration = null; n.Stale = n.AudioAssetId is not null;
            p.Narration.Add(right);
            foreach (var voice in p.Tracks.SelectMany(t => t.Clips).Where(c => c.NarrationId == n.Id && c.Start >= at - .001 && c.SeriesNumbers.Intersect(right.SeriesNumbers).Any())) voice.NarrationId = right.Id;
        }
    }
}
