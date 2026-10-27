using System.IO;

namespace AIHub.Services.VideoPreparation;

/// <summary>One editable material and its working translation. Does not contain another project.</summary>
public class VideoPreparationText
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Form { get; set; } = "";
    public string IdeaText { get; set; } = "";
    public List<VideoConceptRevision> IdeaHistory { get; set; } = [];
    public string IdeaRevisionId { get; set; } = "";
    public string AssistantModelId { get; set; } = "";
    public int ImprovementFreedom { get; set; } = 1;
    public string EnglishText { get; set; } = "";
    public List<VideoTranslationRevision> TranslationHistory { get; set; } = [];
    public string TranslationRevisionId { get; set; } = "";
    public string TranslationModelId { get; set; } = "";
    public bool TranslationModelExplicit { get; set; }
    public string ContextRevisionId { get; set; } = "";
    public string ContextText { get; set; } = "";
    public string ContextOriginal { get; set; } = "";
    public OutputProvenance Provenance { get; set; } = new() { Scenario = "video-preparation/project" };

    public void ValidateText()
    {
        if (!Guid.TryParseExact(Id, "N", out _) || IdeaText is null || Form is not ("" or "story" or "imagery")
            || ContextRevisionId is null || ContextText is null || ContextOriginal is null
            || ContextRevisionId != "" && !Guid.TryParseExact(ContextRevisionId, "N", out _)
            || Provenance is null || Provenance.Program != "LOPATA" || Provenance.Scenario != "video-preparation/project")
            throw new InvalidDataException("VideoPrep.InvalidArchive");
        _ = Provenance.Json(); VideoConceptHistory.Validate(this); VideoTranslationHistory.Validate(this);
    }
}
