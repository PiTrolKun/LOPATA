using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace AIHub.Services;

public sealed record LiteraryComponentState(string Key, bool Ready);

public static class LiteraryPreparation
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static readonly string[] Licenses = ["model.runeweaver", "backend.llama", "native.cuda", QdrantOptions.LicenseId,
        GigaEmbeddingInstallation.LicenseId, GigaEmbeddingInstallation.RuntimeLicenseId, .. LiteraryJellyInstallation.Licenses];
    public static async Task<IReadOnlyList<LiteraryComponentState>> CheckAsync(IProgress<LiteraryPreparationProgress> progress, CancellationToken ct)
    {
        var result = new List<LiteraryComponentState>();
        progress.Report(new("Checking", -1, "Runeweaver"));
        var rune = false;
        try { await LiteraryModelLocation.ResolveAsync(ct); rune = true; }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException) { }
        result.Add(new("Runeweaver", rune));
        progress.Report(new("Checking", -1, "llama.cpp"));
        result.Add(new("Llama", await LlamaReadyAsync(ct)));
        progress.Report(new("Checking", -1, "Qdrant"));
        result.Add(new("Qdrant", await QdrantInstaller.IsInstalledAsync(QdrantRuntime.Shared.Options, ct)));
        progress.Report(new("Checking", -1, "Giga-Embeddings"));
        result.Add(new("Giga", await GigaEmbeddingInstallation.ModelReadyAsync(ct)));
        progress.Report(new("Checking", -1, "Python / PyTorch"));
        result.Add(new("Python", await GigaEmbeddingInstallation.RuntimeReadyAsync(ct)));
        foreach (var mode in LiteraryJellyInstallation.Modes)
        {
            progress.Report(new("JellyModels", -1, mode));
            result.Add(new("Jelly." + mode, await LiteraryJellyInstallation.FindModelAsync(mode, ct) is not null));
            result.Add(new("Jelly." + mode + ".Runtime", await LiteraryJellyInstallation.FindDependenciesAsync(mode, ct) is not null));
        }
        return result;
    }
    private static async Task<bool> LlamaReadyAsync(CancellationToken ct)
    {
        if (!new[] { "llama-server.exe", "ggml-cuda.dll", "cublas64_12.dll", "cublasLt64_12.dll", "cudart64_12.dll" }
            .All(name => File.Exists(Path.Combine(LlamaBackendPaths.DirectoryPath, name)))) return false;
        var info = new ProcessStartInfo(LlamaBackendPaths.ServerExecutablePath) { WorkingDirectory = LlamaBackendPaths.DirectoryPath,
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("--version");
        try
        {
            using var process = OwnedProcessRegistry.Shared.Start(info, "Literary backend check");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); return process.ExitCode == 0 && ((await output) + (await error)).Contains("9442"); }
            finally { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
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
            if (Missing("Llama")) await InstallLlamaAsync(progress, ct);
            if (Missing("Runeweaver"))
            {
                var folder = new StorageSettingsStore().LoadOrCreate().Models.Locations.FirstOrDefault()?.Path;
                var path = Path.Combine(string.IsNullOrWhiteSpace(folder) ? AppDataPaths.ComponentModelsDirectory : folder,
                    "Runeweaver", "MN-12B-Runeweaver-RP-RU.Q4_K_M.gguf");
                await LiteraryArtifactDownload.GetAsync(new Uri("https://huggingface.co/limloop/MN-12B-Runeweaver-RP-RU-GGUF/resolve/84fa96954eef3eec4e92433e133c5bb1c774fc22/MN-12B-Runeweaver-RP-RU.Q4_K_M.gguf"),
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
            if (Missing("Python")) await GigaEmbeddingInstallation.InstallRuntimeAsync(progress, ct);
            foreach (var mode in LiteraryJellyInstallation.Modes)
                if (Missing("Jelly." + mode) || Missing("Jelly." + mode + ".Runtime")) await LiteraryJellyInstallation.InstallAsync(mode, progress, ct);
            progress.Report(new("Checking"));
            await QdrantRuntime.Shared.StartAsync(ct);
            await LiterarySourceIndex.RecoverAsync(ct);
        }
        finally { Gate.Release(); }
    }
    private static async Task InstallLlamaAsync(IProgress<LiteraryPreparationProgress> progress, CancellationToken ct)
    {
        var archives = new[]
        {
            ("llama-b9442-bin-win-cuda-12.4-x64.zip", 260238608L, "77d78a1d7a1d80e051c3b43db64c0433b97d11fe12f525ddfa50302f726515f1"),
            ("cudart-llama-bin-win-cuda-12.4-x64.zip", 391443627L, "8c79a9b226de4b3cacfd1f83d24f962d0773be79f1e7b75c6af4ded7e32ae1d6")
        };
        var stage = LlamaBackendPaths.DirectoryPath + ".install"; Directory.CreateDirectory(stage);
        foreach (var (name, size, hash) in archives)
        {
            var zip = Path.Combine(stage, name);
            await LiteraryArtifactDownload.GetAsync(new Uri("https://github.com/ggml-org/llama.cpp/releases/download/b9442/" + name), zip,
                size, hash, "sha256", new InlineProgress<double>(p => progress.Report(new("Llama", p, name))), ct);
            using var archive = ZipFile.OpenRead(zip);
            foreach (var entry in archive.Entries.Where(e => e.Name.Length > 0)) entry.ExtractToFile(Path.Combine(stage, entry.Name), true);
        }
        // Existing runtime is left intact until both archives have been verified/extracted.
        if (Directory.Exists(LlamaBackendPaths.DirectoryPath))
            Directory.Move(LlamaBackendPaths.DirectoryPath, LlamaBackendPaths.DirectoryPath + ".previous-" + Guid.NewGuid().ToString("N"));
        Directory.Move(stage, LlamaBackendPaths.DirectoryPath);
    }
}
