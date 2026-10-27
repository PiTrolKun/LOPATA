using System.IO;

namespace AIHub.Services.VideoPreparation;

public sealed record VideoConceptRevision(string Id, string Text, string Source, string? ParentId,
    DateTimeOffset CreatedUtc, OutputModelUse? Model = null, int? Freedom = null,
    string? PromptVersion = null, string Changes = "", string Clarifications = "", string Form = "",
    string ContextRevisionId = "", string ContextText = "");

/// <summary>Append-only versions; editing an earlier version never deletes its successors.</summary>
public static class VideoConceptHistory
{
    public static void Capture(VideoPreparationText project)
    {
        var selected = project.IdeaHistory.FirstOrDefault(v => v.Id == project.IdeaRevisionId);
        if (selected?.Text == project.IdeaText && selected.ContextRevisionId == project.ContextRevisionId && selected.ContextText == project.ContextText
            || selected is null && project.IdeaText.Length == 0) return;
        var version = new VideoConceptRevision(Guid.NewGuid().ToString("N"), project.IdeaText, "user",
            selected?.Id, DateTimeOffset.UtcNow, Form: project.Form, ContextRevisionId: project.ContextRevisionId, ContextText: project.ContextText);
        project.IdeaHistory.Add(version); project.IdeaRevisionId = version.Id;
    }

    public static bool Move(VideoPreparationText project, int direction)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        var previous = project.IdeaHistory.FindIndex(v => v.Id == project.IdeaRevisionId);
        var draft = previous >= 0 && project.IdeaHistory[previous].Text != project.IdeaText;
        Capture(project);
        var index = draft ? direction == -1 ? previous : previous + 1
            : project.IdeaHistory.FindIndex(v => v.Id == project.IdeaRevisionId) + direction;
        if (index < 0 || index >= project.IdeaHistory.Count) return false;
        var version = project.IdeaHistory[index];
        project.IdeaText = version.Text; project.IdeaRevisionId = version.Id;
        project.ContextRevisionId = version.ContextRevisionId; project.ContextText = version.ContextText;
        return true;
    }

    public static bool Apply(VideoPreparationText project, VideoConceptRequest request, VideoConceptAnswer answer)
    {
        if (project.Id != request.ProjectId) throw new InvalidDataException("VideoPrep.AiWrongProject");
        VideoConceptProtocol.Validate(answer);
        if (project.IdeaHistory.Any(v => v.Id == request.Id)) return false;
        if (!project.IdeaHistory.Any(v => v.Id == request.ParentId) || request.Freedom is < 1 or > 3
            || !Guid.TryParseExact(request.Id, "N", out _) || request.Form is not ("story" or "imagery"))
            throw new InvalidDataException("VideoPrep.AiInvalidRequest");
        var select = request.ActivateResult && project.IdeaText == request.Text && project.IdeaRevisionId == request.ParentId && project.Form == request.Form
            && project.ContextRevisionId == request.ContextRevisionId && project.ContextText == request.ContextText;
        Capture(project);
        var revision = new VideoConceptRevision(request.Id, answer.Text, "ai", request.ParentId,
            DateTimeOffset.UtcNow, answer.Model, request.Freedom, request.PromptVersion, answer.Changes, answer.Clarifications, request.Form,
            request.ContextRevisionId, request.ContextText);
        project.IdeaHistory.Add(revision);
        if (select) { project.IdeaText = revision.Text; project.IdeaRevisionId = revision.Id; }
        VideoTranslationHistory.UpdateProvenance(project);
        return select;
    }

    public static void Validate(VideoPreparationText project)
    {
        if (project.IdeaHistory is null || project.IdeaHistory.Count > 100000 || project.IdeaRevisionId is null
            || !VideoConceptModels.Ids.Contains(project.AssistantModelId) && project.AssistantModelId != ""
            || project.ImprovementFreedom is < 1 or > 3) throw new InvalidDataException("VideoPrep.InvalidArchive");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var version in project.IdeaHistory)
        {
            if (version is null || !Guid.TryParseExact(version.Id, "N", out _) || !seen.Add(version.Id)
                || version.Text is null || version.Changes is null || version.Clarifications is null || version.ContextRevisionId is null || version.ContextText is null
                || version.ContextRevisionId != "" && !Guid.TryParseExact(version.ContextRevisionId, "N", out _)
                || version.Form is not ("" or "story" or "imagery")
                || version.ParentId is { } parent && (parent == version.Id || !seen.Contains(parent))
                || version.Source is not ("user" or "ai")
                || version.Source == "ai" && (version.Model is null || version.Freedom is not (1 or 2 or 3)
                    || string.IsNullOrWhiteSpace(version.PromptVersion))) throw new InvalidDataException("VideoPrep.InvalidArchive");
            if (version.Model is not null) _ = new OutputProvenance { Models = [version.Model] }.Json();
        }
        if (project.IdeaRevisionId != "" && !seen.Contains(project.IdeaRevisionId)) throw new InvalidDataException("VideoPrep.InvalidArchive");
    }
}
