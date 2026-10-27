using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.VideoPreparation;

public sealed record VideoReferenceBatch(string Id, string ProjectId, string ProjectPath, VideoConceptRequest[] Requests);
public static class VideoReferenceWork
{
    public const string Kind = "video.preparation.reference_prompts";
    public static void Validate(VideoReferenceBatch batch)
    {
        if (!Guid.TryParseExact(batch.Id, "N", out _) || !Guid.TryParseExact(batch.ProjectId, "N", out _)
            || batch.ProjectPath is null || !batch.ProjectPath.EndsWith(VideoPreparationArchive.Extension, StringComparison.OrdinalIgnoreCase)
            || batch.Requests is null || batch.Requests.Length is < 1 or > 128) throw new InvalidDataException("VideoPrep.AiInvalidRequest");
        var ids = new HashSet<string>(); var materials = new HashSet<string>(); var first = batch.Requests[0];
        foreach (var r in batch.Requests)
            if (r is null || !Guid.TryParseExact(r.Id, "N", out _) || !ids.Add(r.Id) || !Guid.TryParseExact(r.ParentId, "N", out _)
                || !VideoReferencePromptProtocol.IsRequest(r) || r.PromptVersion != VideoReferencePromptProtocol.Version
                || r.ProjectId != batch.ProjectId || r.ProjectPath != batch.ProjectPath || !VideoConceptModels.Ids.Contains(r.ModelId)
                || r.ModelId != first.ModelId || r.ModelPath != first.ModelPath || r.EngineSetId != first.EngineSetId
                || r.ContextText != first.ContextText || r.ResponseLanguage != first.ResponseLanguage || r.Form is not ("story" or "imagery")
                || string.IsNullOrWhiteSpace(r.ContextText) || r.Text is null || string.IsNullOrWhiteSpace(r.ResponseLanguage)
                || r.Purpose == VideoReferencePromptProtocol.ListPurpose && batch.Requests.Length != 1
                || r.Purpose == VideoReferencePromptProtocol.PromptPurpose && (!Guid.TryParseExact(r.MaterialId, "N", out _) || !materials.Add(r.MaterialId)))
                throw new InvalidDataException("VideoPrep.AiInvalidRequest");
    }
    public static Task RunAsync(VideoReferenceBatch batch, string title, IVideoConceptAssistant assistant, VideoConceptReceiptStore receipts,
        Func<VideoConceptRequest, VideoConceptAnswer, Task> commit, CancellationToken token, BackgroundOperationState? restored = null)
    {
        Validate(batch);
        var state = restored ?? new BackgroundOperationState { Id = batch.Id, Kind = Kind, Title = title,
            Project = batch.ProjectPath, Input = JsonSerializer.SerializeToElement(batch) };
        return ApplicationBackgroundOperations.RunAsync(Kind, title, batch.ProjectPath, batch, async attempt =>
        {
            foreach (var r in batch.Requests)
            {
                attempt.ThrowIfCancellationRequested();
                var answer = receipts.Read(r);
                if (answer is null)
                {
                    await VideoConceptRuntimeLifecycle.BeforeAsync(assistant);
                    try { answer = await assistant.GenerateAsync(r, attempt); }
                    finally { await VideoConceptRuntimeLifecycle.AfterAsync(assistant); }
                    attempt.ThrowIfCancellationRequested();
                    VideoConceptProtocol.Validate(answer); VideoReferencePromptProtocol.ValidateReply(answer.Text, r);
                    receipts.Save(r, answer);
                }
                attempt.ThrowIfCancellationRequested();
                VideoReferencePromptProtocol.ValidateReply(answer.Text, r);
                await commit(r, answer);
            }
            return true;
        }, token, state);
    }
}
