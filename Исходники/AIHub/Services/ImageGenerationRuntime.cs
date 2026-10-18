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
        var executable = executableOverride ?? ImageGenerationInstallation.Executable(cards);
        var info = new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        void Arg(string key, object value) { info.ArgumentList.Add(key); info.ArgumentList.Add(Convert.ToString(value, CultureInfo.InvariantCulture)!); }
        Arg("--prompt-file", promptFile); Arg("--output", output);
        Arg("--width", request.Width); Arg("--height", request.Height); Arg("--seed", request.Seeds[index]);
        Arg("--steps", model.Steps); Arg("--cfg-scale", model.Cfg); Arg("--sampling-method", model.Sampler);
        Arg("--batch-count", 1);
        if (model.ClipSkip > 0) Arg("--clip-skip", model.ClipSkip);
        Arg("--diffusion-model", ImageGenerationInstallation.Weight(cards, "diffusion"));
        Arg("--llm", ImageGenerationInstallation.Weight(cards, "llm"));
        info.ArgumentList.Add("--diffusion-fa"); // Legacy command; ManagedCommand adjusts only the selected newer contract.
        Arg("--vae", ImageGenerationInstallation.Weight(cards, "vae"));
        if (model.Id == "krea") Arg("--flow-shift", 1.15); // Native Krea denoiser uses the author's mu directly.
        info.ArgumentList.Add("--offload-to-cpu");
        info.ArgumentList.Add("--vae-tiling");
        return info;
    }
    public async Task GenerateAsync(ImageGenerationRequest request, int index,
        IReadOnlyList<ManagedModelArtifactCard> cards, string promptFile, string output, CancellationToken token)
        => _ = await GenerateWithReceiptAsync(request, index, cards, promptFile, output, token);

    public async Task<ImageGenerationRuntimeReceipt?> GenerateWithReceiptAsync(ImageGenerationRequest request, int index,
        IReadOnlyList<ManagedModelArtifactCard> cards, string promptFile, string output, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var setId = request.EngineSetId ?? EngineSetSelection.ActiveId;
        var selection = await SdRuntimeSelector.SelectAsync(cards, false, token, setId);
        var result = await AttemptAsync(request, index, cards, promptFile, output, selection, token);
        if (result.ExitCode != 0 && selection.UsesGpu && IsHardwareFailure(result.Log))
        {
            token.ThrowIfCancellationRequested();
            var cpu = await SdRuntimeSelector.SelectAsync(cards, true, token, setId);
            var retry = await AttemptAsync(request, index, cards, promptFile, output, cpu, token);
            result = (retry.ExitCode, result.Log + "\nCPU retry:\n" + retry.Log);
            selection = cpu;
        }
        await File.WriteAllTextAsync(Path.Combine(request.SessionDirectory, request.Id + "_" + index + ".runtime.log"), result.Log, token);
        if (result.ExitCode != 0) throw new BackgroundOperationFailedException("Generation.RuntimeError");
        return new(selection.EngineSetId, selection.Version, selection.Revision, selection.Backend, selection.ComponentId);
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
        if (!selection.UsesGpu)
        {
            info.ArgumentList.Add("--backend"); info.ArgumentList.Add("cpu");
            info.ArgumentList.Add("--params-backend"); info.ArgumentList.Add("cpu");
        }
        return info;
    }

    internal static bool IsHardwareFailure(string log) => NativeHardwareFailure.IsRecoverable(log);

    private static async Task<(int ExitCode, string Log)> AttemptAsync(ImageGenerationRequest request, int index,
        IReadOnlyList<ManagedModelArtifactCard> cards, string promptFile, string output,
        SdRuntimeSelection selection, CancellationToken token)
    {
        using var process = OwnedProcessRegistry.Shared.Start(ManagedCommand(request, index, cards, promptFile, output, selection), "ImageGeneration");
        var stdout = DrainAsync(process.StandardOutput); var stderr = DrainAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(token);
            var log = $"Backend: {selection.Revision}; engineSet={selection.EngineSetId}; version={selection.Version}; runtime={selection.ComponentId}; backend={selection.Backend}; executable={selection.Executable}\nExit: 0x{process.ExitCode:X8}\n"
                + await stdout + Environment.NewLine + await stderr;
            return (process.ExitCode, log);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
        }
    }
    private static async Task<string> DrainAsync(StreamReader reader)
    {
        var tail = new StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        { tail.Append(buffer, 0, count); if (tail.Length > 65536) tail.Remove(0, tail.Length - 65536); }
        return tail.ToString();
    }
}

public sealed class ImageGenerationRuntime(IImageGenerationWorker? worker = null)
{
    private readonly IImageGenerationWorker _worker = worker ?? new ImageGenerationNativeWorker();
    public async Task<ImageGenerationTurn> RunAsync(ImageGenerationRequest request,
        IReadOnlyList<ManagedModelArtifactCard> cards, Action? changed, CancellationToken token)
    {
        ImageGenerationCatalog.Validate(request);
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
        ImageGenerationSessionStore.AddTurn(request);
        var promptFile = Path.Combine(request.SessionDirectory, request.Id + ".input.txt");
        await File.WriteAllTextAsync(promptFile, request.Prompt, new UTF8Encoding(false), token);
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
