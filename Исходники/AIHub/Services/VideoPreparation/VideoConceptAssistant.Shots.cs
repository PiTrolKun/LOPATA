using System.IO;
using System.Text.Json;

namespace AIHub.Services.VideoPreparation;

public interface IVideoShotsProgress
{
    Task<VideoConceptAnswer> GenerateShotsAsync(VideoConceptRequest request, Action<string>? reasoning, CancellationToken token);
}
public sealed partial class VideoConceptAssistant : IVideoShotsProgress
{
    public async Task<VideoConceptAnswer> GenerateShotsAsync(VideoConceptRequest request, Action<string>? reasoning, CancellationToken token)
    {
        VideoShotsWork.ValidateRequest(request);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(10));
        await _residentGate.WaitAsync(deadline.Token);
        try
        {
            await EnsureResidentAsync(new(request.ModelId, request.ModelPath, request.EngineSetId), deadline.Token);
            var system = VideoShotsProtocol.Instructions(request); var input = VideoShotsProtocol.Input(request);
            var used = await _runtime.CountPoetryAsync(system, [new("user", input, request.Id)], deadline.Token);
            var budget = OutputTokens(_capacity, used);
            var payload = JsonSerializer.Serialize(new
            {
                model = "local", messages = new[] { new { role = "system", content = system }, new { role = "user", content = input } },
                max_tokens = budget, temperature = request.Purpose is VideoShotsProtocol.Translate or VideoShotsQuality.Purpose ? .2 : .65, top_p = .9,
                seed = 42, stream = true, cache_prompt = false,
                chat_template_kwargs = new { enable_thinking = request.Purpose != VideoShotsProtocol.Translate && MusicPoetryReasoning.SupportsId(request.ModelId) },
                reasoning_format = "deepseek", response_format = VideoShotsProtocol.Schema(request)
            });
            var response = await _runtime.GeneratePrivateStreamJsonAsync(_model!, payload, reasoning, deadline.Token);
            using var doc = JsonDocument.Parse(response); var choice = doc.RootElement.GetProperty("choices")[0];
            if (choice.GetProperty("finish_reason").GetString() != "stop") throw new InvalidDataException("VideoPrep.ShotsInvalidReply");
            var text = choice.GetProperty("message").GetProperty("content").GetString()!;
            var answer = new VideoConceptAnswer(text, "", "", _baseIdentity! with { Role = request.Purpose == VideoShotsQuality.Purpose ? "video scene review" : request.Purpose == VideoShotsProtocol.Translate ? "video shots translation" : "video scenes and shots" });
            VideoConceptProtocol.Validate(answer);
            if (request.Purpose == VideoShotsProtocol.Outline) _ = VideoShotsProtocol.ReadOutline(answer.Text, request.ContextText);
            else if (request.Purpose == VideoShotsQuality.Purpose) _ = VideoShotsQuality.Read(answer.Text);
            else _ = VideoShotsProtocol.ReadScene(answer.Text, request);
            return answer;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested) { throw new TimeoutException("VideoPrep.AiTimeout"); }
        finally
        {
            try { if (deadline.IsCancellationRequested) await _runtime.RetirePoetryAsync(); }
            finally { _residentGate.Release(); RuntimeChanged?.Invoke(); }
        }
    }
}
