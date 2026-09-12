using System.IO;
using System.Text.Json;

namespace AIHub.Services;

public static class LiteraryJellyInstallation
{
    public static readonly string[] Modes = ["gliner", "nuextract"];
    public static readonly string[] Licenses = ["model.jelly-gliner", "model.jelly-nuextract", "runtime.jelly-python"];
    public static string Script => Path.Combine(AppContext.BaseDirectory, "Tools", "jelly_worker.py");
    public static void ValidateMode(string mode)
    { if (mode is not ("runeweaver" or "gliner" or "nuextract")) throw new InvalidDataException("Unknown memory executor: " + mode); }
    public static JsonElement Manifest(string mode)
    {
        ValidateMode(mode);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Tools", "jelly-manifest.json")));
        return doc.RootElement.GetProperty("models").EnumerateArray().Single(m =>
            m.GetProperty("repository").GetString() == (mode == "gliner" ? "fastino/gliner2.5-multi-v1" : "numind/NuExtract3")).Clone();
    }
    public static string ModelDirectory(string mode)
    {
        var manifest = Manifest(mode);
        var storage = new StorageSettingsStore().LoadOrCreate().Models.Locations.FirstOrDefault()?.Path;
        return Path.Combine(string.IsNullOrWhiteSpace(storage) ? AppDataPaths.ComponentModelsDirectory : storage,
            manifest.GetProperty("directory").GetString()!, manifest.GetProperty("revision").GetString()!);
    }
    public static string Dependencies(string mode) => Path.Combine(AppDataPaths.RuntimeDirectory, "Python", "Jelly", mode, "v1");
    private static string? StandModel(string mode) => AppDataPaths.ProjectRoot is { } root
        ? Path.Combine(root, "Тесты", "JellyModels", "models", Manifest(mode).GetProperty("directory").GetString()!) : null;
    private static string? StandDependencies(string mode) => AppDataPaths.ProjectRoot is { } root
        ? Path.Combine(root, "Тесты", "JellyModels", "deps", mode) : null;
    private static (string Hash, string Algorithm) Digest(JsonElement file) => file.TryGetProperty("sha256", out var hash)
        ? (hash.GetString()!, "sha256") : (file.GetProperty("git_blob_sha1").GetString()!, "gitsha1");
    public static async Task<string?> FindModelAsync(string mode, CancellationToken ct)
    {
        foreach (var folder in new[] { ModelDirectory(mode), StandModel(mode) }.Where(f => f is not null))
        {
            var ready = true;
            foreach (var file in Manifest(mode).GetProperty("files").EnumerateArray())
            {
                var (hash, algorithm) = Digest(file);
                if (!await LiteraryArtifactDownload.ValidAsync(Path.Combine(folder!, file.GetProperty("filename").GetString()!),
                    file.GetProperty("size").GetInt64(), hash, algorithm, ct)) { ready = false; break; }
            }
            if (ready) return folder;
        }
        return null;
    }
    public static async Task<string?> FindDependenciesAsync(string mode, CancellationToken ct)
    {
        if (!File.Exists(GigaEmbeddingInstallation.Python)) return null;
        foreach (var folder in new[] { Dependencies(mode), StandDependencies(mode) }.Where(Directory.Exists))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                await GigaEmbeddingInstallation.RunAsync([Script, "--mode", mode, "--deps", folder!, "--check"], null, timeout.Token);
                return folder;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or LiteraryEmbeddingException) { }
        }
        return null;
    }
    public static async Task InstallAsync(string mode, IProgress<LiteraryPreparationProgress> progress, CancellationToken ct)
    {
        await ComponentLicenseGate.EnsureAsync(Licenses, ct);
        var manifest = Manifest(mode);
        if (await FindModelAsync(mode, ct) is null)
        {
            var files = manifest.GetProperty("files").EnumerateArray().ToArray();
            double total = files.Sum(f => (double)f.GetProperty("size").GetInt64()), done = 0;
            foreach (var file in files)
            {
                var name = file.GetProperty("filename").GetString()!; var size = file.GetProperty("size").GetInt64(); var (hash, algorithm) = Digest(file);
                var before = done;
                await LiteraryArtifactDownload.GetAsync(new Uri($"https://huggingface.co/{manifest.GetProperty("repository").GetString()}/resolve/{manifest.GetProperty("revision").GetString()}/{name}"),
                    Path.Combine(ModelDirectory(mode), name), size, hash, algorithm,
                    new InlineProgress<double>(p => progress.Report(new("JellyModels", (before + size * p / 100) * 100 / total, mode + " · " + name))), ct);
                done += size;
            }
        }
        if (await FindDependenciesAsync(mode, ct) is not null) return;
        // Use a new isolated target. The common torch runtime and its Transformers stay untouched.
        var target = Dependencies(mode); var staging = target + ".install-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        using var versions = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Tools", "jelly-dependencies.json")));
        var packages = versions.RootElement.GetProperty(mode).EnumerateObject().Select(p => p.Name + "==" + p.Value.GetString());
        var arguments = new[] { "-m", "pip", "--isolated", "install", "--disable-pip-version-check", "--no-cache-dir", "--only-binary=:all:",
            "--no-deps", "--target", staging, "--report", Path.Combine(staging, "install.json"), "--index-url", "https://pypi.org/simple" }.Concat(packages);
        try
        {
            await GigaEmbeddingInstallation.RunAsync(arguments, line => progress.Report(new("JellyLibraries", -1, line)), ct);
            await GigaEmbeddingInstallation.RunAsync([Script, "--mode", mode, "--deps", staging, "--check"], null, ct);
            if (Directory.Exists(target)) Directory.Move(target, target + ".previous-" + Guid.NewGuid().ToString("N"));
            Directory.Move(staging, target);
        }
        finally
        {
            if (Directory.Exists(staging) && Path.GetFullPath(staging).StartsWith(Path.GetFullPath(target) + ".install-", StringComparison.OrdinalIgnoreCase))
            {
                LiteraryProjectLayout.CheckTreePath(staging);
                Directory.Delete(staging, true);
            }
        }
    }
}
