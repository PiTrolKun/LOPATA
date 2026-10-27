using System.IO;
using System.Text.Json;

namespace AIHub.Services.VideoPreparation;

/// <summary>Model-independent preparation document; UI navigation is stored separately from creative edits.</summary>
public sealed class VideoPreparationProject : VideoPreparationText
{
    public int Schema { get; set; } = 1;
    public string Title { get; set; } = "";
    public VideoPreparationText? Development { get; set; }
    public VideoWorldDocument? World { get; set; }
    public VideoReferenceLibrary? References { get; set; }
    public VideoShotDocument? Shots { get; set; }
    public VideoStoryboardDocument? Storyboard { get; set; }
    public VideoGeneratorPlan? Generator { get; set; }
    public string LastAssistantModelId { get; set; } = "";
    public string CurrentStep { get; set; } = "form";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<VideoPreparationAttachment> Attachments { get; set; } = [];
    public static readonly string[] Steps = ["form", "idea", "translation", "development", "development_translation", "world", "world_translation", "references", "shots", "shots_translation", "storyboard", "package", "finish", "generator"];

    public VideoPreparationProject Clone() => JsonSerializer.Deserialize<VideoPreparationProject>(JsonSerializer.Serialize(this))!;

    public void Validate()
    {
        if (Schema != 1 || !Guid.TryParseExact(Id, "N", out _) || Title is null || Title.Length > 4096
            || IdeaText is null || Form is not ("" or "story" or "imagery") || !Steps.Contains(CurrentStep)
            || CurrentStep != "form" && Form.Length == 0 || Attachments is null || Attachments.Count > 100000
            || Attachments.Any(a => a is null || string.IsNullOrWhiteSpace(a.Path) || a.Role is null || a.Role.Length > 256)
            || Provenance is null || Provenance.Program != "LOPATA" || Provenance.Scenario != "video-preparation/project"
            || LastAssistantModelId is null || LastAssistantModelId != "" && !VideoConceptModels.Ids.Contains(LastAssistantModelId)
            || Provenance.Models is null || Provenance.Models.Any(m => m is null)) throw new InvalidDataException("VideoPrep.InvalidArchive");
        _ = Provenance.Json();
        ValidateText();
        VideoWorld.Validate(this);
        VideoReferences.Validate(this);
        VideoReferencePlans.Validate(this);
        VideoShots.Validate(this);
        VideoStoryboard.Validate(this); VideoGeneratorPlan.Validate(this);
        if (Development is { } development)
        {
            development.ValidateText();
            if (development.Id != Id || development.ContextRevisionId != "" && !TranslationHistory.Any(v =>
                    v.Id == development.ContextRevisionId && v.Text == development.ContextText)
                || development.IdeaHistory.Any(r => r.ContextRevisionId != "" && !TranslationHistory.Any(v =>
                    v.Id == r.ContextRevisionId && v.Text == r.ContextText))) throw new InvalidDataException("VideoPrep.InvalidArchive");
        }
    }
}

public sealed record VideoPreparationAttachment(string Path, string Role);
