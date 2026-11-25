using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using AIHub.Models;
using AIHub.Services.VideoUpscale;
using AIHub.Services.VoiceNotifications;
using Lopata.Updates;

namespace AIHub.Services.VideoConverter;

internal sealed record ConversionProgress(ConversionJob Job, int Position, int Count, string Stage, double? Fraction = null);

internal static partial class ConversionBatch
{
    internal const string Kind = "video-converter";
    internal const string Title = "Перекодирование видео";
    internal const string StartVoice = "VideoConverter.VoiceStart";
    internal const string EndVoice = "VideoConverter.VoiceComplete";
    private const int MaximumSizePasses = 4;

    internal static Task<ConversionJob> RunAsync(ConversionJob job, ConversionCheckpoint checkpoint,
        IProgress<ConversionProgress> progress, CancellationToken token, BackgroundOperationState? restored = null)
    {
        var started = restored is null ? 0 : 1;
        void AnnounceStart()
        {
            if (Interlocked.Exchange(ref started, 1) == 0)
                VoiceNotificationEvents.Publish(StartVoice, family: "operation-start", group: 2);
        }
        return ApplicationBackgroundOperations.RunAsync(Kind, Title, job.Id, checkpoint,
            attempt => Task.Run(() => ExecuteAsync(job, checkpoint, progress, AnnounceStart, attempt), attempt),
            token, restored, allowIdleQueue: true);
    }

    private static async Task<ConversionJob> ExecuteAsync(ConversionJob job, ConversionCheckpoint checkpoint,
        IProgress<ConversionProgress> progress, Action announceStart, CancellationToken token)
    {
        using var lease = new BackgroundOperationStore(ConversionStore.ExistingJobPath(job.Id), false).TryOwnOperation()
            ?? throw new IOException("Эта очередь конвертера уже открыта в работающем экземпляре ЛОПАТЫ.");
        job = ConversionStore.ReadJob(job.Id) ?? job;
        var indexes = job.Items.Select((item, index) => (item, index))
            .Where(p => checkpoint.Items.Contains(p.item.Id, StringComparer.Ordinal)).Select(p => p.index).ToArray();
        if (job.Id != checkpoint.JobId || checkpoint.Items.Distinct().Count() != checkpoint.Items.Length
            || indexes.Length != checkpoint.Items.Length
            || checkpoint.SaveLarger is not null && (indexes.Length != 1 || job.Items[indexes[0]].Id != checkpoint.SaveLarger))
            throw new InvalidDataException("Контрольная точка конвертера не совпадает с очередью.");
        job.Options.Validate();
        var scratch = ConversionStore.Scratch(job);
        Directory.CreateDirectory(scratch);
        SafeUpdatePath.RejectLinks(job.Options.OutputDirectory);
        token.ThrowIfCancellationRequested();
        announceStart();

        int position = 0;
        void Report(string stage, double? value = null) =>
            progress.Report(new(job with { Items = job.Items.ToArray() }, position, indexes.Length, stage, value));
        void Save(ConversionItem item, int index)
        {
            job.Items[index] = item;
            ConversionStore.SaveJob(job);
        }

        Report("Проверка комплекта кодировщиков");
        await UpscaleFfmpeg.PrepareAsync(token);
        if (job.ArchiveCandidate is { } interrupted && await ConversionStore.VerifyFile(interrupted.Path, interrupted, token))
            job = await RecordArchiveAsync(job, interrupted, token);

        bool? verifiedArchive = null;
        foreach (var index in indexes)
        {
            position++;
            token.ThrowIfCancellationRequested();
            var item = job.Items[index];
            var timer = Stopwatch.StartNew();
            try
            {
                if (item.Candidate is { } published && await ConversionStore.VerifyFile(published.Path, published, token))
                {
                    ConversionStore.DiscardCandidate(job, item.Id, published);
                    item = item with { Status = ConversionStatus.Ready, Output = published, Candidate = null, Error = null };
                    Save(item, index);
                    Report("Проверенный результат восстановлен");
                }
                if (item.Output is { } existing && await ConversionStore.VerifyFile(existing.Path, existing, token))
                {
                    if (!job.Options.Archive && ConversionStore.HasUnpackedOutput(job, item))
                    {
                        var (path, _) = ConversionStore.VideoDestination(job, item, scratch, existing.Extension);
                        var separate = existing with { Path = path, ArchiveName = null };
                        item = item with { Candidate = separate };
                        Save(item, index);
                        item = await CommitAsync(job, item, separate, existing.Path, true, Report, token);
                        Save(item, index);
                    }
                    continue;
                }
                if (job.Archive is { } archive && ConversionStore.ArchivedEntry(archive, item) is not null
                    && (verifiedArchive ??= await ConversionStore.VerifyFile(archive.Path, archive, token)))
                    continue;
                item = item with { Status = ConversionStatus.Running, Output = null, Error = null };
                Save(item, index);
                Report("Чтение исходника");
                if (!File.Exists(item.Input)) throw new FileNotFoundException("Исходник недоступен. Верните его на прежнее место.", item.Input);
                await using var source = new FileStream(item.Input, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var fingerprint = new ConversionSource(source.Length, File.GetLastWriteTimeUtc(item.Input),
                    Convert.ToHexString(await SHA256.HashDataAsync(source, token)));
                var saveLarger = checkpoint.SaveLarger == item.Id;
                if (saveLarger && (item.Candidate is null || fingerprint != item.Source))
                    throw new InvalidDataException("Исходник изменился или проверенный результат недоступен. Повторите обработку.");

                if (item.Candidate is { } earlier && fingerprint == item.Source)
                {
                    var earlierScratch = ConversionStore.ScratchFile(scratch, item.Id, earlier.Extension);
                    if (await ConversionStore.VerifyFile(earlierScratch, earlier, token))
                    {
                        var (path, archiveName) = ConversionStore.VideoDestination(job, item, scratch, earlier.Extension);
                        if (earlier.Path != path || earlier.ArchiveName != archiveName)
                        {
                            earlier = earlier with { Path = path, ArchiveName = archiveName };
                            item = item with { Candidate = earlier };
                            Save(item, index);
                        }
                        item = await CommitAsync(job, item, earlier, earlierScratch, saveLarger, Report, token);
                        Save(item, index);
                        Report(ItemState(item));
                        continue;
                    }
                }
                if (saveLarger) throw new IOException("Проверенный промежуточный файл недоступен. Повторите обработку.");
                if (item.Candidate is { } stale) ConversionStore.DiscardCandidate(job, item.Id, stale);
                item = item with { Source = fingerprint, Candidate = null, Seconds = 0 };
                Save(item, index);
                var media = await ConversionMedia.ProbeAsync(item.Input, token);
                var (candidate, stage) = await EncodeItemAsync(job, item, media, scratch, timer, Report, token);
                try
                {
                    item = item with { Candidate = candidate, Seconds = candidate.Seconds };
                    // Record the verified digest and intended name before the final move. Recovery can adopt a published file.
                    Save(item, index);
                    item = await CommitAsync(job, item, candidate, stage, false, Report, token);
                    Save(item, index);
                    Report(ItemState(item));
                }
                finally
                {
                    if (job.Items[index].Candidate is null) ConversionStore.ClearScratchFile(stage);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Save(item with { Status = ApplicationBackgroundOperations.IsSuspending(token) ? ConversionStatus.Pending : ConversionStatus.Canceled,
                    Seconds = timer.Elapsed.TotalSeconds }, index);
                throw;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
                or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
            {
                OwnedProcessRegistry.Log("video_conversion_item_failed", "VideoConverter", detail: error.ToString());
                var message = error is UnauthorizedAccessException ? "Доступ к файлу или папке запрещён. Проверьте доступ к исходнику, рабочей папке и папке результата."
                    : ConversionEncoder.ShortError(error);
                Save(item with { Status = ConversionStatus.Failed, Error = message, Seconds = timer.Elapsed.TotalSeconds }, index);
                Report("Ошибка файла");
            }
        }
        if (job.Options.Archive)
        {
            try
            {
                Report("Упаковка проверенных видео в ZIP");
                job = await ArchiveAsync(job, scratch, token);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                job = (ConversionStore.ReadJob(job.Id) ?? job) with { ArchiveError = ConversionEncoder.ShortError(error) };
                ConversionStore.SaveJob(job);
            }
        }
        else if (job.ArchiveCandidate is not null || job.ArchiveError is not null)
        {
            if (job.ArchiveCandidate is { } candidate)
                ConversionStore.DiscardCandidate(job, job.Id, candidate, "archive");
            job = job with { ArchiveCandidate = null, ArchiveError = null };
            ConversionStore.SaveJob(job);
        }
        Report("Обработка завершена");
        return job;
    }

    private static async Task<ConversionItem> CommitAsync(ConversionJob job, ConversionItem item, ConversionArtifact artifact,
        string staged, bool saveLarger, Action<string, double?> report, CancellationToken token)
    {
        if (artifact.Limit is long limit && artifact.Bytes > limit && !saveLarger
            && !CanKeepSmaller(job.Options.Mode, item.Source?.Size, artifact.Bytes))
            return item with { Status = ConversionStatus.OverLimit, Error =
                "Проверенное видео превышает цель. Измените параметры и повторите или отдельно подтвердите сохранение большего файла." };
        report("Запись готового файла", null);
        await ConversionStore.PublishAsync(staged, artifact, item.Id, null, token);
        return item with { Status = ConversionStatus.Ready, Output = artifact, Candidate = null, Error = null };
    }

    private static string ItemState(ConversionItem item) => item.Status == ConversionStatus.Ready ? "Файл проверен" : "Размер выше цели";
}
