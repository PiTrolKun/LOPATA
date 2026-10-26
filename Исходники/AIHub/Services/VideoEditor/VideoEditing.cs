using System.IO;
using System.Text.Json;

namespace AIHub.Services.VideoEditor;

public sealed class VideoEditing
{
    private readonly List<string> _undo = [], _redo = [];
    public VideoProject Project { get; private set; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    internal long Revision { get; private set; }
    public VideoEditing(VideoProject project) { project.Validate(); VideoSeries.Ensure(project); Project = project; }
    public void Edit(Action<VideoProject> action)
    {
        var before = JsonSerializer.Serialize(Project);
        try { action(Project); VideoSeries.Ensure(Project); Project.Validate(); }
        catch { Project = JsonSerializer.Deserialize<VideoProject>(before)!; throw; }
        _undo.Add(before); while (_undo.Count > 100 || _undo.Count > 1 && _undo.Sum(s => (long)s.Length) > 8 * 1024 * 1024) _undo.RemoveAt(0); _redo.Clear(); Project.ModifiedUtc = DateTimeOffset.UtcNow; Revision++;
    }
    public void Undo() { if (!CanUndo) return; _redo.Add(JsonSerializer.Serialize(Project)); Project = JsonSerializer.Deserialize<VideoProject>(_undo[^1])!; _undo.RemoveAt(_undo.Count - 1); Revision++; }
    public void Redo() { if (!CanRedo) return; _undo.Add(JsonSerializer.Serialize(Project)); Project = JsonSerializer.Deserialize<VideoProject>(_redo[^1])!; _redo.RemoveAt(_redo.Count - 1); Revision++; }
    public static void Split(VideoProject p, string clipId, double at, Dictionary<int, int>? series = null, bool linked = true)
    {
        var track = p.Tracks.Single(t => t.Clips.Any(c => c.Id == clipId));
        if (track.Locked) return;
        var selected = track.Clips.Single(c => c.Id == clipId);
        if (linked && selected.LinkId is {} linkId && p.Tracks.Any(t => t.Locked && t.Clips.Any(c => c.LinkId == linkId))) return;
        VideoSeries.Ensure(p); series ??= [];
        var related = !linked || selected.LinkId is null ? new[] { selected } : p.Tracks.Where(t => !t.Locked).SelectMany(t => t.Clips).Where(c => c.LinkId == selected.LinkId).ToArray();
        var rightLink = Guid.NewGuid().ToString("N");
        foreach (var c in related)
        {
            if (at <= c.Start + .001 || at >= c.Start + c.Duration - .001) continue;
            var owner = p.Tracks.Single(t => t.Clips.Contains(c));
            var right = JsonSerializer.Deserialize<VideoClip>(JsonSerializer.Serialize(c))!;
            right.Id = Guid.NewGuid().ToString("N"); right.LinkId = c.LinkId is null ? null : rightLink;
            right.SeriesNumbers = VideoSeries.SplitNumbers(p, c.SeriesNumbers, series);
            var offset = at - c.Start; right.Start = at; right.Duration -= offset;
            if (c.PlannedDuration is {} planned) { c.PlannedDuration = Math.Min(planned, offset); right.PlannedDuration = Math.Max(0, planned - offset); }
            if (!c.Freeze) right.SourceIn += offset * c.Speed;
            right.FadeIn = 0; c.Duration = offset; c.FadeOut = 0; owner.Clips.Add(right);
        }
    }
    public static void Move(VideoProject p, string clipId, double start, string? targetTrack = null)
    {
        var track = p.Tracks.Single(t => t.Clips.Any(c => c.Id == clipId)); if (track.Locked) return;
        var clip = track.Clips.Single(c => c.Id == clipId); var delta = Math.Max(0, start) - clip.Start;
        if (!p.SelectBySeries && clip.LinkId is {} linked && p.Tracks.Any(t => t.Locked && t.Clips.Any(c => c.LinkId == linked))) return;
        var related = p.SelectBySeries || clip.LinkId is null ? new[] { clip } : p.Tracks.Where(t => !t.Locked).SelectMany(t => t.Clips).Where(c => c.LinkId == clip.LinkId).ToArray();
        delta = Math.Max(delta, -related.Min(c => c.Start)); foreach (var c in related) c.Start += delta;
        if (targetTrack is not null && p.Tracks.Single(t => t.Id == targetTrack) is { Locked: false } destination && destination.Kind == track.Kind)
        { track.Clips.Remove(clip); destination.Clips.Add(clip); }
    }
    public static void SetTiming(VideoProject p, string clipId, double start, double sourceIn, double duration, double speed)
    {
        var clip = p.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == clipId);
        var related = p.Tracks.SelectMany(t => t.Clips).Where(c => c.Id == clipId || !p.SelectBySeries && clip.LinkId is not null && c.LinkId == clip.LinkId).ToArray();
        if (p.Tracks.Any(t => t.Locked && t.Clips.Any(related.Contains))) return;
        var startDelta = start - clip.Start; var sourceDelta = sourceIn - clip.SourceIn;
        var previousDuration = clip.Duration; var ratio = clip.Speed / speed;
        foreach (var c in related)
        {
            c.Start += startDelta; c.SourceIn += sourceDelta;
            c.Duration = (c.Duration - previousDuration) * ratio + duration; c.Speed /= ratio;
        }
    }
    public static bool TooLong(VideoProject p, VideoClip c) => c.PlannedDuration is {} planned ? c.Duration > planned + .05 : c.NarrationId is not null && p.Narration.FirstOrDefault(n => n.Id == c.NarrationId) is { } n && c.Duration > n.End - n.Start + .05;
}

public static class VideoProjectStore
{
    public static string Root => Path.Combine(AppDataPaths.BaseDirectory, "VideoEditor");
    public static string Work(string id) { if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException(); var path = Path.Combine(Root, "Projects", id); Lopata.Updates.SafeUpdatePath.RejectLinks(path); return path; }
    public static string Recovery(string id) => Path.Combine(Work(id), "recovery.lopataVideo");
    public static void Save(VideoProject project, string path)
    {
        project.Validate(); VideoSeries.Ensure(project); path = Path.GetFullPath(path);
        if (project.Assets.Any(a => Path.GetFullPath(a.Path).Equals(path, StringComparison.OrdinalIgnoreCase))) throw new IOException("VideoEditor.ProtectedSource");
        if (File.Exists(path) && Load(path).Id != project.Id) throw new IOException("VideoEditor.ProtectedSource");
        project.Provenance = Provenance(project);
        _ = project.Provenance.Json(); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(project, new JsonSerializerOptions { WriteIndented = true })); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static VideoProject Load(string path)
    {
        using (var file = File.OpenRead(path)) if (VideoProjectArchive.IsZip(file)) return VideoProjectArchive.Load(path);
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("VideoEditor.InvalidProject");
        var p = JsonSerializer.Deserialize<VideoProject>(File.ReadAllText(path)) ?? throw new InvalidDataException("VideoEditor.InvalidProject"); p.Validate(); VideoSeries.Ensure(p); return p;
    }
    internal static OutputProvenance Provenance(VideoProject project) => new() { Scenario = "video-editor/project", CreatedUtc = project.CreatedUtc, Models = project.Assets.SelectMany(a => a.Models).Concat(project.Narration.SelectMany(n => n.Models)).Distinct().ToArray(), ModelHistory = "recorded-project; imported-history-may-be-unknown" };
    public static void VerifySources(VideoProject p)
    {
        foreach (var a in p.Assets)
        {
            var f = new FileInfo(a.Path);
            if (!f.Exists) throw new FileNotFoundException("VideoEditor.MissingSource", a.Path);
            if (f.Length != a.Length || f.LastWriteTimeUtc != a.LastWriteUtc) throw new IOException("VideoEditor.SourceChanged");
        }
    }
}
