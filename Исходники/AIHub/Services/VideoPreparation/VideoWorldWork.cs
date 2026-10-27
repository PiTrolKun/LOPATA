using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.VideoPreparation;

public sealed record VideoWorldBatch(string Id, string ProjectId, string ProjectPath, VideoConceptRequest[] Requests);

/// <summary>Three bounded independent requests in one resumable operation; durable results never auto-apply.</summary>
public static class VideoWorldWork
{
    public static void Validate(VideoWorldBatch batch)
    {
        if (!Guid.TryParseExact(batch.Id, "N", out _) || !Guid.TryParseExact(batch.ProjectId, "N", out _)
            || batch.ProjectPath is null || batch.Requests is null || batch.Requests.Length is not (1 or 3) || batch.Requests.Any(r => r is null)
            || batch.Requests.Select(r => r.Id).Distinct().Count() != batch.Requests.Length
            || batch.Requests.Length == 3 && !batch.Requests.Select(r => r.Freedom).SequenceEqual(new[] { 1, 2, 3 })
            || batch.Requests.Any(r => r.Freedom is < 1 or > 3)) throw new InvalidDataException("VideoPrep.AiInvalidRequest");
        var first = batch.Requests[0];
        foreach (var r in batch.Requests)
            if (r.Purpose != VideoWorld.Purpose || r.ProjectId != batch.ProjectId || r.ProjectPath != batch.ProjectPath
                || r.Text != first.Text || r.ContextText != first.ContextText || r.ContextOriginal != first.ContextOriginal
                || r.ParentId != first.ParentId || r.ModelId != first.ModelId || r.ModelPath != first.ModelPath || r.ResponseLanguage != first.ResponseLanguage
                || r.Form != first.Form || r.ContextRevisionId != first.ContextRevisionId || r.EngineSetId != first.EngineSetId
                || r.PromptVersion != VideoWorldProtocol.Version
                || r.ActivateResult) throw new InvalidDataException("VideoPrep.AiInvalidRequest");
    }
    public static Task RunAsync(VideoWorldBatch batch, string title, IVideoConceptAssistant assistant, VideoConceptReceiptStore receipts,
        Func<VideoConceptRequest, string, VideoConceptAnswer?, Task> commit, CancellationToken token, BackgroundOperationState? restored = null)
    {
        Validate(batch);
        var state = restored ?? new BackgroundOperationState
        {
            Id = batch.Id,
            Kind = VideoWorld.Kind,
            Title = title,
            Project = batch.ProjectPath,
            Input = JsonSerializer.SerializeToElement(batch)
        };
        return ApplicationBackgroundOperations.RunAsync(VideoWorld.Kind, title, batch.ProjectPath, batch, async attempt =>
        {
            foreach (var r in batch.Requests)
            {
                attempt.ThrowIfCancellationRequested();
                VideoConceptAnswer? answer = receipts.Read(r);
                if (answer is null)
                {
                    await commit(r, "running", null);
                    await VideoConceptRuntimeLifecycle.BeforeAsync(assistant);
                    Exception? failure = null;
                    try
                    {
                        answer = await assistant.GenerateAsync(r, attempt);
                        attempt.ThrowIfCancellationRequested();
                        VideoConceptProtocol.Validate(answer); _ = VideoWorldProtocol.Read(answer.Text);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e) { failure = e; }
                    finally { await VideoConceptRuntimeLifecycle.AfterAsync(assistant); }
                    attempt.ThrowIfCancellationRequested();
                    if (failure is not null)
                    {
                        ComponentAuditFailures.Report(failure);
                        OwnedProcessRegistry.Log("world_option_failed", "VideoPreparation", detail: failure.GetType().Name);
                        await commit(r, "failed", null); continue;
                    }
                    receipts.Save(r, answer!);
                }
                attempt.ThrowIfCancellationRequested();
                _ = VideoWorldProtocol.Read(answer!.Text); await commit(r, "ready", answer);
            }
            return true;
        }, token, state);
    }
}
