using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.ImageLayers;

internal sealed class LayerVisionSession(ManagedModelLibraryStore library, bool gemma) : IAsyncDisposable
{
    private const int LayerImageTokenMinimum = 1024;
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private OmniLlamaRuntimeService? _gamma;
    private LlamaServerRuntimeService? _gemma;
    private DebugModelInfo? _gemmaModel;

    internal OutputModelUse[] Models => gemma
        ? [new(ManagedModelCatalog.LetItOutArtifactId, LetItOutModel.DisplayName, ManagedModelCatalog.LetItOutRevision, "cross-check"),
            new(ManagedModelCatalog.LetItOutVisionArtifactId, "Gemma 4 F16", ManagedModelCatalog.LetItOutRevision, "vision projector")]
        : [new(ManagedModelCatalog.OmniGammaArtifactId, ManagedModelCatalog.OmniGammaDisplayName,
            ManagedModelCatalog.OmniGammaRevision, "layer analysis")];

    internal async Task<LayerMap[]> MapAsync(byte[] image, LayerDepth depth, string? target, bool russian, CancellationToken token,
        bool portraitParts = false, LayerReviewAnchor[]? review = null) =>
        LayerAnalysisProtocol.ReadMap(await AskAsync(image, review is null
                ? LayerAnalysisProtocol.MapPrompt(depth, target, russian, portraitParts) : LayerAnalysisProtocol.ReviewPrompt(russian, review),
            LayerAnalysisProtocol.MapSchema(depth, portraitParts), 4200, token), depth);

    internal async Task<int[]?> LocateAsync(byte[] image, string target, CancellationToken token) =>
        LayerAnalysisProtocol.ReadBox(await AskAsync(image, LayerAnalysisProtocol.LocatePrompt(target),
            LayerAnalysisProtocol.LocateSchema, 160, token));

    internal async Task<(int[]? Box, bool Person)> LocatePersonAsync(byte[] image, string target, CancellationToken token) =>
        LayerAnalysisProtocol.ReadPersonBox(await AskAsync(image, LayerAnalysisProtocol.LocatePrompt(target) +
            " In addition set person=true only when the target is a whole visible human, including a person cropped by the frame. " +
            "Set person=false for a detached body part, garment, animal or object.",
            LayerAnalysisProtocol.LocatePersonSchema, 180, token));

    internal async Task PrepareAsync(CancellationToken token)
    {
        await ApplicationBackgroundOperations.RetireModelsAsync();
        if (!gemma)
        {
            _gamma = new(library, OmniLlamaProfile.Gamma, LayerImageTokenMinimum);
            await _gamma.PrepareAsync(Log, null, token).ConfigureAwait(false);
            return;
        }

        var core = await ManagedModelRuntimeAudit.VerifyAsync(library, ManagedModelCatalog.LetItOutArtifactId, token);
        var vision = await ManagedModelRuntimeAudit.VerifyAsync(library, ManagedModelCatalog.LetItOutVisionArtifactId, token);
        var modelPath = Path.Combine(core.InstallDirectory, core.Files.Single(file => file.Purpose == "main_model").RelativePath);
        var projectorPath = Path.Combine(vision.InstallDirectory, vision.Files.Single(file => file.Purpose == "vision_projector").RelativePath);
        _gemmaModel = new()
        {
            Name = core.DisplayName, Path = modelPath, Format = "gguf", Role = "let_it_out",
            SizeBytes = new FileInfo(modelPath).Length
        };
        _gemma = new(new UserContextService(new UserProfileStore(), new IpLocationService()));
        _gemma.ConfigurePrivateNoRetry();
        _gemma.ConfigureLetItOut((_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(LlamaGpuDecision.Hybrid);
        }, null, visionGpuAllowance: LetItOutMemoryPolicy.VramReserve);
        await _gemma.SetLetItOutProjectorAsync(projectorPath, token);
        await _gemma.WarmLetItOutAsync(_gemmaModel, token);
    }

    internal async Task<string> AskAsync(byte[] jpeg, string prompt, object schema, int maxTokens, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(12));
        var url = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg);
        var content = new object[] { new { type = "image_url", image_url = new { url } }, new { type = "text", text = prompt } };
        var data = JsonSerializer.Serialize(new
        {
            messages = new[] { new { role = "user", content } },
            stream = gemma, temperature = 0, seed = 10291, cache_prompt = false, max_tokens = maxTokens,
            chat_template_kwargs = new { enable_thinking = false },
            response_format = new { type = "json_schema", json_schema = new { name = "image_layers", strict = true, schema } }
        });
        try
        {
            if (_gemma is not null)
            {
                var result = await _gemma.GenerateLetItOutAsync(_gemmaModel!, data, maxTokens,
                    new Progress<ModelStreamChunk>(), deadline.Token);
                if (result.FinishReason != "stop") throw new InvalidDataException("ImageLayers.InvalidReply");
                return result.Content;
            }

            using var request = new StringContent(data, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(new Uri(_gamma!.Endpoint, "v1/chat/completions"), request, deadline.Token);
            response.EnsureSuccessStatusCode();
            using var reply = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            var choice = reply.RootElement.GetProperty("choices")[0];
            if (choice.GetProperty("finish_reason").GetString() != "stop")
                throw new InvalidDataException("ImageLayers.InvalidReply");
            return choice.GetProperty("message").GetProperty("content").GetString() ?? "";
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("ImageLayers.Timeout"); }
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        if (_gamma is not null)
        {
            _gamma.Dispose();
            await _gamma.AwaitProcessRetirementAsync(CancellationToken.None);
        }
        if (_gemma is not null)
        {
            try { await _gemma.RetireLetItOutAsync(); }
            finally { _gemma.Dispose(); }
        }
    }

    private static void Log(string text) => OwnedProcessRegistry.Log("image_layers_gamma", "ImageLayers", detail: text);
}
