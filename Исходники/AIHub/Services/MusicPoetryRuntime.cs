using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public interface IMusicPoetryRuntime : IDisposable
{
    Task<string> GenerateAsync(DebugModelInfo model, MusicPoetrySession request, string guidance,
        Action<MusicPoetryContext> contextReady, CancellationToken token, bool assemble = false);
}

public sealed class MusicPoetryIncompleteReply(string reply) : IOException("Music.Poetry.Truncated")
{
    public string Reply { get; } = reply;
}

/// <summary>Private creative requests over existing device selectors and native runtimes.</summary>
public sealed class MusicPoetryRuntime(UserContextService userContext) : IMusicPoetryRuntime
{
    private readonly LlamaServerRuntimeService _llama = new(userContext);
    private readonly FinancialModelRuntime _bin = new(userContext);
    public async Task<string> GenerateAsync(DebugModelInfo model, MusicPoetrySession request, string guidance,
        Action<MusicPoetryContext> contextReady, CancellationToken token, bool assemble = false)
    {
        if (!File.Exists(model.Path)) throw new FileNotFoundException("Music.Poetry.ModelMissing");
        if (!MusicPoetryModelCatalog.IsCompatible(model)) throw new InvalidDataException("Music.Poetry.ModelUnsupported");
        try
        {
            // Library inventory IDs are not license IDs; use the same component as the original scenario.
            await ComponentLicenseGate.EnsureAsync(MusicPoetryModelCatalog.Find(model)!.LicenseId, token);
            var estimated = model.Format.Equals("chatllm", StringComparison.OrdinalIgnoreCase);
            var capacity = estimated ? 8192 : Math.Min(CoreContextRuntimeLimits.CurrentBackendContextLimit,
                (await Task.Run(() => LiteraryModelMemoryMetadata.Read(model.Path), token)).ModelContextTokens);
            if (!estimated) capacity = await _llama.PreparePoetryAsync(model, token);
            var reserve = estimated ? 4096 : Math.Min(4096, capacity / 3);
            var context = await MusicPoetryContextWindow.BuildAsync(request, guidance, capacity, reserve,
                estimated ? (system, messages) => Task.FromResult(MusicPoetryContextWindow.Estimate(system, messages))
                    : (system, messages) => _llama.CountPoetryAsync(system, messages, token), estimated);
            contextReady(context);
            if (estimated) {
                var binReply = await _bin.GenerateConversationAsync(model, context.System,
                context.Messages.Select(m => new FinancialDiscussionMessage(m.Role, m.Text)).ToArray(), reserve, token,
                truncated: content => throw new MusicPoetryIncompleteReply(content));
                return binReply;
            }
            var profile = MusicPoetryProfileCatalog.Find(MusicPoetryModelCatalog.Find(model)!.Id);
            var payload = JsonSerializer.Serialize(new {
                model = "local", messages = new[] { new { role = "system", content = context.System } }
                    .Concat(context.Messages.Select(m => new { role = m.Role, content = m.Text })).ToArray(),
                max_tokens = reserve, temperature = profile?.Temperature ?? .8, top_p = profile?.TopP ?? .95,
                top_k = profile?.TopK ?? 40, min_p = profile?.MinP ?? 0,
                repeat_penalty = profile?.RepeatPenalty ?? 1, presence_penalty = profile?.PresencePenalty ?? 0,
                stream = false, cache_prompt = false,
                chat_template_kwargs = new { enable_thinking = false }, response_format = assemble ? MusicPoetryProtocol.AssemblySchema : MusicPoetryProtocol.Schema
            });
            var response = await _llama.GeneratePrivateJsonAsync(model, payload, token);
            using var doc = JsonDocument.Parse(response);
            var choice = doc.RootElement.GetProperty("choices")[0];
            var content = choice.GetProperty("message").GetProperty("content").GetString()
                ?? throw new InvalidDataException("Music.Poetry.InvalidReply");
            if (choice.GetProperty("finish_reason").GetString() == "length") throw new MusicPoetryIncompleteReply(content);
            return content;
        }
        finally { await _llama.RetirePoetryAsync(); await _bin.RetireAsync(); }
    }
    public void Dispose() { _llama.Dispose(); _bin.Dispose(); }
    public static IReadOnlyList<DebugModelInfo> Discover(StorageSettings settings) => MusicPoetryModelCatalog.Discover(settings);
}
