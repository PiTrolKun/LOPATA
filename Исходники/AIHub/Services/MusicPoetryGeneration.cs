using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

internal static class MusicPoetryGeneration
{
    internal static string Payload(DebugModelInfo model, MusicPoetryContext context, int reserve,
        MusicPoetryReasoningOptions reasoning, bool assemble, bool stream = false)
    {
        var profile = MusicPoetryProfileCatalog.Find(MusicPoetryModelCatalog.Find(model)!.Id);
        return JsonSerializer.Serialize(new {
            model = "local", messages = MusicPoetryReasoningHistory.Messages(context.System, context.Messages),
            max_tokens = reserve, temperature = profile?.Temperature ?? .8, top_p = profile?.TopP ?? .95,
            top_k = profile?.TopK ?? 40, min_p = profile?.MinP ?? 0,
            repeat_penalty = profile?.RepeatPenalty ?? 1, presence_penalty = profile?.PresencePenalty ?? 0,
            stream, cache_prompt = false,
            chat_template_kwargs = reasoning.TemplateOptions(), reasoning_format = "deepseek",
            response_format = assemble ? MusicPoetryProtocol.AssemblySchema : MusicPoetryProtocol.Schema
        });
    }

    internal static string Answer(string response)
    {
        using var doc = JsonDocument.Parse(response);
        var choice = doc.RootElement.GetProperty("choices")[0];
        var content = choice.GetProperty("message").GetProperty("content").GetString()
            ?? throw new InvalidDataException("Music.Poetry.InvalidReply");
        return Finish(content, choice.GetProperty("finish_reason").GetString() ?? "");
    }

    internal static string Finish(string content, string finishReason)
    {
        var filter = new LlamaAnswerContentFilter();
        var answer = filter.Append(content) + filter.Complete();
        if (finishReason == "length") throw new MusicPoetryIncompleteReply(answer);
        return answer;
    }
}
