using System.IO;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.VectorGraphics;

public sealed class VectorWork : IDisposable
{
    private readonly LiteraryChatRuntime _runtime = new();
    public async Task<string> RunAsync(VectorRequest request, Action<string, int> status, CancellationToken token,
        IProgress<VectorStreamChunk>? live = null)
    {
        using var audit = ComponentAudit.CaptureOperation();
        var state = VectorStore.Load(request.Id);
        if (state.Request != request) throw new InvalidDataException("Vector.Error.Job");
        var answerPath = VectorStore.FilePath(request.Id, "answer.txt");
        var svgPath = VectorStore.FilePath(request.Id, "image.svg"); var preview = VectorStore.FilePath(request.Id, "preview.png");
        try
        {
            await ComponentLicenseGate.EnsureAsync([VectorModels.ArtifactId(request.ModelId), VectorRenderer.TermsId], token);
            // A completed inference is committed before rendering; resume never regenerates it.
            if (!File.Exists(answerPath) || state.Provenance is null)
            {
                VectorStore.Save(state = state with { Phase = "generating", Error = null }); status("Generating", 0);
                live?.Report(new(VectorStreamKind.Reset, ""));
                string answer; OutputProvenance facts;
                {
                    var partial = new StringBuilder(); var updated = System.Diagnostics.Stopwatch.StartNew();
                    var progress = new InlineProgress<ModelStreamChunk>(chunk =>
                    {
                        partial.Append(chunk.Text); status("Generating", partial.Length);
                        live?.Report(new(VectorStreamKind.Svg, chunk.Text));
                        if (updated.ElapsedMilliseconds > 1500)
                        { File.WriteAllText(VectorStore.FilePath(request.Id, "partial.txt"), partial.ToString()); updated.Restart(); }
                    });
                    VectorChatReply reply;
                    var reasoning = new InlineProgress<ModelStreamChunk>(chunk => live?.Report(new(VectorStreamKind.Reasoning, chunk.Text)));
                    try { reply = await _runtime.VectorChatAsync(VectorPrompt.Compose(request.Prompt, request.Options), progress, token, reasoning); }
                    finally { if (partial.Length > 0) File.WriteAllText(VectorStore.FilePath(request.Id, "partial.txt"), partial.ToString()); }
                    facts = new OutputProvenance { Scenario = "vector-graphics/generation", CreatedUtc = request.CreatedUtc, ModelHistory = "recorded",
                        Models = [new(ManagedModelCatalog.OmniGammaArtifactId, LiteraryModelLocation.DisplayName, ManagedModelCatalog.OmniGammaRevision, "SVG text generation"),
                            new("llama.cpp", "llama.cpp " + reply.Backend, reply.Revision, "text inference; engine set " + reply.EngineSet)] };
                    answer = reply.SvgAnswer;
                }
                await File.WriteAllTextAsync(answerPath, answer, token);
                VectorStore.Save(state = state with { Phase = "generated", Provenance = facts });
                ApplicationBackgroundOperations.Current?.SaveCheckpoint(new { request.Id, Phase = "generated" });
            }
            var raw = await File.ReadAllTextAsync(answerPath, token);
            var prepared = VectorSvg.Prepare(raw);
            var svg = VectorSvg.WithProvenance(prepared.Svg, state.Provenance!, request.Author);
            state = state with { SyntaxCorrections = prepared.SyntaxCorrections };
            if (prepared.SyntaxCorrections > 0)
                await File.WriteAllTextAsync(VectorStore.FilePath(request.Id, "syntax-corrections.json"), JsonSerializer.Serialize(new {
                    kind = "tspan-closing-tag-typo", count = prepared.SyntaxCorrections,
                    originalAnswerSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))),
                    correctedSvgSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(svg))) }), token);
            await File.WriteAllTextAsync(svgPath, svg, token);
            status("Rendering", 0); VectorStore.Save(state = state with { Phase = "rendering", Error = null });
            var binding = VectorStore.FilePath(request.Id, "preview.source-sha256");
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(svg)));
            if (!File.Exists(preview) || !File.Exists(binding) || await File.ReadAllTextAsync(binding, token) != digest)
            {
                await VectorRenderer.RenderAsync(svg, preview, state.Provenance!, token);
                await File.WriteAllTextAsync(binding, digest, token);
            }
            var export = state.ExportPath;
            if (export is null)
            {
                Directory.CreateDirectory(request.OutputDirectory);
                export = Path.Combine(request.OutputDirectory, "Vector_" + request.CreatedUtc.ToString("yyyyMMdd_HHmmss") + "_" + request.Id + ".svg");
                if (!File.Exists(export)) File.Copy(svgPath, export, false);
                else if (await File.ReadAllTextAsync(export, token) != svg) throw new IOException("Vector.Error.Export");
            }
            VectorStore.Save(state with { Phase = "completed", Error = null, ExportPath = export }); status("Ready", 0); return export;
        }
        catch (OperationCanceledException)
        { VectorStore.Save(state with { Phase = "waiting", Error = null }); throw; }
        catch (Exception error)
        { VectorStore.Save(state with { Phase = "failed", Error = error.Message }); throw; }
        finally { _runtime.Stop(); }
    }
    public static bool IsTerminalContentError(string? key) => key is "Vector.Error.InvalidSvg" or "Vector.Error.IncompleteSvg"
        or "Vector.Error.UnsafeSvg" or "Vector.Error.Complexity" or "Vector.Error.ViewBox" or "Vector.Error.Context";
    public static void RecoverFailedContent(BackgroundOperationController controller)
    {
        if (controller.State is not { Kind: VectorStore.BackgroundKind, Phase: BackgroundOperationPhase.Waiting, RequiresDecision: true } background
            || controller.HasLiveOperation(background.Id) || background.Project is null) return;
        try
        {
            var job = VectorStore.Load(background.Project);
            if (job.Phase == "failed" && IsTerminalContentError(job.Error)) controller.FailWaiting(background.Id, job.Error!);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        { OwnedProcessRegistry.Log("vector_recovery_unavailable", "Vector", detail: error.GetType().Name); }
    }
    public void Dispose() => _runtime.Dispose();
}
