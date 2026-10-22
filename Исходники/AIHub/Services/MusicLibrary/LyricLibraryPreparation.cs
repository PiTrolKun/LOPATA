using System.IO;
using AIHub.Models;
using System.Collections.Concurrent;

namespace AIHub.Services.MusicLibrary;

/// <summary>Shared readiness for every consumer. Only successful verification establishes a reusable stamp.</summary>
public static class LyricLibraryPreparation
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _verified;
    private static readonly ConcurrentDictionary<string, byte> BrokenRuntimes = new();
    public static Func<string, CancellationToken, Task<bool>>? RequestPreparation { get; set; }
    public static string ModelsRoot => new StorageSettingsStore().LoadOrCreate().Models.Locations.FirstOrDefault()?.Path
        ?? AppDataPaths.ComponentModelsDirectory;
    private static ManagedModelArtifactCard Definition(string root) => RagBookModels.Cards(root).Single(c => c.ModelArtifactId == RagBookModels.EmbeddingId);
    private static string? Stamp(string root)
    {
        var definition = Definition(root); var card = new ManagedModelLibraryStore().Load(definition.ModelArtifactId);
        if (!BrokenRuntimes.IsEmpty || !ManagedPythonRuntime.HasCpu || card is null || card.Status != ManagedModelStatuses.Installed || card.InstallDirectory != definition.InstallDirectory || card.Revision != definition.Revision) return null;
        var full = ComponentAudit.FullRequired;
        var parts = new List<string> { Path.GetFullPath(root), ManagedPythonRuntime.CpuDirectory, full ? "full" : "presence" };
        foreach (var file in card.Files)
        {
            var info = new FileInfo(Path.Combine(card.InstallDirectory, file.RelativePath));
            if (!info.Exists || (full && (info.Length != file.SizeBytes || file.VerifiedSizeBytes != info.Length
                || file.VerifiedLastWriteTimeUtc != info.LastWriteTimeUtc))) return null;
            parts.Add(full ? info.FullName + ":" + info.Length + ":" + info.LastWriteTimeUtc.Ticks : info.FullName);
        }
        var python = new FileInfo(Path.Combine(ManagedPythonRuntime.CpuDirectory, "python.exe"));
        if (!python.Exists) return null;
        parts.Add(full ? python.Length + ":" + python.LastWriteTimeUtc.Ticks : python.FullName);
        return LyricEntry.Hash(string.Join(";", parts));
    }
    public static bool IsReady(string root) => _verified is not null && _verified == Stamp(root);
    public static void Invalidate() => _verified = null;
    internal static void InvalidateRuntime(string flavor)
    {
        var id = new[] { HardwareRuntimeCatalog.PythonCpuId, HardwareRuntimeCatalog.PythonCudaId,
            HardwareRuntimeCatalog.PythonCuda128Id, HardwareRuntimeCatalog.PythonXpuId, HardwareRuntimeCatalog.PythonRocmId }
            .FirstOrDefault(id => HardwareRuntimeCatalog.PythonProfile(id).Flavor() == flavor);
        if (id is not null) BrokenRuntimes[id] = 0;
        Invalidate();
    }
    public static async Task EnsureAsync(string root, CancellationToken token)
    {
        using var auditOperation = ComponentAudit.CaptureOperation();
        if (IsReady(root) && await VerifyCachedAsync(root, token)) return;
        await Gate.WaitAsync(token);
        try
        {
            if (IsReady(root) && await VerifyCachedAsync(root, token)) return;
            if (RequestPreparation is null || !await RequestPreparation(root, token)) throw new OperationCanceledException(token);
            if (!IsReady(root)) throw new InvalidDataException("Music.Library.NotReady");
        }
        finally { Gate.Release(); }
    }
    private static async Task<bool> VerifyCachedAsync(string root, CancellationToken token)
    {
        try
        {
            var store = new ManagedModelLibraryStore();
            using var acquisition = new ManagedModelAcquisitionService(store);
            var card = await acquisition.VerifyAsync(Definition(root).ModelArtifactId, null, token);
            if (card.Status != ManagedModelStatuses.Installed) { Invalidate(); return false; }
            await PythonRuntimeBundleVerifier.VerifyCpuAsync(ManagedPythonRuntime.CpuDirectory, token);
            _verified = Stamp(root);
            return _verified is not null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Invalidate(); return false; }
    }

    public static async Task<bool> PrepareAsync(string root, bool download, IProgress<string> progress, CancellationToken token)
    {
        using var auditOperation = ComponentAudit.CaptureOperation();
        using var auditLog = ComponentAudit.CaptureLog(progress.Report);
        Invalidate();
        using var preparation = new RagBookModelPreparation(root, new AppSettingsStore().LoadOrCreate().ModelDownloads?.MaximumParallelConnections ?? 0);
        progress.Report("Giga-Embeddings 3B");
        var model = await preparation.PrepareAsync(RagBookModels.EmbeddingId, download,
            new InlineProgress<ManagedModelDownloadProgress>(p => progress.Report($"{p.Stage} · {p.DownloadedBytes / 1048576d:F0}/{p.TotalBytes / 1048576d:F0} MB")), token);
        var runtimeReady = true;
        foreach (var id in BrokenRuntimes.Keys.Prepend(HardwareRuntimeCatalog.PythonCpuId).Distinct())
        {
            var ready = false; var manager = new ComponentManager(); var entry = ComponentCatalog.Find(id)!;
            var directory = manager.GetInstallDirectory(entry); var profile = HardwareRuntimeCatalog.PythonProfile(id);
            progress.Report("Python · " + profile.Flavor());
            try { await PythonRuntimeBundleVerifier.VerifyAsync(profile, directory, token); ready = true; }
            catch (Exception error) when (error is IOException or InvalidDataException) { progress.Report(error.Message); }
            if (!ready && download)
            {
                await ComponentLicenseGate.EnsureAsync(entry.LicenseIds, token);
                await manager.DownloadAndInstallAsync(id, null, token, forceReinstall: true);
                await PythonRuntimeBundleVerifier.VerifyAsync(profile, directory, token); ready = true;
            }
            if (ready) BrokenRuntimes.TryRemove(id, out _); else runtimeReady = false;
        }
        token.ThrowIfCancellationRequested();
        if (!runtimeReady || model.Status != ManagedModelStatuses.Installed)
        { progress.Report("Music.Library.NotReady · runtime=" + runtimeReady + "; model=" + model.Status); return false; }
        _verified = Stamp(root);
        if (_verified is null) progress.Report("Music.Library.NotReady · verification stamp changed");
        return _verified is not null;
    }
    public static string ModelDirectory(string root) => Definition(root).InstallDirectory;
}
