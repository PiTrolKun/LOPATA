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
        try
        {
            var licenses = new ManagedModelLibraryStore().LoadAll().Where(card => card.Files.Any(file =>
                Path.GetFullPath(Path.Combine(card.InstallDirectory, file.RelativePath)).Equals(Path.GetFullPath(model.Path), StringComparison.OrdinalIgnoreCase)))
                .Select(card => card.ModelArtifactId).ToList();
            if (model.IsCoreModel) licenses.Add(ManagedModelCatalog.CoreArtifactId);
            await ComponentLicenseGate.EnsureAsync(licenses.Distinct(StringComparer.Ordinal).ToArray(), token);
            var estimated = model.Format.Equals("chatllm", StringComparison.OrdinalIgnoreCase);
            var capacity = estimated ? 8192 : Math.Min(CoreContextRuntimeLimits.CurrentBackendContextLimit,
                (await Task.Run(() => LiteraryModelMemoryMetadata.Read(model.Path), token)).ModelContextTokens);
            var reserve = estimated ? 4096 : Math.Min(4096, capacity / 3);
            if (!estimated) await _llama.PrepareAsync(model, _ => { }, token);
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
            var payload = JsonSerializer.Serialize(new {
                model = "local", messages = new[] { new { role = "system", content = context.System } }
                    .Concat(context.Messages.Select(m => new { role = m.Role, content = m.Text })).ToArray(),
                max_tokens = reserve, temperature = .8, top_p = .95, stream = false, cache_prompt = false,
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
    public static IReadOnlyList<DebugModelInfo> Discover(StorageSettings settings) => FinancialModelDiscovery.Discover(settings)
        .Where(m => !new[] { "yue2", "vocoder", "flux", "diffusion", "stable-diffusion" }
            .Any(word => Path.GetFileName(m.Path).Contains(word, StringComparison.OrdinalIgnoreCase)))
        .ToArray();
}
