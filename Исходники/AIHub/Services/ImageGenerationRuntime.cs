using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using AIHub.Models;
using Lopata.Inspector;
using AIHub.Services.VisualScenario;

namespace AIHub.Services;

public interface IImageGenerationWorker
{
    Task GenerateAsync(ImageGenerationRequest request, int index, IReadOnlyList<ManagedModelArtifactCard> cards,
        string promptFile, string output, CancellationToken token);
    async Task<ImageGenerationRuntimeReceipt?> GenerateWithReceiptAsync(ImageGenerationRequest request, int index,
        IReadOnlyList<ManagedModelArtifactCard> cards, string promptFile, string output, CancellationToken token)
    { await GenerateAsync(request, index, cards, promptFile, output, token); return null; }
}

public sealed class ImageGenerationNativeWorker : IImageGenerationWorker
{
    private readonly IProgress<BitmapSource>? _previewFrames;
    private readonly ImageGenerationResident? _resident;
    private readonly Action<bool>? _starting;

    public ImageGenerationNativeWorker() { }

    internal ImageGenerationNativeWorker(IProgress<BitmapSource>? previewFrames, ImageGenerationResident? resident = null,
        Action<bool>? starting = null)
    {
        _previewFrames = previewFrames;
        _resident = resident;
        _starting = starting;
    }

    public static ProcessStartInfo Command(ImageGenerationRequest request, int index,
        IReadOnlyList<ManagedModelArtifactCard> cards, string promptFile, string output, string? executableOverride = null)
        => CreateCommand(request, index, cards, promptFile, output, executableOverride, false);

    private static ProcessStartInfo CreateCommand(ImageGenerationRequest request, int index,
        IReadOnlyList<ManagedModelArtifactCard> cards, string promptFile, string output, string? executableOverride, bool server)
    {
        var model = ImageGenerationCatalog.Get(request.ModelId);
        ImageLora.ImageLoraCatalog.ValidateRawPrompt(request.Prompt);
        var executable = executableOverride ?? ImageGenerationInstallation.Executable(cards);
        var info = new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        void Arg(string key, object value) { info.ArgumentList.Add(key); info.ArgumentList.Add(Convert.ToString(value, CultureInfo.InvariantCulture)!); }
        if (!server)
        {
            Arg("--prompt-file", promptFile); Arg("--output", output);
            Arg("--width", request.Width); Arg("--height", request.Height); Arg("--seed", request.Seeds[index]);
        }
        else
        {
            Arg("--width", model.DefaultWidth); Arg("--height", model.DefaultHeight);
        }
        Arg("--steps", model.Steps); Arg("--cfg-scale", model.Cfg); Arg("--sampling-method", model.Sampler);
        if (model.Sigmas is { } sigmas) Arg("--sigmas", sigmas);
        Arg("--batch-count", 1);
        if (model.ClipSkip > 0) Arg("--clip-skip", model.ClipSkip);
        Arg("--diffusion-model", ImageGenerationInstallation.Weight(cards, "diffusion"));
        Arg("--llm", ImageGenerationInstallation.Weight(cards, "llm"));
        info.ArgumentList.Add("--diffusion-fa"); // Legacy command; ManagedCommand adjusts only the selected newer contract.
        Arg("--vae", ImageGenerationInstallation.Weight(cards, "vae"));
        if (request.ReferenceImages.Length > 0)
        {
            if (model.Id == "qwen-image-21-base-gguf")
                Arg("--llm_vision", ImageGenerationInstallation.Weight(cards, "llm_vision"));
            if (!server)
            {
                foreach (var reference in request.ReferenceImages) Arg("-r", reference);
                var refOptions = ImageGenerationReferences.NativeOptions(request);
                if (refOptions.Length > 0) Arg("--ref-image-args", refOptions);
            }
        }
        if (model.Id == "krea") Arg("--flow-shift", 1.15); // Native Krea denoiser uses the author's mu directly.
        info.ArgumentList.Add("--offload-to-cpu");
        info.ArgumentList.Add("--vae-tiling");
        ImageLora.ImageLoraCatalog.ValidateUses(request.Loras, request.ModelId);
        if (request.Loras.Length > 0) {
            Arg("--lora-model-dir", Path.Combine(request.ModelsRoot, "ImageLoRA"));
            Arg("--lora-apply-mode", "at_runtime");
        }
        return info;
    }
    public async Task GenerateAsync(ImageGenerationRequest request, int index,
        IReadOnlyList<ManagedModelArtifactCard> cards, string promptFile, string output, CancellationToken token)
        => _ = await GenerateWithReceiptAsync(request, index, cards, promptFile, output, token);

    public async Task<ImageGenerationRuntimeReceipt?> GenerateWithReceiptAsync(ImageGenerationRequest request, int index,
        IReadOnlyList<ManagedModelArtifactCard> cards, string promptFile, string output, CancellationToken token)
    {
        using var auditOperation = ComponentAudit.CaptureOperation();
        token.ThrowIfCancellationRequested();
        var model = ImageGenerationCatalog.Get(request.ModelId);
        var setId = request.EngineSetId ?? EngineSetSelection.ActiveId;
        var selection = await SelectVerifiedRuntimeAsync(request, cards, token);
        using var prepared = ImageGenerationReferences.NeedsPreparation(request)
            ? await Task.Run(() => ImageGenerationReferences.Prepare(request.ReferenceImages, token), token)
            : null;
        if (prepared is not null) request = request with { ReferenceImages = prepared.Paths };
        var resident = _resident;
        var inspection = ImageInspectorAttempt.Create(request, selection, cards, index);
        (int ExitCode, string Log, ImageGenerationGpuPlacement Placement) result;
        try
        {
            if (resident is not null && CanUseServer(request, selection))
                result = await resident.GenerateAsync(request, index, cards, output, selection, _previewFrames, token, _starting, inspection);
            else
            {
                if (resident is not null) await resident.RetireAsync();
                result = await AttemptAsync(request, index, cards, promptFile, output, selection, model.RequiresGpu, token, inspection);
            }
        }
        catch (Exception error)
        {
            inspection?.Complete(ImageInspectorAttempt.Evaluate(error, token));
            throw;
        }
        var retryOnCpu = !model.RequiresGpu && result.ExitCode != 0 && selection.UsesGpu && IsHardwareFailure(result.Log);
        inspection?.Complete(ImageInspectorAttempt.Evaluate(result.ExitCode, result.Log, result.Placement, selection.UsesGpu));
        if (retryOnCpu)
        {
            token.ThrowIfCancellationRequested();
            var cpu = await SdRuntimeSelector.SelectAsync(cards, true, token, setId);
            var cpuInspection = ImageInspectorAttempt.Create(request, cpu, cards, index, retry: true);
            try
            {
                var retry = await AttemptAsync(request, index, cards, promptFile, output, cpu, false, token, cpuInspection);
                cpuInspection?.Complete(ImageInspectorAttempt.Evaluate(retry.ExitCode, retry.Log, retry.Placement, false));
                result = (retry.ExitCode, result.Log + "\nCPU retry:\n" + retry.Log, retry.Placement);
                selection = cpu;
            }
            catch (Exception error)
            {
                cpuInspection?.Complete(ImageInspectorAttempt.Evaluate(error, token));
                throw;
            }
        }
        await File.WriteAllTextAsync(Path.Combine(request.SessionDirectory, request.Id + "_" + index + ".runtime.log"), result.Log, token);
        if (model.RequiresGpu)
        {
            if (result.Placement == ImageGenerationGpuPlacement.GpuUnavailable)
                throw new BackgroundOperationFailedException("Generation.Qwen.GpuUnavailable");
            if (result.Placement == ImageGenerationGpuPlacement.MemoryUnavailable
                || (result.ExitCode != 0 && (result.Log.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
                    || result.Log.Contains("failed to allocate", StringComparison.OrdinalIgnoreCase)
                    || result.Log.Contains("VK_ERROR_OUT_OF", StringComparison.OrdinalIgnoreCase))))
                throw new BackgroundOperationFailedException("Generation.Qwen.MemoryUnavailable");
            if (result.ExitCode != 0 || result.Placement == ImageGenerationGpuPlacement.Unknown)
                throw new BackgroundOperationFailedException("Generation.Qwen.RuntimeError");
        }
        if (result.ExitCode != 0) throw new BackgroundOperationFailedException("Generation.RuntimeError");
        if (request.Loras.Length > 0 && (LoraRejected(result.Log) || !LorasApplied(result.Log, request.Loras)))
            throw new BackgroundOperationFailedException("Generation.Lora.NotApplied");
        return new(selection.PackageRevision is null ? selection.EngineSetId : null,
            selection.Version, selection.Revision, selection.Backend, selection.ComponentId) {
            Placement = model.RequiresGpu ? result.Placement.ToString() : null,
            Schedule = model.Sigmas,
            ArtistRevision = cards.FirstOrDefault(card => card.Files.Any(file => file.Purpose == "diffusion"))?.Revision,
            Loras = request.Loras.Select(u => { var d = ImageLora.ImageLoraCatalog.Find(u.Id);
                return new ImageLoraApplied(u.Id, d.Repository, u.Revision, d.Files.Single().Sha256, u.Strength, d.Trigger); }).ToArray() };
    }

    internal async Task<bool> WarmAsync(ImageGenerationRequest request, IReadOnlyList<ManagedModelArtifactCard> cards,
        CancellationToken token)
    {
        if (_resident is not { } resident) return false;
        using var audit = ComponentAudit.CaptureOperation();
        var selection = await SelectVerifiedRuntimeAsync(request, cards, token);
        if (!CanUseServer(request, selection)) return false;
        var (width, height) = ImageGenerationResident.WarmupSize(ImageGenerationCatalog.Get(request.ModelId));
        var inspection = ImageInspectorAttempt.Create(request with { Width = width, Height = height },
            selection, cards, 0, warmup: true);
        try
        {
            await resident.WarmAsync(request, cards, selection, token, inspection);
            inspection?.Complete(InspectorOutcome.Success);
        }
        catch (Exception error)
        {
            inspection?.Complete(ImageInspectorAttempt.Evaluate(error, token));
            throw;
        }
        return true;
    }

    internal static bool CanUseServer(ImageGenerationRequest request, SdRuntimeSelection selection) =>
        request.Loras.Length == 0 && selection.SupportsModernArguments
        && File.Exists(Path.Combine(selection.Directory, "sd-server.exe"));

    private static async Task<SdRuntimeSelection> SelectVerifiedRuntimeAsync(ImageGenerationRequest request,
        IReadOnlyList<ManagedModelArtifactCard> cards, CancellationToken token)
    {
        var model = ImageGenerationCatalog.Get(request.ModelId);
        var setId = request.EngineSetId ?? EngineSetSelection.ActiveId;
        if (!ImageGenerationCatalog.SupportsEngineSet(model, setId))
            throw new BackgroundOperationFailedException("Generation.Qwen.EngineSetRequired");
        var store = new ManagedModelLibraryStore();
        foreach (var card in cards)
            await ManagedModelRuntimeAudit.VerifyAsync(store, card.ModelArtifactId, token);
        if (cards.SingleOrDefault(card => card.ModelArtifactId == "generation-runtime") is { } legacyRuntime)
            await ImageGenerationInstallation.EnsureRuntimeAsync(legacyRuntime, token);
        return await SdRuntimeSelector.SelectAsync(cards, false, token, setId, requireGpu: model.RequiresGpu);
    }

    internal static ProcessStartInfo ManagedCommand(ImageGenerationRequest request, int index,
        IReadOnlyList<ManagedModelArtifactCard> cards, string promptFile, string output, SdRuntimeSelection selection,
        string? previewPattern = null, bool server = false)
    {
        var executable = server ? Path.Combine(selection.Directory, "sd-server.exe") : selection.Executable;
        var info = CreateCommand(request, index, cards, promptFile, output, executable, server);
        if (selection.SupportsModernArguments)
        {
            info.ArgumentList.Remove("--diffusion-fa");
            info.ArgumentList.Add("--flash-attn"); info.ArgumentList.Add("off,diffusion=on");
            if (previewPattern is not null)
            {
                info.ArgumentList.Add("--preview"); info.ArgumentList.Add("proj");
                info.ArgumentList.Add("--preview-interval"); info.ArgumentList.Add("1");
                info.ArgumentList.Add("--preview-path"); info.ArgumentList.Add(previewPattern);
            }
        }
        RuntimeDeviceProbe.ClearBackendOverrides(info);
        info.ArgumentList.Remove("--offload-to-cpu");
        info.ArgumentList.Add("--auto-fit"); info.ArgumentList.Add("on");
        var model = ImageGenerationCatalog.Get(request.ModelId);
        var miniMax = model.Id == MiniMaxImageRuntime.ModelId;
        if (selection.UsesGpu && server && miniMax)
        {
            // Leave space for driver allocations and workspace during the transition from encoding to sampling.
            info.ArgumentList.Add("--max-vram"); info.ArgumentList.Add("-4");
        }
        else if (selection.UsesGpu && (model.RequiresGpu || miniMax)
            && (server || (long)request.Width * request.Height > 1024L * 1024))
        {
            // The server can receive a larger job without changing its startup arguments.
            info.ArgumentList.Add("--max-vram");
            info.ArgumentList.Add("-1");
        }
        if (!selection.UsesGpu)
        {
            info.ArgumentList.Add("--backend"); info.ArgumentList.Add("cpu");
            info.ArgumentList.Add("--params-backend"); info.ArgumentList.Add("cpu");
        }
        return info;
    }

    internal static bool IsHardwareFailure(string log) => NativeHardwareFailure.IsRecoverable(log);
    internal static bool LoraRejected(string log) => System.Text.RegularExpressions.Regex.IsMatch(log,
        @"unused lora tensor|skipp(?:ed|ing)[^\r\n]*lora|(?:failed|unable|cannot|could not)[^\r\n]*lora|lora[^\r\n]*(?:not found|failed|incompatible|mismatch)|Only \([^\r\n]+LoRA tensors|LOPATA_LORA_REJECTED",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    internal static bool LorasApplied(string log, ImageLora.ImageLoraUse[] uses)
    {
        var lines = log.Replace('\\','/').Split('\n');
        return uses.All(u => lines.Any(line => line.Contains("/" + u.Id + "/" + u.Revision[..12] + "/adapter.safetensors", StringComparison.OrdinalIgnoreCase)
            && System.Text.RegularExpressions.Regex.Match(line, @"\((\d+)\s*/\s*(\d+)\) LoRA tensors have been applied") is { Success: true } match
            && match.Groups[1].Value == match.Groups[2].Value && match.Groups[1].Value != "0"));
    }

    private async Task<(int ExitCode, string Log, ImageGenerationGpuPlacement Placement)> AttemptAsync(ImageGenerationRequest request, int index,
        IReadOnlyList<ManagedModelArtifactCard> cards, string promptFile, string output,
        SdRuntimeSelection selection, bool requireGpu, CancellationToken token, ImageInspectorAttempt? inspection)
    {
        if (inspection is not null)
        {
            inspection.SetWarm(false);
            await inspection.BeginAsync(token);
        }
        using var preview = _previewFrames is not null && selection.SupportsModernArguments
            ? ImageGenerationLivePreview.TryCreate() : null;
        using var process = OwnedProcessRegistry.Shared.Start(
            ManagedCommand(request, index, cards, promptFile, output, selection, preview?.OutputPattern), "ImageGeneration");
        inspection?.Attach(process);
        using var stopPreview = preview is null ? null : new CancellationTokenSource();
        var watchPreview = preview?.WatchAsync(_previewFrames!, stopPreview!.Token) ?? Task.CompletedTask;
        var rejected = new TaskCompletionSource<ImageGenerationGpuPlacement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var placement = ImageGenerationGpuPlacement.Unknown;
        void Observe(string line)
        {
            inspection?.ObserveNativeProgress(line);
            if (!requireGpu || !line.Contains("auto-fit:", StringComparison.Ordinal)) return;
            var observed = ImageGenerationPlacementLog.Read(line);
            if (observed is ImageGenerationGpuPlacement.GpuUnavailable or ImageGenerationGpuPlacement.MemoryUnavailable)
                rejected.TrySetResult(observed);
            else if (observed != ImageGenerationGpuPlacement.Unknown)
                placement = observed;
        }
        var stdout = DrainAsync(process.StandardOutput, requireGpu || inspection is not null ? Observe : null);
        var stderr = DrainAsync(process.StandardError, requireGpu || inspection is not null ? Observe : null);
        try
        {
            var exit = process.WaitForExitAsync(token);
            if (requireGpu && await Task.WhenAny(exit, rejected.Task) == rejected.Task)
                Stop(process);
            await exit;
            var outputLog = await stdout + Environment.NewLine + await stderr;
            if (rejected.Task.IsCompletedSuccessfully) placement = rejected.Task.Result;
            var model = ImageGenerationCatalog.Get(request.ModelId);
            var log = $"Backend: {selection.Revision}; engineSet={(selection.PackageRevision is null ? selection.EngineSetId : "dedicated")}; version={selection.Version}; runtime={selection.ComponentId}; backend={selection.Backend}; executable={selection.Executable}\nExit: 0x{process.ExitCode:X8}\n"
                + (requireGpu ? $"Placement: {placement}; model={request.ModelId}; seed={request.Seeds[index]}; size={request.Width}x{request.Height}\n"
                    : "")
                + (requireGpu || selection.PackageRevision is not null
                    ? $"Sampling: {model.Sampler}, steps={model.Steps}, cfg={model.Cfg}, sigmas={model.Sigmas ?? "automatic"}\n" : "")
                + outputLog;
            return (process.ExitCode, log, placement);
        }
        finally
        {
            stopPreview?.Cancel();
            inspection?.Emit(InspectorEventKind.ReleaseRequested);
            Stop(process);
            await process.WaitForExitAsync(CancellationToken.None);
            inspection?.Emit(InspectorEventKind.ProcessExited);
            await Task.WhenAll(stdout, stderr, watchPreview);
        }
    }
    internal static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) when (process.HasExited) { }
        catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
    }
    private static async Task<string> DrainAsync(StreamReader reader, Action<string>? observe = null)
    {
        var tail = new StringBuilder(); var buffer = new char[4096]; int count; var rejected = false; var previous = "";
        var observerTail = "";
        var applied = new HashSet<string>(StringComparer.Ordinal);
        while ((count = await reader.ReadAsync(buffer)) > 0)
        { var chunk = previous + new string(buffer, 0, count); rejected |= LoraRejected(chunk);
            var lines = chunk.Split('\n');
            foreach (var line in lines)
                if (applied.Count < 64 && line.Contains("LoRA tensors have been applied", StringComparison.Ordinal))
                    applied.Add(line.Trim());
            if (observe is not null)
            {
                var received = (observerTail + new string(buffer, 0, count)).Split('\r', '\n');
                for (var i = 0; i < received.Length - 1; ++i)
                    if (received[i].Length > 0) observe(received[i]);
                observerTail = received[^1][^Math.Min(received[^1].Length, 4096)..];
            }
            previous = chunk[^Math.Min(chunk.Length, 4096)..];
            tail.Append(buffer, 0, count); if (tail.Length > 65536) tail.Remove(0, tail.Length - 65536); }
        if (observe is not null && observerTail.Length > 0) observe(observerTail);
        return (rejected ? "LOPATA_LORA_REJECTED\n" : "") + string.Join('\n', applied) + "\n" + tail;
    }
}

public sealed class ImageGenerationRuntime(IImageGenerationWorker? worker = null)
{
    private readonly IImageGenerationWorker _worker = worker ?? new ImageGenerationNativeWorker();
    internal Task<bool> WarmAsync(ImageGenerationRequest request, IReadOnlyList<ManagedModelArtifactCard> cards,
        CancellationToken token) => _worker is ImageGenerationNativeWorker native
        ? native.WarmAsync(request, cards, token) : Task.FromResult(false);
    public Task<ImageGenerationTurn> RunAsync(ImageGenerationRequest request,
        IReadOnlyList<ManagedModelArtifactCard> cards, Action? changed, CancellationToken token) =>
        RunAsync(request, cards, changed, token, recordBackgroundCheckpoint: true);

    internal async Task<ImageGenerationTurn> RunAsync(ImageGenerationRequest request,
        IReadOnlyList<ManagedModelArtifactCard> cards, Action? changed, CancellationToken token, bool recordBackgroundCheckpoint)
    {
        ImageGenerationCatalog.Validate(request);
        ImageLora.ImageLoraCatalog.ValidateRawPrompt(request.Prompt);
        ImageLora.ImageLoraCatalog.ValidateUses(request.Loras, request.ModelId);
        if (!ImageOutputDimensions.IsSupported(request.OutputLongestSide)) throw new ArgumentException("Generation.InvalidOutputSize");
        var senseNova = request.ModelId == VisualSenseNova.Id;
        if (_worker is ImageGenerationNativeWorker)
        {
            var saved = ImageGenerationSessionStore.Load(request.SessionDirectory).Turns.SingleOrDefault(t => t.Request.Id == request.Id);
            // Old pending checkpoints predate set selection and belong to the legacy contract.
            var capturedSet = saved is not null ? saved.Request.EngineSetId ?? NativeRuntimeReleasePolicy.LegacySet
                : request.EngineSetId ?? EngineSetSelection.ActiveId;
            NativeRuntimeReleasePolicy.ValidateSetId(capturedSet);
            request = request with { EngineSetId = capturedSet };
        }
        using var loraLease = _worker is ImageGenerationNativeWorker
            ? await ImageLora.ImageLoraAssets.AcquireAsync(request.ModelsRoot, request.Loras, request.ModelId, token) : null;
        ImageGenerationSessionStore.AddTurn(request);
        var promptFile = Path.Combine(request.SessionDirectory, request.Id + ".input.txt");
        await File.WriteAllTextAsync(promptFile, request.Prompt, new UTF8Encoding(false), token);
        if (request.Loras.Length > 0) {
            promptFile = Path.Combine(request.SessionDirectory, request.Id + ".lora-input.txt");
            await File.WriteAllTextAsync(promptFile, ImageLora.ImageLoraCatalog.EffectivePrompt(request.Prompt, request.Loras), new UTF8Encoding(false), token);
        }
        for (var i = 0; i < request.Seeds.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var target = ImageGenerationSessionStore.ResultPath(request, i);
            var existing = ImageGenerationSessionStore.Load(request.SessionDirectory).Turns.Single(t => t.Request.Id == request.Id);
            var previous = existing.Results.SingleOrDefault(r => r.Index == i);
            var receiptPath = Path.Combine(request.SessionDirectory, request.Id + "_" + i + ".runtime.json");
            var receipt = previous?.Runtime ?? (File.Exists(receiptPath)
                ? JsonSerializer.Deserialize<ImageGenerationRuntimeReceipt>(await File.ReadAllTextAsync(receiptPath, token)) : null);
            if (senseNova) receipt = VisualSenseNova.NormalizeReceipt(receipt);
            // A pause may arrive after a valid PNG was published but before the checkpoint.
            if (!IsValidImage(target, request))
            {
                var partial = Path.Combine(request.SessionDirectory, request.Id + "_" + i + ".pending.png");
                // A saved SenseNova receipt proves the staged frame passed validation before export.
                if (!senseNova || receipt is not { ComponentId: VisualSenseNova.Profile, EngineSetId: null }
                    || !IsValidImage(partial, request))
                {
                    if (File.Exists(partial)) File.Delete(partial);
                    if (senseNova && File.Exists(receiptPath))
                    {
                        Lopata.Updates.SafeUpdatePath.RejectLinks(receiptPath);
                        File.Delete(receiptPath);
                    }
                    receipt = await _worker.GenerateWithReceiptAsync(request, i, cards, promptFile, partial, token);
                    token.ThrowIfCancellationRequested();
                    if (!IsValidImage(partial, request)) throw new BackgroundOperationFailedException("Generation.InvalidImage");
                }
                if (receipt is not null) await SaveReceiptAsync(receiptPath, receipt, token);
                if (request.Metadata is not null || request.OutputFormat != ImageGenerationFileFormat.Png)
                {
                    var metadata = ImageGenerationMetadata.Create(request, i, DateTimeOffset.Now, receipt);
                    await Task.Run(() => PngMetadataWriter.Write(partial, metadata, token), token);
                }
                token.ThrowIfCancellationRequested();
                File.Move(partial, target, true);
            }
            var result = previous ?? new ImageGenerationResult(i, Path.GetFileName(target), request.Seeds[i]) { Runtime = receipt };
            if (previous is null) ImageGenerationSessionStore.PutResult(request, result);
            result = await Task.Run(() => ImageGenerationOutput.Prepare(request, result, token), token);
            if (result.ProcessingError is null)
                result = await Task.Run(() => ImageGenerationExport.Save(request, result), token);
            if (ApplicationInspector.DescribeImage(request, i * 2 + 1) is { } inspectorResult)
            {
                var saved = result.ProcessingError is null && result.ExportError is null;
                ApplicationInspector.Publish(inspectorResult with
                {
                    Kind = InspectorEventKind.Saved,
                    Stage = "output_write",
                    Outcome = saved ? InspectorOutcome.Success : InspectorOutcome.SaveFailure
                });
            }
            if (recordBackgroundCheckpoint)
                ApplicationBackgroundOperations.Current?.SaveCheckpoint(new { request.Id, Completed = i + 1 });
            changed?.Invoke();
        }
        return ImageGenerationSessionStore.Load(request.SessionDirectory).Turns.Single(t => t.Request.Id == request.Id);
    }
    public static bool IsValidImage(string path, ImageGenerationRequest request)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var file = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return decoder.Frames.Count == 1 && decoder.Frames[0].PixelWidth == request.Width && decoder.Frames[0].PixelHeight == request.Height;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or FormatException) { return false; }
    }

    private static async Task SaveReceiptAsync(string destination, ImageGenerationRuntimeReceipt receipt, CancellationToken token)
    {
        Lopata.Updates.SafeUpdatePath.RejectLinks(destination);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(receipt), token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
