using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using AIHub.Models;

namespace AIHub.Services;

public sealed record LetItOutChatPreparationProgress(string StageKey, long DownloadedBytes = 0,
    long TotalBytes = 0, double BytesPerSecond = 0);

public sealed record LetItOutChatReadiness(string? CoreModelPath, bool CoreReady, bool RuntimeReady,
    long RequiredBytes, long RemainingBytes, string StatusKey, string? RuntimeBackend = null,
    bool CanDownload = false, bool RepairsEngineSet = false)
{
    public bool Ready => CoreReady && RuntimeReady && !string.IsNullOrWhiteSpace(CoreModelPath);
}

/// <summary>Prepares the scenario's Gemma and selected llama engine without loading model weights.</summary>
public static class LetItOutChatPreparation
{
    private static readonly string[] LegacyRuntimeIds = [HardwareRuntimeCatalog.LlamaCpuId, HardwareRuntimeCatalog.LlamaVulkanId];

    public static async Task<LetItOutChatReadiness> PrepareAsync(StorageSettings storage, int connections,
        bool download, IProgress<LetItOutChatPreparationProgress> progress, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, ApplicationBackgroundOperations.ExitToken);
        var ct = lifetime.Token;
        ct.ThrowIfCancellationRequested();
        using var auxiliary = ApplicationBackgroundOperations.Current?.BeginAuxiliary();
        using var audit = ComponentAudit.CaptureOperation();
        using var auditLog = ComponentAudit.CaptureLog(Log);
        var setId = EngineSetSelection.ActiveId;
        progress.Report(new("CheckingCore"));
        var state = await LetItOutModel.CheckAsync(storage, ct);
        var coreReady = state.Availability == CoreModelAvailability.Installed;
        if (coreReady) await ComponentLicenseGate.EnsureAsync(ManagedModelCatalog.LetItOutArtifactId, ct);
        var canPrepareCore = state.HasEnoughSpace && state.Availability is
            CoreModelAvailability.Missing or CoreModelAvailability.Partial or CoreModelAvailability.Invalid;
        progress.Report(new("CheckingRuntime"));
        var runtime = await FindRuntimeAsync(setId, ct);
        if (download && (!coreReady || runtime is null))
        {
            // Installation cannot replace a component while an earlier idle conversation owns it.
            await ApplicationBackgroundOperations.RetireModelsAsync();
            ct.ThrowIfCancellationRequested();
            RequireSameSet(setId);
            if (!coreReady && canPrepareCore)
            {
                await DownloadCoreAsync(state, connections, progress, ct);
                state = await LetItOutModel.CheckAsync(storage, ct);
                coreReady = state.Availability == CoreModelAvailability.Installed;
            }
            if (coreReady && runtime is null && setId == NativeRuntimeReleasePolicy.UpdatedSet)
            {
                await PrepareRuntimeAsync(setId, connections, progress, ct);
                progress.Report(new("CheckingRuntime"));
                runtime = await FindRuntimeAsync(setId, ct);
            }
        }
        ct.ThrowIfCancellationRequested();
        RequireSameSet(setId);
        var runtimeBytes = runtime is null ? RuntimeDownloadBytes(setId) : 0;
        // The shared downloader resumes its partial/segments, never an unverified final GGUF.
        var partialBytes = state.ModelPath is { } path
            ? SegmentedModelFileDownloader.GetPartialStoredBytes(path, LetItOutModel.TotalBytes) : 0;
        var coreBytes = coreReady ? 0 : Math.Max(0, LetItOutModel.TotalBytes - partialBytes);
        var status = state.Availability switch
        {
            CoreModelAvailability.StorageNotConfigured or CoreModelAvailability.ModelsFolderUnavailable => "StorageRequired",
            _ when !coreReady && !state.HasEnoughSpace => "SpaceRequired",
            _ when !coreReady => "Missing",
            _ when setId != NativeRuntimeReleasePolicy.UpdatedSet => "EngineRequired",
            _ when runtime is null => "RuntimeMissing",
            _ => "Ready"
        };
        return new(coreReady && runtime is not null ? state.ModelPath : null, coreReady, runtime is not null,
            LetItOutModel.TotalBytes + runtimeBytes, coreBytes + runtimeBytes, status,
            runtime?.Bundle.Backend, CanDownload: (!coreReady && canPrepareCore) || coreReady && runtime is null && setId == NativeRuntimeReleasePolicy.UpdatedSet,
            RepairsEngineSet: runtime is null && setId == NativeRuntimeReleasePolicy.UpdatedSet);
    }

    internal static async Task DownloadCoreAsync(CoreModelCheckResult state, int connections,
        IProgress<LetItOutChatPreparationProgress> progress, CancellationToken token,
        ManagedModelLibraryStore? store = null, HttpClient? httpClient = null)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(state.ModelsRoot) || string.IsNullOrWhiteSpace(state.ModelPath))
            throw new InvalidOperationException("LetItOut.Preparation.StorageRequired");
        // Recreate the pinned card from current storage; a stale library path must not redirect the download.
        var card = ManagedModelCatalog.CreateLetItOut(state.ModelsRoot);
        var target = Path.GetFullPath(Path.Combine(card.InstallDirectory, LetItOutModel.FileName));
        if (!target.Equals(Path.GetFullPath(state.ModelPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("LetItOut.Preparation.StorageRequired");
        store ??= new ManagedModelLibraryStore();
        store.Upsert(card);
        using var acquisition = new ManagedModelAcquisitionService(store, httpClient)
            { MaximumParallelConnections = connections };
        progress.Report(new("DownloadingCore"));
        // DownloadAsync validates the existing final file and replaces it only after the new partial passes SHA.
        // It also invalidates the shared audit receipt; the caller performs first-use core verification afterwards.
        await acquisition.DownloadAsync(card.ModelArtifactId, new ForwardProgress<ManagedModelDownloadProgress>(p =>
            progress.Report(new(p.Stage.StartsWith("verif", StringComparison.Ordinal) ? "CheckingCore" : "DownloadingCore",
                p.DownloadedBytes, p.TotalBytes, p.BytesPerSecond))), token);
    }

    private static async Task<LlamaRuntimeSelection?> FindRuntimeAsync(string setId, CancellationToken token)
    {
        RequireSameSet(setId);
        if (setId != NativeRuntimeReleasePolicy.UpdatedSet) return null;
        try
        {
            var selected = await LlamaRuntimeSelector.SelectAsync(0, false, Log, token);
            RequireSameSet(setId);
            if (selected.Bundle.EngineSetId != setId) throw new InvalidOperationException("EngineSets.Changed");
            return selected;
        }
        catch (Exception error) when (error is IOException or TimeoutException or Win32Exception)
        {
            OwnedProcessRegistry.Log("preparation_runtime_unavailable", "LetItOutChat", detail: error.GetType().Name);
            return null;
        }
    }

    private static long RuntimeDownloadBytes(string setId) => setId == NativeRuntimeReleasePolicy.UpdatedSet
        ? EngineSetService.Shared.GetCatalog().Single(s => s.Id == setId).DownloadBytes
        : ComponentCatalog.ResolveDependencies(LegacyRuntimeIds).Sum(entry => entry.DownloadSizeBytes);

    private static async Task PrepareRuntimeAsync(string setId, int connections,
        IProgress<LetItOutChatPreparationProgress> progress, CancellationToken token)
    {
        RequireSameSet(setId);
        progress.Report(new("DownloadingRuntime"));
        if (setId == NativeRuntimeReleasePolicy.UpdatedSet)
        {
            var engineSets = EngineSetService.Shared;
            var definition = engineSets.GetCatalog().Single(set => set.Id == setId);
            await ComponentLicenseGate.EnsureAsync(definition.LicenseIds, token);
            var previousConnections = engineSets.MaximumParallelConnections;
            try
            {
                engineSets.MaximumParallelConnections = connections;
                // An explicit repair is an installation, including when the normal audit is off.
                using var installation = ComponentAudit.Installation();
                var clock = Stopwatch.StartNew();
                long firstReportedBytes = -1;
                await engineSets.DownloadAsync(setId, new ForwardProgress<ManagedModelDownloadProgress>(p =>
                {
                    if (firstReportedBytes < 0) { firstReportedBytes = p.DownloadedBytes; clock.Restart(); }
                    var speed = p.BytesPerSecond > 0 ? p.BytesPerSecond
                        : Math.Max(0, p.DownloadedBytes - firstReportedBytes) / Math.Max(.1, clock.Elapsed.TotalSeconds);
                    progress.Report(new("DownloadingRuntime", p.DownloadedBytes, p.TotalBytes, speed));
                }), token);
            }
            finally { engineSets.MaximumParallelConnections = previousConnections; }
        }
        else
        {
            var manager = new ComponentManager();
            foreach (var id in LegacyRuntimeIds)
            {
                RequireSameSet(setId);
                var installed = await manager.DownloadAndInstallAsync(id, new ForwardProgress<ComponentDownloadProgress>(p =>
                    progress.Report(new("DownloadingRuntime", p.DownloadedBytes, p.TotalBytes, p.BytesPerSecond))), token,
                    forceReinstall: true);
                if (!installed.IsAvailable) throw new IOException("LetItOut.Preparation.RuntimeMissing");
            }
        }
        RequireSameSet(setId);
    }

    private static void RequireSameSet(string setId)
    {
        if (EngineSetSelection.ActiveId != setId) throw new InvalidOperationException("EngineSets.Changed");
    }

    private static void Log(string message) => OwnedProcessRegistry.Log("preparation_runtime", "LetItOutChat", detail: message);
    private sealed class ForwardProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
