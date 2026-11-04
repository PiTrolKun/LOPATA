using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.VideoEditor;

namespace AIHub.Services.VideoUpscale;

internal sealed class UpscaleComponents : IDisposable
{
    internal const string FlashRevision = "ac60d1e3a5dc0a6a24c4057b672e22b588145cce";
    internal const string WeightRevision = "46b79520946ac67dec94d1566490690953c7debc";
    internal const string IntRevision = "56c806f1357d90c8a0b4b054e21bcefe3cb9ac1d";
    private readonly ManagedModelLibraryStore _store = new();
    private readonly ManagedModelAcquisitionService _download;
    internal UpscaleComponents(int parallel = 4) { _download = new(_store); _download.MaximumParallelConnections = parallel; }
    private sealed record Weight(string Role, string RelativePath, string SourceUrl, long SizeBytes, string Sha256, string Purpose);
    private static T Read<T>(string file) => JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Tools", "VideoUpscale", file)))!;
    internal static IReadOnlyList<ManagedModelArtifactCard> Cards(string root, UpscaleProject project)
    {
        List<ManagedModelArtifactCard> cards = [];
        var flash = project.Steps.Where(s => s.Enabled && s.Engine == UpscaleEngine.Flash).ToArray();
        if (flash.Length > 0) {
            cards.Add(Card(root, "video-upscale-flash-runtime", "FlashVSR+ · Python CUDA runtime", "python-3.10.18-torch-2.7-cu128-1", Read<List<ManagedModelArtifactFile>>("runtime.json"), "PSF; BSD; Apache-2.0; MIT; NVIDIA CUDA", "https://pytorch.org/"));
            cards.Add(Card(root, "video-upscale-flash-source", "FlashVSR+ · T8 ModelHandle", FlashRevision,
                [new() { RelativePath = "source.zip", SourceUrl = "https://codeload.github.com/T8mars/Comfyui-Flashvsr-Plus-T8/zip/" + FlashRevision, SizeBytes = 17340225, Sha256 = "15804913d2429097a98c22e43b7268d798cc0757febd7752daf513f70fde131c", Purpose = "source" }], "Apache-2.0", "https://github.com/T8mars/Comfyui-Flashvsr-Plus-T8"));
            var weights = Read<List<Weight>>("weights.json");
            foreach (var step in flash.DistinctBy(s => (s.Get("mode", "turbo"), s.Get("precision", "int8_convrot") == "int8_convrot"))) {
                var mode = step.Get("mode", "turbo"); var quant = step.Get("precision", "int8_convrot") == "int8_convrot";
                var roles = new[] { mode + (quant ? "_int8_convrot" : "_dit"), mode + "_lq", "tiny" };
                cards.Add(Card(root, WeightId(step), "FlashVSR+ " + mode + (quant ? " INT8" : " BF16/FP16"), quant ? IntRevision : WeightRevision,
                    weights.Where(w => roles.Contains(w.Role)).Select(w => new ManagedModelArtifactFile { RelativePath = w.RelativePath, SourceUrl = w.SourceUrl, SizeBytes = w.SizeBytes, Sha256 = w.Sha256, Purpose = "model" }).ToList(), "Apache-2.0", "https://huggingface.co/ruchangyao/FlashVSR_plus"));
            }
        }
        if (project.Steps.Any(s => s.Enabled && s.Engine is not (UpscaleEngine.Scale or UpscaleEngine.Flash)))
            cards.Add(Card(root, "video-upscale-visual-enhancer", "Visual Enhancer CLI · NVIDIA RTX/VFX/NR", "21.0",
                [new() { RelativePath = "Visual.Enhancer.v21.0.zip", SourceUrl = "https://github.com/Merserk/dlss5-visual-enhancer/releases/download/v21.0/Visual.Enhancer.v21.0.zip", SizeBytes = 824005214, Sha256 = "a28ea3ae0f3c3fcaa5100fdc0025b60f26236ac2b30107e7e5fad0cb0d25573d", Purpose = "runtime" }], "Visual Enhancer custom licence; NVIDIA proprietary components", "https://github.com/Merserk/dlss5-visual-enhancer"));
        return cards;
    }
    private static ManagedModelArtifactCard Card(string root, string id, string name, string revision, List<ManagedModelArtifactFile> files, string license, string page) => new() {
        ModelArtifactId = id, Family = "Video upscale", DisplayName = name, Role = ManagedModelRoles.Tool, Provider = "Upstream", RepositoryId = page,
        Revision = revision, License = license, SourcePage = page, Format = "Pinned runtime / weights", IsManaged = true, CanRemoveFiles = true,
        ModelsRoot = root, InstallDirectory = Path.Combine(root, "VideoUpscale", id, revision), Origin = ManagedModelOrigins.PredefinedScenario,
        Consumers = [new() { Id = "video_upscale", DisplayName = "Апскейл видео / Video upscale", Kind = "utility" }], Files = files };
    internal static string WeightId(UpscaleStep s) => "video-upscale-flash-" + s.Get("mode", "turbo") + (s.Get("precision", "int8_convrot") == "int8_convrot" ? "-int8" : "-float");
    internal async Task<bool> PrepareAsync(string root, UpscaleProject project, bool download, IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token)
    {
        using var audit = ComponentAudit.CaptureOperation();
        if (!Path.IsPathFullyQualified(root)) throw new InvalidOperationException("VideoEditor.StorageRequired");
        var cards = Cards(root, project); await ComponentLicenseGate.EnsureAsync(cards.Select(c => c.ModelArtifactId).ToArray(), token);
        var ready = true;
        foreach (var card in cards) {
            var old = _store.Load(card.ModelArtifactId);
            _store.Upsert(old is not null && old.Revision == card.Revision && old.InstallDirectory == card.InstallDirectory ? old : card);
            var actual = await _download.VerifyAsync(card.ModelArtifactId, progress, token);
            if (actual.Status != ManagedModelStatuses.Installed && download) actual = await _download.DownloadAsync(card.ModelArtifactId, progress, token);
            if (actual.Status != ManagedModelStatuses.Installed) { ready = false; continue; }
            if (card.Files.Any(f => f.Purpose != "model")) {
                try { await UpscaleRuntime.PrepareAsync(actual, token, reinstall: download); }
                catch (IOException) when (!download) { ready = false; }
            }
        }
        return ready;
    }
    public void Dispose() => _download.Dispose();
}
