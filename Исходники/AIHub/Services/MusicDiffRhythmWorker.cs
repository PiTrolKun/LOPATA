using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Owned offline adapter to the pinned official API; no inference implementation here.</summary>
public sealed class MusicDiffRhythmWorker : IMusicYueWorker
{
    public event Action<string>? Log;
    public string? LastHardware { get; private set; }
    public string? LastRuntimePack { get; private set; }
    public string? LastRequestReceipt { get; private set; }
    private IReadOnlyList<ManagedModelArtifactCard> _verifiedCards = [];
    internal sealed record PreparationReceipt(string Hardware, IReadOnlyList<ManagedModelArtifactCard> Cards);
    public Task PlanAsync(string model, MusicYueRequest request, string requestPath, string planPath, CancellationToken token) =>
        throw new NotSupportedException("DiffRhythm has no separate language model planning stage.");
    public Task SynthesizeAsync(string model, string decoder, MusicYueRequest request, string requestPath, string outputPath, CancellationToken token) =>
        RunAsync(Root(model), request, requestPath, outputPath, false, token);
    private static string Root(string model) => Directory.GetParent(model)!.Parent!.Parent!.FullName;
    internal static async Task<PreparationReceipt> ProbeAsync(string root, CancellationToken token, Action<string> log)
    {
        var worker = new MusicDiffRhythmWorker(); worker.Log += log;
        var path = Path.Combine(Path.GetTempPath(), "lopata-diff2-" + Guid.NewGuid().ToString("N"));
        try {
            await worker.RunAsync(root, new("", "", 1, 1, 30) { Expert = MusicDiffRhythmCatalog.Defaults() }, path + ".json", path + ".probe", true, token);
            return new(worker.LastHardware ?? "DiffRhythm / PyTorch", worker._verifiedCards);
        }
        finally { foreach (var suffix in new[] { ".json", ".probe", ".probe.receipt.json" }) if (File.Exists(path + suffix)) File.Delete(path + suffix); }
    }
    private async Task RunAsync(string root, MusicYueRequest request, string requestPath, string output, bool probe, CancellationToken token)
    {
        request.Validate(); var payload = MusicDiffRhythmRequest.Build(request);
        if (File.Exists(output)) throw new IOException("Music output already exists.");
        Log?.Invoke("[Load] DiffRhythm 2 · official API");
        var preparation = Stopwatch.StartNew();
        var speech = new CoreVoiceRuntimeLocator().Find() ?? throw new IOException("Missing bundled eSpeak NG runtime.");
        Log?.Invoke("[Prepare] Verifying pinned DiffRhythm source");
        await MusicDiffRhythmSource.VerifyAsync(token);
        StageFinished("source", preparation.Elapsed);
        var prepared = await MusicDiffRhythmRuntime.PrepareAsync(root, token, line => Log?.Invoke(line));
        _verifiedCards = prepared.Cards;
        StageFinished("libraries", preparation.Elapsed);
        Log?.Invoke("[Prepare] Verifying Python hardware runtime and selecting device");
        // The pinned torchaudio ABI is 2.10. ROCm's separate 2.9.1 pack is not
        // interchangeable; unsupported GPU packs fall back to the prepared CPU.
        var runtime = await ManagedPythonRuntime.ResolveAsync("auto", token, new HashSet<string> {
            HardwareRuntimeCatalog.PythonCuda128Id, HardwareRuntimeCatalog.PythonCudaId, HardwareRuntimeCatalog.PythonXpuId }, line => Log?.Invoke(line));
        StageFinished("hardware", preparation.Elapsed);
        LastRuntimePack = runtime.Entry.Id + "/" + MusicDiffRhythmCatalog.RuntimeRevision;
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(payload), token);
        var temporary = output + "." + Guid.NewGuid().ToString("N") + ".partial";
        var info = new ProcessStartInfo(runtime.Python) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
        foreach (var key in new[] { "HF_HUB_OFFLINE", "TRANSFORMERS_OFFLINE", "HF_DATASETS_OFFLINE" }) info.Environment[key] = "1";
        info.Environment["PYTHONNOUSERSITE"] = "1"; info.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        info.Environment["GRADIO_ANALYTICS_ENABLED"] = "False";
        info.Environment["PYTHONUTF8"] = "1";
        info.Environment["PHONEMIZER_ESPEAK_LIBRARY"] = speech.LibraryPath;
        info.Environment["ESPEAK_DATA_PATH"] = speech.DataPath;
        ManagedPythonLaunch.ScriptArguments(info, [Path.Combine(AppContext.BaseDirectory, "Tools", "music_diffrhythm_worker.py"),
            prepared.Overlay, MusicDiffRhythmSource.DirectoryPath, MusicModelVariants.Artifact(root, MusicDiffRhythmCatalog.Variation, MusicDiffRhythmCatalog.Variation),
            MusicModelVariants.Artifact(root, MusicDiffRhythmCatalog.Variation, MusicDiffRhythmCatalog.Companions), requestPath, temporary, runtime.Device, probe ? "probe" : "generate"]);
        token.ThrowIfCancellationRequested();
        Log?.Invoke(probe ? "[Check] Importing official DiffRhythm API · timeout=90s" : "[Load] Starting official DiffRhythm pipeline");
        using var process = OwnedProcessRegistry.Shared.Start(info, "Music.DiffRhythm2");
        var errors = new Queue<string>();
        var messages = new Queue<string>();
        async Task Pump(StreamReader reader, bool stderr) {
            string? line; while ((line = await reader.ReadLineAsync()) is not null) {
                if (line.Length > 4096) line = line[..4096];
                if (line.StartsWith("[Hardware]", StringComparison.Ordinal)) LastHardware = line[10..].Trim();
                var tail = stderr ? errors : messages;
                lock (tail) { tail.Enqueue(line); while (tail.Count > (stderr ? 48 : 12)) tail.Dequeue(); }
                try { Log?.Invoke(line); }
                catch (Exception error) { OwnedProcessRegistry.Log("music_log_callback_failed", "Music.DiffRhythm2", detail: error.GetType().Name); }
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
                OwnedProcessRegistry.Log("worker_failed", "Music.DiffRhythm2", process.Id, failure);
                throw new IOException(failure);
            }
            if (!probe) _ = MusicWaveFile.ReadDuration(temporary);
            var receipt = temporary + ".receipt.json";
            if (!File.Exists(receipt) || new FileInfo(receipt).Length > 1_048_576) throw new InvalidDataException("Missing or oversized DiffRhythm receipt.");
            LastRequestReceipt = await File.ReadAllTextAsync(receipt, token);
            using var json = JsonDocument.Parse(LastRequestReceipt);
            File.Move(temporary, output, false);
        }
        finally {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
            await Task.WhenAll(stdout, stderr);
            foreach (var path in new[] { temporary, temporary + ".receipt.json" }) if (File.Exists(path)) File.Delete(path);
            // Derived solely from our own temporary output, never from Python stdout.
            var staging = Path.GetFullPath(temporary + ".source");
            var parent = Path.GetFullPath(Path.GetDirectoryName(temporary)!);
            if (Path.GetDirectoryName(staging) != parent) throw new InvalidDataException("Unexpected DiffRhythm staging path.");
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }
    private void StageFinished(string stage, TimeSpan elapsed)
    {
        var detail = $"stage={stage}; elapsedMs={(long)elapsed.TotalMilliseconds}";
        OwnedProcessRegistry.Log("diffrhythm_preparation_progress", "Music.DiffRhythm2", detail: detail);
        Log?.Invoke("[Prepare] " + detail);
    }
    internal static string FailureDetail(int exitCode, IEnumerable<string> stderr, IEnumerable<string> stdout)
    {
        var errorLines = stderr.ToArray();
        var detail = string.Join('\n', errorLines.Length > 0 ? errorLines : stdout);
        if (detail.Length > 32768) detail = detail[^32768..];
        return "DiffRhythm worker failed (" + exitCode + "). " + detail;
    }
    internal static async Task ExecuteProcessPhaseAsync(Func<CancellationToken, Task> action, bool probe,
        CancellationToken token, TimeSpan? probeLimit = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        var limit = probeLimit ?? TimeSpan.FromSeconds(90);
        if (probe) timeout.CancelAfter(limit);
        try { await action(timeout.Token); }
        catch (OperationCanceledException) when (probe && !token.IsCancellationRequested && timeout.IsCancellationRequested)
        { throw new IOException($"DiffRhythm API import timed out after {limit.TotalSeconds:0.###} seconds (Python process stage)."); }
    }
}
