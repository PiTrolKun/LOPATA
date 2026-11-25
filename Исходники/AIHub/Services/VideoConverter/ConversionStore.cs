using System.IO;
using System.Text.Json;
using AIHub.Services.VideoUpscale;
using Lopata.Updates;

namespace AIHub.Services.VideoConverter;

internal enum ConversionStatus { Pending, Running, Ready, Failed, Canceled, OverLimit }

internal sealed record ConversionSource(long Size, DateTime WriteUtc, string Sha256);

internal sealed record ConversionArchiveEntry(string ItemId, string Name, string Sha256);

internal sealed record ConversionArtifact(string Path, string Extension, string Sha256, long Bytes, string Details,
    double Seconds, long? Limit = null)
{
    public string? ArchiveName { get; init; }
    public ConversionArchiveEntry[]? Entries { get; init; }
    internal string FileName => ArchiveName ?? System.IO.Path.GetFileName(Path);
}

internal sealed record ConversionItem(string Id, string Input)
{
    public ConversionStatus Status { get; init; }
    public ConversionSource? Source { get; init; }
    public ConversionArtifact? Candidate { get; init; }
    public ConversionArtifact? Output { get; init; }
    public string? Error { get; init; }
    public string? RecycleNote { get; init; }
    public double Seconds { get; init; }
}

internal sealed record ConversionJob(string Id, ConversionOptions Options, ConversionItem[] Items)
{
    public int Schema { get; init; } = 1;
    public ConversionArtifact? Archive { get; init; }
    public ConversionArtifact? ArchiveCandidate { get; init; }
    public string? ArchiveError { get; init; }
}

internal sealed record ConversionDraft(ConversionOptions Options, string[] Inputs, string? JobId);
internal sealed record ConversionCheckpoint(string JobId, string[] Items, string? SaveLarger = null);

internal static class ConversionStore
{
    internal const int QueueLimit = 512;
    private const string JobFamily = "VideoConverter/Jobs";
    private const string ScratchFamily = "VideoConverter/Scratch";
    internal static string NewId() => Guid.NewGuid().ToString("N");
    private static bool ValidId(string? id) => Guid.TryParseExact(id, "N", out _);

    private static string DraftPath => Path.Combine(AppDataPaths.BaseDirectory, "VideoConverter", "last-settings.json");
    internal static ConversionDraft ReadDraft()
    {
        var draft = Read<ConversionDraft>(DraftPath) ?? new(new(), [], null);
        if (draft.Options is null || draft.Inputs is null || draft.Inputs.Length > QueueLimit || draft.JobId is not null && !ValidId(draft.JobId))
            throw new InvalidDataException("Не удалось прочитать последние настройки конвертера.");
        return draft;
    }
    internal static void SaveDraft(ConversionDraft draft) => BackgroundOperationStore.SaveAtomic(DraftPath, draft, tolerateBriefFileLock: true);

    internal static string JobPath(ConversionJob job) => Path.Combine(
        ManagedWorkStorage.Current.Job(JobFamily, job.Id, outputDirectory: job.Options.Archive ? null : job.Options.OutputDirectory),
        "queue.json");
    internal static string ExistingJobPath(string id)
    {
        if (!ValidId(id)) throw new InvalidDataException("Некорректный номер очереди конвертера.");
        var storage = ManagedWorkStorage.Current;
        var folder = storage.ManagedFolder(JobFamily, id) ?? storage.LegacyJob(JobFamily, id);
        return Path.Combine(folder, "queue.json");
    }

    internal static ConversionJob? ReadJob(string id)
    {
        if (Read<ConversionJob>(ExistingJobPath(id)) is not { } job) return null;
        if (job.Schema != 1 || job.Id != id || job.Options is null || job.Items is null || job.Items.Length > QueueLimit
            || job.Items.Any(item => item is null || !ValidId(item.Id) || !Path.IsPathFullyQualified(item.Input) || !ConversionOptions.Accepts(item.Input)
                || !Enum.IsDefined(item.Status) || !ValidArtifact(item.Candidate) || !ValidArtifact(item.Output))
            || job.Items.Select(item => item.Id).Distinct().Count() != job.Items.Length
            || job.Items.Select(item => item.Input).Distinct(StringComparer.OrdinalIgnoreCase).Count() != job.Items.Length
            || !ValidArtifact(job.Archive, true) || !ValidArtifact(job.ArchiveCandidate, true))
            throw new InvalidDataException("Не удалось прочитать очередь конвертера.");
        job.Options.Validate();
        return job;
    }

    private static bool ValidArtifact(ConversionArtifact? artifact, bool zip = false) => artifact is null
        || artifact.Bytes > 0 && ValidHash(artifact.Sha256)
        && Path.IsPathFullyQualified(artifact.Path) && Path.GetExtension(artifact.Path) == artifact.Extension
        && (zip ? artifact.Extension == ".zip" && artifact.ArchiveName is null && ValidEntries(artifact.Entries)
            : artifact.Extension is (".mp4" or ".mkv" or ".webm") && artifact.Entries is null
              && (artifact.ArchiveName is null || IsFileName(artifact.ArchiveName) && Path.GetExtension(artifact.ArchiveName) == artifact.Extension));

    private static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
    private static bool IsFileName(string? name) => !string.IsNullOrWhiteSpace(name) && name is not ("." or "..")
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static bool ValidEntries(ConversionArchiveEntry[]? entries) => entries is null
        || entries.Length <= QueueLimit
        && entries.All(entry => entry is not null && ValidId(entry.ItemId) && IsFileName(entry.Name) && ValidHash(entry.Sha256))
        && entries.Select(entry => entry.ItemId).Distinct(StringComparer.Ordinal).Count() == entries.Length
        && entries.Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == entries.Length;

    internal static ConversionArchiveEntry? ArchivedEntry(ConversionArtifact? archive, ConversionItem item) =>
        item.Output is { } video
            ? archive?.Entries?.FirstOrDefault(entry => entry.ItemId == item.Id && entry.Sha256 == video.Sha256)
            : null;

    internal static bool HasUnpackedOutput(ConversionJob job, ConversionItem item) =>
        item.Output?.ArchiveName is not null && ArchivedEntry(job.Archive, item) is null;

    internal static string? DeliveredPath(ConversionJob job, ConversionItem item)
    {
        if (item.Output is not { } output) return null;
        if (output.ArchiveName is null) return output.Path;
        return ArchivedEntry(job.Archive, item) is null ? null : job.Archive?.Path;
    }

    internal static void SaveJob(ConversionJob job) => BackgroundOperationStore.SaveAtomic(JobPath(job), job, tolerateBriefFileLock: true);

    private static T? Read<T>(string path) where T : class
    {
        SafeUpdatePath.RejectLinks(path);
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(file, BackgroundOperationStore.Options)
                ?? throw new InvalidDataException("Данные конвертера пусты.");
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal static string Scratch(ConversionJob job) => ManagedWorkStorage.Current.Job(ScratchFamily, job.Id, temporary: true);

    internal static string ScratchFile(string scratch, string item, string extension, string prefix = "file")
    {
        if (!ValidId(item) || extension is not (".mp4" or ".mkv" or ".webm" or ".zip"))
            throw new InvalidDataException("Некорректный путь служебного файла.");
        var path = Path.Combine(scratch, $"{prefix}-{item}{extension}");
        SafeUpdatePath.RejectLinks(path);
        return path;
    }

    internal static (string Path, string? ArchiveName) VideoDestination(
        ConversionJob job, ConversionItem item, string scratch, string extension)
    {
        var stem = Path.GetFileNameWithoutExtension(item.Input) + " — ЛОПАТА";
        if (!job.Options.Archive)
            return (AvailableName(job.Options.OutputDirectory, stem, extension, job.Items.Select(i => i.Input)), null);

        var path = ScratchFile(scratch, item.Id, extension, "ready");
        if (job.Items.Any(input => input.Input.Equals(path, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Служебное имя результата занято исходником.");
        return (path, stem[..Math.Min(stem.Length, 170)] + extension);
    }

    internal static void DiscardStagedOutput(ConversionJob job, ConversionItem item)
    {
        if (item.Output is not { ArchiveName: not null } output) return;
        var staged = ScratchFile(Scratch(job), item.Id, output.Extension, "ready");
        if (output.Path.Equals(staged, StringComparison.OrdinalIgnoreCase)
            && !job.Items.Any(input => input.Input.Equals(staged, StringComparison.OrdinalIgnoreCase)))
            ClearScratchFile(staged);
    }

    internal static string DefaultOutput => ManagedWorkStorage.Current.Results("VideoConverter/Результаты");

    internal static void ClearScratchFile(string path)
    {
        SafeUpdatePath.RejectLinks(path);
        try { if (File.Exists(path)) File.Delete(path); } catch (DirectoryNotFoundException) { }
    }

    internal static void DiscardCandidate(ConversionJob job, string id, ConversionArtifact artifact, string prefix = "file")
    {
        ClearScratchFile(ScratchFile(Scratch(job), id, artifact.Extension, prefix));
        ClearScratchFile(PendingPath(artifact.Path, id));
    }

    internal static async Task<bool> VerifyFile(string path, ConversionArtifact expected, CancellationToken token)
    {
        SafeUpdatePath.RejectLinks(path);
        try
        {
            var file = new FileInfo(path);
            return file.Exists && file.Length == expected.Bytes
                && await UpscaleMediaTools.Hash(path, token) == expected.Sha256;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal static string AvailableName(string folder, string stem, string extension, IEnumerable<string> inputs)
    {
        var occupied = inputs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Keep enough space for the suffix on files whose source name already uses the full Windows name limit.
        if (stem.Length > 170) stem = stem[..170];
        for (var suffix = 1; suffix <= 10000; suffix++)
        {
            var name = Path.Combine(folder, stem + (suffix == 1 ? "" : $" ({suffix})") + extension);
            if (!occupied.Contains(name) && !File.Exists(name) && !Directory.Exists(name)) return name;
        }
        throw new IOException("Не удалось подобрать свободное имя файла в папке сохранения.");
    }

    internal static async Task PublishAsync(string draft, ConversionArtifact candidate, string id,
        ConversionArtifact? previous, CancellationToken token)
    {
        var target = candidate.Path;
        var folder = Path.GetDirectoryName(target) ?? throw new InvalidDataException("Не задана папка готового файла.");
        SafeUpdatePath.RejectLinks(target);
        if (await VerifyFile(target, candidate, token)) { ClearScratchFile(draft); return; }
        if (!await VerifyFile(draft, candidate, token))
            throw new IOException("Проверенный промежуточный файл недоступен. Повторите обработку.");
        var replace = previous is not null && target.Equals(previous.Path, StringComparison.OrdinalIgnoreCase)
            && await VerifyFile(target, previous, token);
        if (!replace && (File.Exists(target) || Directory.Exists(target)))
            throw new IOException("Имя результата занято другим файлом. Можно повторить обработку.");

        var sameVolume = string.Equals(Path.GetPathRoot(draft), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase);
        using var capacity = ManagedWorkStorage.Current.Reserve(folder, sameVolume ? 0 : candidate.Bytes,
            temporary: candidate.ArchiveName is not null);
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(folder);
        if (sameVolume)
        {
            if (replace) File.Replace(draft, target, null);
            else File.Move(draft, target);
            return;
        }

        var pending = PendingPath(target, id);
        SafeUpdatePath.RejectLinks(pending);
        // Only this operation knows the random id. Remove a leftover copy from an interrupted commit.
        ClearScratchFile(pending);
        try
        {
            await using (var input = File.OpenRead(draft))
            await using (var copy = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(copy, token);
                copy.Flush(true);
            }
            if (!await VerifyFile(pending, candidate, token)) throw new IOException("Проверка записи на диск результата не пройдена.");
            token.ThrowIfCancellationRequested();
            if (replace)
            {
                if (!await VerifyFile(target, previous!, token)) throw new IOException("Готовый архив изменился во время записи.");
                File.Replace(pending, target, null);
            }
            else File.Move(pending, target);
            ClearScratchFile(draft);
        }
        finally { ClearScratchFile(pending); }
    }

    private static string PendingPath(string target, string id) => Path.Combine(Path.GetDirectoryName(target)!, $".lopata-{id}.pending");
}
