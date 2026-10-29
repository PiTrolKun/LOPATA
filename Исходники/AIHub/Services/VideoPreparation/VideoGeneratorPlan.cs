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
    public bool FinalReached { get; set; }
    public string PreparationStep { get; set; } = "storyboard";
    public VideoGeneratorSettings? Draft { get; set; }
    public string SelectedId { get; set; } = "";
    public List<VideoGeneratorChoice> History { get; set; } = [];
    public static readonly VideoGeneratorCandidate[] Candidates = [
        new("ltx-2.5", "LTX-2.5", "https://docs.ltx.io/open-source-model/usage-guides/audio-to-video/"),
        new("minimax-h3", "MiniMax H3", "https://github.com/MiniMax-AI/MiniMax-H3")];
    public static VideoGeneratorChoice? Selected(VideoPreparationProject p) => p.Generator?.History.FirstOrDefault(v => v.Id == p.Generator.SelectedId);
    public static bool CanResume(VideoPreparationProject p) => p.Generator?.FinalReached == true && Selected(p) is not null;
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
        if (g.H3 is { } generation) VideoH3Run.Validate(p, generation.Runs);
        if (g.H3 is { } h3 && h3.Plans.Values.SelectMany(v => v.VideoReferenceRoles.Concat(v.AudioReferenceRoles))
            .Any(role => p.Attachments.Count(a => a.Role == role) != 1)) Invalid();
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn] private static void Invalid() => throw new InvalidDataException("VideoPrep.InvalidArchive");
}
