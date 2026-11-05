using System.IO;
using AIHub.Models;

namespace AIHub.Services.ImageLora;

internal static class ImageLoraAssets
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Readers = new(StringComparer.OrdinalIgnoreCase);
    internal static bool Ready(ImageLoraDefinition d, string root)
    {
        if (!d.Supported || string.IsNullOrWhiteSpace(root)) return false;
        var card = d.Card(root); var saved = new ManagedModelLibraryStore().Load(card.ModelArtifactId);
        return saved is not null && saved.Status == ManagedModelStatuses.Installed && saved.Revision == d.Revision
            && ManagedModelPathIdentity.SameDirectory(saved.InstallDirectory, card.InstallDirectory)
            && card.Files.All(f => {
                var info = new FileInfo(RuntimeFileAudit.Contained(card.InstallDirectory, f.RelativePath));
                var known = saved.Files.SingleOrDefault(s => s.RelativePath == f.RelativePath && s.Sha256 == f.Sha256);
                return info.Exists && (!ComponentAudit.FullRequired || known is not null && info.Length == f.SizeBytes
                    && known.VerifiedSizeBytes == info.Length && known.VerifiedLastWriteTimeUtc == info.LastWriteTimeUtc);
            });
    }
    internal static async Task PrepareAsync(string root, ImageLoraDefinition[] definitions, bool download,
        IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token)
    {
        using var install = download ? ComponentAudit.Installation() : null;
        using var operation = ComponentAudit.CaptureOperation();
        foreach (var d in definitions) { ImageLoraCatalog.Validate(d); if (!d.Supported) throw new InvalidDataException("Generation.Lora.Incompatible"); }
        await ComponentLicenseGate.EnsureAsync(definitions.Select(d => d.LicenseId).Distinct().ToArray(), token);
        await Gate.WaitAsync(token);
        try {
            var store = new ManagedModelLibraryStore(); using var service = new ManagedModelAcquisitionService(store) {
                MaximumParallelConnections = new AppSettingsStore().LoadOrCreate().ModelDownloads?.MaximumParallelConnections ?? 0 };
            foreach (var d in definitions) {
                var card = d.Card(root);
                if (download && Readers.GetValueOrDefault(card.InstallDirectory) > 0) throw new InvalidOperationException("Generation.Lora.InUse");
                store.Upsert(card);
                var state = await service.VerifyAsync(card.ModelArtifactId, progress, token);
                if (state.Status != ManagedModelStatuses.Installed && download) state = await service.DownloadAsync(card.ModelArtifactId, progress, token);
                if (state.Status != ManagedModelStatuses.Installed) throw new InvalidDataException("Generation.Lora.Missing");
                var file = RuntimeFileAudit.Contained(card.InstallDirectory, "adapter.safetensors");
                await ComponentAudit.RunAsync(d.LicenseId + ".layout", card.InstallDirectory, d.Revision,
                    ct => { ct.ThrowIfCancellationRequested(); if (!File.Exists(file)) throw new FileNotFoundException("Generation.Lora.Missing"); return Task.CompletedTask; },
                    ct => Task.Run(() => {
                    using var stream = File.OpenRead(file); Span<byte> prefix = stackalloc byte[8]; stream.ReadExactly(prefix);
                    var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(prefix);
                    if (length is < 2 or > 2000000 || length + 8 >= (ulong)stream.Length) throw new InvalidDataException("Generation.Lora.Invalid");
                    var bytes = new byte[(int)length]; stream.ReadExactly(bytes);
                    using var header = System.Text.Json.JsonDocument.Parse(bytes);
                    ImageLoraTensorRules.ValidateStorage(header.RootElement, stream.Length - (long)length - 8);
                    ImageLoraTensorRules.Validate(header.RootElement, d.ModelId);
                }, ct), token);
                await ManagedModelRuntimeAudit.VerifyAsync(store, card.ModelArtifactId, token);
            }
        } catch (Exception e) { ComponentAuditFailures.Report(e); throw; }
        finally { Gate.Release(); }
    }
    internal static async Task<IDisposable> AcquireAsync(string root, ImageLoraUse[] uses, string model, CancellationToken token)
    {
        ImageLoraCatalog.ValidateUses(uses, model);
        if (uses.Length == 0) return new Lease([]);
        var definitions = uses.Select(u => ImageLoraCatalog.Find(u.Id)).ToArray();
        await PrepareAsync(root, definitions, false, null, token);
        await Gate.WaitAsync(token);
        try {
            if (definitions.Any(d => !Ready(d, root))) throw new InvalidDataException("Generation.Lora.Missing");
            var paths = definitions.Select(d => d.Card(root).InstallDirectory).ToArray();
            foreach (var path in paths) Readers.AddOrUpdate(path, 1, (_, n) => n + 1);
            return new Lease(paths);
        } finally { Gate.Release(); }
    }
    internal static async Task RemoveAsync(ImageLoraDefinition d, string root, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try {
            var card = d.Card(root); var store = new ManagedModelLibraryStore();
            if (Readers.GetValueOrDefault(card.InstallDirectory) > 0) throw new InvalidOperationException("Generation.Lora.InUse");
            store.Upsert(card);
            new ManagedModelRemovalService(store, new DelegateModelUsageGuard(_ => Readers.GetValueOrDefault(card.InstallDirectory) > 0))
                .RemoveScenarioFiles(card.ModelArtifactId, card.Files.Select(f => f.RelativePath).ToArray(), true);
            ComponentAudit.Invalidate(card.InstallDirectory);
        } finally { Gate.Release(); }
    }
    private sealed class Lease(string[] paths) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            foreach (var path in paths) Readers.AddOrUpdate(path, 0, (_, n) => Math.Max(0, n-1)); }
    }
}
