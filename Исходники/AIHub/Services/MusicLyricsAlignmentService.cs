using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Offline whole-line CTC search; compatible managed GPU first, CPU fallback.</summary>
public sealed class MusicLyricsAlignmentService(ComponentManager? components = null, MusicAudioRuntime? audio = null)
{
    public const string Provider = "GigaAM-v3-CTC/whole-line-search";
    public const string LegacyProvider = "whisper.cpp/source-occurrence-match";
    public const string MatcherVersion = "gigaam-whole-line-1";
    public const string ProviderVersion = "v3_ctc@7447938d791c4f3e643386ee22c33777004293a5/whole-line-1";
    public static readonly string[] RequiredComponents = [.. MusicLyricsComponentCatalog.ComponentIds];
    public event Action<string>? Log;
    private readonly ComponentManager _components = components ?? new();
    private readonly MusicAudioRuntime _audio = audio ?? MusicAudioRuntime.Default;

    public async Task<MusicLyricsAlignmentResult> AlignAsync(string audioPath, string lyrics, string workDirectory, CancellationToken token,
        string speechLanguage = "auto")
    {
        using var auditOperation = ComponentAudit.CaptureOperation();
        using var auditLog = ComponentAudit.CaptureLog(line => Log?.Invoke(line));
        audioPath = Path.GetFullPath(audioPath);
        if (!File.Exists(audioPath)) throw new FileNotFoundException("Lyrics alignment audio is missing.", audioPath);
        if (string.IsNullOrWhiteSpace(lyrics)) throw new InvalidDataException("No source lyrics to align.");
        if (speechLanguage != "auto" && (speechLanguage.Length is < 2 or > 3 || !speechLanguage.All(char.IsAsciiLetterLower)))
            throw new ArgumentException("Invalid lyrics language.", nameof(speechLanguage));
        var root = Path.GetFullPath(workDirectory);
        Directory.CreateDirectory(root);
        var attempt = Path.Combine(root, "attempt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attempt);
        var wave = Path.Combine(attempt, "audio.wav"); var resultPath = Path.Combine(attempt, "alignment.json");
        var logs = new StringBuilder();
        void Emit(string message) {
            lock (logs) { if (logs.Length < 1_000_000) logs.AppendLine(message); }
            try { Log?.Invoke(message); }
            catch (Exception error) { OwnedProcessRegistry.Log("music_log_callback_failed", "Music.LyricsAlignment", detail: error.GetType().Name); }
        }
        var stopwatch = Stopwatch.StartNew();
        try
        {
            // The host's usual acknowledgement gate remains active for acquisition and use.
            var progress = new AlignmentProgress(p => Emit($"[Lyrics prepare] {p.ComponentId}: {p.Stage} {p.DownloadedBytes}/{p.TotalBytes}"));
            await MusicLyricsComponentCatalog.PrepareAsync(true, progress, token);
            await MusicLyricsComponentCatalog.VerifyAsync(_components, token);
            var runtime = await ManagedPythonRuntime.ResolveAsync("auto", token, MusicLyricsDevicePolicy.Profiles, Emit);
            if (runtime.Device != "cpu" && runtime.FreeBytes < MusicLyricsDevicePolicy.MinimumGpuFreeBytes) {
                Emit($"[Lyrics hardware] {runtime.Device}: free={runtime.FreeBytes}, requiredReserve={MusicLyricsDevicePolicy.MinimumGpuFreeBytes}; CPU fallback.");
                runtime = await ManagedPythonRuntime.ResolveAsync("cpu", token, log: Emit);
            }
            Emit($"[Lyrics hardware] selected={runtime.Device}; profile={runtime.Entry.Id}; reason={runtime.Reason}");
            var script = Path.Combine(AppContext.BaseDirectory, "Tools", "music_lyrics_gigaam.py");
            if (!File.Exists(script)) throw new FileNotFoundException("GigaAM lyrics worker is missing.", script);
            await _audio.PrepareAsync(token);
            using var probe = JsonDocument.Parse(await _audio.RunAsync(true,
                ["-v", "error", "-show_entries", "format=duration", "-of", "json", audioPath], token));
            var duration = double.Parse(probe.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
            if (!double.IsFinite(duration) || duration is <= 0 or > 1800) throw new InvalidDataException("Audio duration exceeds the alignment limit.");
            Emit("[Lyrics] Decode a read-only copy to mono PCM16/16 kHz; GigaAM v3 CTC.");
            await _audio.RunAsync(false, ["-nostdin", "-v", "error", "-n", "-i", audioPath, "-map", "0:a:0", "-map_metadata", "-1",
                "-c:a", "pcm_s16le", "-ar", "16000", "-ac", "1", "-f", "wav", wave], token);
            var rows = MusicLyricsCtcProtocol.SourceRows(lyrics);
            for (var retry = 0; retry < 2; retry++) {
                var requestPath = Path.Combine(attempt, $"request-{retry}.json");
                await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new {
                    schema = 1, audio = wave, original_audio = audioPath, source_lyrics = MusicTimedLyrics.Normalize(lyrics),
                    source_hash = MusicTimedLyrics.HashLyrics(lyrics), duration_seconds = duration, speech_language = speechLanguage,
                    weights = MusicLyricsComponentCatalog.GetModelPath(_components), import_roots = MusicLyricsComponentCatalog.GetImportRoots(_components),
                    lines = rows.Select(row => new { index = row.Index, text = row.Text }), output = resultPath,
                    device = runtime.Device, component_audit_prepared = true,
                    threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8), provider = Provider, version = ProviderVersion,
                    note = "Whole-source-line CTC likelihood, no transcript prompt, no inferred intervals." }), Encoding.UTF8, token);
                var outcome = await MusicLyricsWorkerProcess.RunAsync(runtime.Python, script, requestPath, Emit, token);
                if (retry == 0 && MusicLyricsDevicePolicy.CanRetryOnCpu(runtime.Device, outcome.ExitCode, outcome.Error, token.IsCancellationRequested)) {
                    Emit("[Lyrics hardware] GPU worker exited with a recoverable hardware error; retry once on CPU.");
                    runtime = await ManagedPythonRuntime.ResolveAsync("cpu", token, log: Emit);
                    continue;
                }
                if (outcome.ExitCode != 0 || !File.Exists(resultPath)) throw new IOException("Lyrics acoustic search failed; see the saved worker log.");
                if (new FileInfo(resultPath).Length > 16_777_216) throw new InvalidDataException("Oversized acoustic result.");
                var json = await File.ReadAllTextAsync(resultPath, token);
                var result = MusicLyricsCtcProtocol.Read(json, lyrics, duration);
                Emit($"[Lyrics] {result.State}; elapsed={stopwatch.Elapsed.TotalSeconds:F2}s; {result.Diagnostics}");
                return result;
            }
            throw new IOException("Lyrics acoustic device attempts were exhausted.");
        }
        finally
        {
            if (File.Exists(wave)) File.Delete(wave);
            // Preserve real inputs, acoustic evidence, result and diagnostics for the durable operation.
            string text; lock (logs) text = logs.ToString();
            await File.WriteAllTextAsync(Path.Combine(attempt, "worker.log"), text, Encoding.UTF8, CancellationToken.None);
        }
    }

    public static MusicLyricsAlignmentResult MatchRecognition(string json, string lyrics, double duration, string version)
    {
        var words = MusicLyricsAlignmentMatcher.ReadWhisperJson(json, duration);
        var baseline = MusicLyricsAlignmentMatcher.Match(lyrics, words, duration, LegacyProvider, version);
        return MusicLyricsPhraseMatcher.Match(json, lyrics, baseline);
    }

    private sealed class AlignmentProgress(Action<ComponentDownloadProgress> report) : IProgress<ComponentDownloadProgress>
    {
        public void Report(ComponentDownloadProgress value) => report(value);
    }

}
