using System.IO;

namespace AIHub.Services.VideoPreparation;

public sealed record VideoTranslationRevision(string Id, string Text, string Source, string? ParentId,
    string IdeaRevisionId, string IdeaText, string Form, DateTimeOffset CreatedUtc,
    OutputModelUse? Model = null, string? PromptVersion = null, string Clarifications = "");

/// <summary>English edits keep the exact source revision. Old translations survive source changes.</summary>
public static class VideoTranslationHistory
{
    public static VideoTranslationRevision? Selected(VideoPreparationText project)
        => project.TranslationHistory.FirstOrDefault(v => v.Id == project.TranslationRevisionId);

    public static bool IsCurrent(VideoPreparationText project)
        => Selected(project) is { } v && v.IdeaRevisionId == project.IdeaRevisionId
            && v.IdeaText == project.IdeaText && v.Form == project.Form;

    public static void Capture(VideoPreparationText project)
    {
        var selected = Selected(project);
        if (selected?.Text == project.EnglishText || selected is null && project.EnglishText.Length == 0) return;
        var version = new VideoTranslationRevision(Guid.NewGuid().ToString("N"), project.EnglishText, "user", selected?.Id,
            selected?.IdeaRevisionId ?? project.IdeaRevisionId, selected?.IdeaText ?? project.IdeaText,
            selected?.Form ?? project.Form, DateTimeOffset.UtcNow, selected?.Model, selected?.PromptVersion);
        project.TranslationHistory.Add(version); project.TranslationRevisionId = version.Id;
    }

    public static void UseOriginal(VideoPreparationText project, bool select = true)
    {
        VideoConceptHistory.Capture(project); Capture(project);
        if (IsCurrent(project) && project.EnglishText == project.IdeaText) return;
        var existing = project.TranslationHistory.FirstOrDefault(v => v.Source == "original" && v.IdeaRevisionId == project.IdeaRevisionId
            && v.IdeaText == project.IdeaText && v.Form == project.Form && v.Text == project.IdeaText);
        if (existing is not null) { if (select) Select(project, existing); return; }
        var revision = new VideoTranslationRevision(Guid.NewGuid().ToString("N"), project.IdeaText, "original",
            Selected(project)?.Id, project.IdeaRevisionId, project.IdeaText, project.Form, DateTimeOffset.UtcNow);
        project.TranslationHistory.Add(revision); if (select) Select(project, revision);
    }

    public static bool Apply(VideoPreparationText project, VideoConceptRequest request, VideoConceptAnswer answer)
    {
        VideoConceptProtocol.Validate(answer); VideoTranslationProtocol.ValidateTarget(request);
        if (!VideoTranslationProtocol.IsTranslation(request) || !VideoTranslationProtocol.SupportedVersion(request.PromptVersion)
            || request.ProjectId != project.Id || !Guid.TryParseExact(request.Id, "N", out _)
            || !project.IdeaHistory.Any(v => v.Id == request.ParentId && v.Text == request.Text)
            || request.TargetRevisionId != "" && !project.TranslationHistory.Any(v => v.Id == request.TargetRevisionId))
            throw new InvalidDataException("VideoPrep.AiInvalidRequest");
        if (project.TranslationHistory.Any(v => v.Id == request.Id)) return false;
        var select = request.ActivateResult && project.IdeaRevisionId == request.ParentId && project.IdeaText == request.Text && project.Form == request.Form
            && project.TranslationRevisionId == request.TargetRevisionId && project.EnglishText == request.TargetText;
        Capture(project);
        var revision = new VideoTranslationRevision(request.Id, answer.Text, "ai",
            request.TargetRevisionId == "" ? null : request.TargetRevisionId, request.ParentId, request.Text,
            request.Form, DateTimeOffset.UtcNow, answer.Model, request.PromptVersion, answer.Clarifications);
        project.TranslationHistory.Add(revision);
        if (select) Select(project, revision);
        UpdateProvenance(project); return select;
    }

    public static bool Move(VideoPreparationText project, int direction)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        var previous = project.TranslationHistory.FindIndex(v => v.Id == project.TranslationRevisionId);
        var draft = previous >= 0 && project.TranslationHistory[previous].Text != project.EnglishText;
        Capture(project);
        var index = draft ? direction == -1 ? previous : previous + 1
            : project.TranslationHistory.FindIndex(v => v.Id == project.TranslationRevisionId) + direction;
        if (index < 0 || index >= project.TranslationHistory.Count) return false;
        Select(project, project.TranslationHistory[index]); return true;
    }

    public static void ConfirmCurrentSource(VideoPreparationText project)
    {
        VideoConceptHistory.Capture(project); Capture(project);
        if (string.IsNullOrWhiteSpace(project.EnglishText) || IsCurrent(project)) return;
        var selected = Selected(project);
        var revision = new VideoTranslationRevision(Guid.NewGuid().ToString("N"), project.EnglishText, "user", selected?.Id,
            project.IdeaRevisionId, project.IdeaText, project.Form, DateTimeOffset.UtcNow, selected?.Model, selected?.PromptVersion);
        project.TranslationHistory.Add(revision); Select(project, revision);
    }

    private static void Select(VideoPreparationText project, VideoTranslationRevision revision)
    { project.EnglishText = revision.Text; project.TranslationRevisionId = revision.Id; }

    public static void UpdateProvenance(VideoPreparationText project)
    {
        var models = project.IdeaHistory.Where(v => v.Model is not null).Select(v => v.Model!)
            .Concat(project.TranslationHistory.Where(v => v.Model is not null).Select(v => v.Model!)).Distinct().ToArray();
        if (project is VideoPreparationProject { Development: { } child })
            models = models.Concat(child.IdeaHistory.Where(v => v.Model is not null).Select(v => v.Model!))
                .Concat(child.TranslationHistory.Where(v => v.Model is not null).Select(v => v.Model!)).Distinct().ToArray();
        if (project is VideoPreparationProject { World: { } world }) models = models.Concat(VideoWorld.Models(world)).Distinct().ToArray();
        if (project is VideoPreparationProject { References.Plan: { } plan }) models = models.Concat(VideoReferencePlans.Models(plan)).Distinct().ToArray();
        if (project is VideoPreparationProject { References.Drawing: { } drawing }) models = models.Concat(drawing.Variants.SelectMany(v => v.Models)).Distinct().ToArray();
        if (project is VideoPreparationProject { Shots: { } shots }) models = models.Concat(VideoShots.Models(shots)).Distinct().ToArray();
        project.Provenance = project.Provenance with { ModelHistory = models.Length == 0 ? project.Provenance.ModelHistory : "recorded", Models = models };
    }

    public static void Validate(VideoPreparationText project)
    {
        if (project.EnglishText is null || project.TranslationHistory is null || project.TranslationHistory.Count > 100000
            || project.TranslationRevisionId is null || project.TranslationModelId is null
            || project.TranslationModelId != "" && !VideoConceptModels.Ids.Contains(project.TranslationModelId)) Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in project.TranslationHistory!)
        {
            if (v is null || !Guid.TryParseExact(v.Id, "N", out _) || !seen.Add(v.Id) || v.Text is null || v.IdeaText is null
                || v.Clarifications is null || v.Form is not ("story" or "imagery") || v.Source is not ("user" or "ai" or "original")
                || v.ParentId is { } parent && (parent == v.Id || !seen.Contains(parent))
                || !project.IdeaHistory.Any(i => i.Id == v.IdeaRevisionId && i.Text == v.IdeaText)
                || v.Source == "ai" && (v.Model is null || string.IsNullOrWhiteSpace(v.PromptVersion))) Invalid();
            if (v!.Model is not null) _ = new OutputProvenance { Models = [v.Model] }.Json();
        }
        if (project.TranslationRevisionId != "" && !seen.Contains(project.TranslationRevisionId!)) Invalid();
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Invalid() => throw new InvalidDataException("VideoPrep.InvalidArchive");
}
