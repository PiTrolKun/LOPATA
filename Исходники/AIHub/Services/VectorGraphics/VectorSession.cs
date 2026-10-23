using System.IO;
using System.Text.Json;

namespace AIHub.Services.VectorGraphics;

/// <summary>Only a current or explicitly restored session is visible; clearing never removes durable jobs.</summary>
public sealed class VectorSession
{
    private readonly string _root;
    private readonly List<string> _jobs = [];
    private readonly HashSet<string> _hidden = [];
    public string Id { get; private set; } = Guid.NewGuid().ToString("N");
    public VectorSession(string? root = null) => _root = root ?? Path.Combine(AppDataPaths.BaseDirectory, "VectorGraphics", "Sessions");
    public void Add(VectorRequest request)
    {
        if (request.SessionId != Id) throw new InvalidDataException("Vector.Error.Job");
        if (!_jobs.Contains(request.Id)) _jobs.Add(request.Id);
        Persist();
    }
    public void Restore(VectorRequest request)
    {
        var id = request.SessionId ?? request.Id;
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Vector.Error.Job");
        if (Id != id)
        {
            Id = id; _jobs.Clear(); _hidden.Clear();
            var path = PathFor(id);
            if (request.SessionId is not null && File.Exists(path))
            {
                if (new FileInfo(path).Length > 32L * 1024 * 1024) throw new InvalidDataException("Vector.Error.Job");
                var stored = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? [];
                if (stored.Any(s => !Guid.TryParseExact(s, "N", out _))) throw new InvalidDataException("Vector.Error.Job");
                _jobs.AddRange(stored.Distinct());
            }
        }
        if (!_jobs.Contains(request.Id)) _jobs.Add(request.Id);
        _hidden.Remove(request.Id); // Explicit restoration may reveal the requested result.
    }
    public VectorResult[] Visible()
    {
        var results = new List<VectorResult>();
        foreach (var id in _jobs.Where(id => !_hidden.Contains(id)))
        {
            try
            {
                var job = VectorStore.Load(id);
                if (job.Request.SessionId == Id || job.Request.SessionId is null && job.Request.Id == Id) results.Add(job);
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            { OwnedProcessRegistry.Log("vector_session_unavailable", "Vector", detail: e.GetType().Name); }
        }
        return results.ToArray();
    }
    public void Clear() { foreach (var id in _jobs) _hidden.Add(id); }
    private string PathFor(string id)
    {
        var path = Path.Combine(_root, id + ".json"); Lopata.Updates.SafeUpdatePath.RejectLinks(path); return path;
    }
    private void Persist()
    {
        Directory.CreateDirectory(_root); var path = PathFor(Id); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(_jobs)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
