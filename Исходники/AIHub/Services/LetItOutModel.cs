using System.IO;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>The scenario's pinned model. Preparation and execution share the managed audit identity.</summary>
internal static class LetItOutModel
{
    internal const string DisplayName = "Gemma 4 26B A4B Instruct QAT · UD-Q4_K_XL";
    internal const string FileName = "gemma-4-26B-A4B-it-qat-UD-Q4_K_XL.gguf";
    internal const long TotalBytes = 14_249_047_104;

    internal static string? ModelsRoot(StorageSettings settings)
    {
        var roots = settings.Models.Locations.Select(l => l.Path?.Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p)).Cast<string>().ToArray();
        return roots.FirstOrDefault(p => File.Exists(ModelPath(p))) ?? roots.FirstOrDefault();
    }

    internal static string ModelPath(string root) => Path.Combine(ManagedModelCatalog.CreateLetItOut(root).InstallDirectory, FileName);

    internal static async Task<CoreModelCheckResult> CheckAsync(StorageSettings settings, CancellationToken token)
    {
        var root = ModelsRoot(settings);
        var state = new CoreModelCheckResult { RequiredBytes = TotalBytes,
            Availability = CoreModelAvailability.StorageNotConfigured };
        if (root is null) return state;
        state.ModelsRoot = root; state.ModelPath = ModelPath(root);
        state.ModelDirectory = Path.GetDirectoryName(state.ModelPath);
        state.Availability = CoreModelAvailability.ModelsFolderUnavailable;
        if (!Directory.Exists(root)) return state;
        var remaining = Math.Max(0, TotalBytes - SegmentedModelFileDownloader.GetPartialStoredBytes(state.ModelPath, TotalBytes));
        try { state.FreeBytes = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!).AvailableFreeSpace; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { state.FreeBytes = 0; }
        state.HasEnoughSpace = state.FreeBytes >= remaining + 512L * 1024 * 1024;
        state.Availability = CoreModelAvailability.Missing;
        if (!File.Exists(state.ModelPath)) return state;
        var store = Register(root);
        using var acquisition = new ManagedModelAcquisitionService(store);
        var card = await acquisition.VerifyAsync(ManagedModelCatalog.LetItOutArtifactId, null, token);
        state.Availability = card.Status == ManagedModelStatuses.Installed
            ? CoreModelAvailability.Installed : CoreModelAvailability.Invalid;
        return state;
    }

    internal static ManagedModelLibraryStore Register(string root)
    {
        var store = new ManagedModelLibraryStore();
        store.Upsert(ManagedModelCatalog.CreateLetItOut(root));
        return store;
    }

    internal static async Task VerifyForUseAsync(string path, CancellationToken token)
    {
        var root = ModelsRoot(new StorageSettingsStore().LoadOrCreate());
        LetItOutChatRuntime.ValidateCorePath(path, root is null ? null : ModelPath(root));
        await ComponentLicenseGate.EnsureAsync(ManagedModelCatalog.LetItOutArtifactId, token);
        await ManagedModelRuntimeAudit.VerifyAsync(Register(root!), ManagedModelCatalog.LetItOutArtifactId, token);
    }
}
