using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AIHub.Services;

public sealed record MusicYueRequest(string Style, string Lyrics, int LanguageSeed, int SoundSeed, int DurationSeconds = 360)
{
    public string Abc { get; init; } = "";
    public int PlanTokenLimit { get; init; } = 4096;
    public object NativeRequest => new { style = Style, lyrics = Lyrics, abc = Abc, cot = "full", duration = DurationSeconds,
        lm_seed = LanguageSeed, seed = SoundSeed, steps = 32, lm_batch_size = 1, synth_batch_size = 1,
        output_format = "wav16", abc_sampling = new { max_tokens = PlanTokenLimit, min_tokens = Math.Min(32, PlanTokenLimit) },
        semantic_sampling = new { max_tokens = Math.Min(9000, checked(DurationSeconds * 25)),
            min_tokens = Math.Min(200, checked(DurationSeconds * 25)) } };
    public int OutputReserve => checked(PlanTokenLimit + Math.Min(9000, DurationSeconds * 25));
    public void CheckContext(IMusicTokenizer tokenizer, CancellationToken token)
    {
        Validate();
        var prefix = checked(tokenizer.Count(MusicTextBudget.BuildText(Lyrics, Style), token) + 2);
        var needed = string.IsNullOrWhiteSpace(Abc) ? checked(prefix + OutputReserve)
            : checked(prefix + tokenizer.Count(Abc, token) + 2 + Math.Min(9000, DurationSeconds * 25));
        if (needed > MusicTextBudget.FillLimit) throw new InvalidDataException("Music request exceeds the context budget including generated output.");
    }
    public void Validate()
    {
        if (LanguageSeed < 0 || SoundSeed < 0 || DurationSeconds is < 1 or > 360 || PlanTokenLimit is < 32 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(DurationSeconds), "YuE2 seeds and duration must be fixed before execution.");
    }
}

/// <summary>One owned CLI process per stage. Completed plan files can survive a cancelled synthesis.</summary>
public interface IMusicYueWorker
{
    event Action<string>? Log;
    Task PlanAsync(string model, MusicYueRequest request, string requestPath, string planPath, CancellationToken token);
    Task SynthesizeAsync(string model, string decoder, MusicYueRequest request, string requestPath, string outputPath, CancellationToken token);
}
public sealed class MusicYueWorker(string runtimeDirectory) : IMusicYueWorker
{
    public event Action<string>? Log;
    public event Action<MusicHardwareChoice>? HardwareChanged;
    public async Task PlanAsync(string model, MusicYueRequest request, string requestPath, string planPath, CancellationToken token)
    {
        request.Validate();
        await PrepareAsync(model, request, token);
        await WriteRequestAsync(requestPath, request, token);
        await RunStageAsync("yue-plan.exe", model, DecoderBesideModel(model), request, requestPath, planPath, false, token);
        var plan = await File.ReadAllTextAsync(planPath, Encoding.UTF8, token);
        if (string.IsNullOrWhiteSpace(plan) || new FileInfo(planPath).Length > 1_048_576)
            throw new InvalidDataException("YuE2 produced no usable musical plan.");
    }
    public async Task SynthesizeAsync(string model, string decoder, MusicYueRequest request, string requestPath,
        string outputPath, CancellationToken token)
    {
        request.Validate();
        if (string.IsNullOrWhiteSpace(request.Abc)) throw new InvalidDataException("A saved musical plan is required before synthesis.");
        await PrepareAsync(model, request, token);
        await VerifyModelAsync(decoder, MusicComponentCatalog.DecoderId, token);
        await WriteRequestAsync(requestPath, request, token);
        await RunStageAsync("yue-synth.exe", model, decoder, request, requestPath, outputPath, true, token);
        _ = MusicWaveFile.ReadDuration(outputPath);
    }
    private async Task PrepareAsync(string model, MusicYueRequest request, CancellationToken token)
    {
        string[] components = MusicYueRuntime.IsCuda(runtimeDirectory)
            ? [MusicYueRuntime.ComponentId, MusicYueRuntime.CudaComponentId, MusicYueRuntime.VulkanComponentId, MusicComponentCatalog.ModelId, MusicComponentCatalog.DecoderId]
            : [MusicYueRuntime.ComponentId, MusicComponentCatalog.ModelId, MusicComponentCatalog.DecoderId];
        await ComponentLicenseGate.EnsureAsync(components, token);
        await MusicYueRuntime.VerifyAsync(runtimeDirectory, token);
        await VerifyModelAsync(model, MusicComponentCatalog.ModelId, token);
        if (!System.Runtime.Intrinsics.X86.Avx2.IsSupported) throw new PlatformNotSupportedException("This YuE2 CPU build requires AVX2.");
        await Task.Run(() => request.CheckContext(new MusicTokenizer(MusicTokenizerMetadata.Read(model, token)), token), token);
    }
    private static string DecoderBesideModel(string model)
    {
        // Cards retain their canonical installation layout; never guess a sibling file name.
        var modelsRoot = Directory.GetParent(Path.GetDirectoryName(model)!)!.Parent!.Parent!.FullName;
        var card = MusicComponentCatalog.CreateCards(modelsRoot).Single(c => c.ModelArtifactId == MusicComponentCatalog.DecoderId);
        return Path.Combine(card.InstallDirectory, card.Files.Single().RelativePath);
    }
    private async Task RunStageAsync(string executable, string model, string decoder, MusicYueRequest request,
        string requestPath, string outputPath, bool synthesis, CancellationToken token)
    {
        if (File.Exists(outputPath)) throw new IOException("Music stage output already exists.");
        var choice = await MusicHardwareProbe.CheckAsync(runtimeDirectory, model, decoder, request, synthesis, token, line => Log?.Invoke(line));
        await MusicGpuFallback.ExecuteAsync(choice, async selected =>
        {
            HardwareChanged?.Invoke(selected);
            var directory = selected.Device.Backend == "CPU" ? MusicYueRuntime.DirectoryForPack(MusicYueRuntime.CpuPack) : runtimeDirectory;
            await MusicYueRuntime.VerifyAsync(directory, token);
            var temporary = outputPath + "." + Guid.NewGuid().ToString("N") + ".part";
            var args = new List<string> { "--model", model, "--request", requestPath, "--out", temporary,
                "--max-seq", selected.Demand.ContextTokens.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            if (synthesis) { args.Add("--vae"); args.Add(decoder); }
            try { await RunAsync(directory, selected.Device.Name, executable, args, token); File.Move(temporary, outputPath, false); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }, async () => await MusicHardwareProbe.CheckAsync(runtimeDirectory, model, decoder, request, synthesis, token,
            line => Log?.Invoke(line), "GpuFailed"), line => Log?.Invoke(line), token);
    }
    private static async Task VerifyModelAsync(string path, string id, CancellationToken token)
    {
        var expected = MusicComponentCatalog.CreateCards("").Single(c => c.ModelArtifactId == id).Files.Single();
        if (new FileInfo(path).Length != expected.SizeBytes) throw new InvalidDataException("YuE2 model size mismatch.");
        using var file = File.OpenRead(path);
        var hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(file, token));
        if (!hash.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("YuE2 model checksum mismatch.");
    }
    private static async Task WriteRequestAsync(string path, MusicYueRequest request, CancellationToken token)
    {
        // New requests only: never silently replace the durable request of a prior attempt.
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, request.NativeRequest, cancellationToken: token);
        await stream.FlushAsync(token);
        stream.Flush(flushToDisk: true);
    }
    private async Task RunAsync(string directory, string backend, string executable, IReadOnlyList<string> arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(Path.Combine(directory, executable))
        { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
            RedirectStandardOutput = true, StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        MusicHardwareProbe.CleanEnvironment(info); info.Environment["GGML_BACKEND"] = backend;
        using var process = OwnedProcessRegistry.Shared.Start(info, "Music.YuE2");
        var tail = new StringBuilder();
        var output = DrainAsync(process.StandardOutput, false);
        var errors = DrainAsync(process.StandardError, true);
        try
        {
            await process.WaitForExitAsync(token);
            await Task.WhenAll(output, errors);
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                var diagnostic = $"YuE2 {executable} exited with {process.ExitCode}: {tail}";
                if (backend != "CPU" && MusicHardwarePolicy.IsRecoverableGpuFailure(diagnostic)) throw new MusicGpuException(diagnostic);
                throw new InvalidOperationException(diagnostic);
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                // No cancelled token: pause is confirmed only once the model process has actually exited.
                await process.WaitForExitAsync(CancellationToken.None);
            }
            await Task.WhenAll(output, errors);
        }
        async Task DrainAsync(StreamReader reader, bool error)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Length > 4096) line = line[..4096];
                if (error) { tail.AppendLine(line); if (tail.Length > 4096) tail.Remove(0, tail.Length - 4096); }
                try { Log?.Invoke(line); }
                catch (Exception callback) { OwnedProcessRegistry.Log("music_log_callback_failed", "Music.YuE2", detail: callback.GetType().Name); }
            }
        }
    }
}
