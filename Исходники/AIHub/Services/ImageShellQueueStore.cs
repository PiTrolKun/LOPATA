using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed class ImageShellJob
{
    public ImageShellRequest Request { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ImageShellQueueItem> Items { get; set; } = [];
    public bool Finished { get; set; }
}

public sealed class ImageShellQueueItem
{
    public string Path { get; set; } = "";
    public string? OutputPath { get; set; }
    public ImageUtilityItemStatus Status { get; set; } = ImageUtilityItemStatus.Pending;
    public int Attempts { get; set; }
    public string? Error { get; set; }
}

/// <summary>Durable inbox; acknowledgement is sent only after Enqueue has persisted the request.</summary>
public sealed class ImageShellQueueStore
{
    private readonly string _root;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public ImageShellQueueStore(string root) => _root = System.IO.Path.GetFullPath(root);
    private string JobPath(string id) => System.IO.Path.Combine(_root, ValidateId(id) + ".json");
    private static string ValidateId(string id) => Guid.TryParseExact(id, "N", out _) ? id
        : throw new InvalidDataException("Invalid shell request identifier.");

    public bool Enqueue(ImageShellRequest request)
    {
        request = request.Validate();
        lock (_gate)
        {
            ValidateId(request.RequestId);
            if (request.Paths.Count == 0 || request.Paths.Count > ImageShellRequest.MaximumPaths || !Enum.IsDefined(request.Operation))
                throw new InvalidDataException("Invalid shell request.");
            foreach (var path in request.Paths)
                if (!System.IO.Path.IsPathFullyQualified(path) || path.IndexOf('\0') >= 0)
                    throw new InvalidDataException("An absolute local file path is required.");
            if (Load(request.RequestId) is { } previous)
            {
                if (previous.Request.Operation != request.Operation || !previous.Request.Paths.SequenceEqual(request.Paths))
                    throw new InvalidDataException("Request identifier is already used by a different command.");
                return false;
            }
            Save(new() { Request = request, Items = request.Paths.Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => new ImageShellQueueItem { Path = path }).ToList() });
            return true;
        }
    }

    public ImageShellJob? Load(string id)
    {
        lock (_gate)
        {
            var path = JobPath(id); Lopata.Updates.SafeUpdatePath.RejectLinks(path);
            if (!File.Exists(path)) return null;
            var job = JsonSerializer.Deserialize<ImageShellJob>(File.ReadAllBytes(path), Json)
                ?? throw new InvalidDataException("Empty shell queue file.");
            return ValidateLoadedJob(job, id);
        }
    }

    private static ImageShellJob ValidateLoadedJob(ImageShellJob job, string expectedId)
    {
        job.Request = job.Request?.Validate() ?? throw new InvalidDataException("Missing shell queue request.");
        if (!job.Request.RequestId.Equals(expectedId, StringComparison.OrdinalIgnoreCase)
            || job.Items is null || job.Items.Count != job.Request.Paths.Count)
            throw new InvalidDataException("Shell queue contents do not match the request.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in job.Items)
        {
            if (item is null || !job.Request.Paths.Contains(item.Path, StringComparer.OrdinalIgnoreCase) || !paths.Add(item.Path)
                || item.Status is not (ImageUtilityItemStatus.Pending or ImageUtilityItemStatus.Running
                    or ImageUtilityItemStatus.Completed or ImageUtilityItemStatus.Failed)
                || item.Attempts is < 0 or > 4
                || (item.Status == ImageUtilityItemStatus.Pending && item.Attempts >= 4)
                || (item.Status is ImageUtilityItemStatus.Running or ImageUtilityItemStatus.Completed && item.Attempts == 0)
                || (item.Status == ImageUtilityItemStatus.Failed && item.Attempts != 4)
                || (job.Finished && item.Status is not (ImageUtilityItemStatus.Completed or ImageUtilityItemStatus.Failed))
                || (item.Status == ImageUtilityItemStatus.Completed && string.IsNullOrWhiteSpace(item.OutputPath)))
                throw new InvalidDataException("Invalid shell queue item state.");
            if (item.OutputPath is not null)
                item.OutputPath = (job.Request with { Paths = [item.OutputPath] }).Validate().Paths[0];
        }
        return job;
    }

    public void Save(ImageShellJob job)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_root);
            var path = JobPath(job.Request.RequestId); Lopata.Updates.SafeUpdatePath.RejectLinks(path);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { JsonSerializer.Serialize(file, job, Json); file.Flush(flushToDisk: true); }
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    public IReadOnlyList<ImageShellJob> Pending()
    {
        if (!Directory.Exists(_root)) return [];
        var result = new List<ImageShellJob>();
        foreach (var path in Directory.EnumerateFiles(_root, "*.json"))
        {
            try { if (Load(System.IO.Path.GetFileNameWithoutExtension(path)) is { Finished: false } job) result.Add(job); }
            catch (Exception error) when (error is IOException or JsonException or InvalidDataException)
            { OwnedProcessRegistry.Log("shell_inbox_invalid", "ImageShell", detail: error.GetType().Name); }
        }
        return result.OrderBy(job => job.CreatedAt).ToArray();
    }

    public FileStream? TryLease(string id)
    {
        Directory.CreateDirectory(_root);
        var path = System.IO.Path.Combine(_root, ValidateId(id) + ".lock");
        Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }
    public string Transactions(string id) => System.IO.Path.Combine(_root, "transactions", ValidateId(id));
}
