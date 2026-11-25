using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AIHub.Services.VideoUpscale;

namespace AIHub.Services.VideoConverter;

internal static partial class ConversionBatch
{
    private static async Task<ConversionJob> ArchiveAsync(ConversionJob job, string scratch, CancellationToken token)
    {
        var inputs = new List<(ConversionArchiveEntry Entry, ConversionItem Item)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in job.Items)
        {
            if (item is not { Status: ConversionStatus.Ready, Output: { } video }) continue;
            var name = video.FileName;
            var stem = Path.GetFileNameWithoutExtension(name);
            for (int suffix = 2; !names.Add(name); suffix++) name = $"{stem} ({suffix}){video.Extension}";
            inputs.Add((new(item.Id, name, video.Sha256), item));
        }
        if (inputs.Count == 0) return job;

        var level = ZipLevel(job.Options);
        var entries = inputs.Select(input => input.Entry).ToArray();
        var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(level + "\n" +
            string.Join("\n", entries.Select(entry => $"{entry.Sha256} {entry.Name}")))));
        var previous = job.Archive;
        var verified = previous is not null && await ConversionStore.VerifyFile(previous.Path, previous, token) ? previous : null;
        var sameFolder = verified is not null && InOutputFolder(verified, job.Options);
        if (sameFolder && verified!.Details == signature)
            return await RecordArchiveAsync(job, verified with { Entries = entries }, token);

        var stage = ConversionStore.ScratchFile(scratch, job.Id, ".zip", "archive");
        var candidate = job.ArchiveCandidate;
        try
        {
            if (candidate is null || candidate.Details != signature || !InOutputFolder(candidate, job.Options)
                || !await ConversionStore.VerifyFile(stage, candidate, token))
            {
                ConversionStore.ClearScratchFile(stage);
                using var reservation = ManagedWorkStorage.Current.Reserve(
                    scratch, inputs.Sum(input => input.Item.Output!.Bytes) + 1024L * 1024, temporary: true);
                await WriteArchiveAsync(stage, inputs, verified, level, token);

                // Keep a delivered archive if replacing it would discard any of its previously saved entries.
                var replace = sameFolder && verified!.Entries is { } oldEntries && oldEntries.All(entries.Contains);
                var target = replace ? verified!.Path
                    : ConversionStore.AvailableName(job.Options.OutputDirectory, "Видео — ЛОПАТА", ".zip", []);
                candidate = new(target, ".zip", await UpscaleMediaTools.Hash(stage, token), new FileInfo(stage).Length, signature, 0);
            }
            candidate = candidate with { Entries = entries };
            job = job with { ArchiveCandidate = candidate, ArchiveError = null };
            ConversionStore.SaveJob(job);
            await ConversionStore.PublishAsync(stage, candidate, job.Id, verified, token);
            job = await RecordArchiveAsync(job, candidate, token);
            return job;
        }
        finally { if (job.ArchiveCandidate is null) ConversionStore.ClearScratchFile(stage); }
    }

    private static bool InOutputFolder(ConversionArtifact archive, ConversionOptions options) =>
        string.Equals(Path.GetDirectoryName(archive.Path), options.OutputDirectory, StringComparison.OrdinalIgnoreCase);

    private static async Task WriteArchiveAsync(string stage, List<(ConversionArchiveEntry Entry, ConversionItem Item)> inputs,
        ConversionArtifact? previous, CompressionLevel level, CancellationToken token)
    {
        await using (var file = new FileStream(stage, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 1024, true))
        {
            using var oldZip = previous?.Entries is not null ? ZipFile.OpenRead(previous.Path) : null;
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
                foreach (var (entry, item) in inputs)
                {
                    token.ThrowIfCancellationRequested();
                    await using var video = await OpenArchiveInputAsync(item, previous, oldZip, token);
                    await using var member = zip.CreateEntry(entry.Name, level).Open();
                    await video.CopyToAsync(member, token);
                }
            file.Flush(true);
        }

        using var verified = ZipFile.OpenRead(stage);
        if (verified.Entries.Count != inputs.Count) throw new IOException("В ZIP отсутствуют готовые файлы.");
        foreach (var (member, expected) in verified.Entries.Zip(inputs))
        {
            await using var content = member.Open();
            if (member.FullName != expected.Entry.Name || member.Length != expected.Item.Output!.Bytes
                || Convert.ToHexString(await SHA256.HashDataAsync(content, token)) != expected.Entry.Sha256)
                throw new IOException("Проверка содержимого ZIP не пройдена.");
        }
    }

    private static async Task<Stream> OpenArchiveInputAsync(ConversionItem item, ConversionArtifact? previous, ZipArchive? oldZip,
        CancellationToken token)
    {
        var output = item.Output!;
        if (await ConversionStore.VerifyFile(output.Path, output, token)) return File.OpenRead(output.Path);
        if (ConversionStore.ArchivedEntry(previous, item) is { } oldEntry
            && oldZip?.GetEntry(oldEntry.Name) is { } member && member.Length == output.Bytes)
            return member.Open();
        throw new IOException($"Проверенный файл больше недоступен: {output.FileName}.");
    }

    private static async Task<ConversionJob> RecordArchiveAsync(ConversionJob job, ConversionArtifact archive, CancellationToken token)
    {
        job = job with { Archive = archive, ArchiveCandidate = null, ArchiveError = null };
        ConversionStore.SaveJob(job);
        foreach (var item in job.Items)
        {
            if (item.Output is not { ArchiveName: not null } output || ConversionStore.ArchivedEntry(archive, item) is null)
                continue;
            try
            {
                if (await ConversionStore.VerifyFile(output.Path, output, token))
                    ConversionStore.DiscardStagedOutput(job, item);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                OwnedProcessRegistry.Log("video_conversion_archive_cleanup_failed", "VideoConverter", detail: error.ToString());
            }
        }
        return job;
    }

    internal static CompressionLevel ZipLevel(ConversionOptions options) => options.Mode != ConversionMode.Percent
        ? CompressionLevel.Optimal : options.Reduction switch
        {
            0 => CompressionLevel.NoCompression,
            <= 25 => CompressionLevel.Fastest,
            <= 65 => CompressionLevel.Optimal,
            _ => CompressionLevel.SmallestSize
        };
}
