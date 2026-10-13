using System.IO;
using AIHub.Models;

namespace AIHub.Services;

public interface IMusicPoetryRuntime : IDisposable
{
    Task<string> GenerateAsync(DebugModelInfo model, MusicPoetrySession request, string guidance,
        Action<MusicPoetryContext> contextReady, CancellationToken token, bool assemble = false, string? reasoningMode = null,
        Action<MusicPoetryStreamUpdate>? reasoningProgress = null, string? interfaceLanguage = null,
        MusicPoetryReasoningPreference? reasoningPreference = null);
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
        Action<MusicPoetryContext> contextReady, CancellationToken token, bool assemble = false, string? reasoningMode = null,
        Action<MusicPoetryStreamUpdate>? reasoningProgress = null, string? interfaceLanguage = null,
        MusicPoetryReasoningPreference? reasoningPreference = null)
    {
        if (!File.Exists(model.Path)) throw new FileNotFoundException("Music.Poetry.ModelMissing");
        if (!MusicPoetryModelCatalog.IsCompatible(model)) throw new InvalidDataException("Music.Poetry.ModelUnsupported");
        var reasoning = MusicPoetryReasoning.Resolve(model, reasoningMode, reasoningPreference);
        try
        {
            // Library inventory IDs are not license IDs; use the same component as the original scenario.
            await ComponentLicenseGate.EnsureAsync(MusicPoetryModelCatalog.Find(model)!.LicenseId, token);
            var estimated = model.Format.Equals("chatllm", StringComparison.OrdinalIgnoreCase);
            var capacity = estimated ? 8192 : Math.Min(CoreContextRuntimeLimits.CurrentBackendContextLimit,
                (await Task.Run(() => LiteraryModelMemoryMetadata.Read(model.Path), token)).ModelContextTokens);
            if (!estimated) capacity = await _llama.PreparePoetryAsync(model, token, reasoningMode, reasoningPreference);
            var reserve = estimated ? 4096 : reasoning.ReplyReserve(capacity);
            var context = await MusicPoetryContextWindow.BuildAsync(request, guidance, capacity, reserve,
                estimated ? (system, messages) => Task.FromResult(MusicPoetryContextWindow.Estimate(system, messages))
                    : (system, messages) => _llama.CountPoetryAsync(system, messages, token), estimated, interfaceLanguage,
                reasoning, MusicPoetryReasoning.ModelId(model));
            contextReady(context);
            if (estimated) {
                var binReply = await _bin.GenerateConversationAsync(model, context.System,
                context.Messages.Select(m => new FinancialDiscussionMessage(m.Role, m.Text)).ToArray(), reserve, token,
                truncated: content => throw new MusicPoetryIncompleteReply(content));
                return binReply;
            }
            var streaming = reasoning.Enabled && reasoningProgress is not null;
            var payload = MusicPoetryGeneration.Payload(model, context, reserve, reasoning, assemble, streaming);
            if (streaming)
            {
                var result = await _llama.GeneratePoetryStreamAsync(model, payload, reasoningProgress!, token);
                return MusicPoetryGeneration.Finish(result.Content, result.FinishReason);
            }
            var response = await _llama.GeneratePrivateJsonAsync(model, payload, token);
            return MusicPoetryGeneration.Answer(response);
        }
        finally { await _llama.RetirePoetryAsync(); await _bin.RetireAsync(); }
    }
    public void Dispose() { _llama.Dispose(); _bin.Dispose(); }
    public static IReadOnlyList<DebugModelInfo> Discover(StorageSettings settings) => MusicPoetryModelCatalog.Discover(settings);
}
