using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Owned offline adapter to the pinned official API; no inference implementation here.</summary>
public sealed class MusicHeartMuLaWorker : IMusicYueWorker
{
    public event Action<string>? Log;
    public string? LastHardware { get; private set; }
    public string? LastRuntimePack { get; private set; }
    public string? LastRequestReceipt { get; private set; }
    private IReadOnlyList<ManagedModelArtifactCard> _verifiedCards = [];
    internal sealed record PreparationReceipt(string Hardware, IReadOnlyList<ManagedModelArtifactCard> Cards);
    public Task PlanAsync(string model, MusicYueRequest request, string requestPath, string planPath, CancellationToken token) =>
        throw new NotSupportedException("HeartMuLa has no separate language model planning stage.");
    public Task SynthesizeAsync(string model, string decoder, MusicYueRequest request, string requestPath, string outputPath, CancellationToken token) =>
        RunAsync(Root(model), request, requestPath, outputPath, false, token);
    private static string Root(string model) => Directory.GetParent(model)!.Parent!.Parent!.FullName;
    internal static async Task<PreparationReceipt> ProbeAsync(string root, CancellationToken token, Action<string> log)
    {
        var worker = new MusicHeartMuLaWorker(); worker.Log += log;
        var path = Path.Combine(Path.GetTempPath(), "lopata-heartmula-" + Guid.NewGuid().ToString("N"));
        try {
            await worker.RunAsync(root, new("", "", 1, 1, 30) { Expert = MusicHeartMuLaCatalog.Defaults() }, path + ".json", path + ".probe", true, token);
            return new(worker.LastHardware ?? "HeartMuLa / PyTorch", worker._verifiedCards);
        }
        finally { foreach (var suffix in new[] { ".json", ".probe", ".probe.receipt.json" }) if (File.Exists(path + suffix)) File.Delete(path + suffix); }
    }
    private async Task RunAsync(string root, MusicYueRequest request, string requestPath, string output, bool probe, CancellationToken token)
    {
        request.Validate(); var payload = MusicHeartMuLaRequest.Build(request);
        if (File.Exists(output)) throw new IOException("Music output already exists.");
        Log?.Invoke("[Load] HeartMuLa · official API");
        var preparation = Stopwatch.StartNew();
        Log?.Invoke("[Prepare] Verifying pinned HeartMuLa source");
        await MusicHeartMuLaSource.VerifyAsync(token);
        StageFinished("source", preparation.Elapsed);
        var prepared = await MusicHeartMuLaRuntime.PrepareAsync(root, token, line => Log?.Invoke(line));
        _verifiedCards = prepared.Cards;
        StageFinished("libraries", preparation.Elapsed);
        Log?.Invoke("[Prepare] Verifying Python hardware runtime and selecting device");
        // The pinned torchaudio/torchvision ABI is 2.10. ROCm's 2.9.1 pack is not
        // interchangeable; unsupported GPU packs fall back to the prepared CPU.
        var runtime = await ManagedPythonRuntime.ResolveAsync("auto", token, new HashSet<string> {
            HardwareRuntimeCatalog.PythonCuda128Id, HardwareRuntimeCatalog.PythonCudaId, HardwareRuntimeCatalog.PythonXpuId }, line => Log?.Invoke(line));
        StageFinished("hardware", preparation.Elapsed);
        LastRuntimePack = runtime.Entry.Id + "/" + MusicHeartMuLaCatalog.RuntimeRevision;
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(payload), token);
        var temporary = output + "." + Guid.NewGuid().ToString("N") + ".partial";
        var info = new ProcessStartInfo(runtime.Python) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
        foreach (var key in new[] { "HF_HUB_OFFLINE", "TRANSFORMERS_OFFLINE", "HF_DATASETS_OFFLINE" }) info.Environment[key] = "1";
        info.Environment["PYTHONNOUSERSITE"] = "1"; info.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        info.Environment["GRADIO_ANALYTICS_ENABLED"] = "False";
        info.Environment["PYTHONUTF8"] = "1";
        ManagedPythonLaunch.ScriptArguments(info, [Path.Combine(AppContext.BaseDirectory, "Tools", "music_heartmula_worker.py"),
            prepared.Overlay, MusicHeartMuLaSource.DirectoryPath, MusicModelVariants.Artifact(root, MusicHeartMuLaCatalog.Variation, MusicHeartMuLaCatalog.Variation),
            MusicModelVariants.Artifact(root, MusicHeartMuLaCatalog.Variation, MusicHeartMuLaCatalog.Companions), requestPath, temporary, runtime.Device, probe ? "probe" : "generate"]);
        token.ThrowIfCancellationRequested();
        Log?.Invoke(probe ? "[Check] Importing official HeartMuLa API · timeout=90s" : "[Load] Starting official HeartMuLa pipeline");
        using var process = OwnedProcessRegistry.Shared.Start(info, "Music.HeartMuLa");
        var errors = new Queue<string>();
        var messages = new Queue<string>();
        async Task Pump(StreamReader reader, bool stderr) {
            string? line; while ((line = await reader.ReadLineAsync()) is not null) {
                if (line.Length > 4096) line = line[..4096];
                if (line.StartsWith("[Hardware]", StringComparison.Ordinal)) LastHardware = line[10..].Trim();
                var tail = stderr ? errors : messages;
                lock (tail) { tail.Enqueue(line); while (tail.Count > (stderr ? 48 : 12)) tail.Dequeue(); }
                try { Log?.Invoke(line); }
                catch (Exception error) { OwnedProcessRegistry.Log("music_log_callback_failed", "Music.HeartMuLa", detail: error.GetType().Name); }
            }
        }
        var stdout = Pump(process.StandardOutput, false); var stderr = Pump(process.StandardError, true);
        try {
            // The API deadline starts only after preparation and process launch.
            // Hashing/extracting libraries is cancellable, but is not an API hang.
            await ExecuteProcessPhaseAsync(async phaseToken => {
                await process.WaitForExitAsync(phaseToken);
                await Task.WhenAll(stdout, stderr);
            }, probe, token);
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) {
                var failure = FailureDetail(process.ExitCode, errors, messages);
                OwnedProcessRegistry.Log("worker_failed", "Music.HeartMuLa", process.Id, failure);
                throw new IOException(failure);
            }
            if (!probe) _ = MusicWaveFile.ReadDuration(temporary);
            var receipt = temporary + ".receipt.json";
            if (!File.Exists(receipt) || new FileInfo(receipt).Length > 1_048_576) throw new InvalidDataException("Missing or oversized HeartMuLa receipt.");
            LastRequestReceipt = await File.ReadAllTextAsync(receipt, token);
            using var json = JsonDocument.Parse(LastRequestReceipt);
            ValidateReceipt(json.RootElement, payload);
            File.Move(temporary, output, false);
        }
        finally {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
            await Task.WhenAll(stdout, stderr);
            foreach (var path in new[] { temporary, temporary + ".receipt.json" }) if (File.Exists(path)) File.Delete(path);
            // Derived solely from our own temporary output, never from Python stdout.
            var staging = Path.GetFullPath(temporary + ".source");
            var parent = Path.GetFullPath(Path.GetDirectoryName(temporary)!);
            if (Path.GetDirectoryName(staging) != parent) throw new InvalidDataException("Unexpected HeartMuLa staging path.");
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }
    internal static void ValidateReceipt(JsonElement receipt, Dictionary<string, object?> request)
    {
        if (receipt.GetProperty("source").GetString() != MusicHeartMuLaCatalog.SourceRevision)
            throw new InvalidDataException("HeartMuLa receipt belongs to another source revision.");
        var actual = receipt.GetProperty("request");
        foreach (var (key, expected) in request) {
            if (!actual.TryGetProperty(key, out var value) ||
                !JsonElement.DeepEquals(value, JsonSerializer.SerializeToElement(expected)))
                throw new InvalidDataException("HeartMuLa receipt request mismatch: " + key);
        }
    }
    private void StageFinished(string stage, TimeSpan elapsed)
    {
        var detail = $"stage={stage}; elapsedMs={(long)elapsed.TotalMilliseconds}";
        OwnedProcessRegistry.Log("heartmula_preparation_progress", "Music.HeartMuLa", detail: detail);
        Log?.Invoke("[Prepare] " + detail);
    }
    internal static string FailureDetail(int exitCode, IEnumerable<string> stderr, IEnumerable<string> stdout)
    {
        var errorLines = stderr.ToArray();
        var detail = string.Join('\n', errorLines.Length > 0 ? errorLines : stdout);
        if (detail.Length > 32768) detail = detail[^32768..];
        return "HeartMuLa worker failed (" + exitCode + "). " + detail;
    }
    internal static async Task ExecuteProcessPhaseAsync(Func<CancellationToken, Task> action, bool probe,
        CancellationToken token, TimeSpan? probeLimit = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        var limit = probeLimit ?? TimeSpan.FromSeconds(90);
        if (probe) timeout.CancelAfter(limit);
        try { await action(timeout.Token); }
        catch (OperationCanceledException) when (probe && !token.IsCancellationRequested && timeout.IsCancellationRequested)
        { throw new IOException($"HeartMuLa API import timed out after {limit.TotalSeconds:0.###} seconds (Python process stage)."); }
    }
}
