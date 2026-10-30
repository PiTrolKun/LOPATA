using System.IO;
using AIHub.Models;

namespace AIHub.Services.VideoPreparation;

internal sealed record VideoVaceEnvironment(ManagedPythonSelection Python, string Libraries, string Comfy,
    IReadOnlyList<ManagedModelArtifactCard> Cards);

/// <summary>Reuses the immutable, audited Comfy core/wheel installation, without loading H3 or its custom nodes.</summary>
internal static class VideoVaceRuntime
{
    private static readonly SemaphoreSlim Gate = new(1);
    internal static IReadOnlyList<ManagedModelArtifactCard> Cards(string modelsRoot) =>
        [.. VideoVaceCatalog.Cards(modelsRoot), VideoH3Catalog.Cards(modelsRoot).Single(c => c.ModelArtifactId == "video-h3-source-comfy"),
            VideoH3RuntimeCatalog.Card(modelsRoot)];

    internal static async Task<VideoVaceEnvironment> PrepareAsync(string modelsRoot, int connections, CancellationToken token,
        Action<string> log, ManagedModelLibraryStore? library = null)
    {
        using var operation = ComponentAudit.CaptureOperation(); using var auditLog = ComponentAudit.CaptureLog(log);
        var cards = Cards(modelsRoot); await ComponentLicenseGate.EnsureAsync(cards.Select(c => c.ModelArtifactId).ToArray(), token);
        var store = library ?? new ManagedModelLibraryStore();
        using var acquisition = new ManagedModelAcquisitionService(store) { MaximumParallelConnections = connections };
        foreach (var card in cards)
        {
            store.Upsert(card); log("[Prepare] " + card.DisplayName);
            if ((await acquisition.VerifyAsync(card.ModelArtifactId, null, token)).Status != ManagedModelStatuses.Installed)
                throw new IOException("VideoPrep.VaceRuntimeRequired");
        }
        var weights = cards.Single(c => c.ModelArtifactId == VideoVaceCatalog.ComponentId);
        await RuntimeFileAudit.VerifyAsync("Video.VACE.Launch." + weights.ModelArtifactId, weights.InstallDirectory, weights.Revision,
            weights.Files.Select(f => new PinnedTreeFile(f.RelativePath, f.SizeBytes, f.Sha256)).ToArray(), token, log);
        var python = await ManagedPythonRuntime.ResolveAsync("auto", token, log: log);
        var root = Path.Combine(AppDataPaths.BaseDirectory, "VideoPreparation", "H3", VideoH3RuntimeCatalog.Data.Revision);
        var libraries = cards.Single(c => c.ModelArtifactId == VideoH3RuntimeCatalog.Id);
        var core = cards.Single(c => c.ModelArtifactId == "video-h3-source-comfy");
        await Gate.WaitAsync(token);
        try
        {
            await Tree(libraries, "libraries", VideoH3RuntimeCatalog.Data.Library, true);
            await Tree(core, core.ModelArtifactId, VideoH3RuntimeCatalog.Data.Trees[core.ModelArtifactId], false);
        }
        finally { Gate.Release(); }
        return new(python, Path.Combine(root, "libraries", libraries.Revision, "Lib", "site-packages"),
            Path.Combine(root, core.ModelArtifactId, core.Revision, VideoH3RuntimeCatalog.Data.Folders[core.ModelArtifactId]), cards);

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
                    foreach (var file in card.Files)
                        await PythonWheelExtractor.ExtractAsync(new(card.ModelArtifactId, card.Revision, file.RelativePath,
                            file.SizeBytes, file.Sha256, new(file.SourceUrl)), Path.Combine(card.InstallDirectory, file.RelativePath), staging, token, wheels);
                    await PinnedFileTreeVerifier.VerifyAsync(staging, inventory, token);
                    token.ThrowIfCancellationRequested(); Directory.Move(staging, target); ComponentAudit.Invalidate(target);
                }
                finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            }
            // Identical directory + inventory means the same audit identity as H3; no scenario-specific duplicate receipt.
            await PinnedFileTreeVerifier.VerifyAsync(target, inventory, token,
                pythonCacheQuarantineDirectory: Path.Combine(root, "BytecodeQuarantine", name, card.Revision),
                cacheQuarantined: _ => log("[Audit] Quarantined unverified Python bytecode; pinned source is unchanged."));
        }
    }
}
