using System.IO;
using System.Text.Json;

namespace AIHub.Services.VideoPreparation;

public sealed record VideoGeneratorCandidate(string Id, string Name, string Source)
{ public override string ToString() => Name; }
public sealed record VideoGeneratorSettings(string ModelId, int Width, int Height, int Fps, long Seed, string Notes);
public sealed record VideoGeneratorChoice(string Id, DateTimeOffset SavedUtc, VideoGeneratorSettings Settings);
public sealed class VideoGeneratorPlan
{
    public VideoH3WorkspaceState? H3 { get; set; }
    public VideoH3WorkspaceState? H3Lite { get; set; }
    public VideoVaceWorkspaceState? Vace { get; set; }
    public bool FinalReached { get; set; }
    public string PreparationStep { get; set; } = "storyboard";
    public VideoGeneratorSettings? Draft { get; set; }
    public string SelectedId { get; set; } = "";
    public List<VideoGeneratorChoice> History { get; set; } = [];
    // Known IDs stay valid in old archives even after a model leaves the selectable set.
    public static readonly VideoGeneratorCandidate[] Candidates = [
        new("ltx-2.5", "LTX-2.5", "https://huggingface.co/Lightricks/LTX-2.5"),
        new("minimax-h3", "MiniMax H3", "https://github.com/MiniMax-AI/MiniMax-H3"),
        new(VideoH3LiteCatalog.ModelId, VideoH3LiteCatalog.Name, "https://huggingface.co/Kijai/MiniMax-H3-experimental"),
        new("wan-vace-1.3b", "Wan2.1 VACE 1.3B", "https://github.com/ali-vilab/VACE")];
    public static readonly VideoGeneratorCandidate[] AvailableCandidates = Candidates.Where(c => c.Id is "minimax-h3" or VideoH3LiteCatalog.ModelId).ToArray();
    public static bool IsAvailable(string id) => AvailableCandidates.Any(c => c.Id == id);
    public static VideoGeneratorChoice? Selected(VideoPreparationProject p) => p.Generator?.History.FirstOrDefault(v => v.Id == p.Generator.SelectedId);
    public static bool CanResume(VideoPreparationProject p) => p.Generator?.FinalReached == true
        && Selected(p) is { } selected && IsAvailable(selected.Settings.ModelId);
    public static VideoGeneratorSettings SettingsForModel(VideoPreparationProject p, string modelId)
    {
        if (!Candidates.Any(c => c.Id == modelId)) throw new InvalidDataException("VideoPrep.GeneratorSettingsInvalid");
        if (Selected(p)?.Settings is { } selected && selected.ModelId == modelId) return selected;
        if (p.Generator?.History.LastOrDefault(c => c.Settings.ModelId == modelId)?.Settings is { } saved) return saved;
        if (modelId == VideoVaceCatalog.ModelId)
        {
            var vace = p.Generator?.Vace?.Settings ?? new VideoVaceSettings();
            return new(modelId, vace.Width, vace.Height, VideoVaceWorkspaceState.Fps, vace.Seed, "");
        }
        return new(modelId, 1280, 720, 24, -1, "");
    }
    public static void ValidateSettings(VideoGeneratorSettings s)
    {
        if (s is null || !Candidates.Any(c => c.Id == s.ModelId) || s.Width is < 128 or > 8192 || s.Height is < 128 or > 8192 || s.Width % 2 != 0 || s.Height % 2 != 0
            || s.Fps is < 1 or > 120 || s.Seed is < -1 || s.Notes is null || s.Notes.Length > 20000) throw new InvalidDataException("VideoPrep.GeneratorSettingsInvalid");
    }
    public static VideoGeneratorChoice SaveChoice(VideoPreparationProject p, VideoGeneratorSettings settings)
    {
        ValidateSettings(settings); var g = p.Generator ??= new(); g.FinalReached = true;
        var choice = new VideoGeneratorChoice(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, settings);
        g.History.Add(choice); g.SelectedId = choice.Id; g.Draft = settings; return choice;
    }
    public static void Validate(VideoPreparationProject p)
    {
        if (p.Generator is not { } g) return;
        if (g.PreparationStep is not ("storyboard" or "package") || g.SelectedId is null || g.History is null || g.History.Count > 1000) Invalid();
        if (g.Draft is not null) ValidateSettings(g.Draft);
        var ids = new HashSet<string>();
        foreach (var h in g.History!) { if (h is null || !Guid.TryParseExact(h.Id, "N", out _) || !ids.Add(h.Id) || h.SavedUtc == default) Invalid(); ValidateSettings(h!.Settings); }
        if (g.SelectedId != "" && !ids.Contains(g.SelectedId)) Invalid();
        VideoH3WorkspaceState.Validate(g.H3);
        VideoH3WorkspaceState.Validate(g.H3Lite);
        if (g.H3Lite is { } lite) VideoH3Run.Validate(p, lite.Runs);
        if (g.H3 is not null && g.H3Lite is not null && g.H3.Runs.Select(r => r.Id).Intersect(g.H3Lite.Runs.Select(r => r.Id)).Any()) Invalid();
        VideoVaceWorkspaceState.Validate(g.Vace);
        if (g.Vace is { } vace) VideoVaceRun.Validate(p, vace.Runs);
        if (g.H3 is { } generation) VideoH3Run.Validate(p, generation.Runs);
        if (new[] { g.H3, g.H3Lite }.Where(s => s is not null).SelectMany(s => s!.Plans.Values).SelectMany(v => v.VideoReferenceRoles.Concat(v.AudioReferenceRoles))
            .Any(role => p.Attachments.Count(a => a.Role == role) != 1)) Invalid();
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn] private static void Invalid() => throw new InvalidDataException("VideoPrep.InvalidArchive");
}
