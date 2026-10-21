using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public interface IMusicLyricsSynchronization
{
    event Action<string>? Log;
    Task<MusicTimedLyrics?> ReadAsync(MusicGenerationJob job, int index, CancellationToken token);
    Task<MusicTimedLyrics?> ReadAdditionalAsync(MusicGenerationJob job, int index, CancellationToken token) => Task.FromResult<MusicTimedLyrics?>(null);
    Task<MusicTimedLyrics?> SynchronizeAsync(string jobId, int index, CancellationToken token);
}

/// <summary>One automatic post-generation step; audio completion and timing availability are independent.</summary>
public sealed class MusicLyricsSynchronization : IMusicLyricsSynchronization
{
    public const string BackgroundKind = "music.lyrics-sync";
    private readonly MusicGenerationJobs _jobs;
    private readonly MusicLyricsTimingStore _store;
    private readonly MusicLyricsAlignmentService _alignment;
    private sealed record Unavailable(string LyricsHash, string PayloadHash, string MatcherVersion, string SpeechLanguage, string Reason);
    public event Action<string>? Log;

    public MusicLyricsSynchronization(MusicGenerationJobs jobs, MusicLyricsAlignmentService? alignment = null)
    {
        _jobs = jobs; _store = new(jobs); _alignment = alignment ?? new();
        _alignment.Log += message => { if (message.StartsWith("[Lyrics", StringComparison.Ordinal)) Log?.Invoke(message); };
    }

    public async Task<MusicTimedLyrics?> ReadAsync(MusicGenerationJob job, int index, CancellationToken token)
    {
        var timing = await _store.ReadAsync(job, index, token);
        // Reanalyse only this known older matcher through the normal durable operation gate.
        // The original cache remains intact until a validated new result is published.
        return timing is not null && NeedsUpgrade(timing) ? null : timing;
    }
    public static bool NeedsUpgrade(MusicTimedLyrics timing) => timing.Provider == MusicLyricsAlignmentService.LegacyProvider ||
        timing.Provider == MusicLyricsAlignmentService.Provider && timing.ProviderVersion != MusicLyricsAlignmentService.ProviderVersion;
    public async Task<MusicTimedLyrics?> ReadAdditionalAsync(MusicGenerationJob job, int index, CancellationToken token)
    {
        var timing = await _store.ReadAdditionalAsync(job, index, token);
        if (timing?.Clock is not { } clock || NeedsUpgrade(timing)) return null;
        var primary = await MusicLyricsTimingMetadata.AudioPayloadHashAsync(job.Variants[index].ResultPath, token);
        return primary.Equals(clock.SourcePayloadHash, StringComparison.OrdinalIgnoreCase) ? timing : null;
    }

    public async Task<MusicTimedLyrics?> SynchronizeAsync(string jobId, int index, CancellationToken token)
    {
        var job = _jobs.Load(jobId);
        if (index < 0 || index >= job.Variants.Length) throw new InvalidDataException("Unknown music variant.");
        var variant = job.Variants[index];
        if (!variant.Completed || job.Wishes?.Instrumental == true || string.IsNullOrWhiteSpace(job.Lyrics)) return null;
        if (await _store.ReadAsync(job, index, token) is { } cached && !NeedsUpgrade(cached)) {
            await SynchronizeAdditionalAsync(job, index, cached, token);
            if (cached.Provider == MusicLyricsAlignmentService.Provider && variant.KaraokeReadiness == MusicKaraokeReadiness.Unavailable)
                _jobs.SetKaraokeReadiness(jobId, index, MusicKaraokeReadiness.Ready);
            if (variant.Stems?.Published == true) await MusicStemPublisher.FinalizeAsync(_jobs, jobId, index, token);
            return cached;
        }
        await VerifyTrackAsync(job, index, variant.ResultPath, token);
        var payload = await MusicLyricsTimingMetadata.AudioPayloadHashAsync(variant.ResultPath, token);
        var lyricsHash = MusicTimedLyrics.HashLyrics(job.Lyrics);
        var language = MusicLyricsSpeechLanguage.Resolve(job);
        var unavailablePath = Path.Combine(_jobs.Folder(jobId), "lyrics-unavailable-" + index + ".json");
        if (File.Exists(unavailablePath)) {
            try {
                var previous = JsonSerializer.Deserialize<Unavailable>(await File.ReadAllTextAsync(unavailablePath, token));
                if (previous is not null && previous.LyricsHash == lyricsHash && previous.PayloadHash == payload &&
                    previous.MatcherVersion == MusicLyricsAlignmentService.MatcherVersion && previous.SpeechLanguage == language) return null;
            }
            catch (Exception error) when (error is IOException or JsonException) { /* A damaged negative cache is not an audio failure. */ }
        }
        await ApplicationBackgroundOperations.RetireModelsAsync();
        var analysisSource = variant.ResultPath;
        if (job.SeparateTracks && variant.AudioFile is { } native && File.Exists(native)) {
            await MusicOutputPublisher.MatchHashAsync(native, variant.AudioHash!, token); analysisSource = native;
        }
        var result = await _alignment.AlignAsync(analysisSource, job.Lyrics,
            Path.Combine(_jobs.Folder(jobId), "lyrics-alignment-" + index), token, language);
        Log?.Invoke("[Lyrics] " + result.Diagnostics);
        token.ThrowIfCancellationRequested();
        // Recognition ran on a copy; confirm that the submitted performance has not changed meanwhile.
        await VerifyTrackAsync(_jobs.Load(jobId), index, variant.ResultPath, token);
        if (result.Lines.Length == 0) {
            var temporary = unavailablePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new Unavailable(lyricsHash, payload,
                    MusicLyricsAlignmentService.MatcherVersion, language, result.State)), token);
                File.Move(temporary, unavailablePath, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return null;
        }
        var timing = new MusicTimedLyrics {
            JobId = job.Id, Variant = index, ProjectId = job.ProjectId, ProjectStep = job.ProjectStep,
            SourceLyrics = MusicTimedLyrics.Normalize(job.Lyrics), LyricsHash = lyricsHash, AudioPayloadHash = payload,
            DurationSeconds = result.DurationSeconds, Provider = result.Provider, ProviderVersion = result.ProviderVersion,
            Accuracy = "approximate", Lines = result.Lines
        };
        if (analysisSource != variant.ResultPath) {
            timing = timing with { AudioPayloadHash = await MusicLyricsTimingMetadata.AudioPayloadHashAsync(analysisSource, token) };
            timing.Validate();
            await MusicStemPublisher.SaveSourceTimingAsync(_jobs, jobId, index, timing, token);
            var mapped = await MusicLyricsStemClock.MapAsync(job, variant, timing, _jobs.Folder(jobId), token);
            if (mapped is null) { Log?.Invoke("[Lyrics] Original mix timing saved to TXT; separated voice clock could not be verified."); return null; }
            timing = mapped;
        }
        timing.Validate(); await _store.PublishAsync(jobId, index, timing, token);
        await SynchronizeAdditionalAsync(_jobs.Load(jobId), index, timing, token);
        _jobs.SetKaraokeReadiness(jobId, index, MusicKaraokeReadiness.Ready);
        if (variant.Stems?.Published == true) await MusicStemPublisher.FinalizeAsync(_jobs, jobId, index, token);
        return timing;
    }

    private async Task SynchronizeAdditionalAsync(MusicGenerationJob job, int index, MusicTimedLyrics timing, CancellationToken token)
    {
        if (job.Variants[index].AdditionalPath is not { } additional ||
            await ReadAdditionalAsync(job, index, token) is not null) return;
        await VerifyTrackAsync(job, index, additional, token);
        await ApplicationBackgroundOperations.RetireModelsAsync();
        var mapped = await new MusicLyricsTimingClock(MusicAudioRuntime.Default).MapAsync(
            job.Variants[index].ResultPath, additional, timing, _jobs.Folder(job.Id), token);
        if (mapped is not null) await _store.PublishAdditionalAsync(job.Id, index, mapped, token);
        else Log?.Invoke("[Lyrics] Additional format has no verified playback clock; keep normal playback.");
    }

    public static async Task VerifyTrackAsync(MusicGenerationJob job, int index, string path, CancellationToken token)
    {
        if (index < 0 || index >= job.Variants.Length) throw new InvalidDataException("Unknown music variant.");
        var variant = job.Variants[index];
        var primary = string.Equals(Path.GetFullPath(path), Path.GetFullPath(variant.ResultPath), StringComparison.OrdinalIgnoreCase);
        var additional = variant.AdditionalPath is { } extra && string.Equals(Path.GetFullPath(path), Path.GetFullPath(extra), StringComparison.OrdinalIgnoreCase);
        var expected = primary ? variant.ResultHash ?? (job.Output is null ? variant.AudioHash : null) : additional ? variant.AdditionalHash : null;
        if (!variant.Completed || expected is null) throw new InvalidDataException("Unlinked music playback file.");
        using var file = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
        if (!string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Music playback checksum mismatch.");
    }
}
