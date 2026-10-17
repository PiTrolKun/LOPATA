using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using NAudio.Wave;

namespace LyricVideoStand;

internal sealed class StandSession
{
    public required StandConfig Config { get; init; }
    public required string SourceHash { get; init; }
    public required string PcmPath { get; init; }
    public required string WavePath { get; init; }
    public required string Artist { get; init; }
    public required string Title { get; init; }
    public required AudioAnalysis Analysis { get; init; }
    public required StandProvenance Provenance { get; init; }
    public MusicTimedLyrics? Lyrics { get; init; }
    public byte[]? Cover { get; init; }
    public double SongDuration => Analysis.Duration;
    public double TotalDuration => SongDuration + 4.5;
    public double CoverDuration => Cover is null ? 0 : 1d / 30;
    public double MainStart => CoverDuration + 3.5 + 3;
    public string Stage(double t) => t < CoverDuration ? "обложка" : t < CoverDuration + 3.5 ? "заставка"
        : t < MainStart ? "исполнитель / название" : t < SongDuration ? "основная сцена" : "финальные титры";

    public static async Task<StandSession> LoadAsync(StandConfig config, StandLog log, CancellationToken token)
    {
        Directory.CreateDirectory(config.Output);
        log.Write("Preflight: verifying existing local runtime manifests; no acquisition.");
        await StandProcess.VerifyAsync(config.AudioDirectory, "manifest.json", token);
        await StandProcess.VerifyAsync(config.VideoDirectory, "provenance.json", token);
        using var input = File.OpenRead(config.Song);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
        var runtime = new MusicAudioRuntime(config.AudioDirectory);
        var metadata = await AudioConversionMetadata.ReadAsync(runtime, config.Song, token);
        var lyrics = await MusicLyricsTimingMetadata.ReadAsync(config.Song, token);
        if (lyrics is not null && !lyrics.AudioPayloadHash.Equals(await MusicLyricsTimingMetadata.AudioPayloadHashAsync(config.Song, token), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Lyrics do not belong to this audio payload.");
        var cache = Path.Combine(config.Output, "cache", hash[..16]); Directory.CreateDirectory(cache);
        var pcm = Path.Combine(cache, "audio.pcm"); var wave = Path.Combine(cache, "audio.wav");
        var receipt = Path.Combine(cache, "cache.json");
        var valid = false;
        if (File.Exists(receipt) && File.Exists(pcm) && File.Exists(wave))
        {
            using var stored = JsonDocument.Parse(await File.ReadAllTextAsync(receipt, token));
            var root = stored.RootElement;
            valid = root.GetProperty("SourceHash").GetString() == hash
                && root.GetProperty("PcmHash").GetString() == await HashAsync(pcm, token)
                && root.GetProperty("WaveHash").GetString() == await HashAsync(wave, token);
        }
        if (!valid)
        {
            log.Write("Decoding: 48 kHz / stereo / s16le; source is read-only.");
            var temporary = pcm + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await runtime.RunAsync(false, ["-nostdin", "-v", "error", "-n", "-i", config.Song, "-map", "0:a:0",
                    "-map_metadata", "-1", "-ar", "48000", "-ac", "2", "-c:a", "pcm_s16le", "-f", "wav", temporary], token);
                File.Move(temporary, wave, true);
                using (var wav = new WaveFileReader(wave))
                using (var raw = File.Create(pcm))
                {
                    if (wav.WaveFormat.SampleRate != 48000 || wav.WaveFormat.Channels != 2 || wav.WaveFormat.BitsPerSample != 16)
                        throw new InvalidDataException("Unexpected decoded waveform.");
                    wav.CopyTo(raw);
                }
                await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new {
                    Program = "LOPATA", ProgramVersion = config.Version, Scenario = "lyric-video/stand-cache", CreatedUtc = DateTimeOffset.UtcNow,
                    SourceHash = hash, PcmHash = await HashAsync(pcm, token), WaveHash = await HashAsync(wave, token)
                }), token);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        else log.Write("Verified PCM/WAV cache reused.");
        log.Write("Analysis: stereo-power FFT / 20 ms hop / 2048 samples; deterministic curves.");
        var analysis = await Task.Run(() => AudioAnalysis.ReadPcm(pcm, token), token);
        if (lyrics is not null && Math.Abs(lyrics.DurationSeconds - analysis.Duration) > .5)
            throw new InvalidDataException("Decoded duration and saved lyric clock disagree.");
        string Tag(string key) => metadata.Tags.GetValueOrDefault(key, "");
        var models = new List<ModelUse>();
        if (Tag("LOPATA_MODEL").Length > 0) models.Add(new(Tag("LOPATA_MODEL"), "Музыка", Tag("LOPATA_MODEL_REVISION")));
        if (Tag("LOPATA_COVER_MODEL").Length > 0) models.Add(new(Tag("LOPATA_COVER_MODEL"), "Обложка", ""));
        if (lyrics is not null) models.Add(new(lyrics.Provider.Split('/')[0], "Синхронизация текста", lyrics.ProviderVersion));
        var session = new StandSession {
            Config = config, SourceHash = hash, PcmPath = pcm, WavePath = wave,
            Artist = Tag("artist"), Title = Tag("title").Length > 0 ? Tag("title") : Path.GetFileNameWithoutExtension(config.Song),
            Analysis = analysis, Lyrics = lyrics, Provenance = new(config.Version, "lyric-video/stand", models),
            Cover = (metadata.Covers.FirstOrDefault(c => c.Type == 3) ?? metadata.Covers.FirstOrDefault())?.Data
        };
        await File.WriteAllTextAsync(Path.Combine(config.Output, "analysis.json"), JsonSerializer.Serialize(new {
            Provenance = session.Provenance, SourceHash = hash, session.Artist, session.Title, analysis.Duration, analysis.Rms95,
            Mode = "energy-and-spectrum; not a beat tracker", analysis.Points
        }), token);
        log.Write($"Ready: {analysis.Duration:F3}s audio; {analysis.Points.Length} analysis windows; {lyrics?.Lines.Length ?? 0} timed lines; cover={session.Cover is not null}.");
        log.Write("Intro fixed: first cover frame + 3.5s brand + 3s title; early speech never shifts the clock.");
        log.Write("Credits: known model roles only, 4.5s after music; text/cover history may be unknown.");
        return session;
    }
    public async Task VerifySourceAsync(CancellationToken token)
    {
        if (await HashAsync(Config.Song, token) != SourceHash) throw new IOException("Source changed during stand run.");
    }
    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        using var file = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(file, token));
    }
}
