using System.IO;
using AIHub.Models;

namespace AIHub.Services.MusicLora;

public static class MusicLoraAssets
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Readers = new(StringComparer.OrdinalIgnoreCase);
    public static Func<string, MusicLoraUse[], CancellationToken, Task<bool>>? RequestPreparation { get; set; }
    public static bool Ready(MusicLoraDefinition d, string root)
    {
        var expected = d.Card(root); var saved = new ManagedModelLibraryStore().Load(expected.ModelArtifactId);
        return saved is not null && saved.Revision == d.Revision && saved.InstallDirectory == expected.InstallDirectory
            && expected.Files.All(f => {
                var info = new FileInfo(Path.Combine(expected.InstallDirectory, f.RelativePath));
                var known = saved.Files.SingleOrDefault(x => x.RelativePath == f.RelativePath && x.Sha256 == f.Sha256);
                return info.Exists && info.Length == f.SizeBytes && known is not null && known.VerifiedSizeBytes == info.Length
                    && known.VerifiedLastWriteTimeUtc == info.LastWriteTimeUtc;
            });
    }
    public static async Task<bool> PrepareAsync(string root, IReadOnlyList<MusicLoraUse> uses, bool download,
        IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token)
    {
        // One acknowledgement for the whole action, before registration or any download.
        if (download) {
            var ids = uses.Select(u => {
                var definition = MusicLoraCatalog.Find(u.Id);
                if (definition.Revision != u.Revision) throw new InvalidDataException("Music.Lora.Incompatible");
                return definition.LicenseId;
            }).Distinct().ToArray();
            if (ids.Length > 0) await ComponentLicenseGate.EnsureAsync(ids, token);
        }
        await Gate.WaitAsync(token);
        try {
            var store = new ManagedModelLibraryStore(); using var service = new ManagedModelAcquisitionService(store) {
                MaximumParallelConnections = new AppSettingsStore().LoadOrCreate().ModelDownloads?.MaximumParallelConnections ?? 0 };
            foreach (var u in uses) {
                var d = MusicLoraCatalog.Find(u.Id); if (d.Revision != u.Revision) throw new InvalidDataException("Music.Lora.Incompatible");
                var card = d.Card(root);
                if (download && Readers.GetValueOrDefault(card.InstallDirectory) > 0) throw new InvalidOperationException("Music.Lora.InUse");
                store.Upsert(card);
                if (download) await service.DownloadAsync(card.ModelArtifactId, progress, token);
                else await service.VerifyAsync(card.ModelArtifactId, progress, token);
            }
            return uses.All(u => Ready(MusicLoraCatalog.Find(u.Id), root));
        } finally { Gate.Release(); }
    }
    public static async Task EnsureAsync(string root, MusicLoraUse[] uses, string variation, CancellationToken token)
    {
        MusicLoraCatalog.Validate(uses, variation); if (uses.Length == 0) return;
        await ComponentLicenseGate.EnsureAsync(uses.Select(u => MusicLoraCatalog.Find(u.Id).LicenseId).ToArray(), token);
        if (uses.All(u => Ready(MusicLoraCatalog.Find(u.Id), root))) return;
        if (RequestPreparation is null || !await RequestPreparation(root, uses, token)) throw new InvalidDataException("Music.Lora.Missing");
        if (!uses.All(u => Ready(MusicLoraCatalog.Find(u.Id), root))) throw new InvalidDataException("Music.Lora.Missing");
    }
    public static async Task RemoveAsync(MusicLoraDefinition definition, string root, CancellationToken token, ManagedModelLibraryStore? library = null)
    {
        await Gate.WaitAsync(token);
        try {
            var card = definition.Card(root); var expected = Path.GetFullPath(Path.Combine(root, "MusicLoRA")) + Path.DirectorySeparatorChar;
            var directory = Path.GetFullPath(card.InstallDirectory);
            if (!directory.StartsWith(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Music.Lora.Invalid");
            if (Readers.GetValueOrDefault(directory) > 0) throw new InvalidOperationException("Music.Lora.InUse");
            Lopata.Updates.SafeUpdatePath.RejectLinks(directory);
            if (Directory.Exists(directory)) {
                var files = card.Files.Select(f => Path.Combine(directory, f.RelativePath)).ToArray();
                foreach (var file in files) Lopata.Updates.SafeUpdatePath.RejectLinks(file);
                foreach (var file in files) if (File.Exists(file)) File.Delete(file);
            }
            card.Status = ManagedModelStatuses.FilesRemoved; (library ?? new ManagedModelLibraryStore()).Upsert(card);
        } finally { Gate.Release(); }
    }
    public static async Task<IDisposable> AcquireAsync(string root, MusicLoraUse[] uses, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try {
            if (uses.Any(u => !Ready(MusicLoraCatalog.Find(u.Id), root))) throw new InvalidDataException("Music.Lora.Missing");
            var paths = uses.Select(u => MusicLoraCatalog.Find(u.Id).Card(root).InstallDirectory).ToArray();
            foreach (var path in paths) Readers.AddOrUpdate(path, 1, (_, n) => n + 1);
            return new ReadLease(paths);
        } finally { Gate.Release(); }
    }
    private sealed class ReadLease(string[] paths) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0)
            foreach (var path in paths) Readers.AddOrUpdate(path, 0, (_, n) => Math.Max(0, n - 1)); }
    }
}
