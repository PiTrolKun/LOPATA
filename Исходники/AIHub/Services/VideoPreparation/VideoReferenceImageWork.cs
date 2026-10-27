using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.VideoPreparation;

public sealed record VideoReferenceImageRequest(string ProjectId, string ProjectPath, string PromptId,
    string Definition, string CardId, string Purpose, string SourceDescription, ImageGenerationRequest Image);

public static class VideoReferenceImageWork
{
    public const string Kind = "video.preparation.reference_images";
    public static void Validate(VideoReferenceImageRequest r)
    {
        if (r is null || r.Image is null || !Guid.TryParseExact(r.ProjectId, "N", out _)
            || !Guid.TryParseExact(r.PromptId, "N", out _) || string.IsNullOrEmpty(r.ProjectPath)
            || !r.ProjectPath.EndsWith(VideoPreparationArchive.Extension, StringComparison.OrdinalIgnoreCase)
            || r.CardId != "" && !Guid.TryParseExact(r.CardId, "N", out _) || !VideoReferences.Purposes.Contains(r.Purpose)
            || r.Definition is null || r.Definition.Length > 100000 || r.SourceDescription is null || r.SourceDescription.Length > 8_000_000
            || r.Image.Seeds.Length != 1 || r.Image.OutputFolder != "" || r.Image.Metadata is null || r.Image.OutputLongestSide != 0)
            throw new InvalidDataException("VideoPrep.AiInvalidRequest");
        ImageGenerationCatalog.Validate(r.Image);
    }
    public static Task RunAsync(VideoReferenceImageRequest r, string title, IImageGenerationPreparation installation,
        ImageGenerationRuntime runtime, Func<VideoReferenceImageRequest, ImageGenerationTurn, Task> commit,
        CancellationToken token, BackgroundOperationState? restored = null)
    {
        Validate(r);
        var state = restored ?? new BackgroundOperationState { Id = r.Image.Id, Kind = Kind, Title = title,
            Project = r.ProjectPath, Input = JsonSerializer.SerializeToElement(r) };
        return ApplicationBackgroundOperations.RunAsync(Kind, title, r.ProjectPath, r, async attempt =>
        {
            var cards = await installation.PrepareAsync(r.Image.ModelsRoot, r.Image.ModelId, false, null, attempt);
            attempt.ThrowIfCancellationRequested();
            await ApplicationBackgroundOperations.RetireModelsAsync();
            var turn = await runtime.RunAsync(r.Image, cards, null, attempt);
            attempt.ThrowIfCancellationRequested();
            await commit(r, turn); return true;
        }, token, state);
    }
    public static async Task ApplyAsync(VideoPreparationProject p, VideoReferenceImageRequest r,
        ImageGenerationTurn turn, string directory, CancellationToken token)
    {
        Validate(r);
        if (p.Id != r.ProjectId || turn.Request != r.Image && JsonSerializer.Serialize(turn.Request) != JsonSerializer.Serialize(r.Image))
            throw new InvalidDataException("VideoPrep.AiWrongProject");
        var drawing = VideoReferenceDrawings.Ensure(p);
        if (drawing.Variants.Any(v => v.Id == r.Image.Id)) return;
        if (VideoReferences.Ensure(p).Images.Count >= 512) throw new InvalidDataException("VideoPrep.RefLimit");
        var result = turn.Results.Single(v => v.Index == 0);
        if (result.ProcessingError is not null) throw new IOException(result.ProcessingError);
        var file = await VideoReferenceFiles.ImportAsync(ImageGenerationSessionStore.ResultPath(r.Image, 0), directory, token);
        file.Image.Origin = "generated-reference";
        var models = ImageGenerationCatalog.Manifest.Artifacts.Where(a => a.Id != "generation-runtime" && ImageGenerationCatalog.Get(r.Image.ModelId).Components.Contains(a.Id))
            .Select(a => new OutputModelUse(a.Id, a.Name, a.Revision, "reference-image"))
            .Concat(result.Runtime is { } executed ? new[] { new OutputModelUse(executed.ComponentId, "stable-diffusion.cpp", executed.BackendCommit, "reference-image-executor") } : [])
            .ToArray();
        var variant = new VideoReferenceVariant(r.Image.Id, r.PromptId, file.Image.Id, r.Image.Prompt,
            r.Definition, r.CardId, r.Purpose, r.SourceDescription, r.Image.ModelId, r.Image.Seeds[0], models, result.Runtime);
        p.Attachments.Add(new(file.Path, file.Image.AttachmentRole)); p.References!.Images.Add(file.Image);
        drawing.Variants.Add(variant); VideoTranslationHistory.UpdateProvenance(p);
    }
}
