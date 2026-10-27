using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.VideoPreparation;

public interface IVideoConceptAssistant
{
    Task<VideoConceptAnswer> GenerateAsync(VideoConceptRequest request, CancellationToken token);
}

/// <summary>Private, tool-free requests; the same audited llama runtime and staged GPU/hybrid/CPU placement as the tested candidates.</summary>
public sealed partial class VideoConceptAssistant : IVideoConceptAssistant, IVideoConceptResident
{
    public async Task<VideoConceptAnswer> GenerateAsync(VideoConceptRequest request, CancellationToken token)
    {
        if (VideoShotsProtocol.IsRequest(request)) return await GenerateShotsAsync(request, null, token);
        Validate(request);
        var translating = VideoTranslationProtocol.IsTranslation(request);
        var developing = VideoDevelopment.IsDevelopment(request);
        var world = VideoWorld.IsRequest(request);
        var references = VideoReferencePromptProtocol.IsRequest(request);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var attempt = deadline.Token;
        await _residentGate.WaitAsync(attempt);
        try
        {
            await EnsureResidentAsync(new(request.ModelId, request.ModelPath, request.EngineSetId), attempt);
            var runtime = _runtime; var model = _model!; var capacity = _capacity;
            var identity = _baseIdentity! with { Role = references ? request.Purpose == VideoReferencePromptProtocol.ListPurpose ? "video reference discovery" : "video reference drawing prompt"
                : world ? translating ? "video world translation" : "video visual world"
                : developing ? translating ? "video development translation" : "video development plan"
                : translating ? "video concept translation" : "video concept improvement" };
            var protection = translating && request.PromptVersion == VideoTranslationProtocol.Version ? new VideoTranslationProtection(request.Text) : null;
            var prepared = protection is null ? request : request with { Text = protection.Text };
            var system = references ? VideoReferencePromptProtocol.Instructions(request)
                : translating ? request.PromptVersion == VideoTranslationProtocol.LegacyVersion ? VideoTranslationProtocol.LegacyInstructions : VideoTranslationProtocol.Instructions
                : world ? VideoWorldProtocol.Instructions(request.Freedom, request.Purpose == VideoWorld.CardPurpose)
                : developing ? VideoDevelopmentProtocol.Instructions(request.Freedom) : VideoConceptProtocol.Instructions(request.Freedom);
            var input = references ? VideoReferencePromptProtocol.Input(prepared) : world && !translating ? VideoWorldProtocol.Input(prepared)
                : developing && !translating ? VideoDevelopmentProtocol.Input(prepared) : VideoConceptProtocol.Input(prepared);
            var used = await runtime.CountPoetryAsync(system, [new("user", input, request.Id)], attempt);
            var reserve = OutputTokens(capacity, used);
            var payload = JsonSerializer.Serialize(new
            {
                model = "local",
                messages = new[] { new { role = "system", content = system }, new { role = "user", content = input } },
                max_tokens = reserve,
                temperature = translating ? .2 : .7,
                top_p = .9,
                top_k = 40,
                min_p = 0,
                repeat_penalty = 1.05,
                seed = 42,
                stream = false,
                cache_prompt = false,
                chat_template_kwargs = new { enable_thinking = false },
                reasoning_format = "deepseek",
                response_format = references ? VideoReferencePromptProtocol.Schema(request) : translating ? VideoTranslationProtocol.Schema : request.Purpose == VideoWorld.Purpose ? VideoWorldProtocol.Schema : VideoConceptProtocol.Schema
            });
            var response = await runtime.GeneratePrivateJsonAsync(model, payload, attempt);
            attempt.ThrowIfCancellationRequested();
            var answer = references ? VideoReferencePromptProtocol.Parse(response, identity, request) : translating ? VideoTranslationProtocol.Parse(response, identity)
                : request.Purpose == VideoWorld.Purpose ? VideoWorldProtocol.Parse(response, identity) : VideoConceptProtocol.Parse(response, identity);
            return protection is null ? answer : protection.Restore(answer);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested) { throw new TimeoutException("VideoPrep.AiTimeout"); }
        finally
        {
            try { if (attempt.IsCancellationRequested) await _runtime.RetirePoetryAsync(); }
            finally { _residentGate.Release(); RuntimeChanged?.Invoke(); }
        }
    }

    internal static int OutputTokens(int capacity, int used)
    {
        var reserve = (int)Math.Min(4096, (long)capacity - used - 256);
        if (reserve >= 1024) return reserve;
        OwnedProcessRegistry.Log("video_prompt_context_limit", "VideoPreparation", detail: $"capacity={capacity};input={used};minimumOutput=1024;headroom=256");
        throw new BackgroundOperationFailedException("VideoPrep.AiContextLimit");
    }

    public static void Validate(VideoConceptRequest request)
    {
        if (!Guid.TryParseExact(request.Id, "N", out _) || !Guid.TryParseExact(request.ProjectId, "N", out _)
            || !Guid.TryParseExact(request.ParentId, "N", out _) || !VideoConceptModels.Ids.Contains(request.ModelId)
            || request.Freedom is < 1 or > 3 || request.Form is not ("story" or "imagery")
            || request.Text is null || string.IsNullOrWhiteSpace(request.Text) && request.Purpose is not (VideoDevelopment.Purpose or VideoWorld.Purpose or VideoWorld.CardPurpose)
            || request.ProjectPath is null || request.ModelPath is null
            || request.ContextRevisionId is null || request.ContextText is null || request.ContextOriginal is null
            || request.Purpose == VideoWorld.CardPurpose && !Guid.TryParseExact(request.MaterialId, "N", out _)
            || request.Purpose is not ("concept" or VideoTranslationProtocol.Purpose or VideoDevelopment.Purpose or VideoDevelopment.TranslationPurpose or VideoWorld.Purpose or VideoWorld.CardPurpose or VideoWorld.TranslationPurpose or VideoReferencePromptProtocol.ListPurpose or VideoReferencePromptProtocol.PromptPurpose)
            || (VideoTranslationProtocol.IsTranslation(request) ? !VideoTranslationProtocol.SupportedVersion(request.PromptVersion)
                : request.PromptVersion != (VideoReferencePromptProtocol.IsRequest(request) ? VideoReferencePromptProtocol.Version : VideoWorld.IsRequest(request) ? VideoWorldProtocol.Version : VideoDevelopment.IsDevelopment(request) ? VideoDevelopmentProtocol.Version : VideoConceptProtocol.Version))
            || request.Purpose == VideoReferencePromptProtocol.PromptPurpose && !Guid.TryParseExact(request.MaterialId, "N", out _)
            || (VideoDevelopment.IsDevelopment(request) || VideoWorld.IsRequest(request) || VideoReferencePromptProtocol.IsRequest(request)) && (!Guid.TryParseExact(request.ContextRevisionId, "N", out _)
                || string.IsNullOrWhiteSpace(request.ContextText) || request.ContextOriginal is null || request.ResponseLanguage is null))
            throw new InvalidDataException("VideoPrep.AiInvalidRequest");
        VideoTranslationProtocol.ValidateTarget(request);
        if (!request.ProjectPath.EndsWith(VideoPreparationArchive.Extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("VideoPrep.InvalidExtension");
        if (request.EngineSetId != EngineSetSelection.ActiveId) throw new BackgroundOperationWaitingException("VideoPrep.AiEngineChanged");
        if (!File.Exists(request.ModelPath)) throw new FileNotFoundException("VideoPrep.AiModelMissing");
    }
}
