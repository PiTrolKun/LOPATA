using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using AIHub.Models;
using Lopata.Inspector;

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
        var path = Path.Combine(AIHub.Services.ManagedWorkStorage.Current.ScratchRoot("MusicWorkers"), "lopata-ace-" + Guid.NewGuid().ToString("N"));
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
        var inspection = probe ? null : await ApplicationInspector.BeginAttemptAsync("ACE XL Turbo", runtime.Entry.Id,
            runtime.Device, token, model: MusicAceCatalog.Variation, stage: "model_loading");
        var info = new ProcessStartInfo(runtime.Python) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = !probe && !plan,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
        if (info.RedirectStandardInput) info.StandardInputEncoding = new System.Text.UTF8Encoding(false);
        foreach (var key in new[] { "HF_HUB_OFFLINE", "TRANSFORMERS_OFFLINE", "HF_DATASETS_OFFLINE" }) info.Environment[key] = "1";
        info.Environment["PYTHONNOUSERSITE"] = "1"; info.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        info.Environment["GRADIO_ANALYTICS_ENABLED"] = "False";
        if (info.RedirectStandardInput) info.Environment["LOPATA_ACE_INSPECTOR"] = "1";
        else info.Environment.Remove("LOPATA_ACE_INSPECTOR");
        ManagedPythonLaunch.ScriptArguments(info, [Path.Combine(AppContext.BaseDirectory, "Tools", "music_ace_worker.py"),
            prepared.Overlay, MusicAceSource.DirectoryPath, MusicModelVariants.Artifact(root, MusicAceCatalog.Variation, MusicAceCatalog.Variation),
            MusicModelVariants.Artifact(root, MusicAceCatalog.Variation, MusicAceCatalog.Companions), requestPath, temporary, runtime.Device, probe ? "probe" : plan?"plan":"generate"]);
        token.ThrowIfCancellationRequested();
        Log?.Invoke(probe ? "[Check] Importing official ACE API · timeout=90s" : "[Load] Starting official ACE pipeline");
        using var process = OwnedProcessRegistry.Shared.Start(info, "Music.ACE.XL");
        inspection?.Attach(process);
        var errors = new Queue<string>();
        var diagnostic = new Queue<string>();
        InspectorDeferredException? deferred = null;
        async Task Pump(StreamReader reader, bool control) {
            string? line; while ((line = await reader.ReadLineAsync()) is not null) {
                if (line.Length > 4096) line = line[..4096];
                if (control && info.RedirectStandardInput && line.StartsWith(InspectorPrefix, StringComparison.Ordinal))
                {
                    try
                    {
                        if (TryReadMemoryCheck(line, runtime.Device, out var stage, out var needed, out var available)
                            && inspection is not null)
                            await inspection.CheckAsync(stage, needed, available, token);
                    }
                    catch (InspectorDeferredException error) { deferred = error; }
                    try { await process.StandardInput.WriteLineAsync(deferred is null ? "continue" : "defer"); }
                    catch (IOException) when (process.HasExited || token.IsCancellationRequested) { }
                    continue;
                }
                if (line.StartsWith("[Hardware]", StringComparison.Ordinal)) LastHardware = line[10..].Trim();
                lock (errors) { errors.Enqueue(line); while (errors.Count > 12) errors.Dequeue(); }
                if (!control) { diagnostic.Enqueue(line); while (diagnostic.Count > 32) diagnostic.Dequeue(); }
                try { Log?.Invoke(line); }
                catch (Exception error) { OwnedProcessRegistry.Log("music_log_callback_failed", "Music.ACE.XL", detail: error.GetType().Name); }
            }
        }
        var stdout = Pump(process.StandardOutput, true); var stderr = Pump(process.StandardError, false);
        var writing = false;
        var failed = false;
        try {
            // The API deadline starts only after preparation and process launch.
            // Hashing/extracting libraries is cancellable, but is not an API hang.
            await ExecuteProcessPhaseAsync(async phaseToken => {
                var exit = process.WaitForExitAsync(phaseToken);
                var first = await Task.WhenAny(exit, stdout, stderr);
                await first;
                await Task.WhenAll(exit, stdout, stderr);
            }, probe, token);
            token.ThrowIfCancellationRequested();
            if (deferred is not null) throw deferred;
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
            writing = true;
            inspection?.Stage("output_write");
            File.Move(temporary, output, false);
            inspection?.Succeed();
        }
        catch (Exception error) {
            failed = true;
            if (inspection is not null)
            {
                InspectorOutcome? outcome = writing ? InspectorOutcome.SaveFailure
                    : !token.IsCancellationRequested && error is not OperationCanceledException && process.HasExited && process.ExitCode != 0
                        ? GenerationFailureClassifier.FromWorker(process.ExitCode,
                            stderr.IsCompletedSuccessfully ? string.Join('\n', diagnostic) : null, runtime.Device) : null;
                await inspection.FailAsync(error, token, outcome);
            }
            throw;
        }
        finally {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
            try { await Task.WhenAll(stdout, stderr); }
            catch (Exception) when (failed) { }
            finally { foreach (var path in new[] { temporary, temporary + ".receipt.json" }) if (File.Exists(path)) File.Delete(path); }
        }
    }
    private const string InspectorPrefix = "[LOPATA Inspector] ";

    internal static bool TryReadMemoryCheck(string line, string device, out string stage, out long? needed, out long? available)
    {
        stage = "";
        needed = available = null;
        if (!line.StartsWith(InspectorPrefix, StringComparison.Ordinal)) return false;
        var fields = line[InspectorPrefix.Length..].Split('|');
        if (fields.Length != 4 || fields[1] != device || fields[0] is not ("model_loading" or "generating")) return false;
        stage = fields[0];
        if (fields[2] == "?" && fields[3] == "?") return true;
        if (!long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var required) || required <= 0
            || !long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var free) || free < 0) return false;
        needed = required;
        available = free;
        return true;
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
