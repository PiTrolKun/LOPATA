using System.ComponentModel;
using System.IO;
using AIHub.Models;

namespace AIHub.Services.LivingArcade;

public sealed record LivingArcadeModelPaths(string Main, string Artist);
public sealed record LivingArcadePreparationProgress(string StageKey, long DownloadedBytes = 0,
    long TotalBytes = 0, double BytesPerSecond = 0);
public sealed record LivingArcadeReadiness(LivingArcadeModelPaths? Paths, bool ModelsReady,
    bool RuntimeReady, long RequiredBytes, long RemainingBytes, string StatusKey, bool CanDownload)
{
    public bool Ready => ModelsReady && RuntimeReady && Paths is not null;
}

/// <summary>Pinned author-provided GGUFs; verification and use share managed audit identities.</summary>
public static class LivingArcadeModels
{
    public const string MainArtifactId = "model-qwen25-coder7b-q4km";
    public const string ArtistArtifactId = "model-qwen25-coder3b-q4km";
    public const string MainFile = "qwen2.5-coder-7b-instruct-q4_k_m.gguf";
    public const string ArtistFile = "qwen2.5-coder-3b-instruct-q4_k_m.gguf";
    public const long MainBytes = 4_683_073_536;
    public const long ArtistBytes = 2_104_932_800;
    public const string MainRevision = "13fb94bfda8c8cf22497dc57b78f391a9acb426a";
    public const string ArtistRevision = "f74adce6aa16316c625447af059dbebe4983757c";

    public static string? ModelsRoot(StorageSettings storage)
    {
        var roots = storage.Models.Locations.Select(l => l.Path?.Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p)).Cast<string>().ToArray();
        return roots.FirstOrDefault(root => CreateCards(root).Any(card => File.Exists(PathOf(card)))) ?? roots.FirstOrDefault();
    }

    public static IReadOnlyList<ManagedModelArtifactCard> CreateCards(string root) =>
        [ManagedModelCatalog.CreateLivingArcadeMain(root), ManagedModelCatalog.CreateLivingArcadeArtist(root)];
    private static string PathOf(ManagedModelArtifactCard card) => Path.Combine(card.InstallDirectory, card.Files[0].RelativePath);

    public static async Task<LivingArcadeReadiness> PrepareAsync(StorageSettings storage, int connections,
        bool download, IProgress<LivingArcadePreparationProgress> progress, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, ApplicationBackgroundOperations.ExitToken);
        using var auxiliary = ApplicationBackgroundOperations.Current?.BeginAuxiliary();
        using var audit = ComponentAudit.CaptureOperation();
        var ct = lifetime.Token;
        var root = ModelsRoot(storage);
        var total = MainBytes + ArtistBytes;
        if (root is null || !Directory.Exists(root)) return new(null, false, false, total, total, "Arcade.Preparation.StorageRequired", false);
        var cards = CreateCards(root);
        await LivingArcadeLibrary.Load().VerifyAsync(ct);
        var store = new ManagedModelLibraryStore();
        foreach (var card in cards) store.Upsert(card);
        using var acquisition = new ManagedModelAcquisitionService(store) { MaximumParallelConnections = connections };
        progress.Report(new("Arcade.Preparation.Checking"));
        var missing = new List<ManagedModelArtifactCard>();
        foreach (var card in cards)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(PathOf(card))) missing.Add(card);
            else if ((await acquisition.VerifyAsync(card.ModelArtifactId, null, ct)).Status != ManagedModelStatuses.Installed) missing.Add(card);
        }
        var remaining = missing.Sum(card => Math.Max(0, card.TotalBytes - SegmentedModelFileDownloader.GetPartialStoredBytes(PathOf(card), card.TotalBytes)));
        var available = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!).AvailableFreeSpace;
        var hasSpace = available >= remaining + 512L * 1024 * 1024;
        var set = EngineSetSelection.ActiveId;
        var runtimeReady = await RuntimeAvailableAsync(set, ct);
        if (download && (missing.Count != 0 || !runtimeReady))
        {
            await ApplicationBackgroundOperations.RetireModelsAsync();
            if (missing.Count != 0 && !hasSpace) return new(null, false, runtimeReady, total, remaining, "Arcade.Preparation.SpaceRequired", false);
            await ComponentLicenseGate.EnsureAsync(cards.Select(card => card.ModelArtifactId).ToArray(), ct);
            foreach (var card in missing)
            {
                var downloaded = await acquisition.DownloadAsync(card.ModelArtifactId,
                    new InlineProgress<ManagedModelDownloadProgress>(p => progress.Report(new("Arcade.Preparation.Downloading",
                        p.DownloadedBytes, p.TotalBytes, p.BytesPerSecond))), ct);
                if (downloaded.Status != ManagedModelStatuses.Installed) throw new IOException("Arcade.Preparation.Failed");
                await ManagedModelRuntimeAudit.VerifyAsync(store, card.ModelArtifactId, ct);
            }
            missing.Clear(); remaining = 0;
            if (!runtimeReady && set == NativeRuntimeReleasePolicy.UpdatedSet)
            {
                progress.Report(new("Arcade.Preparation.Runtime"));
                var engines = EngineSetService.Shared;
                var definition = engines.GetCatalog().Single(s => s.Id == set);
                await ComponentLicenseGate.EnsureAsync(definition.LicenseIds, ct);
                var previous = engines.MaximumParallelConnections;
                try
                {
                    engines.MaximumParallelConnections = connections;
                    using var installation = ComponentAudit.Installation();
                    await engines.DownloadAsync(set, new InlineProgress<ManagedModelDownloadProgress>(p => progress.Report(
                        new("Arcade.Preparation.Runtime", p.DownloadedBytes, p.TotalBytes, p.BytesPerSecond))), ct);
                }
                finally { engines.MaximumParallelConnections = previous; }
                runtimeReady = await RuntimeAvailableAsync(set, ct);
            }
        }
        if (set != EngineSetSelection.ActiveId) throw new InvalidOperationException("EngineSets.Changed");
        ct.ThrowIfCancellationRequested();
        var modelsReady = missing.Count == 0;
        if (modelsReady) await ComponentLicenseGate.EnsureAsync(cards.Select(card => card.ModelArtifactId).ToArray(), ct);
        var paths = modelsReady ? new LivingArcadeModelPaths(PathOf(cards[0]), PathOf(cards[1])) : null;
        return new(paths, modelsReady, runtimeReady, total, remaining,
            !hasSpace && !modelsReady ? "Arcade.Preparation.SpaceRequired" : !modelsReady ? "Arcade.Preparation.Missing"
                : !runtimeReady ? "Arcade.Preparation.EngineRequired" : "Arcade.Preparation.Ready",
            hasSpace && (!modelsReady || !runtimeReady && set == NativeRuntimeReleasePolicy.UpdatedSet));
    }

    internal static async Task VerifyForUseAsync(LivingArcadeModelPaths paths, CancellationToken token)
    {
        var root = ModelsRoot(new StorageSettingsStore().LoadOrCreate()) ?? throw new IOException("Arcade.Preparation.StorageRequired");
        var cards = CreateCards(root);
        var store = new ManagedModelLibraryStore();
        await ComponentLicenseGate.EnsureAsync(cards.Select(card => card.ModelArtifactId).ToArray(), token);
        await LivingArcadeLibrary.Load().VerifyAsync(token);
        foreach (var (card, path) in cards.Zip(new[] { paths.Main, paths.Artist }))
        {
            if (!Path.IsPathFullyQualified(path) || !Path.GetFullPath(path).Equals(Path.GetFullPath(PathOf(card)), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Arcade.Preparation.ModelChanged");
            store.Upsert(card);
            await ManagedModelRuntimeAudit.VerifyAsync(store, card.ModelArtifactId, token);
        }
    }

    private static async Task<bool> RuntimeAvailableAsync(string set, CancellationToken token)
    {
        if (set != NativeRuntimeReleasePolicy.UpdatedSet) return false;
        try
        {
            var selected = await LlamaRuntimeSelector.SelectAsync(0, false, _ => { }, token);
            return selected.Bundle.EngineSetId == set;
        }
        catch (Exception error) when (error is IOException or Win32Exception or TimeoutException) { return false; }
    }

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    { public void Report(T value) => action(value); }
}
