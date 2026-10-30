using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.VideoPreparation;

public sealed class VideoBoardFrame
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ShotId { get; set; } = "";
    public string ImageId { get; set; } = "";
    public double Seconds { get; set; } = 5;
    public bool Estimated { get; set; }
    public string Notes { get; set; } = "";
}
public sealed class VideoBoardAudio
{
    public string Role { get; set; } = "video-board/audio";
    public string Name { get; set; } = "";
    public double Seconds { get; set; }
    public MusicTimedLyrics? TimedLyrics { get; set; }
    public string MetadataNote { get; set; } = "";
}
public sealed record VideoBoardRevision(string Id, string Snapshot);
public sealed class VideoStoryboardDocument
{
    public string Source { get; set; } = "";
    public List<VideoBoardFrame> Frames { get; set; } = [];
    public VideoBoardAudio? Audio { get; set; }
    public string SelectedId { get; set; } = "";
    public string RevisionId { get; set; } = "";
    public List<VideoBoardRevision> History { get; set; } = [];
}
public static class VideoStoryboard
{
    public static IEnumerable<(VideoScenePlan Scene, VideoShotPlan Shot)> Plans(VideoPreparationProject p) =>
        (p.Shots?.Scenes ?? []).SelectMany(s => s.Shots.Select(t => (s, t)));
    public static string Source(VideoPreparationProject p) => VideoShots.Hash(VideoShots.Snapshot(p.Shots?.Scenes ?? []));
    public static VideoStoryboardDocument Ensure(VideoPreparationProject p)
    {
        if (p.Storyboard is { } existing) return existing;
        p.Storyboard = new(); Synchronize(p); return p.Storyboard;
    }
    public static bool Current(VideoPreparationProject p) => p.Storyboard is { } b && b.Source == Source(p);
    public static void Synchronize(VideoPreparationProject p, bool restorePlanOrder = false)
    {
        var b = p.Storyboard ??= new(); Capture(p);
        var plans = Plans(p).ToArray(); var ids = plans.Select(x => x.Shot.Id).ToHashSet();
        b.Frames.RemoveAll(f => !ids.Contains(f.ShotId));
        foreach (var (_, shot) in plans)
            if (!b.Frames.Any(f => f.ShotId == shot.Id)) b.Frames.Add(new() { ShotId = shot.Id,
                Seconds = shot.Duration > 0 ? shot.Duration : 5, Estimated = shot.Duration == 0,
                ImageId = "" });
        if (restorePlanOrder)
        { var frames = b.Frames.ToDictionary(f => f.ShotId); b.Frames = plans.Select(x => frames[x.Shot.Id]).ToList(); }
        b.Source = Source(p); if (!b.Frames.Any(f => f.Id == b.SelectedId)) b.SelectedId = b.Frames.FirstOrDefault()?.Id ?? ""; Capture(p);
    }
    private static string Snapshot(VideoStoryboardDocument b) => JsonSerializer.Serialize(new State(b.Source, b.Frames, b.Audio));
    private sealed record State(string Source, List<VideoBoardFrame> Frames, VideoBoardAudio? Audio);
    public static void Capture(VideoPreparationProject p)
    {
        if (p.Storyboard is not { } b || b.Source.Length != 64) return;
        var snapshot = Snapshot(b); if (b.History.FirstOrDefault(h => h.Id == b.RevisionId)?.Snapshot == snapshot) return;
        var v = new VideoBoardRevision(Guid.NewGuid().ToString("N"), snapshot); b.History.Add(v); b.RevisionId = v.Id;
    }
    public static bool MoveVersion(VideoPreparationProject p, int delta)
    {
        var b = Ensure(p); var old = b.History.FindIndex(v => v.Id == b.RevisionId);
        var draft = old >= 0 && b.History[old].Snapshot != Snapshot(b); Capture(p);
        var index = draft && delta == -1 ? old : b.History.FindIndex(v => v.Id == b.RevisionId) + delta;
        if (delta is not (-1 or 1) || index < 0 || index >= b.History.Count) return false;
        var v = b.History[index]; var state = JsonSerializer.Deserialize<State>(v.Snapshot)!;
        b.Source = state.Source; b.Frames = state.Frames; b.Audio = state.Audio; b.RevisionId = v.Id;
        b.SelectedId = b.Frames.FirstOrDefault()?.Id ?? ""; return true;
    }
    public static double Duration(VideoPreparationProject p) => p.Storyboard?.Frames.Sum(f => f.Seconds) ?? 0;
    public static (VideoBoardFrame? Frame, double Start) At(VideoPreparationProject p, double seconds)
    {
        var total = 0d; VideoBoardFrame? last = null; var start = 0d;
        foreach (var frame in p.Storyboard?.Frames ?? []) { last = frame; start = total; total += frame.Seconds; if (seconds < total) return (frame, start); }
        return (last, start);
    }
    public static string? AudioPath(VideoPreparationProject p) => p.Storyboard?.Audio is { } a ? p.Attachments.FirstOrDefault(x => x.Role == a.Role)?.Path : null;
    public static void Validate(VideoPreparationProject p)
    {
        if (p.Storyboard is not { } b) return;
        ValidateState(new(b.Source, b.Frames, b.Audio));
        if (b.History is null || b.History.Count > 2000 || b.SelectedId is null || b.RevisionId is null
            || b.History.Select(v => v.Id).Distinct().Count() != b.History.Count) Invalid();
        foreach (var h in b.History!)
        { if (!Guid.TryParseExact(h.Id, "N", out _) || h.Snapshot is null || h.Snapshot.Length > 8_000_000) Invalid();
            ValidateState(JsonSerializer.Deserialize<State>(h.Snapshot)!); }
        if (b.RevisionId != "" && !b.History.Any(h => h.Id == b.RevisionId)) Invalid();
        if (b.Audio is { } audio && p.Attachments.Count(a => a.Role == audio.Role) != 1) Invalid();
    }
    private static void ValidateState(State s)
    {
        if (s is null || s.Source is null || s.Source.Length != 64 || s.Frames is null || s.Frames.Count > 16384) Invalid();
        var ids = new HashSet<string>(); var plans = new HashSet<string>();
        foreach (var f in s!.Frames!)
            if (f is null || !Guid.TryParseExact(f.Id, "N", out _) || !ids.Add(f.Id) || !Guid.TryParseExact(f.ShotId, "N", out _) || !plans.Add(f.ShotId)
                || f.ImageId is null || f.ImageId != "" && !Guid.TryParseExact(f.ImageId, "N", out _) || !double.IsFinite(f.Seconds) || f.Seconds is <= 0 or > 86400
                || f.Notes is null || f.Notes.Length > 20000) Invalid();
        if (s.Audio is { } a)
        { if (a.Role is null || !a.Role.StartsWith("video-board/audio/", StringComparison.Ordinal) || a.Name is null || a.Name.Length > 1024
                || !double.IsFinite(a.Seconds) || a.Seconds <= 0 || a.MetadataNote is null || a.MetadataNote.Length > 1024) Invalid(); a.TimedLyrics?.Validate(); }
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn] private static void Invalid() => throw new InvalidDataException("VideoPrep.InvalidArchive");
}
