using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Owned offline adapter to the pinned official API; no inference implementation here.</summary>
public sealed class MusicAceWorker : IMusicYueWorker
{
    public event Action<string>? Log;
    public string? LastHardware { get; private set; }
    public string? LastRuntimePack { get; private set; }
    public string? LastRequestReceipt { get; private set; }
    private IReadOnlyList<ManagedModelArtifactCard> _verifiedCards = [];
    internal sealed record PreparationReceipt(string Hardware, IReadOnlyList<ManagedModelArtifactCard> Cards);
    public Task PlanAsync(string model, MusicYueRequest request, string requestPath, string planPath, CancellationToken token) =>
        RunAsync(Root(model),request,requestPath,planPath,false,token,plan:true);
    public Task SynthesizeAsync(string model, string decoder, MusicYueRequest request, string requestPath, string outputPath, CancellationToken token) =>
        RunAsync(Root(model), request, requestPath, outputPath, false, token);
    private static string Root(string model) => Directory.GetParent(model)!.Parent!.Parent!.FullName;
    internal static async Task<PreparationReceipt> ProbeAsync(string root, CancellationToken token, Action<string> log)
    {
        var worker = new MusicAceWorker(); worker.Log += log;
        var path = Path.Combine(Path.GetTempPath(), "lopata-ace-" + Guid.NewGuid().ToString("N"));
        try {
            var directory = Path.Combine(AppDataPaths.BaseDirectory, "Music", "Python", MusicAceCatalog.RuntimeRevision);
            await ComponentAudit.RunAsync("Music.ACE.Probe", directory, MusicAceCatalog.RuntimeRevision + MusicAceCatalog.SourceRevision,
                ct => worker.RunAsync(root, new("", "", 1, 1) { Expert = MusicAceCatalog.Defaults() }, path + ".json", path + ".probe", true, ct, presenceProbe: true),
                ct => worker.RunAsync(root, new("", "", 1, 1) { Expert = MusicAceCatalog.Defaults() }, path + ".json", path + ".probe", true, ct, presenceProbe: false), token, log);
            return new(worker.LastHardware ?? "ACE / PyTorch", worker._verifiedCards);
        }
        finally { foreach (var suffix in new[] { ".json", ".probe", ".probe.receipt.json" }) if (File.Exists(path + suffix)) File.Delete(path + suffix); }
    }
    private async Task RunAsync(string root, MusicYueRequest request, string requestPath, string output, bool probe, CancellationToken token,bool plan=false, bool presenceProbe=false)
    {
        using var auditOperation = ComponentAudit.CaptureOperation();
        using var auditLog = ComponentAudit.CaptureLog(line => Log?.Invoke(line));
        request.Validate(); var payload = MusicAceRequest.Build(request);
        await MusicLora.MusicLoraAssets.EnsureAsync(root, request.Loras, request.EffectiveExpert.Variation, token);
        using var adapters = await MusicLora.MusicLoraAssets.AcquireAsync(root, request.Loras, token);
        payload["loras"] = request.Loras.Select(u => new { id = u.Id, revision = u.Revision, strength = u.Strength, audit_prepared = true,
            directory = MusicLora.MusicLoraCatalog.Find(u.Id).Card(root).InstallDirectory,
            format = MusicLora.MusicLoraCatalog.Find(u.Id).Format,
            files = MusicLora.MusicLoraCatalog.Find(u.Id).Files.Select(f => new { name = f.RelativePath, sha256 = f.Sha256 }) }).ToArray();
        if (File.Exists(output)) throw new IOException("Music output already exists.");
        Log?.Invoke("[Load] ACE XL Turbo · official API");
        var preparation = Stopwatch.StartNew();
        Log?.Invoke("[Prepare] Verifying pinned ACE source");
        await MusicAceSource.VerifyAsync(token);
        StageFinished("source", preparation.Elapsed);
        var prepared = await MusicAceRuntime.PrepareAsync(root, token, line => Log?.Invoke(line));
        _verifiedCards = prepared.Cards;
        StageFinished("libraries", preparation.Elapsed);
        Log?.Invoke("[Prepare] Verifying Python hardware runtime and selecting device");
        // The pinned torchaudio ABI is 2.10. ROCm's separate 2.9.1 pack is not
        // interchangeable; unsupported GPU packs fall back to the prepared CPU.
        var runtime = await ManagedPythonRuntime.ResolveAsync("auto", token, new HashSet<string> {
            HardwareRuntimeCatalog.PythonCuda128Id, HardwareRuntimeCatalog.PythonCudaId, HardwareRuntimeCatalog.PythonXpuId }, line => Log?.Invoke(line));
        StageFinished("hardware", preparation.Elapsed);
        LastRuntimePack = runtime.Entry.Id + "/" + MusicAceCatalog.RuntimeRevision;
        if (probe && presenceProbe) { LastHardware = runtime.Device + " / " + runtime.Entry.Id; return; }
        if (runtime.Device != "cpu") MusicLora.MusicLoraForecast.ObserveFreeGpu(MusicAceCatalog.Variation, runtime.FreeBytes);
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(payload), token);
        var temporary = output + "." + Guid.NewGuid().ToString("N") + ".partial";
        var info = new ProcessStartInfo(runtime.Python) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
        foreach (var key in new[] { "HF_HUB_OFFLINE", "TRANSFORMERS_OFFLINE", "HF_DATASETS_OFFLINE" }) info.Environment[key] = "1";
        info.Environment["PYTHONNOUSERSITE"] = "1"; info.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        info.Environment["GRADIO_ANALYTICS_ENABLED"] = "False";
        ManagedPythonLaunch.ScriptArguments(info, [Path.Combine(AppContext.BaseDirectory, "Tools", "music_ace_worker.py"),
            prepared.Overlay, MusicAceSource.DirectoryPath, MusicModelVariants.Artifact(root, MusicAceCatalog.Variation, MusicAceCatalog.Variation),
            MusicModelVariants.Artifact(root, MusicAceCatalog.Variation, MusicAceCatalog.Companions), requestPath, temporary, runtime.Device, probe ? "probe" : plan?"plan":"generate"]);
        token.ThrowIfCancellationRequested();
        Log?.Invoke(probe ? "[Check] Importing official ACE API · timeout=90s" : "[Load] Starting official ACE pipeline");
        using var process = OwnedProcessRegistry.Shared.Start(info, "Music.ACE.XL");
        var errors = new Queue<string>();
        async Task Pump(StreamReader reader) {
            string? line; while ((line = await reader.ReadLineAsync()) is not null) {
                if (line.Length > 4096) line = line[..4096];
                if (line.StartsWith("[Hardware]", StringComparison.Ordinal)) LastHardware = line[10..].Trim();
                lock (errors) { errors.Enqueue(line); while (errors.Count > 12) errors.Dequeue(); }
                try { Log?.Invoke(line); }
                catch (Exception error) { OwnedProcessRegistry.Log("music_log_callback_failed", "Music.ACE.XL", detail: error.GetType().Name); }
            }
        }
        var stdout = Pump(process.StandardOutput); var stderr = Pump(process.StandardError);
        try {
            // The API deadline starts only after preparation and process launch.
            // Hashing/extracting libraries is cancellable, but is not an API hang.
            await ExecuteProcessPhaseAsync(async phaseToken => {
                await process.WaitForExitAsync(phaseToken);
                await Task.WhenAll(stdout, stderr);
            }, probe, token);
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new IOException("ACE worker failed (" + process.ExitCode + "). " + string.Join('\n', errors));
            if(plan) {
                if(new FileInfo(temporary).Length>200000)throw new InvalidDataException("Music.AceIdeas.Invalid");
                var idea=JsonSerializer.Deserialize<MusicAceIdeas.MusicAceIdeaData>(await File.ReadAllTextAsync(temporary,token))
                    ??throw new InvalidDataException("Music.AceIdeas.Invalid");idea.Validate();
            } else if (!probe) _ = MusicWaveFile.ReadDuration(temporary);
            var receipt = temporary + ".receipt.json";
            if (!File.Exists(receipt) || new FileInfo(receipt).Length > 1_048_576) throw new InvalidDataException("Missing or oversized ACE receipt.");
            LastRequestReceipt = await File.ReadAllTextAsync(receipt, token);
            using var json = JsonDocument.Parse(LastRequestReceipt);
            File.Move(temporary, output, false);
        }
        finally {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
            await Task.WhenAll(stdout, stderr);
            foreach (var path in new[] { temporary, temporary + ".receipt.json" }) if (File.Exists(path)) File.Delete(path);
        }
    }
    private void StageFinished(string stage, TimeSpan elapsed)
    {
        var detail = $"stage={stage}; elapsedMs={(long)elapsed.TotalMilliseconds}";
        OwnedProcessRegistry.Log("ace_preparation_progress", "Music.ACE.XL", detail: detail);
        Log?.Invoke("[Prepare] " + detail);
    }
    internal static async Task ExecuteProcessPhaseAsync(Func<CancellationToken, Task> action, bool probe,
        CancellationToken token, TimeSpan? probeLimit = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        var limit = probeLimit ?? TimeSpan.FromSeconds(90);
        if (probe) timeout.CancelAfter(limit);
        try { await action(timeout.Token); }
        catch (OperationCanceledException) when (probe && !token.IsCancellationRequested && timeout.IsCancellationRequested)
        { throw new IOException($"ACE API import timed out after {limit.TotalSeconds:0.###} seconds (Python process stage)."); }
    }
}
