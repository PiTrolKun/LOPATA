using System.Diagnostics;
using System.IO;
using AIHub.Services.VideoUpscale;

namespace AIHub.Services.VideoConverter;

internal static partial class ConversionBatch
{
    private static async Task<(ConversionArtifact Artifact, string Stage)> EncodeItemAsync(ConversionJob job, ConversionItem item,
        ConversionMedia media, string scratch, Stopwatch timer, Action<string, double?> report, CancellationToken token)
    {
        List<string> failures = [];
        foreach (var plan in ConversionProfiles.Candidates(job.Options, media))
        {
            token.ThrowIfCancellationRequested();
            var label = ConversionProfiles.Label(plan.Options);
            var extension = plan.Options.Extension;
            var stage = ConversionStore.ScratchFile(scratch, item.Id, extension);
            var sample = ConversionStore.ScratchFile(scratch, item.Id, extension, "probe");
            var keepStage = false;
            try
            {
                report($"Проверка исполнителя · {label}", null);
                ConversionStore.ClearScratchFile(sample);
                var encoder = await ConversionEncoder.SelectAsync(plan, item.Input, sample, token);
                var (bytes, bitrate) = await EncodeProfileAsync(plan, encoder, item.Input, stage, scratch, report, token);
                await ConversionEncoder.VerifyDecodingAsync(stage, token);
                using (var stream = new FileStream(stage, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) stream.Flush(true);
                var (target, archiveName) = ConversionStore.VideoDestination(job, item, scratch, extension);
                List<string> notes = [.. plan.Changes];
                if (encoder.Name != "copy" && job.Options.Executor != ConversionExecutor.Auto && encoder.Executor != job.Options.Executor)
                    notes.Add($"Автоподбор исполнителя · {job.Options.Executor.ToString().ToUpperInvariant()} → {encoder.Executor.ToString().ToUpperInvariant()}");
                if (plan.Limit is long cap && bytes > cap && CanKeepSmaller(job.Options.Mode, media.Bytes, bytes))
                    notes.Add($"Цель −{job.Options.Reduction} %, достигнуто −{(1 - bytes / (double)media.Bytes) * 100:0.#} %");
                var details = $"{label} · {encoder.Label} · {plan.Width} × {plan.Height}"
                    + (bitrate > 0 ? $" · {bitrate} кбит/с" : "") + Environment.NewLine + string.Join(" · ", notes);
                var artifact = new ConversionArtifact(target, extension, await UpscaleMediaTools.Hash(stage, token), bytes,
                    details, timer.Elapsed.TotalSeconds, plan.Limit) { ArchiveName = archiveName };
                keepStage = true;
                return (artifact, stage);
            }
            catch (ConversionCompatibilityException error)
            {
                failures.Add(label + " — " + error.Message);
            }
            finally
            {
                ConversionStore.ClearScratchFile(sample);
                if (!keepStage) ConversionStore.ClearScratchFile(stage);
            }
        }

        throw new ConversionCompatibilityException("Автоподбор не нашёл профиль, сохраняющий параметры и дорожки этого файла."
            + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    private static async Task<(long Bytes, int? Bitrate)> EncodeProfileAsync(ConversionPlan plan, ConversionEncoder encoder,
        string input, string stage, string scratch, Action<string, double?> report, CancellationToken token)
    {
        var bitrate = plan.Bitrate;
        var headroom = 1d;
        for (int attempt = 0; attempt < MaximumSizePasses; attempt++)
        {
            token.ThrowIfCancellationRequested();
            ConversionStore.ClearScratchFile(stage);
            var scratchLimit = checked((long)(EstimatedScratch(plan, bitrate) * headroom));
            using var reservation = ManagedWorkStorage.Current.Reserve(scratch, scratchLimit, temporary: true);
            var stageName = attempt == 0 ? encoder.Label : $"Уточнение размера · проход {attempt + 1} · {encoder.Label}";
            report(stageName, 0);
            await encoder.EncodeAsync(plan, input, stage, bitrate, scratchLimit, fraction => report(stageName, fraction), token);
            if (new FileInfo(stage).Length >= scratchLimit)
            {
                // The encoder may have stopped at the space guard. Such a file is never accepted.
                if (plan.Limit.HasValue && bitrate is int oversized && oversized > 30)
                    bitrate = Math.Max(20, (int)(oversized * .69));
                else headroom *= 2;
                continue;
            }
            report("Проверка видео, звука и метаданных", null);
            var result = await ConversionEncoder.VerifyAsync(plan, stage, token);
            if (plan.Limit is not long cap || result.Bytes <= cap || bitrate is not int kbps || attempt == MaximumSizePasses - 1)
                return (result.Bytes, bitrate);
            var next = (int)Math.Floor(kbps * (cap - plan.OtherBytes) / (double)Math.Max(1, result.Bytes - plan.OtherBytes) * .89);
            if (next < 20 || next >= kbps) return (result.Bytes, bitrate);
            bitrate = next;
        }
        throw new IOException("Промежуточный файл вырос больше оценки. Измените режим сжатия или освободите место в рабочем хранилище.");
    }

    private static bool CanKeepSmaller(ConversionMode mode, long? sourceBytes, long resultBytes) =>
        mode == ConversionMode.Percent && sourceBytes is long size && resultBytes < size;

    private static long EstimatedScratch(ConversionPlan plan, int? bitrate)
    {
        var size = bitrate is int kbps ? (long)(kbps * 125d * plan.Media.Duration) + plan.OtherBytes
            : plan.Lossless && !plan.Copy ? Math.Max(plan.Media.Bytes, (long)(plan.Width * (double)plan.Height * plan.Media.Duration * plan.Media.Fps))
            : plan.Estimate ?? plan.Media.Bytes;
        return Math.Max(16L * 1024 * 1024, (long)(size * (plan.Lossless && !plan.Copy ? 1.75 : 1.35)));
    }
}
