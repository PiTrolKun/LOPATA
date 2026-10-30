using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIHub.Services.VideoPreparation;

public sealed class VideoShotPlan
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Function { get; set; } = "";
    public string Start { get; set; } = "";
    public string End { get; set; } = "";
    public string Composition { get; set; } = "";
    public string Camera { get; set; } = "";
    public string Light { get; set; } = "";
    public string Continuity { get; set; } = "";
    public string Constraints { get; set; } = "";
    public double Duration { get; set; }
    public string[] CardIds { get; set; } = [];
    public string[] ReferenceIds { get; set; } = [];
}
public sealed class VideoScenePlan
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Intent { get; set; } = "";
    public string[] CardIds { get; set; } = [];
    public string[] ReferenceIds { get; set; } = [];
    public List<VideoShotPlan> Shots { get; set; } = [];
}
public sealed record VideoShotRevision(string Id, string Snapshot, string Source, OutputModelUse[] Models);
public sealed class VideoShotCandidate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int Freedom { get; set; } = 1;
    public string Source { get; set; } = "";
    public string BaseSnapshot { get; set; } = "";
    public string RequestIdentity { get; set; } = "";
    public bool Automatic { get; set; }
    public bool Complete { get; set; }
    public List<VideoScenePlan> Scenes { get; set; } = [];
    public List<OutputModelUse> Models { get; set; } = [];
    public Dictionary<string, string> ReviewedScenes { get; set; } = [];
}
public sealed class VideoShotTranslation
{
    public bool Manual { get; set; }
    public string Id { get; set; } = "";
    public string NativeSnapshot { get; set; } = "";
    public VideoScenePlan Scene { get; set; } = new();
    public OutputModelUse Model { get; set; } = new("unknown", "unknown", "unknown", "video shots translation");
}
public sealed class VideoShotDocument
{
    public bool Attempted { get; set; }
    public string ModelId { get; set; } = "";
    public string Source { get; set; } = "";
    public string RevisionId { get; set; } = "";
    public List<VideoScenePlan> Scenes { get; set; } = [];
    public List<VideoShotRevision> History { get; set; } = [];
    public List<VideoShotCandidate> Candidates { get; set; } = [];
    public List<VideoShotTranslation> Translations { get; set; } = [];
    public Dictionary<string, string> SelectedTranslations { get; set; } = [];
    public string SelectedSceneId { get; set; } = "";
    public string SelectedShotId { get; set; } = "";
}

public static class VideoShots
{
    public const string Kind = "video.preparation.shots";
    public const string TranslationKind = "video.preparation.shots_translation";
    public static VideoShotDocument Ensure(VideoPreparationProject p) => p.Shots ??= new();
    public static string Snapshot(IEnumerable<VideoScenePlan> scenes) => JsonSerializer.Serialize(scenes);
    public static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    public static string Source(VideoPreparationProject p) => VideoShotsProtocol.Serialize(new
    {
        concept = p.IdeaText, development = p.Development?.IdeaText ?? "",
        cards = (p.World?.Cards ?? []).Select(c => new { c.Id, c.Name, c.Kind, Description = c.Text.IdeaText, c.Fixed }),
        references = (p.References?.Images ?? []).Where(i => p.References!.Bindings.Any(b => b.ImageId == i.Id)).Select(i => new
        { i.Id, i.Name, bindings = p.References!.Bindings.Where(b => b.ImageId == i.Id).Select(b => new { b.CardId, b.Purpose, b.Notes }),
            prompt = p.References.Drawing?.Variants.FirstOrDefault(v => v.ImageId == i.Id)?.Prompt ?? "" })
    });
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static bool Current(VideoPreparationProject p) => p.Shots?.Source == Hash(Source(p));
    public static void Capture(VideoPreparationProject p)
    {
        if (p.Shots is not { } d || d.Scenes.Count == 0 && d.History.Count == 0) return;
        var snapshot = Snapshot(d.Scenes); var previous = d.History.FirstOrDefault(v => v.Id == d.RevisionId);
        if (previous?.Snapshot == snapshot && previous.Source == d.Source) return;
        var revision = new VideoShotRevision(Guid.NewGuid().ToString("N"), snapshot, d.Source, previous?.Models ?? []);
        d.History.Add(revision); d.RevisionId = revision.Id;
    }
    public static bool Select(VideoPreparationProject p, VideoShotCandidate candidate)
    {
        var d = Ensure(p); if (!d.Candidates.Contains(candidate) || !candidate.Complete) return false;
        Capture(p); d.Scenes = Copy(candidate.Scenes); d.Source = candidate.Source;
        var revision = new VideoShotRevision(Guid.NewGuid().ToString("N"), Snapshot(d.Scenes), d.Source, candidate.Models.Distinct().ToArray());
        d.History.Add(revision); d.RevisionId = revision.Id;
        d.SelectedSceneId = d.Scenes.FirstOrDefault()?.Id ?? ""; d.SelectedShotId = d.Scenes.FirstOrDefault()?.Shots.FirstOrDefault()?.Id ?? "";
        return true;
    }
    public static bool MoveVersion(VideoPreparationProject p, int direction)
    {
        if (direction is not (-1 or 1)) return false;
        var d = Ensure(p); var index = d.History.FindIndex(v => v.Id == d.RevisionId);
        var draft = index >= 0 && d.History[index].Snapshot != Snapshot(d.Scenes); Capture(p);
        index = draft ? direction == -1 ? index : index + 1 : d.History.FindIndex(v => v.Id == d.RevisionId) + direction;
        if (index < 0 || index >= d.History.Count) return false;
        var v = d.History[index]; d.Scenes = JsonSerializer.Deserialize<List<VideoScenePlan>>(v.Snapshot)!; d.Source = v.Source; d.RevisionId = v.Id; return true;
    }
    public static bool Ready(VideoPreparationProject p) => p.Shots is { Scenes.Count: > 0 } d && d.Scenes.All(s =>
        s.Name.Trim().Length > 0 && s.Shots.Count > 0 && s.Shots.All(t => t.Name.Trim().Length > 0 && t.Start.Trim().Length > 0
            && t.End.Trim().Length > 0 && t.ReferenceIds.All(id => p.References?.Bindings.Any(b => b.ImageId == id) == true)
            && t.CardIds.All(id => p.World?.Cards.Any(c => c.Id == id) == true)));
    public static VideoShotTranslation? SelectedTranslation(VideoShotDocument d, string sceneId) =>
        d.SelectedTranslations.TryGetValue(sceneId, out var id) ? d.Translations.FirstOrDefault(v => v.Id == id) : null;
    public static bool TranslationCurrent(VideoShotDocument d, VideoScenePlan scene) => SelectedTranslation(d, scene.Id) is { } t && t.NativeSnapshot == Snapshot([scene]);
    public static bool SameStructure(VideoScenePlan native, VideoScenePlan? translated) => translated is not null && native.Id == translated.Id
        && native.CardIds.SequenceEqual(translated.CardIds) && native.ReferenceIds.SequenceEqual(translated.ReferenceIds)
        && native.Shots.Count == translated.Shots.Count && native.Shots.Zip(translated.Shots).All(pair => pair.First.Id == pair.Second.Id
            && pair.First.Duration == pair.Second.Duration && pair.First.CardIds.SequenceEqual(pair.Second.CardIds) && pair.First.ReferenceIds.SequenceEqual(pair.Second.ReferenceIds));
    public static IEnumerable<OutputModelUse> Models(VideoShotDocument d) => d.History.SelectMany(v => v.Models)
        .Concat(d.Candidates.SelectMany(v => v.Models)).Concat(d.Translations.Select(v => v.Model)).Distinct();
    public static void ValidateScenes(List<VideoScenePlan> scenes)
    {
        if (scenes is null || scenes.Count > 4096 || scenes.Sum(s => s?.Shots?.Count ?? 0) > 16384) Invalid();
        var ids = new HashSet<string>();
        foreach (var s in scenes!)
        {
            if (s is null || !Id(s.Id) || !ids.Add(s.Id) || !Text(s.Name) || !Text(s.Intent) || s.Shots is null) Invalid();
            Refs(s!.CardIds); Refs(s.ReferenceIds);
            foreach (var t in s.Shots!)
            {
                if (t is null || !Id(t.Id) || !ids.Add(t.Id) || new[] { t.Name, t.Function, t.Start, t.End, t.Composition, t.Camera, t.Light, t.Continuity, t.Constraints }.Any(v => !Text(v))
                    || !double.IsFinite(t.Duration) || t.Duration < 0 || t.Duration > 86400) Invalid();
                Refs(t!.CardIds); Refs(t.ReferenceIds);
            }
        }
    }
    public static void Validate(VideoPreparationProject p)
    {
        if (p.Shots is not { } d) return;
        if (d.ModelId is null || d.ModelId != "" && !VideoConceptModels.Ids.Contains(d.ModelId) || d.Source is null || d.Source.Length > 64
            || d.History is null || d.History.Count > 10000 || d.Candidates is null || d.Candidates.Count > 1000 || d.Translations is null || d.Translations.Count > 16384
            || d.SelectedTranslations is null || d.SelectedTranslations.Count > 4096 || d.RevisionId is null || d.SelectedSceneId is null || d.SelectedShotId is null) Invalid();
        ValidateScenes(d.Scenes); var ids = new HashSet<string>();
        foreach (var h in d.History!)
        {
            if (h is null || !Id(h.Id) || !ids.Add(h.Id) || h.Snapshot is null || h.Snapshot.Length > 8_000_000 || h.Source is null || h.Source.Length > 64 || h.Models is null) Invalid();
            ValidateScenes(JsonSerializer.Deserialize<List<VideoScenePlan>>(h!.Snapshot)!); _ = new OutputProvenance { Models = h.Models }.Json();
        }
        if (d.RevisionId != "" && !ids.Contains(d.RevisionId)) Invalid(); ids.Clear();
        foreach (var c in d.Candidates!)
        {
            if (c is null || !Id(c.Id) || !ids.Add(c.Id) || c.Freedom is < 1 or > 3 || c.Source is null || c.Source.Length != 64
                || c.BaseSnapshot is null || c.BaseSnapshot.Length > 8_000_000 || c.Models is null || c.RequestIdentity is null || c.RequestIdentity.Length > 64
                || c.ReviewedScenes is null || c.ReviewedScenes.Count > 4096
                || c.ReviewedScenes.Any(pair => !Id(pair.Key) || pair.Value is null || pair.Value.Length != 64)) Invalid();
            ValidateScenes(c!.Scenes); ValidateScenes(JsonSerializer.Deserialize<List<VideoScenePlan>>(c.BaseSnapshot)!); _ = new OutputProvenance { Models = c.Models.ToArray() }.Json();
        }
        ids.Clear();
        foreach (var t in d.Translations!)
        {
            if (t is null || !Id(t.Id) || !ids.Add(t.Id) || t.NativeSnapshot is null || t.NativeSnapshot.Length > 8_000_000 || t.Model is null) Invalid();
            ValidateScenes([t!.Scene]); ValidateScenes(JsonSerializer.Deserialize<List<VideoScenePlan>>(t.NativeSnapshot)!);
            _ = new OutputProvenance { Models = [t.Model] }.Json();
        }
        if (d.SelectedTranslations.Any(pair => !Id(pair.Key) || !d.Translations.Any(t => t.Id == pair.Value && t.Scene.Id == pair.Key))) Invalid();
    }
    private static bool Id(string? s) => Guid.TryParseExact(s, "N", out _);
    private static bool Text(string? s) => s is not null && s.Length <= 20000;
    private static void Refs(string[]? values) { if (values is null || values.Length > 512 || values.Any(s => !Id(s)) || values.Distinct().Count() != values.Length) Invalid(); }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn] private static void Invalid() => throw new InvalidDataException("VideoPrep.InvalidArchive");
}
