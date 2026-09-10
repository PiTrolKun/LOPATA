using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace AIHub.Services;

public sealed record LiteraryPreparationProgress(string Stage, double Percent = -1, string Detail = "");

public static class GigaEmbeddingInstallation
{
    public const string LicenseId = "model.giga-embeddings-480m";
    public const string RuntimeLicenseId = "runtime.giga-python";
    public const string Revision = "0c94f705aa35719324fb46f7e75b0a5c275da6e4";
    public static string Root => Path.Combine(AppDataPaths.RuntimeDirectory, "Python", "giga-embeddings", "py312-torch210-transformers530");
    public static string Python => Path.Combine(Root, "python.exe");
    public static string Script => Path.Combine(AppContext.BaseDirectory, "Tools", "giga_embeddings.py");
    public static string ModelDirectory
    {
        get
        {
            var storage = new StorageSettingsStore().LoadOrCreate();
            var root = storage.Models.Locations.FirstOrDefault()?.Path;
            return Path.Combine(string.IsNullOrWhiteSpace(root) ? AppDataPaths.ComponentModelsDirectory : root,
                "Giga-Embeddings-instruct-480M-0826", Revision);
        }
    }
    public sealed record Artifact(string Name, long Size, string Hash, string Algorithm);
    public static IReadOnlyList<Artifact> Files()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Tools", "giga-manifest.json")));
        if (doc.RootElement.GetProperty("revision").GetString() != Revision) throw new InvalidDataException("Giga manifest revision mismatch.");
        return doc.RootElement.GetProperty("files").EnumerateArray().Select(f => new Artifact(f.GetProperty("name").GetString()!,
            f.GetProperty("size").GetInt64(), f.GetProperty("hash").GetString()!, f.GetProperty("algorithm").GetString()!)).ToArray();
    }
    public static async Task<bool> ModelReadyAsync(CancellationToken ct)
    {
        foreach (var f in Files())
            if (!await LiteraryArtifactDownload.ValidAsync(Path.Combine(ModelDirectory, f.Name), f.Size, f.Hash, f.Algorithm, ct)) return false;
        return true;
    }
    public static async Task<bool> RuntimeReadyAsync(CancellationToken ct)
    {
        if (!File.Exists(Python) || !File.Exists(Path.Combine(Root, "ready.json"))) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try { await RunAsync([Script, "--check"], null, timeout.Token); return true; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { return false; }
    }
    public static async Task InstallModelAsync(IProgress<LiteraryPreparationProgress> progress, CancellationToken ct)
    {
        await ComponentLicenseGate.EnsureAsync(LicenseId, ct);
        var files = Files(); double total = files.Sum(f => (double)f.Size), completed = 0;
        foreach (var f in files)
        {
            var before = completed;
            await LiteraryArtifactDownload.GetAsync(new Uri($"https://huggingface.co/ai-sage/Giga-Embeddings-instruct-480M-0826/resolve/{Revision}/{f.Name}"),
                Path.Combine(ModelDirectory, f.Name), f.Size, f.Hash, f.Algorithm,
                new InlineProgress<double>(p => progress.Report(new("Giga", (before + f.Size * p / 100) * 100 / total, f.Name))), ct);
            completed += f.Size;
        }
    }
    public static async Task InstallRuntimeAsync(IProgress<LiteraryPreparationProgress> progress, CancellationToken ct)
    {
        await ComponentLicenseGate.EnsureAsync(RuntimeLicenseId, ct);
        Directory.CreateDirectory(Root);
        if (new DriveInfo(Path.GetPathRoot(Root)!).AvailableFreeSpace < 8L * 1024 * 1024 * 1024)
            throw new IOException("The isolated CUDA environment needs at least 8 GiB of free disk space during installation.");
        var zip = Path.Combine(Root, "python.zip");
        await LiteraryArtifactDownload.GetAsync(new Uri("https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip"), zip,
            11133606, "4acbed6dd1c744b0376e3b1cf57ce906f9dc9e95e68824584c8099a63025a3c3", "sha256",
            new InlineProgress<double>(p => progress.Report(new("Python", p))), ct);
        ZipFile.ExtractToDirectory(zip, Root, true);
        await LiteraryArtifactDownload.GetAsync(new Uri("https://files.pythonhosted.org/packages/44/3c/d717024885424591d5376220b5e836c2d5293ce2011523c9de23ff7bf068/pip-25.3-py3-none-any.whl"),
            Path.Combine(Root, "pip.whl"), 1778622, "9655943313a94722b7774661c21049070f6bbb0a1516bf02f7c8d5d9201514cd", "sha256", null, ct);
        Directory.CreateDirectory(Path.Combine(Root, "Lib", "site-packages"));
        await File.WriteAllTextAsync(Path.Combine(Root, "python312._pth"), "python312.zip\n.\nLib/site-packages\npip.whl\nimport site\n", ct);
        // Pip wheels keep their own LICENSE/NOTICE files in this dedicated runtime.
        await RunAsync(["-m", "pip", "--isolated", "install", "--disable-pip-version-check", "--no-cache-dir", "--only-binary=:all:",
            "--report", Path.Combine(Root, "torch-install.json"), "--index-url", "https://download.pytorch.org/whl/cu128", "torch==2.10.0"],
            line => progress.Report(new("Libraries", -1, line)), ct);
        await RunAsync(["-m", "pip", "--isolated", "install", "--disable-pip-version-check", "--no-cache-dir", "--only-binary=:all:",
            "--report", Path.Combine(Root, "transformers-install.json"), "--index-url", "https://pypi.org/simple", "transformers==5.3.0"],
            line => progress.Report(new("Libraries", -1, line)), ct);
        await RunAsync([Script, "--check"], null, ct);
        await File.WriteAllTextAsync(Path.Combine(Root, "ready.json"), JsonSerializer.Serialize(new { python = "3.12.10", torch = "2.10.0", transformers = "5.3.0", checkedAt = DateTimeOffset.UtcNow }), ct);
    }
    public static async Task RunAsync(IEnumerable<string> arguments, Action<string>? onLine, CancellationToken ct)
    {
        var info = new ProcessStartInfo(Python) { WorkingDirectory = Root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        info.Environment["PYTHONUTF8"] = "1"; info.Environment["PYTHONUNBUFFERED"] = "1";
        info.Environment["HF_HUB_OFFLINE"] = "1"; info.Environment["TRANSFORMERS_OFFLINE"] = "1";
        var logs = Path.Combine(AppDataPaths.BaseDirectory, "Diagnostics", "Giga"); Directory.CreateDirectory(logs);
        var log = Path.Combine(logs, DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N") + ".log");
        using var process = OwnedProcessRegistry.Shared.Start(info, "Giga-Embeddings");
        var error = new System.Text.StringBuilder();
        async Task Pump(StreamReader reader, bool stderr)
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (stderr) { if (error.Length < 4000) error.AppendLine(line); }
                else onLine?.Invoke(line);
                // Separate stdout/stderr logs avoid competing writes and preserve native diagnostics.
                await File.AppendAllTextAsync(log + (stderr ? ".stderr" : ""), line + "\n", ct);
            }
        }
        var stdout = Pump(process.StandardOutput, false); var stderr = Pump(process.StandardError, true);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(ct), stdout, stderr);
            if (process.ExitCode != 0) throw new IOException($"Giga worker exited {process.ExitCode}: {error}");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
        }
    }
}

internal sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}
