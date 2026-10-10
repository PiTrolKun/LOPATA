using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed class PublisherStore : IDisposable
{
    private readonly string _path;
    private readonly FileStream _lease;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public PublisherStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "directions.json");
        _lease = new(Path.Combine(directory, "publisher.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public PublisherState Load()
    {
        if (!File.Exists(_path)) return new();
        try
        {
            var state = JsonSerializer.Deserialize<PublisherState>(File.ReadAllText(_path), Options)
                ?? throw new InvalidDataException();
            if (state.SchemaVersion != 1 || state.Directions is null) throw new InvalidDataException();
            // A persisted in-flight request may already have been accepted by VK.
            foreach (var direction in state.Directions)
            {
                if (direction is null || direction.Id == Guid.Empty || direction.CommunityId <= 0
                    || string.IsNullOrEmpty(direction.ProtectedToken) || direction.Releases is null
                    || direction.SeenReleaseIds is null || direction.InitialReleaseIds is null
                    || direction.Publications is null || direction.Queue is null || direction.AutomaticReleaseIds is null
                    || direction.Events is null || direction.CheckMinutes < 15 || direction.QueueHours is < 0 or > 720
                    || direction.Releases.Any(r => r is null || r.Id <= 0)
                    || direction.Publications.Any(p => p is null || p.ReleaseId <= 0
                        || p.Status is not ("sending" or "uncertain" or "published" or "failed")
                        || (p.Status == "published" && (p.PostId is not > 0 || p.PublishedAt is null))))
                    throw new InvalidDataException();
                if (PublisherGitHubClient.ParseRepository("https://github.com/" + direction.Repository) != direction.Repository)
                    throw new InvalidDataException();
                foreach (var item in direction.Publications.Where(p => p.Status == "sending")) item.Status = "uncertain";
                if (direction.Publications.Any(p => p.Status == "uncertain")) direction.StatusKey = "Publisher.Uncertain";
            }
            return state;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or PublisherException)
        { throw new PublisherException("Publisher.StoreInvalid"); }
    }

    public void Save(PublisherState state)
    {
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(file, state, Options); file.Flush(true); }
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static PublisherState Copy(PublisherState state) =>
        JsonSerializer.Deserialize<PublisherState>(JsonSerializer.Serialize(state, Options), Options)!;
    public void Dispose() => _lease.Dispose();
}
