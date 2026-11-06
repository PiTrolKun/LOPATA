using System.IO;
using System.Diagnostics;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.LivingArcade;

public sealed partial class LivingArcadeGeneration
{
    private async Task PrepareModelAsync(LivingArcadeModelPaths paths, bool artist, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var path = artist ? paths.Artist : paths.Main;
        if (_preparedPath == path && _runtime.PoetrySession is not null) return;
        Status("Loading");
        await ApplicationBackgroundOperations.RetireModelsExceptAsync(_runtime);
        await _runtime.PreparePoetryAsync(Model(paths, artist), token,
            requestedContextTokens: 16384,
            reasoningOptions: MusicPoetryReasoningOptions.Disabled,
            gpuAdmission: (admission, ct) => {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(admission.Reason == "cpu_fallback" ? LlamaGpuDecision.Cpu : LlamaGpuDecision.Hybrid);
            });
        _preparedPath = path;
        StatusChanged?.Invoke(_logicalStage);
    }

    private static DebugModelInfo Model(LivingArcadeModelPaths paths, bool artist) => new() {
        Name = artist ? "Qwen2.5-Coder 3B Instruct" : "Qwen2.5-Coder 7B Instruct",
        Path = artist ? paths.Artist : paths.Main, Format = "gguf",
        SizeBytes = artist ? LivingArcadeModels.ArtistBytes : LivingArcadeModels.MainBytes,
        Role = artist ? "living_arcade_artist" : "living_arcade_designer" };

    private async Task<T> AskAsync<T>(LivingArcadeModelPaths paths, bool artist, string system,
        string input, int outputTokens, Func<string, T> parse, CancellationToken token, int repairs = 2,
        object? responseFormat = null)
    {
        await PrepareModelAsync(paths, artist, token);
        var correction = "";
        for (var attempt = 0; ; attempt++)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(_runtime.PoetrySession?.Mode == "Gpu" ? 8 : 30));
            var payload = JsonSerializer.Serialize(new {
                messages = new[] { new { role = "system", content = system }, new { role = "user", content = input + correction } },
                temperature = artist ? .8 : .7, top_p = .8, top_k = 20, repeat_penalty = 1.08,
                max_tokens = outputTokens, stream = false,
                response_format = responseFormat ?? new { type = "json_object" },
                chat_template_kwargs = new { enable_thinking = false }
            });
            // Native/process failures leave this method immediately. Only completed but invalid content can be repaired.
            string raw;
            var clock = Stopwatch.StartNew();
            try
            {
                raw = await _runtime.GenerateBoundedPrivateJsonAsync(Model(paths, artist), payload,
                    outputTokens, _ => { }, deadline.Token) ?? throw new InvalidDataException("Arcade.Ai.ContextTooSmall");
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
            { throw new TimeoutException("Arcade.Ai.TimedOut"); }
            InferenceTrace?.Invoke(new(artist ? LivingArcadeModels.ArtistArtifactId : LivingArcadeModels.MainArtifactId,
                _logicalStage, attempt, payload, raw));
            using (var receipt = JsonDocument.Parse(raw))
            {
                int? prompt = null, completion = null;
                if (receipt.RootElement.TryGetProperty("usage", out var usage))
                {
                    if (usage.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt32(out var pn)) prompt = pn;
                    if (usage.TryGetProperty("completion_tokens", out var c) && c.TryGetInt32(out var cn)) completion = cn;
                }
                InferenceCompleted?.Invoke(new(artist ? LivingArcadeModels.ArtistArtifactId : LivingArcadeModels.MainArtifactId,
                    _logicalStage, clock.Elapsed.TotalSeconds, prompt, completion));
            }
            try { return parse(ReadCompletion(raw)); }
            catch (Exception error) when ((error is JsonException or InvalidDataException) && attempt < repairs)
            {
                var detail = error.Data["ValidationDetail"] as string ?? error.Message;
                correction = "\nYour last completed JSON failed validation. Return a new corrected object matching the schema and ranges exactly. " +
                    "Validation: " + detail[..Math.Min(detail.Length, 900)];
            }
        }
    }

    internal static string ReadCompletion(string raw)
    {
        if (raw.Length > 300_000) throw new InvalidDataException("Arcade.Ai.InvalidOutput");
        using var response = JsonDocument.Parse(raw);
        var choices = response.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() != 1 || choices[0].GetProperty("finish_reason").GetString() != "stop")
            throw new InvalidDataException("Arcade.Ai.IncompleteOutput");
        var message = choices[0].GetProperty("message");
        if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array && calls.GetArrayLength() != 0)
            throw new InvalidDataException("Arcade.Ai.InvalidOutput");
        var content = message.GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(content)) throw new InvalidDataException("Arcade.Ai.InvalidOutput");
        return content;
    }
}
