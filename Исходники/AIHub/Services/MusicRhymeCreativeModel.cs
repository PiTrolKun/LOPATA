using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>A fresh plain conversation per stage, using the same selected native runners as the workshop.</summary>
internal static class MusicRhymeCreativeModel
{
    internal static async Task<string> GenerateAsync(LlamaServerRuntimeService llama, FinancialModelRuntime bin,
        DebugModelInfo model, string prompt, MusicRhymeTuning tuning, Action<string>? diagnostic, CancellationToken token)
    {
        tuning.Validate(); token.ThrowIfCancellationRequested();
        if (!File.Exists(model.Path)) throw new FileNotFoundException("Music.Poetry.ModelMissing");
        var candidate = MusicPoetryModelCatalog.Find(model);
        if (candidate is null || !MusicRhymePreferences.Allowed(candidate.Id) || !MusicPoetryModelCatalog.IsCompatible(model))
            throw new InvalidDataException("Music.Poetry.ModelUnsupported");
        try
        {
            await ComponentLicenseGate.EnsureAsync(candidate.LicenseId, token);
            string reply;
            if (model.Format.Equals("chatllm", StringComparison.OrdinalIgnoreCase))
            {
                var capacity = Math.Min(8192, tuning.Integer("ModelContext"));
                var reserve = MusicRhymeModelSettings.Reserve(tuning, capacity);
                if (prompt.Length / 2 > capacity - Math.Max(4096, reserve) - 96)
                    throw new InvalidDataException("Music.Rhyme.ContextFull");
                reply = await bin.GenerateUserOnlyAsync(model, prompt, reserve, token,
                    truncated: content => throw new MusicPoetryIncompleteReply(content));
            }
            else
            {
                var reasoning = MusicRhymeModelSettings.ReasoningOptions(model, tuning);
                var capacity = await llama.PreparePoetryAsync(model, token,
                    requestedContextTokens: tuning.Integer("ModelContext"), reasoningOptions: reasoning,
                    startupTimeout: TimeSpan.FromSeconds(tuning.Integer("ServerStartSeconds")));
                var count = await llama.CountRhymeCreativeAsync(prompt, token);
                var reserve = MusicRhymeModelSettings.Reserve(tuning, capacity);
                if (count + reserve > capacity) throw new InvalidDataException("Music.Rhyme.ContextFull");
                var sampling = MusicRhymeModelSettings.Sampling(tuning, MusicPoetryProfileCatalog.Find(candidate.Id));
                var payload = Payload(prompt, reserve, tuning, sampling, reasoning);
                var response = await llama.GeneratePrivateJsonAsync(model, payload, token);
                diagnostic?.Invoke(response);
                reply = MusicPoetryGeneration.Answer(response);
            }
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(reply) || reply.Length > tuning.Integer("MaxAnswerChars"))
                throw new InvalidDataException("Music.Rhyme.InvalidReply");
            return reply;
        }
        finally { await llama.RetirePoetryAsync(); await bin.RetireAsync(); }
    }

    internal static string Payload(string prompt, int reserve, MusicRhymeTuning tuning,
        (double TopP, int TopK, double MinP, double RepeatPenalty) sampling, MusicPoetryReasoningOptions reasoning) =>
        JsonSerializer.Serialize(new {
            model = "local", messages = new[] { new { role = "user", content = prompt } },
            max_tokens = reserve, temperature = tuning.Number("Temperature"), top_p = sampling.TopP, top_k = sampling.TopK,
            min_p = sampling.MinP, repeat_penalty = sampling.RepeatPenalty, stream = false, cache_prompt = false,
            chat_template_kwargs = reasoning.TemplateOptions(), reasoning_format = "deepseek"
        }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
