using System.IO;

namespace AIHub.Services;

public sealed record MusicLayerFitResult(string Path, string Hash, MusicLayerFitSummary Summary);
public interface IMusicLayerFitter
{
    Task<MusicLayerFitResult> FitAsync(string originalVoice, string freshVoice, string freshLayer, string originalLyrics, string freshLyrics,
        string folder, Action<string> log, CancellationToken token);
}

/// <summary>Acoustic phrase anchors when available; explicit duration-only fallback for casual covers.</summary>
public sealed class MusicAudioTaskAlignment(
    Func<string, string, string, CancellationToken, Task<MusicLyricsAlignmentResult>>? measure = null) : IMusicLayerFitter
{
    public async Task<MusicLayerFitResult> FitAsync(string originalVoice, string freshVoice, string freshLayer, string originalLyrics, string freshLyrics,
        string folder, Action<string> log, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(originalLyrics) || string.IsNullOrWhiteSpace(freshLyrics)) throw new InvalidDataException("Music.Transform.AlignmentLyrics");
        var originalSeconds = MusicWaveFile.ReadDuration(originalVoice).TotalSeconds;
        var freshSeconds = MusicWaveFile.ReadDuration(freshVoice).TotalSeconds;
        _ = MusicLayerFitPlan.Create(originalSeconds, freshSeconds, originalLyrics, freshLyrics, null, null);
        var a = await Read(originalVoice, originalLyrics, "original-timing");
        var b = await Read(freshVoice, freshLyrics, "fresh-timing");
        var plan = MusicLayerFitPlan.Create(originalSeconds, freshSeconds, originalLyrics, freshLyrics, a, b);
        log($"Approximate fit: {plan.Method}; phrases={plan.MatchedPhrases}/{plan.ExpectedPhrases}; original={originalSeconds:F3}s; fresh={freshSeconds:F3}s; maxShift={plan.MaximumShiftSeconds:F3}s.");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "fitted-" + Guid.NewGuid().ToString("N") + ".wav");
        try {
            await Task.Run(() => MusicPcmTimeFit.Write(freshLayer, path, plan, token), token);
            return new(path, await MusicAudioTaskFiles.HashAsync(path, token), plan);
        } catch { if (File.Exists(path)) File.Delete(path); throw; }

        async Task<MusicLyricsAlignmentResult?> Read(string audio, string lyrics, string name)
        {
            token.ThrowIfCancellationRequested();
            try {
                var work = Path.Combine(folder, name);
                if (measure is not null) return await measure(audio, lyrics, work, token);
                return await MusicCoverAlignmentCache.ReadAsync(audio, lyrics, work, log, token);
            } catch (IOException e) when (!e.Message.StartsWith("Music.Transform.InputChanged", StringComparison.Ordinal)) {
                log("Phrase search unavailable (" + e.GetType().Name + "); keeping an explicitly approximate duration fit.");
                return null;
            }
        }
    }
}
