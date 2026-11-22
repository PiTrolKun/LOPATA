using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Lopata.Inspector;

public sealed partial class InspectorStore
{
    private string JournalDirectory => Path.Combine(DirectoryPath, "journal");
    private string NoticeDirectory => Path.Combine(DirectoryPath, "notices");
    private string ExperienceDirectory => Path.Combine(DirectoryPath, "experience");

    internal void Record(InspectorEvent item) => Append("events", item);
    internal void Record(InspectorSample item) => Append("samples", item);

    private void Append<T>(string kind, T item)
    {
        var directory = Path.Combine(JournalDirectory, kind);
        var file = Path.Combine(directory, DateTimeOffset.UtcNow.ToString("yyyy-MM-dd-HH", CultureInfo.InvariantCulture) + ".jsonl");
        try
        {
            Directory.CreateDirectory(directory);
            var entry = JsonSerializer.SerializeToUtf8Bytes(item, JsonOptions);
            using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            stream.Write(entry);
            stream.WriteByte((byte)'\n');
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    internal bool RecordNotice(InspectorNotice notice) => WithLock("notices", () =>
    {
        var path = NoticePath(notice.Id);
        if (Read<InspectorNotice>(path) is not null) return false;
        return TryWrite(path, notice);
    });

    internal bool HasNotice(string id) => File.Exists(NoticePath(id));

    public bool ConfirmNoticeDisplayed(string? noticeId)
    {
        if (!ValidNoticeId(noticeId)) return false;
        return WithLock("notices", () =>
        {
            var path = NoticePath(noticeId!);
            var notice = Read<InspectorNotice>(path);
            if (notice is null) return false;
            return notice.Shown || TryWrite(path, notice with { Shown = true });
        });
    }

    private static bool ValidNoticeId(string? id) =>
        id is { Length: 64 } && id.All(char.IsAsciiHexDigit);

    private string NoticePath(string id) => Path.Combine(NoticeDirectory, id + ".json");

    public InspectorNotice[] ReadNotices() =>
        RecentNotices(DateTimeOffset.UtcNow).Where(item => item.CanRate && item.Shown).ToArray();

    private InspectorNotice[] RecentNotices(DateTimeOffset now) => Files(NoticeDirectory)
        .Select(Read<InspectorNotice>)
        .OfType<InspectorNotice>()
        .Where(item => item.TimestampUtc >= now - NoticeAge && item.TimestampUtc <= now)
        .OrderByDescending(item => item.TimestampUtc).ToArray();

    public bool Rate(string noticeId, bool helpful)
    {
        if (!ValidNoticeId(noticeId)) return false;
        try
        {
            return WithLock("notices", () =>
            {
                var path = NoticePath(noticeId);
                var notice = Read<InspectorNotice>(path);
                if (notice is not { CanRate: true, Shown: true } ||
                    notice.TimestampUtc < DateTimeOffset.UtcNow - NoticeAge) return false;
                var updated = notice with { Helpful = helpful };
                if (notice.Helpful != helpful) Write(path, updated);
                RecalculateRatings(updated);
                return true;
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    internal void FinishNotices(string operationId, int? attempt, InspectorOutcome outcome, InspectorExperience? experience)
    {
        WithLock("notices", () =>
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var notice in RecentNotices(now).Where(item => item.OperationId == operationId
                         && (attempt is null || item.Attempt == attempt)))
            {
                var updated = notice with
                {
                    Outcome = outcome.ToString(),
                    ExperienceId = experience?.Id ?? notice.ExperienceId,
                    ExperienceContext = experience?.Context ?? notice.ExperienceContext,
                    ExperienceCompletedUtc = experience?.CompletedUtc ?? notice.ExperienceCompletedUtc
                };
                Write(NoticePath(notice.Id), updated);
                if (updated.Helpful is not null) RecalculateRatings(updated);
            }
            return true;
        });
    }

    private void RecalculateRatings(InspectorNotice notice)
    {
        if (notice is not { Shown: true, ExperienceId: not null, ExperienceContext: not null,
            ExperienceCompletedUtc: not null, Helpful: not null }) return;
        var path = ExperiencePath(notice.ExperienceContext, notice.ExperienceCompletedUtc.Value);
        WithLock("experience", () =>
        {
            var group = Read<InspectorExperience[]>(path) ?? [];
            var index = Array.FindIndex(group, item => item.Id == notice.ExperienceId);
            if (index < 0) return false;
            var experience = group[index];
            var votes = experience.Ratings ?? new Dictionary<string, bool>();
            votes[notice.Id] = notice.Helpful.Value;
            group[index] = experience with
            {
                Ratings = votes,
                HelpfulCount = votes.Values.Count(value => value),
                UnhelpfulCount = votes.Values.Count(value => !value)
            };
            Write(path, group);
            return true;
        });
    }

    internal void RecordExperience(InspectorExperience item)
    {
        WithLock("experience", () =>
        {
            var path = ExperiencePath(item.Context, item.CompletedUtc);
            var group = Read<InspectorExperience[]>(path) ?? [];
            if (group.Any(existing => existing.Id == item.Id)) return true;
            return TryWrite(path, group.Append(item).ToArray());
        });
    }

    private string ExperiencePath(string context, DateTimeOffset date) =>
        Path.Combine(ExperienceDirectory, Hash(context), date.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json");

    internal InspectorExperience[] ReadExperience(string context, DateTimeOffset now) =>
        ReadExperienceFiles(Files(Path.Combine(ExperienceDirectory, Hash(context))))
            .Where(item => item.CompletedUtc >= now - ExperienceAge && item.CompletedUtc <= now)
            .Take(128)
            .ToArray();

    private static IEnumerable<InspectorExperience> ReadExperienceFiles(string[] paths) =>
        paths.OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)
            .ThenByDescending(File.GetLastWriteTimeUtc)
            .SelectMany(path => (Read<InspectorExperience[]>(path) ?? []).OrderByDescending(item => item.CompletedUtc));

    public InspectorReport ReadReport()
    {
        var cutoff = DateTimeOffset.UtcNow;
        var experience = ReadExperienceFiles(Files(ExperienceDirectory, recursive: true))
            .Where(item => item.CompletedUtc >= cutoff - ExperienceAge && item.CompletedUtc <= cutoff).Take(120).ToArray();
        return new InspectorReport(
            ReadJournal<InspectorEvent>("events", 700).Where(item => item.TimestampUtc >= cutoff - DetailAge).ToArray(),
            ReadJournal<InspectorSample>("samples", 4096).Where(item => item.TimestampUtc >= cutoff - DetailAge).ToArray(),
            RecentNotices(cutoff).Where(item => item.Shown).ToArray(),
            experience);
    }

    private T[] ReadJournal<T>(string kind, int max)
    {
        var collected = new List<T>();
        foreach (var path in JournalFiles(Path.Combine(JournalDirectory, kind)).OrderDescending(StringComparer.Ordinal))
        {
            var rows = new Queue<T>();
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (reader.ReadLine() is { } line)
                {
                    try
                    {
                        if (JsonSerializer.Deserialize<T>(line, JsonOptions) is not { } item) continue;
                        if (rows.Count == max) rows.Dequeue();
                        rows.Enqueue(item);
                    }
                    catch (JsonException) { }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            var recent = rows.Reverse().Take(max - collected.Count);
            collected.AddRange(recent);
            if (collected.Count >= max) break;
        }
        collected.Reverse();
        return collected.ToArray();
    }

    private static string[] JournalFiles(string directory)
    {
        try
        {
            return !Directory.Exists(directory) ? [] : Directory.GetFiles(directory, "*.jsonl",
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    internal void Rotate(DateTimeOffset now)
    {
        Trim(JournalFiles(JournalDirectory), now - DetailAge, DetailLimit);
        Trim(Files(ExperienceDirectory, recursive: true), now - ExperienceAge, ExperienceLimit);
        WithLock("notices", () =>
        {
            foreach (var path in Files(NoticeDirectory))
            {
                var item = Read<InspectorNotice>(path);
                if (item is null || item.TimestampUtc < now - NoticeAge) Delete(path);
            }
            return true;
        });
        Trim(Files(Path.Combine(DirectoryPath, "receipts")), now - TimeSpan.FromMinutes(2), 1024 * 1024);
        Trim(Files(Path.Combine(DirectoryPath, "queue")), now - DetailAge, DetailLimit);
    }

    private static void Trim(string[] paths, DateTimeOffset cutoff, long capacity)
    {
        long total = 0;
        var files = paths.Select(path => new FileInfo(path)).OrderByDescending(file => file.LastWriteTimeUtc);
        foreach (var file in files)
        {
            try
            {
                if (file.LastWriteTimeUtc < cutoff || file.Length > capacity - total) Delete(file.FullName);
                else total += file.Length;
            }
            catch (IOException) { }
        }
    }
}
