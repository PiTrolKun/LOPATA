using System.IO;
using System.Text.Json;
using AIHub.Models;
using NAudio.Wave;

namespace AIHub.Services.LyricVideo;

internal sealed record ClipConfig(string Song, string Output, string Language = "ru")
{
    public string Version => MusicSongMetadata.AppVersion;
    public string VideoDirectory => ClipRuntime.VideoDirectory;
}

internal sealed class ClipSession
{
    public required ClipConfig Config { get; init; }
    public required string SourceHash { get; init; }
    public required string PcmPath { get; init; }
    public required string WavePath { get; init; }
    public required string Artist { get; init; }
    public required string Title { get; init; }
    public required string SourceLyrics { get; init; }
    public required Dictionary<string, string> SourceTags { get; init; }
    public required AudioAnalysis Analysis { get; init; }
    public required OutputProvenance Provenance { get; set; }
    public required Func<string, string> Localize { get; init; }
    public MusicTimedLyrics? Lyrics { get; init; }
    public byte[]? Cover { get; init; }
    public double SongDuration => Analysis.Duration;
    public double TotalDuration => Math.Max(SongDuration, MainStart) + 4.5;
    public double CoverDuration => Cover is null ? 0 : 1d / 30;
    public double MainStart => CoverDuration + 6.5;
    public string Stage(double t) => Localize("LyricVideo.Stage." + (t < CoverDuration ? "Cover" : t < CoverDuration + 3.5 ? "Brand"
        : t < MainStart ? "Title" : t < SongDuration ? "Scene" : "Credits"));

    private sealed record Cache(string SourceHash, string PcmHash, string WaveHash, string AnalysisHash);
    public static async Task<ClipSession> LoadAsync(ClipConfig config, Action<string>? log, CancellationToken token)
    {
        Directory.CreateDirectory(config.Output);
        var localizer = new LocalizationService(); localizer.Load(config.Language);
        var l = localizer.T;
        log?.Invoke(l("LyricVideo.Preparing"));
        var runtime = MusicAudioRuntime.Default;
        await runtime.PrepareAsync(token); await ClipRuntime.PrepareAsync(token);
        var hash = await ClipRuntime.HashAsync(config.Song, token);
        var metadata = await AudioConversionMetadata.ReadAsync(runtime, config.Song, token);
        MusicTimedLyrics? lyrics = null;
        try
        {
            lyrics = await MusicLyricsTimingMetadata.ReadAsync(config.Song, token);
            if (lyrics is not null && !lyrics.AudioPayloadHash.Equals(await MusicLyricsTimingMetadata.AudioPayloadHashAsync(config.Song, token), StringComparison.OrdinalIgnoreCase))
                lyrics = null;
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or JsonException)
        { log?.Invoke(l("LyricVideo.NoTiming")); }
        var cache = Path.Combine(config.Output, "cache", hash); Directory.CreateDirectory(cache);
        var pcm = Path.Combine(cache, "audio.pcm"); var wave = Path.Combine(cache, "audio.wav");
        var analysisPath = Path.Combine(cache, "analysis.json"); var receipt = Path.Combine(cache, "cache.json");
        AudioAnalysis? analysis = null;
        if (File.Exists(receipt) && File.Exists(pcm) && File.Exists(wave) && File.Exists(analysisPath))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<Cache>(await File.ReadAllBytesAsync(receipt, token));
                if (saved is not null && saved.SourceHash == hash && saved.PcmHash == await ClipRuntime.HashAsync(pcm, token)
                    && saved.WaveHash == await ClipRuntime.HashAsync(wave, token) && saved.AnalysisHash == await ClipRuntime.HashAsync(analysisPath, token))
                    analysis = JsonSerializer.Deserialize<AudioAnalysis>(await File.ReadAllBytesAsync(analysisPath, token));
            }
            catch (JsonException) { /* Rebuild an incomplete internal cache; never alter the song. */ }
        }
        if (analysis is null)
        {
            var temporary = Path.Combine(cache, Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                await runtime.DecodeAsync(config.Song, temporary, token);
                File.Move(temporary, wave, true);
                using (var wav = new WaveFileReader(wave))
                using (var raw = File.Create(pcm))
                {
                    if (wav.WaveFormat.SampleRate != 48000 || wav.WaveFormat.Channels != 2 || wav.WaveFormat.BitsPerSample != 16)
                        throw new InvalidDataException("Unexpected decoded waveform.");
                    await wav.CopyToAsync(raw, token);
                }
                analysis = await Task.Run(() => AudioAnalysis.ReadPcm(pcm, token), token);
                RagBookPreparation.AtomicJson(analysisPath, analysis);
                RagBookPreparation.AtomicJson(receipt, new Cache(hash, await ClipRuntime.HashAsync(pcm, token),
                    await ClipRuntime.HashAsync(wave, token), await ClipRuntime.HashAsync(analysisPath, token)));
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        if (lyrics is not null && Math.Abs(lyrics.DurationSeconds - analysis.Duration) > .5) lyrics = null;
        string Tag(string key) => metadata.Tags.GetValueOrDefault(key, "");
        var models = new List<OutputModelUse>();
        void Model(string key, string role, string revision = "")
        { if (Tag(key).Length > 0) models.Add(new(Tag(key), Tag(key), revision, role)); }
        Model("LOPATA_MODEL", "music", Tag("LOPATA_MODEL_REVISION"));
        Model("LOPATA_COVER_MODEL", "cover"); Model("LOPATA_TEXT_MODEL", "lyrics");
        if (lyrics is not null) models.Add(new(lyrics.Provider.Split('/')[0], lyrics.Provider.Split('/')[0], lyrics.ProviderVersion, "lyrics synchronization"));
        var session = new ClipSession {
            Config = config, SourceHash = hash, PcmPath = pcm, WavePath = wave, Artist = Tag("artist"),
            Title = Tag("title").Length > 0 ? Tag("title") : Path.GetFileNameWithoutExtension(config.Song),
            SourceLyrics = lyrics?.SourceLyrics ?? Tag("lyrics"), SourceTags = metadata.Tags, Analysis = analysis, Lyrics = lyrics,
            Provenance = new() { Scenario = "music-video", Models = models.ToArray(), ModelHistory = "inherited-audio-tags; other-creation-history-unknown" },
            Localize = l, Cover = (metadata.Covers.FirstOrDefault(c => c.Type == 3) ?? metadata.Covers.FirstOrDefault())?.Data
        };
        await session.VerifySourceAsync(token);
        log?.Invoke(l(lyrics is null ? "LyricVideo.NoTiming" : "LyricVideo.Ready"));
        return session;
    }
    public async Task VerifySourceAsync(CancellationToken token)
    {
        if (await ClipRuntime.HashAsync(Config.Song, token) != SourceHash) throw new IOException("LyricVideo.SourceChanged");
    }
}
