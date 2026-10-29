using System.IO;
using AIHub.Models;

namespace AIHub.Services.VideoPreparation;

internal sealed record VideoH3Environment(ManagedPythonSelection Python, string Libraries, string Comfy, string Timeline,
    string Upscaler, string Spectrum, string Turbo, IReadOnlyList<ManagedModelArtifactCard> Cards);

/// <summary>Isolated, immutable source/wheel trees. Shared runtimes and downloaded originals are never edited.</summary>
internal static class VideoH3Runtime
{
    private static readonly SemaphoreSlim Gate = new(1);
    internal static async Task<VideoH3Environment> PrepareAsync(string modelsRoot, int connections, CancellationToken token, Action<string> log, ManagedModelLibraryStore? library = null)
    {
        using var operation = ComponentAudit.CaptureOperation(); using var auditLog = ComponentAudit.CaptureLog(log);
        var cards = VideoH3Catalog.Cards(modelsRoot).ToList(); var libraries = VideoH3RuntimeCatalog.Card(modelsRoot); cards.Add(libraries);
        await ComponentLicenseGate.EnsureAsync(cards.Select(c => c.ModelArtifactId).ToArray(), token);
        var store = library ?? new ManagedModelLibraryStore();
        using var downloads = new ManagedModelAcquisitionService(store) { MaximumParallelConnections = connections };
        foreach (var c in cards)
        {
            store.Upsert(c); log("[Prepare] " + c.DisplayName);
            var checkedCard = await downloads.VerifyAsync(c.ModelArtifactId, null, token);
            if (checkedCard.Status != ManagedModelStatuses.Installed && c.ModelArtifactId == VideoH3RuntimeCatalog.Id)
                checkedCard = await downloads.DownloadAsync(c.ModelArtifactId, new InlineProgress(p => log($"[Download] {p.FileName} {p.DownloadedBytes}/{p.TotalBytes}")), token);
            if (checkedCard.Status != ManagedModelStatuses.Installed) throw new IOException("VideoPrep.H3FilesRequired");
        }
        // Acquisition does not stand in for the first use audit of a new installation.
        foreach (var c in cards.Where(c => c.ModelArtifactId.EndsWith("-models", StringComparison.Ordinal) || c.ModelArtifactId.EndsWith("-model", StringComparison.Ordinal)))
            await RuntimeFileAudit.VerifyAsync("Video.H3.Launch." + c.ModelArtifactId, c.InstallDirectory, c.Revision,
                c.Files.Select(f => new PinnedTreeFile(f.RelativePath, f.SizeBytes, f.Sha256)).ToArray(), token, log);
        var python = await ManagedPythonRuntime.ResolveAsync("auto", token, log: log);
        var root = Path.Combine(AppDataPaths.BaseDirectory, "VideoPreparation", "H3", VideoH3RuntimeCatalog.Data.Revision);
        await Gate.WaitAsync(token);
        try
        {
            await Tree(libraries, "libraries", VideoH3RuntimeCatalog.Data.Library, true);
            foreach (var c in cards.Where(c => VideoH3RuntimeCatalog.Data.Trees.ContainsKey(c.ModelArtifactId)))
                await Tree(c, c.ModelArtifactId, VideoH3RuntimeCatalog.Data.Trees[c.ModelArtifactId], false);
        }
        finally { Gate.Release(); }
        return new(python, Path.Combine(root, "libraries", libraries.Revision, "Lib", "site-packages"), Source("comfy"), Source("timeline"), Source("upscaler"), Source("spectrum"), Source("turbo"), cards);

        string Source(string name) { var id = "video-h3-source-" + name; return Path.Combine(root, id, cards.Single(c => c.ModelArtifactId == id).Revision, VideoH3RuntimeCatalog.Data.Folders[id]); }
        async Task Tree(ManagedModelArtifactCard card, string name, PinnedTreeFile[] inventory, bool wheels)
        {
            var target = Path.Combine(root, name, card.Revision);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!Directory.Exists(target))
            {
                var staging = target + "." + Guid.NewGuid().ToString("N") + ".installing";
                Directory.CreateDirectory(staging);
                try
                {
                    foreach (var f in card.Files)
                    {
                        token.ThrowIfCancellationRequested();
                        var input = Path.Combine(card.InstallDirectory, f.RelativePath);
                        if (wheels || f.RelativePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                            await PythonWheelExtractor.ExtractAsync(new(card.ModelArtifactId, card.Revision, f.RelativePath, f.SizeBytes, f.Sha256, new(f.SourceUrl)), input, staging, token, wheels);
                        else
                        {
                            var file = RuntimeFileAudit.Contained(staging, f.RelativePath); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.Copy(input, file);
                        }
                    }
                    await PinnedFileTreeVerifier.VerifyAsync(staging, inventory, token);
                    token.ThrowIfCancellationRequested(); Directory.Move(staging, target); ComponentAudit.Invalidate(target);
                }
                finally
                {
                    // The only recursive cleanup target is this freshly generated staging sibling.
                    if (Directory.Exists(staging)) Directory.Delete(staging, true);
                }
            }
            await PinnedFileTreeVerifier.VerifyAsync(target, inventory, token,
                pythonCacheQuarantineDirectory: Path.Combine(root, "BytecodeQuarantine", name, card.Revision),
                cacheQuarantined: _ => log("[Audit] Quarantined unverified Python bytecode; pinned sources remain fully checked."));
        }
    }
    private sealed class InlineProgress(Action<ManagedModelDownloadProgress> action) : IProgress<ManagedModelDownloadProgress>
    { public void Report(ManagedModelDownloadProgress value) => action(value); }
}
