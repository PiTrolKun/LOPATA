using System.IO;
using System.Text.Json;

namespace AIHub.Services.SpeechAssets;

/// <summary>Internal project catalogue: automatic requests and explicitly fixed projects, as in MusicProjects.</summary>
internal sealed partial class SpeechAssetStore
{
    internal SpeechAssetSession CreateDraft(SpeechAssetSession? defaults = null) => new()
    {
        Number = NextNumber(), Voice = defaults?.Voice ?? new SpeechAssetSession().Voice,
        Profiles = defaults is null ? [] : new(defaults.Profiles),
        ExportFormat = defaults?.ExportFormat ?? MusicAudioFormat.Opus, ExportBitrate = defaults?.ExportBitrate ?? 64
    };

    private long NextNumber()
    {
        var key = Path.GetFullPath(Root);
        lock (Gates.GetOrAdd(key, _ => new()))
        {
            Directory.CreateDirectory(Root);
            var file = Path.Combine(Root, "counter.json");
            var number = 0L;
            if (File.Exists(file))
            {
                try { number = JsonDocument.Parse(File.ReadAllText(file)).RootElement.GetProperty("Number").GetInt64(); }
                catch (JsonException) { number = JsonDocument.Parse(File.ReadAllText(file + ".bak")).RootElement.GetProperty("Number").GetInt64(); }
            }
            number = checked(number + 1);
            WriteAtomic(file, JsonSerializer.Serialize(new { Number = number }));
            return number;
        }
    }

    internal IReadOnlyList<SpeechAssetSession> ListProjects()
    {
        if (!Directory.Exists(Root)) return [];
        var projects = new List<SpeechAssetSession>();
        foreach (var directory in Directory.EnumerateDirectories(Root))
        {
            var id = Path.GetFileName(directory);
            if (!Guid.TryParseExact(id, "N", out _) || !File.Exists(Path.Combine(directory, "session.json"))) continue;
            var session = Load(id);
            if (session.Persistent) projects.Add(session);
        }
        return projects.OrderByDescending(p => p.Number).ToArray();
    }

    internal void FixProject(SpeechAssetSession session)
    {
        if (session.Number == 0) session.Number = NextNumber();
        session.Persistent = true; session.Manual = true; Save(session);
    }

    internal void RenameProject(SpeechAssetSession session, string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 200) throw new InvalidDataException("SpeechAssets.InvalidProjectName");
        session.Name = name; Save(session);
    }

    internal void BeginRun(SpeechAssetSession session, SpeechAssetJob job)
    {
        ApplyRun(session, job); Save(session);
    }

    internal SpeechAssetSession BeginRun(SpeechAssetJob job) => Update(job.SessionId, session => ApplyRun(session, job));

    private void ApplyRun(SpeechAssetSession session, SpeechAssetJob job)
    {
        if (session.Number == 0) session.Number = NextNumber();
        session.Persistent = true; session.PendingJob = job;
        if (job.RunId is { } id && session.Runs.All(r => r.Id != id)) session.Runs.Add(new() { Id = id, Job = job });
    }

    internal void RunState(SpeechAssetJob job, string state)
    {
        if (job.RunId is null) return;
        Update(job.SessionId, session =>
        {
            if (session.Runs.FirstOrDefault(r => r.Id == job.RunId) is { } run) run.State = state;
        });
    }
}
