using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AIHub.Services;

public sealed record LiteraryPreparationProgress(string Stage, double Percent = -1, string Detail = "");

public static class GigaEmbeddingInstallation
{
    public const string LicenseId = "model.giga-embeddings-480m";
    public const string RuntimeLicenseId = "runtime.python-cpu";
    public const string Revision = "0c94f705aa35719324fb46f7e75b0a5c275da6e4";
    internal static string LegacyRoot => Path.Combine(AppDataPaths.RuntimeDirectory, "Python", "giga-embeddings", "py312-torch210-transformers530");
    public static string Root => ManagedPythonRuntime.CpuDirectory;
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
    public static async Task<bool> ModelReadyAsync(CancellationToken ct, bool forceVerification = false)
    {
        foreach (var f in Files())
            if (!await LiteraryArtifactDownload.ValidAsync(Path.Combine(ModelDirectory, f.Name), f.Size, f.Hash, f.Algorithm, ct, forceVerification)) return false;
        return true;
    }
    public static async Task<bool> RuntimeReadyAsync(CancellationToken ct)
    {
        if (!ManagedPythonRuntime.HasCpu) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try { await RunAsync([Script, "--check"], null, timeout.Token); return true; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.ComponentModel.Win32Exception) { return false; }
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
    public static async Task RunAsync(IEnumerable<string> arguments, Action<string>? onLine, CancellationToken ct, string? logDirectory = null)
    {
        var args = arguments.ToArray();
        var deviceIndex = Array.IndexOf(args, "--device");
        if (deviceIndex == args.Length - 1 && deviceIndex >= 0) throw new ArgumentException("Missing Python device policy.", nameof(arguments));
        var runtime = await ManagedPythonRuntime.ResolveAsync(deviceIndex >= 0 ? args[deviceIndex + 1] : "cpu", ct);
        if (deviceIndex >= 0) args[deviceIndex + 1] = runtime.Device;
        var info = new ProcessStartInfo(runtime.Python) { WorkingDirectory = Path.GetDirectoryName(runtime.Python)!, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase)).ToArray())
            info.Environment.Remove(key);
        ManagedPythonLaunch.ScriptArguments(info, args);
        info.Environment["PYTHONUTF8"] = "1"; info.Environment["PYTHONUNBUFFERED"] = "1";
        info.Environment["HF_HUB_OFFLINE"] = "1"; info.Environment["TRANSFORMERS_OFFLINE"] = "1";
        ct.ThrowIfCancellationRequested();
        if (logDirectory is not null && !Directory.Exists(Path.GetDirectoryName(logDirectory))) throw new DirectoryNotFoundException("Project embedding data disappeared.");
        var logs = logDirectory ?? Path.Combine(AppDataPaths.BaseDirectory, "Diagnostics", "Giga"); Directory.CreateDirectory(logs);
        var log = Path.Combine(logs, DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N") + ".log");
        await File.WriteAllTextAsync(log, JsonSerializer.Serialize(new { runtime = runtime.Entry.Id, runtime.Device, runtime.FreeBytes, runtime.Reason }) + "\n", ct);
        using var process = OwnedProcessRegistry.Shared.Start(info, "Giga-Embeddings");
        using var sampling = new CancellationTokenSource();
        long peakWorkingSet = 0, peakPrivate = 0;
        double cpuMilliseconds = 0;
        var resourceTask = SampleAsync();
        async Task SampleAsync()
        {
            try
            {
                while (!sampling.IsCancellationRequested && !process.HasExited)
                {
                    process.Refresh();
                    peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
                    peakPrivate = Math.Max(peakPrivate, process.PrivateMemorySize64);
                    cpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds;
                    await Task.Delay(200, sampling.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
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
            if (process.ExitCode != 0)
            {
                var detail = error.ToString();
                if (detail.Contains("FileNotFoundError") || detail.Contains("PermissionError") || detail.Contains("No space left") || detail.Contains("disk is full"))
                    throw new IOException($"Giga storage failure: {detail}");
                throw new LiteraryEmbeddingException($"Giga worker exited {process.ExitCode}: {detail}");
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            sampling.Cancel(); await resourceTask;
            if (Directory.Exists(logs)) await File.AppendAllTextAsync(log, JsonSerializer.Serialize(new { resources = new { peakWorkingSet, peakPrivate, cpuMilliseconds } }) + "\n");
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
        }
    }
}

internal sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}
