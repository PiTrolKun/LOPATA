namespace AIHub.Services.VideoPreparation;

/// <summary>Development depends on the approved working concept. Source changes require explicit review.</summary>
public static class VideoDevelopment
{
    public const string Purpose = "development";
    public const string TranslationPurpose = "development_translation";
    public const string Kind = "video.preparation.development";
    public const string TranslationKind = "video.preparation.development_translation";
    public static bool IsDevelopment(VideoConceptRequest request) => request.Purpose is Purpose or TranslationPurpose;
    public static bool IsStep(string step) => step is Purpose or TranslationPurpose;

    public static VideoPreparationText Ensure(VideoPreparationProject project)
    {
        project.Development ??= new()
        {
            Id = project.Id,
            AssistantModelId = project.LastAssistantModelId.Length != 0 ? project.LastAssistantModelId
                : project.TranslationModelId.Length != 0 ? project.TranslationModelId : project.AssistantModelId
        };
        if (project.Development.IdeaHistory.Count == 0 && project.Development.IdeaText.Length == 0) RefreshSource(project);
        if (project.Development.IdeaHistory.Count == 0)
        {
            var d = project.Development;
            var empty = new VideoConceptRevision(Guid.NewGuid().ToString("N"), "", "user", null, DateTimeOffset.UtcNow,
                Form: d.Form, ContextRevisionId: d.ContextRevisionId, ContextText: d.ContextText);
            d.IdeaHistory.Add(empty); d.IdeaRevisionId = empty.Id;
        }
        return project.Development;
    }

    public static void RefreshSource(VideoPreparationProject project)
    {
        var text = project.Development ?? throw new InvalidOperationException("VideoPrep.DevelopmentMissing");
        VideoConceptHistory.Capture(text);
        text.Form = project.Form;
        text.ContextRevisionId = project.TranslationRevisionId;
        text.ContextText = project.EnglishText;
        text.ContextOriginal = project.IdeaText;
        VideoConceptHistory.Capture(text);
    }

    public static bool SourceCurrent(VideoPreparationProject project) => project.Development is { } d
        && VideoTranslationHistory.IsCurrent(project) && !string.IsNullOrWhiteSpace(project.EnglishText)
        && d.ContextRevisionId == project.TranslationRevisionId && d.ContextText == project.EnglishText && d.Form == project.Form;

    public static bool RequestCurrent(VideoPreparationProject project, VideoConceptRequest request) => VideoWorld.IsRequest(request)
        ? VideoWorld.RequestCurrent(project, request) : !IsDevelopment(request)
        || request.ContextRevisionId == project.TranslationRevisionId && request.ContextText == project.EnglishText
            && request.Form == project.Form && VideoTranslationHistory.IsCurrent(project);

    public static VideoPreparationText ForRequest(VideoPreparationProject project, VideoConceptRequest request)
        => VideoWorld.IsRequest(request) ? VideoWorld.ForRequest(project, request)
            : IsDevelopment(request) ? project.Development ?? throw new InvalidOperationException("VideoPrep.DevelopmentMissing") : project;

    public static string StepFor(VideoConceptRequest request) => VideoWorld.IsRequest(request)
        ? request.Purpose == VideoWorld.TranslationPurpose ? VideoWorld.TranslationPurpose : "world" : IsDevelopment(request)
        ? VideoTranslationProtocol.IsTranslation(request) ? TranslationPurpose : Purpose
        : VideoTranslationProtocol.IsTranslation(request) ? "translation" : "idea";

    public static void Capture(VideoPreparationProject project)
    {
        VideoWorld.Capture(project);
        VideoConceptHistory.Capture(project); VideoTranslationHistory.Capture(project);
        if (project.Development is { } d)
        {
            VideoConceptHistory.Capture(d); VideoTranslationHistory.Capture(d); VideoTranslationHistory.UpdateProvenance(d);
        }
        if (project.Development is { } child)
        {
            VideoTranslationHistory.UpdateProvenance(project);
            project.Provenance = project.Provenance with
            {
                Models = project.Provenance.Models.Concat(child.Provenance.Models).Distinct().ToArray(),
                ModelHistory = project.Provenance.Models.Length != 0 || child.Provenance.Models.Length != 0 ? "recorded" : project.Provenance.ModelHistory
            };
        }
    }
}
