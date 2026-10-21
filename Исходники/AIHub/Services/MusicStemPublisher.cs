using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Recoverable two-role publication. No original full mix is published as a third result.</summary>
public sealed class MusicStemPublisher(IMusicStemsWorker worker, IMusicAudioEncoder encoder, Action<string>? log = null)
{
    public async Task<MusicGenerationVariant> PublishAudioAsync(MusicGenerationJob job, MusicGenerationVariant variant, int index,
        Action<MusicGenerationVariant> save, CancellationToken token)
    {
        try {
            variant = await MusicStemRecovery.RestoreAsync(variant, save, log, token);
            var bundle = variant.Stems ?? throw new InvalidDataException("Missing song bundle request.");
            if (bundle.Receipt is null) {
                var receipt = await worker.SeparateAsync(variant.AudioFile!, variant.AudioHash!,
                    Path.Combine(Path.GetDirectoryName(variant.AudioFile!)!, "stems-" + Guid.NewGuid().ToString("N")), log ?? (_ => { }), token);
                bundle = bundle with { Receipt = receipt, Error = null }; variant = variant with { Stems = bundle }; save(variant);
            }
            var r = bundle.Receipt!;
            await MusicOutputPublisher.MatchHashAsync(r.VoicePcm, r.VoiceHash, token);
            await MusicOutputPublisher.MatchHashAsync(r.MusicPcm, r.MusicHash, token);
            var output = job.Output!;
            await Publish(variant.ResultPath, r.VoicePcm, output.Format, output.Bitrate, false, false);
            await Publish(MusicStemBundle.MusicPath(variant), r.MusicPcm, output.Format, output.Bitrate, true, false);
            if (output.AdditionalFormat is { } extra) {
                await Publish(variant.AdditionalPath!, r.VoicePcm, extra, output.AdditionalBitrate, false, true);
                await Publish(MusicStemBundle.MusicPath(variant, true), r.MusicPcm, extra, output.AdditionalBitrate, true, true);
            }
            return variant;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) {
            variant = variant with { Stems = (variant.Stems ?? new()) with { Error = error.GetType().Name } }; save(variant);
            throw new MusicStemsException(error);
        }

        async Task Publish(string destination, string source, MusicAudioFormat format, int bitrate, bool music, bool additional)
        {
            var known = music ? additional ? variant.Stems!.AdditionalMusicHash : variant.Stems!.MusicHash : additional ? variant.AdditionalHash : variant.ResultHash;
            if (File.Exists(destination)) {
                if (known is null) throw new IOException("An unrelated file occupies the song bundle.");
                await MusicOutputPublisher.MatchHashAsync(destination, known, token); return;
            }
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
            try {
                await encoder.EncodeAsync(source, temporary, format, bitrate, Metadata(job, variant, index, music ? "music" : "voice"), job.Cover, token);
                var hash = await MusicOutputPublisher.HashAsync(temporary, token);
                variant = music ? variant with { Stems = additional ? variant.Stems! with { AdditionalMusicHash = hash } : variant.Stems! with { MusicHash = hash } }
                    : additional ? variant with { AdditionalHash = hash } : variant with { ResultHash = hash };
                save(variant); token.ThrowIfCancellationRequested(); File.Move(temporary, destination, false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    public static Dictionary<string, string> Metadata(MusicGenerationJob job, MusicGenerationVariant v, int index, string role)
    {
        var tags = new Dictionary<string, string>(MusicSongMetadata.Create(job, v, index), StringComparer.OrdinalIgnoreCase) {
            ["LOPATA_OPERATION"] = "music.generate; music.separate", ["LOPATA_SONG_BUNDLE"] = job.Id + ":" + index,
            ["LOPATA_FILE_ROLE"] = role, ["LOPATA_SEPARATOR"] = "HT-Demucs 6s / ONNX",
            ["LOPATA_SOURCE_AUDIO_SHA256"] = v.AudioHash ?? "unknown",
            ["LOPATA_SEPARATOR_REVISION"] = MusicStemsCatalog.ModelRevision, ["LOPATA_SEPARATOR_SHA256"] = MusicStemsCatalog.ModelHash,
            ["LOPATA_SEPARATOR_RUNTIME"] = v.Stems?.Receipt?.Runtime ?? "unknown",
            ["LOPATA_SEPARATOR_PROVIDER"] = v.Stems?.Receipt?.Provider ?? "unknown",
            ["LOPATA_SEPARATOR_SHARED_GAIN"] = (v.Stems?.Receipt?.Gain ?? 1).ToString("R", CultureInfo.InvariantCulture)
        };
        tags["LOPATA_OUTPUT_SETTINGS"] = JsonSerializer.Serialize(job.Output);
        tags["LOPATA_DURATION_ACTUAL_SECONDS"] = (v.DurationSeconds ?? 0).ToString("R", CultureInfo.InvariantCulture);
        return tags;
    }
    public static string SourceTimingPath(MusicGenerationJobs jobs, string id, int index) => Path.Combine(jobs.Folder(id), "stems-source-timing-" + index + ".json");
    public static async Task FinalizeAsync(MusicGenerationJobs jobs, string id, int index, CancellationToken token)
    {
        var job = jobs.Load(id); var v = job.Variants[index];
        if (v.Stems is null) return;
        if (v.Stems.Published) await VerifyAsync(v, token);
        await VerifyAudioAsync(v, token);
        var text = new StringBuilder();
        text.AppendLine("LOPATA-METADATA " + JsonSerializer.Serialize(Metadata(job, v, index, "text-and-parameters")));
        text.AppendLine().AppendLine("Название / Title: " + job.Title).AppendLine("Исполнитель / Artist: " + job.Artist);
        text.AppendLine().AppendLine("Исходный текст песни / Input song lyrics").AppendLine(job.Lyrics);
        text.AppendLine().AppendLine("Параметры / Parameters");
        foreach (var pair in Metadata(job, v, index, "text-and-parameters").Where(p => p.Key != "lyrics")) text.AppendLine(pair.Key + " = " + pair.Value);
        text.AppendLine().AppendLine("Файлы / Files");
        foreach (var path in Files(v)) text.AppendLine(Path.GetFileName(path));
        text.AppendLine().AppendLine("Тайминги / Timings — seconds from song start");
        text.AppendLine("Привязка в плеере / Playback alignment: " + (v.KaraokeReadiness == MusicKaraokeReadiness.Ready ? "готова / ready" : "недоступна / unavailable"));
        var timingPath = SourceTimingPath(jobs, id, index);
        MusicTimedLyrics? timing = null;
        if (v.Stems.SourceTimingHash is { } sourceHash && File.Exists(timingPath)) {
            await MusicOutputPublisher.MatchHashAsync(timingPath, sourceHash, token);
            timing = JsonSerializer.Deserialize<MusicTimedLyrics>(await File.ReadAllTextAsync(timingPath, token));
            text.AppendLine("Источник измерений / Measurement source: original full mix");
        }
        else { timing = await new MusicLyricsTimingStore(jobs).ReadAsync(job, index, token);
            if (timing is not null) text.AppendLine("Источник привязки / Timing source: voice playback"); }
        if (timing is null) text.AppendLine("Не определены / Unavailable. Text above is the input lyrics, not a verified transcript.");
        else {
            timing.Validate();
            if (timing.JobId != id || timing.Variant != index || timing.ProjectId != job.ProjectId || timing.ProjectStep != job.ProjectStep ||
                timing.SourceLyrics != MusicTimedLyrics.Normalize(job.Lyrics)) throw new InvalidDataException("Unrelated source timing in bundle TXT.");
            text.AppendLine(timing.Provider + " " + timing.ProviderVersion + "; " + timing.Accuracy + "; payload=" + timing.AudioPayloadHash);
            foreach (var line in timing.Lines) {
                text.AppendLine(FormattableString.Invariant($"[{line.StartSeconds:F3}–{line.EndSeconds:F3}] ") + timing.SourceLyrics.Substring(line.SourceStart, line.SourceLength));
                foreach (var word in line.Words) text.AppendLine(FormattableString.Invariant($"  [{word.StartSeconds:F3}–{word.EndSeconds:F3}] ") + timing.SourceLyrics.Substring(word.SourceStart, word.SourceLength));
            }
            text.AppendLine("Строки без указанных интервалов не привязаны / Lines without intervals were not aligned.");
        }
        await MusicStemTextTransaction.PublishAsync(jobs, id, index, text.ToString(), token);
    }
    public static async Task SaveSourceTimingAsync(MusicGenerationJobs jobs, string id, int index, MusicTimedLyrics timing, CancellationToken token)
    {
        var destination = SourceTimingPath(jobs, id, index); var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(timing), new UTF8Encoding(false), token);
            var hash = await MusicOutputPublisher.HashAsync(temporary, token);
            File.Move(temporary, destination, true);
            lock (MusicLyricsTimingStore.Gate(jobs.Folder(id))) {
                var job = jobs.Load(id); var variants = job.Variants.ToArray();
                variants[index] = variants[index] with { Stems = variants[index].Stems! with { SourceTimingHash = hash } }; jobs.Save(job with { Variants = variants });
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static IEnumerable<string> Files(MusicGenerationVariant v) => new[] { v.ResultPath, MusicStemBundle.MusicPath(v),
        v.AdditionalPath, v.AdditionalPath is null ? null : MusicStemBundle.MusicPath(v, true) }.OfType<string>();
    public static async Task VerifyAudioAsync(MusicGenerationVariant v, CancellationToken token) {
        await MusicOutputPublisher.MatchHashAsync(MusicStemBundle.MusicPath(v), v.Stems!.MusicHash!, token);
        if (v.AdditionalPath is not null) await MusicOutputPublisher.MatchHashAsync(MusicStemBundle.MusicPath(v, true), v.Stems.AdditionalMusicHash!, token);
    }
    public static async Task VerifyAsync(MusicGenerationVariant v, CancellationToken token) {
        await VerifyAudioAsync(v, token);
        await MusicOutputPublisher.MatchHashAsync(MusicStemBundle.TextPath(v), v.Stems!.TextHash!, token);
    }
}
