using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.VideoPreparation;

namespace AIHub.Services.SongClip;

internal sealed record H3MusicEnvironment(VideoH3Environment H3, string MotionSource,
    IReadOnlyList<ManagedModelArtifactCard> Cards);

/// <summary>Shares the already pinned H3 bundle; adds only the upstream music node pack.</summary>
internal static class H3MusicComponents
{
    internal const string SourceId = "song-clip-h3-motion-source";
    internal const string SourceRevision = "361624fb406b63eb6694442eac6c895fc1533a70";
    internal static VideoH3Catalog.Part[] Parts { get; } = Read<VideoH3Catalog.Part[]>("AIHub.SongClip.MotionComponents");
    internal static PinnedTreeFile[] SourceTree { get; } = Read<PinnedTreeFile[]>("AIHub.SongClip.MotionTree");
    internal static long TotalBytes => VideoH3Catalog.Parts.Sum(p => p.Files.Sum(f => f.SizeBytes))
        + VideoH3RuntimeCatalog.Data.Files.Sum(f => f.SizeBytes) + Parts.Sum(p => p.Files.Sum(f => f.SizeBytes));

    private static T Read<T>(string resource)
    {
        using var source = typeof(H3MusicComponents).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidDataException("Missing pinned H3 music manifest.");
        return JsonSerializer.Deserialize<T>(source) ?? throw new InvalidDataException("Invalid H3 music manifest.");
    }

    internal static IReadOnlyList<ManagedModelArtifactCard> Cards(string root)
    {
        if (!Path.IsPathFullyQualified(root)) throw new InvalidDataException("Model storage must be selected.");
        var extra = Parts.Select(p => new ManagedModelArtifactCard
        {
            ModelArtifactId = p.Id, Family = "H3 Motion Context Music", DisplayName = p.Name,
            Role = ManagedModelRoles.Tool, Provider = "ComfyUI", RepositoryId = p.Repository,
            Revision = p.Revision, License = p.License, SourcePage = "https://github.com/" + p.Repository + "/tree/" + p.Revision,
            IsManaged = true, IsPinned = true, CanRemoveFiles = false,
            Origin = ManagedModelOrigins.PredefinedScenario, ModelsRoot = root,
            InstallDirectory = Path.Combine(root, "Video", "SongClip", p.Id, p.Revision), Files = p.Files.ToList(),
            Consumers = [new() { Id = "song_clip", DisplayName = "H3 Motion Context Music", Kind = "scenario" }]
        });
        return VideoH3Catalog.Cards(root).Append(VideoH3RuntimeCatalog.Card(root)).Concat(extra).ToArray();
    }

    internal static async Task<H3MusicEnvironment> PrepareAsync(string root, int connections,
        CancellationToken token, Action<string> log)
    {
        using var audit = ComponentAudit.CaptureOperation();
        var cards = Cards(root);
        await ComponentLicenseGate.EnsureAsync(cards.Select(c => c.ModelArtifactId).ToArray(), token);
        var card = cards.Single(c => c.ModelArtifactId == SourceId);
        var store = new ManagedModelLibraryStore(); store.Upsert(card);
        using var acquisition = new ManagedModelAcquisitionService(store) { MaximumParallelConnections = connections };
        if ((await acquisition.VerifyAsync(card.ModelArtifactId, null, token)).Status != ManagedModelStatuses.Installed)
            throw new IOException("SongClip.DownloadRequired");
        await RuntimeFileAudit.VerifyAsync("SongClip.H3Motion.Source", card.InstallDirectory, card.Revision,
            SourceTree, token, log);
        var source = await PrepareSourceAsync(card, token, log);
        // Missing music nodes are an acquisition prerequisite, so reject them
        // before auditing the much larger shared H3 runtime and model bundle.
        VideoH3Environment h3;
        try { h3 = await VideoH3Runtime.PrepareAsync(root, connections, token, log, requireReliableGpuProbe: true); }
        catch (ManagedPythonGpuProbeFailedException error) { throw new IOException("SongClip.GpuProbeFailed", error); }
        return new(h3, source, cards);
    }

    private static async Task<string> PrepareSourceAsync(ManagedModelArtifactCard card, CancellationToken token, Action<string> log)
    {
        var target = Path.Combine(AppDataPaths.BaseDirectory, "SongClip", "Runtime", "h3-motion", card.Revision);
        Lopata.Updates.SafeUpdatePath.RejectLinks(target);
        if (!Directory.Exists(target))
        {
            var staging = target + "." + Guid.NewGuid().ToString("N") + ".installing";
            Directory.CreateDirectory(staging);
            try
            {
                foreach (var file in card.Files)
                {
                    token.ThrowIfCancellationRequested();
                    var source = RuntimeFileAudit.Contained(card.InstallDirectory, file.RelativePath);
                    var destination = RuntimeFileAudit.Contained(staging, file.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(source, destination);
                }
                await PinnedFileTreeVerifier.VerifyAsync(staging, SourceTree, token);
                token.ThrowIfCancellationRequested(); Directory.Move(staging, target); ComponentAudit.Invalidate(target);
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        }
        await PinnedFileTreeVerifier.VerifyAsync(target, SourceTree, token,
            pythonCacheQuarantineDirectory: target + ".bytecode",
            cacheQuarantined: _ => log("[Audit] Quarantined unverified H3 music Python bytecode."));
        return target;
    }
}
