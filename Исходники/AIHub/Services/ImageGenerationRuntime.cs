using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using AIHub.Models;

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
    public static ProcessStartInfo Command(ImageGenerationRequest request, int index,
        IReadOnlyList<ManagedModelArtifactCard> cards, string promptFile, string output, string? executableOverride = null)
    {
        var model = ImageGenerationCatalog.Get(request.ModelId);
        ImageLora.ImageLoraCatalog.ValidateRawPrompt(request.Prompt);
        var executable = executableOverride ?? ImageGenerationInstallation.Executable(cards);
        var info = new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        void Arg(string key, object value) { info.ArgumentList.Add(key); info.ArgumentList.Add(Convert.ToString(value, CultureInfo.InvariantCulture)!); }
        Arg("--prompt-file", promptFile); Arg("--output", output);
        Arg("--width", request.Width); Arg("--height", request.Height); Arg("--seed", request.Seeds[index]);
        Arg("--steps", model.Steps); Arg("--cfg-scale", model.Cfg); Arg("--sampling-method", model.Sampler);
        if (model.Sigmas is { } sigmas) Arg("--sigmas", sigmas);
        Arg("--batch-count", 1);
        if (model.ClipSkip > 0) Arg("--clip-skip", model.ClipSkip);
        Arg("--diffusion-model", ImageGenerationInstallation.Weight(cards, "diffusion"));
        Arg("--llm", ImageGenerationInstallation.Weight(cards, "llm"));
        info.ArgumentList.Add("--diffusion-fa"); // Legacy command; ManagedCommand adjusts only the selected newer contract.
        Arg("--vae", ImageGenerationInstallation.Weight(cards, "vae"));
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
        if (!ImageGenerationCatalog.SupportsEngineSet(model, setId))
            throw new BackgroundOperationFailedException("Generation.Qwen.EngineSetRequired");
        var store = new ManagedModelLibraryStore();
        foreach (var card in cards)
            await ManagedModelRuntimeAudit.VerifyAsync(store, card.ModelArtifactId, token);
        if (cards.SingleOrDefault(card => card.ModelArtifactId == "generation-runtime") is { } legacyRuntime)
            await ImageGenerationInstallation.EnsureRuntimeAsync(legacyRuntime, token);
        var selection = await SdRuntimeSelector.SelectAsync(cards, false, token, setId, requireGpu: model.RequiresGpu);
        var result = await AttemptAsync(request, index, cards, promptFile, output, selection, model.RequiresGpu, token);
        if (!model.RequiresGpu && result.ExitCode != 0 && selection.UsesGpu && IsHardwareFailure(result.Log))
        {
            token.ThrowIfCancellationRequested();
            var cpu = await SdRuntimeSelector.SelectAsync(cards, true, token, setId);
            var retry = await AttemptAsync(request, index, cards, promptFile, output, cpu, false, token);
            result = (retry.ExitCode, result.Log + "\nCPU retry:\n" + retry.Log, retry.Placement);
            selection = cpu;
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
        return new(selection.EngineSetId, selection.Version, selection.Revision, selection.Backend, selection.ComponentId) {
            Placement = model.RequiresGpu ? result.Placement.ToString() : null,
            Schedule = model.Sigmas,
            Loras = request.Loras.Select(u => { var d = ImageLora.ImageLoraCatalog.Find(u.Id);
                return new ImageLoraApplied(u.Id, d.Repository, u.Revision, d.Files.Single().Sha256, u.Strength, d.Trigger); }).ToArray() };
    }

    internal static ProcessStartInfo ManagedCommand(ImageGenerationRequest request, int index,
        IReadOnlyList<ManagedModelArtifactCard> cards, string promptFile, string output, SdRuntimeSelection selection)
    {
        var info = Command(request, index, cards, promptFile, output, selection.Executable);
        info.FileName = selection.Executable; info.WorkingDirectory = selection.Directory;
        if (selection.EngineSetId == NativeRuntimeReleasePolicy.UpdatedSet)
        {
            info.ArgumentList.Remove("--diffusion-fa");
            info.ArgumentList.Add("--flash-attn"); info.ArgumentList.Add("off,diffusion=on");
        }
        RuntimeDeviceProbe.ClearBackendOverrides(info);
        info.ArgumentList.Remove("--offload-to-cpu");
        info.ArgumentList.Add("--auto-fit"); info.ArgumentList.Add("on");
        if (ImageGenerationCatalog.Get(request.ModelId).RequiresGpu && (long)request.Width * request.Height > 1024L * 1024)
        {
            // A negative native budget reserves this much live VRAM for the larger image.
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

    private static async Task<(int ExitCode, string Log, ImageGenerationGpuPlacement Placement)> AttemptAsync(ImageGenerationRequest request, int index,
        IReadOnlyList<ManagedModelArtifactCard> cards, string promptFile, string output,
        SdRuntimeSelection selection, bool requireGpu, CancellationToken token)
    {
        using var process = OwnedProcessRegistry.Shared.Start(ManagedCommand(request, index, cards, promptFile, output, selection), "ImageGeneration");
        var rejected = new TaskCompletionSource<ImageGenerationGpuPlacement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var placement = ImageGenerationGpuPlacement.Unknown;
        void Observe(string line)
        {
            var observed = ImageGenerationPlacementLog.Read(line);
            if (observed is ImageGenerationGpuPlacement.GpuUnavailable or ImageGenerationGpuPlacement.MemoryUnavailable)
                rejected.TrySetResult(observed);
            else if (observed != ImageGenerationGpuPlacement.Unknown)
                placement = observed;
        }
        var stdout = DrainAsync(process.StandardOutput, requireGpu ? Observe : null);
        var stderr = DrainAsync(process.StandardError, requireGpu ? Observe : null);
        try
        {
            var exit = process.WaitForExitAsync(token);
            if (requireGpu && await Task.WhenAny(exit, rejected.Task) == rejected.Task)
                Stop(process);
            await exit;
            var outputLog = await stdout + Environment.NewLine + await stderr;
            if (rejected.Task.IsCompletedSuccessfully) placement = rejected.Task.Result;
            var model = ImageGenerationCatalog.Get(request.ModelId);
            var log = $"Backend: {selection.Revision}; engineSet={selection.EngineSetId}; version={selection.Version}; runtime={selection.ComponentId}; backend={selection.Backend}; executable={selection.Executable}\nExit: 0x{process.ExitCode:X8}\n"
                + (requireGpu ? $"Placement: {placement}; model={request.ModelId}; seed={request.Seeds[index]}; size={request.Width}x{request.Height}\n"
                    + $"Sampling: {model.Sampler}, steps={model.Steps}, cfg={model.Cfg}, sigmas={model.Sigmas ?? "automatic"}\n" : "")
                + outputLog;
            return (process.ExitCode, log, placement);
        }
        finally
        {
            Stop(process);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
        }
    }
    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) when (process.HasExited) { }
        catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
    }
    private static async Task<string> DrainAsync(StreamReader reader, Action<string>? observe = null)
    {
        var tail = new StringBuilder(); var buffer = new char[4096]; int count; var rejected = false; var previous = "";
        var applied = new HashSet<string>(StringComparer.Ordinal);
        while ((count = await reader.ReadAsync(buffer)) > 0)
        { var chunk = previous + new string(buffer, 0, count); rejected |= LoraRejected(chunk);
            var lines = chunk.Split('\n');
            foreach (var line in lines)
                if (applied.Count < 64 && line.Contains("LoRA tensors have been applied", StringComparison.Ordinal))
                    applied.Add(line.Trim());
            if (observe is not null)
                for (var i = 0; i < lines.Length - 1; i++)
                    if (lines[i].Contains("auto-fit:", StringComparison.Ordinal)) observe(lines[i]);
            previous = chunk[^Math.Min(chunk.Length, 4096)..];
            tail.Append(buffer, 0, count); if (tail.Length > 65536) tail.Remove(0, tail.Length - 65536); }
        return (rejected ? "LOPATA_LORA_REJECTED\n" : "") + string.Join('\n', applied) + "\n" + tail;
    }
}

public sealed class ImageGenerationRuntime(IImageGenerationWorker? worker = null)
{
    private readonly IImageGenerationWorker _worker = worker ?? new ImageGenerationNativeWorker();
    public async Task<ImageGenerationTurn> RunAsync(ImageGenerationRequest request,
        IReadOnlyList<ManagedModelArtifactCard> cards, Action? changed, CancellationToken token)
    {
        ImageGenerationCatalog.Validate(request);
        ImageLora.ImageLoraCatalog.ValidateRawPrompt(request.Prompt);
        ImageLora.ImageLoraCatalog.ValidateUses(request.Loras, request.ModelId);
        if (!ImageOutputDimensions.IsSupported(request.OutputLongestSide)) throw new ArgumentException("Generation.InvalidOutputSize");
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
            // A pause may arrive after a valid PNG was published but before the checkpoint.
            if (!IsValidImage(target, request))
            {
                var partial = Path.Combine(request.SessionDirectory, request.Id + "_" + i + ".pending.png");
                if (File.Exists(partial)) File.Delete(partial);
                receipt = await _worker.GenerateWithReceiptAsync(request, i, cards, promptFile, partial, token);
                token.ThrowIfCancellationRequested();
                if (!IsValidImage(partial, request)) throw new BackgroundOperationFailedException("Generation.InvalidImage");
                if (receipt is not null) await SaveReceiptAsync(receiptPath, receipt, token);
                if (request.Metadata is not null)
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
                await Task.Run(() => ImageGenerationExport.Save(request, result), token);
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
