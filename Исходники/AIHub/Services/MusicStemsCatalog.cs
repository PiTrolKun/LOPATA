using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Managed, isolated ORT wheels. Never installs into ACE or a shared Python site-packages.</summary>
public static class MusicStemsCatalog
{
    public const string ModelId = "model.music-stems.htdemucs";
    public const string LicenseId = "music.stems";
    public const string ModelRevision = "49df9b6989cf2150840ea65b0bef77a2e471b678";
    public const string ModelHash = "7ce55792e2231c93fbf92de95f5fd5b3a5e6c89f7db690dfd693e8f1dce56869";
    internal sealed record Payload(string Id, string Name, string Version, string Role, string Url, string File,
        string Sha256, long Size, long InstalledSize, string Health, PinnedTreeFile[] Files);
    private static readonly Payload[] Payloads = Load();
    private static Payload[] Load() {
        using var stream = typeof(MusicStemsCatalog).Assembly.GetManifestResourceStream("AIHub.MusicStemsManifest")
            ?? throw new InvalidDataException("Missing stems manifest.");
        return JsonSerializer.Deserialize<Payload[]>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Invalid stems manifest.");
    }
    public static IReadOnlyList<ComponentCatalogEntry> Entries { get; } = Build();
    private static IReadOnlyList<ComponentCatalogEntry> Build()
    {
        var entries = Payloads.Select(p => new ComponentCatalogEntry {
            Id = p.Id, Name = "Music separation · " + p.Name, Version = p.Version,
            Description = "Pinned isolated music separation dependency.", DeliveryKind = ComponentDeliveryKinds.Archive,
            DownloadUrl = p.Url, FileName = p.File, Sha256 = p.Sha256, DownloadSizeBytes = p.Size,
            InstalledSizeBytes = p.InstalledSize, HealthCheckRelativePath = p.Health,
            License = "Original wheel licenses and notices", LicenseIds = [LicenseId], Source = "https://pypi.org/project/" + p.Name + "/" + p.Version,
            Dependencies = p.Role == "cpu" ? [HardwareRuntimeCatalog.PythonCpuId, .. Payloads.Where(x => x.Role == "common").Select(x => x.Id)] : []
        }).ToList();
        entries.Add(new() { Id = ModelId, Name = "HT-Demucs · voice / music", Version = ModelRevision,
            Description = "Offline song separation into voice and accompaniment.", DeliveryKind = ComponentDeliveryKinds.File,
            DownloadUrl = "https://huggingface.co/StemSplitio/htdemucs-6s-onnx/resolve/" + ModelRevision + "/htdemucs_6s_fp16weights.onnx",
            FileName = "htdemucs_6s_fp16weights.onnx", DownloadSizeBytes = 136428532, InstalledSizeBytes = 136428532,
            Sha256 = ModelHash, HealthCheckRelativePath = "htdemucs_6s_fp16weights.onnx", License = "MIT",
            LicenseIds = [LicenseId], Source = "https://huggingface.co/StemSplitio/htdemucs-6s-onnx/tree/" + ModelRevision,
            Dependencies = [Payloads.Single(x => x.Role == "cpu").Id] });
        return entries;
    }
    public static string ModelPath(ComponentManager manager) => Path.Combine(manager.GetInstallDirectory(Entries.Single(x => x.Id == ModelId)), "htdemucs_6s_fp16weights.onnx");
    internal static string[] ImportRoots(ComponentManager manager, bool cuda) => Payloads.Where(p =>
        p.Role == "common" || p.Role == (cuda ? "cuda" : "cpu")).Select(p => manager.GetInstallDirectory(Entries.Single(x => x.Id == p.Id))).ToArray();
    public static async Task PrepareAsync(bool download, IProgress<ComponentDownloadProgress>? progress, CancellationToken token, bool verifyPython = true)
    {
        var manager = new ComponentManager();
        var ids = ComponentCatalog.ResolveDependencies([ModelId]).Select(x => x.Id).ToArray();
        await ComponentLicenseGate.EnsureAsync(ComponentCatalog.ResolveDependencies(ids).SelectMany(x => x.LicenseIds).Distinct().ToArray(), token);
        if (download) await manager.DownloadAndInstallAsync(ModelId, progress, token);
        foreach (var p in Payloads.Where(p => ids.Contains(p.Id))) {
            var entry = Entries.Single(x => x.Id == p.Id); var root = manager.GetInstallDirectory(entry);
            try { await Verify(root, p.Files, token); }
            catch (IOException) when (download) { await manager.DownloadAndInstallAsync(p.Id, progress, token, forceReinstall: true); await Verify(root, p.Files, token); }
        }
        var modelFiles = new[] { new PinnedTreeFile("htdemucs_6s_fp16weights.onnx", 136428532, ModelHash) };
        try { await Verify(Path.GetDirectoryName(ModelPath(manager))!, modelFiles, token); }
        catch (IOException) when (download) { await manager.DownloadAndInstallAsync(ModelId, progress, token, forceReinstall: true); await Verify(Path.GetDirectoryName(ModelPath(manager))!, modelFiles, token); }
        if (!ManagedPythonRuntime.HasCpu) throw new IOException("Music.Preparation.Missing");
        if (verifyPython) {
            var profiles = manager.GetStatus().Where(x => x.IsAvailable && x.Entry.Id is HardwareRuntimeCatalog.PythonCpuId
                or HardwareRuntimeCatalog.PythonCudaId or HardwareRuntimeCatalog.PythonCuda128Id).ToArray();
            foreach (var profile in profiles) {
                var kind = HardwareRuntimeCatalog.PythonProfile(profile.Entry.Id);
                var root = manager.GetInstallDirectory(profile.Entry);
                try { await PythonRuntimeBundleVerifier.VerifyAsync(kind, root, token); }
                catch (IOException) when (download) { await manager.DownloadAndInstallAsync(profile.Entry.Id, progress, token, forceReinstall: true); await PythonRuntimeBundleVerifier.VerifyAsync(kind, root, token); }
            }
            var selected = await ManagedPythonRuntime.ResolveAsync("auto", token,
                new HashSet<string> { HardwareRuntimeCatalog.PythonCudaId, HardwareRuntimeCatalog.PythonCuda128Id });
            if (selected.Device != "cpu") {
                var payload = Payloads.Single(p => p.Role == "cuda"); var entry = Entries.Single(x => x.Id == payload.Id);
                if (download) await manager.DownloadAndInstallAsync(entry.Id, progress, token);
                try { await Verify(manager.GetInstallDirectory(entry), payload.Files, token); }
                catch (IOException) when (download) { await manager.DownloadAndInstallAsync(entry.Id, progress, token, forceReinstall: true); await Verify(manager.GetInstallDirectory(entry), payload.Files, token); }
            }
        }
    }
    internal static Task VerifyCudaAsync(ComponentManager manager, CancellationToken token) {
        var payload = Payloads.Single(p => p.Role == "cuda");
        return Verify(manager.GetInstallDirectory(Entries.Single(e => e.Id == payload.Id)), payload.Files, token);
    }
    private static Task Verify(string root, IEnumerable<PinnedTreeFile> files, CancellationToken token) =>
        PinnedFileTreeVerifier.VerifyAsync(root, files.ToArray(), token,
            pythonCacheQuarantineDirectory: Path.Combine(AppDataPaths.BaseDirectory, "Music", "StemsCacheQuarantine"));
}
