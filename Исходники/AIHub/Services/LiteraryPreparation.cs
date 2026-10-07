using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AIHub.Services;

public sealed record LiteraryComponentState(string Key, bool Ready);

public static class LiteraryPreparation
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static readonly string[] Licenses = [ManagedModelCatalog.OmniGammaArtifactId, QdrantOptions.LicenseId,
        GigaEmbeddingInstallation.LicenseId, GigaEmbeddingInstallation.RuntimeLicenseId, .. LiteraryJellyInstallation.Licenses];
    public static async Task<IReadOnlyList<LiteraryComponentState>> CheckAsync(IProgress<LiteraryPreparationProgress> progress, CancellationToken ct, bool forceVerification = false)
    {
        var result = new List<LiteraryComponentState>();
        progress.Report(new("Checking", -1, LiteraryModelLocation.DisplayName));
        var rune = false;
        try
        {
            await LiteraryModelLocation.ResolveAsync(ct, forceVerification,
                new InlineProgress<double>(p => progress.Report(new("Checking", p, LiteraryModelLocation.DisplayName))));
            rune = true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException) { }
        result.Add(new("Runeweaver", rune));
        progress.Report(new("Checking", -1, "llama.cpp"));
        result.Add(new("Llama", await LlamaReadyAsync(ct)));
        progress.Report(new("Checking", -1, "Qdrant"));
        result.Add(new("Qdrant", await QdrantInstaller.IsInstalledAsync(QdrantRuntime.Shared.Options, ct)));
        progress.Report(new("Checking", -1, "Giga-Embeddings"));
        result.Add(new("Giga", await GigaEmbeddingInstallation.ModelReadyAsync(ct, forceVerification)));
        progress.Report(new("Checking", -1, "Python / PyTorch"));
        result.Add(new("Python", await GigaEmbeddingInstallation.RuntimeReadyAsync(ct)));
        foreach (var mode in LiteraryJellyInstallation.Modes)
        {
            progress.Report(new("JellyModels", -1, mode));
            result.Add(new("Jelly." + mode, await LiteraryJellyInstallation.FindModelAsync(mode, ct, forceVerification) is not null));
            result.Add(new("Jelly." + mode + ".Runtime", await LiteraryJellyInstallation.FindDependenciesAsync(mode, ct) is not null));
        }
        return result;
    }
    private static async Task<bool> LlamaReadyAsync(CancellationToken ct)
    {
        try
        {
            var manager = new ComponentManager();
            var directory = manager.GetInstallDirectory(ComponentCatalog.Find(HardwareRuntimeCatalog.LlamaCpuId)!);
            await HardwareRuntimeBundleVerifier.VerifyExecutableAsync(HardwareRuntimeCatalog.LlamaCpuId, directory, ct);
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception) { return false; }
    }
    public static async Task InstallAsync(IProgress<LiteraryPreparationProgress> progress, CancellationToken ct)
    {
        await ComponentLicenseGate.EnsureAsync(Licenses, ct);
        await Gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(AppDataPaths.RuntimeDirectory);
            using var lease = new FileStream(Path.Combine(AppDataPaths.RuntimeDirectory, "literary-prepare.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var state = await CheckAsync(progress, ct);
            bool Missing(string key) => !state.Single(s => s.Key == key).Ready;
            if (Missing("Llama")) throw new IOException("Prepare hardware libraries through the main-window downloader first.");
            if (Missing("Runeweaver"))
            {
                var path = LiteraryModelLocation.DownloadPath;
                await LiteraryArtifactDownload.GetAsync(new Uri(LiteraryModelLocation.DownloadUrl),
                    path, LiteraryModelLocation.SizeBytes, LiteraryModelLocation.Sha256, "sha256",
                    new InlineProgress<double>(p => progress.Report(new("Runeweaver", p))), ct);
                Directory.CreateDirectory(AppDataPaths.BaseDirectory);
                var temporary = LiteraryModelLocation.ConfigurationPath + ".tmp";
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new { modelPath = path }), ct);
                File.Move(temporary, LiteraryModelLocation.ConfigurationPath, true);
            }
            if (Missing("Qdrant")) await QdrantInstaller.InstallAsync(QdrantRuntime.Shared.Options,
                new InlineProgress<double>(p => progress.Report(new("Qdrant", p))), ct);
            if (Missing("Giga")) await GigaEmbeddingInstallation.InstallModelAsync(progress, ct);
            if (Missing("Python")) throw new IOException("Prepare Python hardware libraries through the main-window downloader first.");
            foreach (var mode in LiteraryJellyInstallation.Modes)
                if (Missing("Jelly." + mode) || Missing("Jelly." + mode + ".Runtime")) await LiteraryJellyInstallation.InstallAsync(mode, progress, ct);
            progress.Report(new("Checking"));
            await QdrantRuntime.Shared.StartAsync(ct);
            await LiterarySourceIndex.RecoverAsync(ct);
        }
        finally { Gate.Release(); }
    }
}
