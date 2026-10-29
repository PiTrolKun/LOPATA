using System.IO;
using AIHub.Models;

namespace AIHub.Services.VideoPreparation;

internal sealed record VideoH3FilePresence(string CardId, string Path, long Stored, bool Downloaded);
internal sealed class VideoH3FileRow(string cardId, string path, long size)
{
    internal string CardId { get; } = cardId;
    internal string Path { get; } = path;
    internal long Size { get; } = size;
    internal long Stored { get; set; }
    internal string Stage { get; set; } = "waiting";
}

/// <summary>Presentation state only. Presence is never represented as an integrity receipt.</summary>
internal sealed class VideoH3DownloadState(IReadOnlyList<ManagedModelArtifactCard> cards)
{
    internal IReadOnlyList<VideoH3FileRow> Rows { get; } = cards.SelectMany(c => c.Files.Where(f => f.IsRequired)
        .Select(f => new VideoH3FileRow(c.ModelArtifactId, f.RelativePath, f.SizeBytes))).ToArray();
    internal double BytesPerSecond { get; private set; }
    internal long TotalBytes => Rows.Sum(r => r.Size);
    internal long StoredBytes => Rows.Sum(r => r.Stored);
    internal static IReadOnlyList<VideoH3FilePresence> Inspect(IReadOnlyList<ManagedModelArtifactCard> cards) =>
        cards.SelectMany(c => c.Files.Where(f => f.IsRequired).Select(f =>
        {
            var path = System.IO.Path.Combine(c.InstallDirectory, f.RelativePath);
            return new VideoH3FilePresence(c.ModelArtifactId, f.RelativePath,
                SegmentedModelFileDownloader.GetStoredBytes(path, f.SizeBytes), File.Exists(path));
        })).ToArray();
    internal void Refresh(IReadOnlyList<VideoH3FilePresence> files, bool ready = false)
    {
        BytesPerSecond = 0;
        foreach (var row in Rows)
        {
            var file = files.Single(f => f.CardId == row.CardId && f.Path == row.Path);
            row.Stored = Math.Clamp(file.Stored, 0, row.Size);
            row.Stage = ready || row.Stage == "ready" && file.Downloaded ? "ready"
                : file.Downloaded ? "downloaded" : file.Stored > 0 ? "partial" : "waiting";
        }
    }
    internal void Apply(ManagedModelDownloadProgress progress)
    {
        var group = Rows.Where(r => r.CardId == progress.ModelArtifactId).ToArray();
        BytesPerSecond = progress.Stage == "downloading" ? Math.Max(0, progress.BytesPerSecond) : 0;
        if (progress.FileName.Length == 0)
        {
            if (progress.Stage is "verified" or "verifying-presence")
                foreach (var row in group) { row.Stage = "ready"; row.Stored = row.Size; }
            return;
        }
        var current = group.FirstOrDefault(r => r.Path == progress.FileName);
        if (current is null) return;
        long prefix = 0;
        foreach (var row in group)
        {
            if (ReferenceEquals(row, current)) break;
            prefix += row.Size;
            // The common acquisition verifier has advanced past these files.
            if (progress.Stage == "verifying") { row.Stage = "ready"; row.Stored = row.Size; }
        }
        current.Stage = progress.Stage switch { "downloading" => "downloading", "assembling" => "assembling", "restarting" => "restarting", _ => "verifying" };
        if (progress.Stage == "restarting") current.Stored = 0;
        if (progress.Stage is "downloading" or "assembling") current.Stored = Math.Clamp(progress.DownloadedBytes - prefix, 0, current.Size);
    }
    internal void Fail(IReadOnlyList<VideoH3FilePresence> files)
    {
        var active = Rows.Where(r => r.Stage is "downloading" or "assembling" or "verifying" or "restarting").ToArray();
        Refresh(files); foreach (var row in active) row.Stage = "failed";
    }
}
