namespace AIHub.Services.VideoEditor;

internal static class VideoSeriesSpacing
{
    internal sealed record Plan(Dictionary<string, double> Clips, Dictionary<string, double> Text)
    {
        internal int Changed => Clips.Values.Concat(Text.Values).Count(d => d > .001);
        internal void Apply(VideoProject p)
        {
            foreach (var c in p.Tracks.SelectMany(t => t.Clips)) if (Clips.TryGetValue(c.Id, out var delta)) c.Start += delta;
            foreach (var n in p.Narration) if (Text.TryGetValue(n.Id, out var delta)) { n.Start += delta; n.End += delta; if (n.SubtitleStart.HasValue) { n.SubtitleStart += delta; n.SubtitleEnd += delta; } }
        }
    }
    private sealed record Part(string Id, bool Text, string Track, int Group, double Start, double End, bool Locked);
    internal static Plan Build(VideoProject p)
    {
        VideoSeries.Ensure(p); var parents = new Dictionary<int, int>();
        int Find(int n) { if (!parents.TryGetValue(n, out var root)) return parents[n] = n; return root == n ? n : parents[n] = Find(root); }
        foreach (var numbers in p.Tracks.SelectMany(t => t.Clips).Select(c => c.SeriesNumbers).Concat(p.Narration.Select(n => n.SeriesNumbers)))
            foreach (var number in numbers.Skip(1)) parents[Find(number)] = Find(numbers[0]);
        var parts = p.Tracks.SelectMany(t => t.Clips.Select(c => new Part(c.Id, false, t.Id, Find(c.SeriesNumbers[0]), c.Start, c.Start + c.Duration, t.Locked)))
            .Concat(VideoSubtitles.Cues(p).Select(n => new Part(n.Id, true, "subtitle:" + n.Language + ":" + n.SubtitleTrackId, Find(n.SeriesNumbers[0]), n.Start, n.End, false))).ToArray();
        var groups = parts.GroupBy(i => i.Group).OrderBy(g => g.Min(i => i.Start)).ThenBy(g => g.Key).ToArray();
        var ends = new Dictionary<string, double>(); var clips = new Dictionary<string, double>(); var text = new Dictionary<string, double>();
        foreach (var group in groups) {
            double shift = 0;
            foreach (var item in group) if (ends.TryGetValue(item.Track, out var end) && item.Start < end - .001) shift = Math.Max(shift, end + Math.Max(1 / p.Fps, .12) - item.Start);
            shift = Math.Ceiling(shift * p.Fps - 1e-7) / p.Fps;
            if (shift > .001 && group.Any(i => i.Locked)) throw new InvalidOperationException("VideoEditor.SpacingLocked");
            foreach (var item in group) { (item.Text ? text : clips)[item.Id] = shift; ends[item.Track] = Math.Max(ends.GetValueOrDefault(item.Track), item.End + shift); }
        }
        return new(clips, text);
    }
}

internal sealed class VideoSpacingSession
{
    private readonly VideoEditing _editing;
    private readonly long _revision;
    private readonly bool _applied;
    internal VideoProject Original { get; }
    internal bool BelongsTo(VideoEditing editing) => ReferenceEquals(editing, _editing);
    internal bool Untouched => _editing.Revision == _revision;
    internal VideoSpacingSession(VideoEditing editing)
    {
        _editing = editing; Original = editing.Project.Clone(); var plan = VideoSeriesSpacing.Build(editing.Project); _applied = plan.Changed > 0;
        if (_applied) editing.Edit(plan.Apply); _revision = editing.Revision;
    }
    internal bool Finish()
    {
        if (!Untouched || !_applied) return false;
        _editing.Undo(); return true;
    }
}
