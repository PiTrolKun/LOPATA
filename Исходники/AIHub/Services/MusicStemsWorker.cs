using System.Globalization;
using System.IO;
using System.Text.Json;

namespace AIHub.Services;

public interface IMusicStemsWorker
{
    Task<MusicStemReceipt> SeparateAsync(string source, string sourceHash, string folder, Action<string> log, CancellationToken token);
}

public sealed class MusicStemsWorker(string requestedDevice = "auto") : IMusicStemsWorker
{
    public async Task<MusicStemReceipt> SeparateAsync(string source, string sourceHash, string folder, Action<string> log, CancellationToken token)
    {
        using var auditOperation = ComponentAudit.CaptureOperation();
        using var auditLog = ComponentAudit.CaptureLog(log);
        log("[Stems] Preparing HT-Demucs / ONNX separation.");
        await MusicStemsCatalog.PrepareAsync(false, null, token, verifyPython: false);
        await MusicOutputPublisher.MatchHashAsync(source, sourceHash, token);
        var runtime = await ManagedPythonRuntime.ResolveAsync(requestedDevice, token,
            new HashSet<string> { HardwareRuntimeCatalog.PythonCudaId, HardwareRuntimeCatalog.PythonCuda128Id }, log);
        // Upstream HT-Demucs can consume considerably more memory than its weight file.
        if (runtime.Device != "cpu" && runtime.FreeBytes < 8L * 1024 * 1024 * 1024)
            runtime = await ManagedPythonRuntime.ResolveAsync("cpu", token, log: log);
        Directory.CreateDirectory(folder);
        var input = Path.Combine(folder, "input.wav");
        var audio = MusicAudioRuntime.Default; await audio.PrepareAsync(token);
        await audio.RunAsync(false, ["-nostdin", "-v", "error", "-n", "-i", source, "-map", "0:a:0", "-ar", "44100", "-ac", "2", "-c:a", "pcm_s16le", "-f", "wav", input], token);
        var manager = new ComponentManager();
        if (runtime.Device != "cpu") await MusicStemsCatalog.VerifyCudaAsync(manager, token);
        for (var attempt = 0; attempt < 2; attempt++) {
            var sub = Path.Combine(folder, "attempt-" + attempt); Directory.CreateDirectory(sub);
            var voice = Path.Combine(sub, "voice-44100.wav"); var music = Path.Combine(sub, "music-44100.wav");
            var result = Path.Combine(sub, "result.json"); var request = Path.Combine(sub, "request.json");
            await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new {
                schema = 1, input, voice, music, output = result, model = MusicStemsCatalog.ModelPath(manager),
                import_roots = MusicStemsCatalog.ImportRoots(manager, runtime.Device != "cpu"),
                device = runtime.Device, cuda_dlls = runtime.Device == "cpu" ? "" : Path.Combine(Path.GetDirectoryName(runtime.Python)!, "Lib", "site-packages", "torch", "lib"),
                gpu_memory_limit = Math.Max(0, (long)(runtime.FreeBytes * .7)), threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8)
            }), token);
            var outcome = await MusicLyricsWorkerProcess.RunAsync(runtime.Python, Path.Combine(AppContext.BaseDirectory, "Tools", "music_stems.py"), request, log, token, "Music.Separation",
                TimeSpan.FromMinutes(Math.Clamp(MusicWaveFile.ReadDuration(source).TotalSeconds / 3, 30, 240)));
            if (attempt == 0 && runtime.Device != "cpu" && outcome.ExitCode == 42 && outcome.Error.Contains("STEMS_HARDWARE_FAILURE", StringComparison.Ordinal)) {
                log("[Stems] GPU provider failed; retrying once on CPU.");
                runtime = await ManagedPythonRuntime.ResolveAsync("cpu", token, log: log); continue;
            }
            if (outcome.ExitCode != 0) throw new IOException("Separation worker failed: " + outcome.Error);
            using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(result, token));
            var gain = receipt.RootElement.GetProperty("gain").GetDouble();
            if (!double.IsFinite(gain) || gain is <= 0 or > 1) throw new InvalidDataException("Invalid separation gain.");
            var frames = (long)Math.Round(MusicWaveFile.ReadDuration(source).TotalSeconds * 48000);
            var paths = new[] { Path.Combine(sub, "voice.wav"), Path.Combine(sub, "music.wav") };
            for (var i = 0; i < 2; i++) {
                var raw = i == 0 ? voice : music;
                if (new FileInfo(raw).Length != receipt.RootElement.GetProperty("samples").GetInt64() * 4 + 44) throw new InvalidDataException("Truncated separated audio.");
                await audio.RunAsync(false, ["-nostdin", "-v", "error", "-n", "-i", raw,
                    "-ar", "48000", "-ac", "2", "-c:a", "pcm_s16le", "-f", "wav", paths[i]], token);
                MusicPcm16.MatchLength(paths[i], frames);
                if (Math.Abs(MusicWaveFile.ReadDuration(paths[i]).TotalSeconds - frames / 48000d) > 1d / 48000) throw new InvalidDataException("Separated clock mismatch.");
            }
            return new(paths[0], paths[1], await MusicOutputPublisher.HashAsync(paths[0], token),
                await MusicOutputPublisher.HashAsync(paths[1], token), sourceHash,
                receipt.RootElement.GetProperty("provider").GetString()!, runtime.Device, gain) {
                    Runtime = receipt.RootElement.GetProperty("runtime").GetString()! };
        }
        throw new IOException("Separation device attempts exhausted.");
    }
}
