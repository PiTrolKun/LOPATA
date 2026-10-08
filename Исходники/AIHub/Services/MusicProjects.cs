using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Project documents refer to durable Jobs; drafts never get a project file.</summary>
public sealed class MusicProjects(string directory)
{
    public static MusicProjects Default { get; } = new(Path.Combine(AppDataPaths.BaseDirectory, "Music", "Projects"));
    private sealed record Counter(int Schema, long Number);
    private string PathFor(string id) => Guid.TryParseExact(id, "N", out _) ? Path.Combine(directory, id + ".project.json")
        : throw new InvalidDataException("Invalid music project identifier.");
    public MusicProject CreateDraft()
    {
        using var gate = Lock(); var path = Path.Combine(directory, "counter.json");
        var counter = File.Exists(path) ? Read<Counter>(path) : new Counter(1, 0);
        if (counter.Schema != 1 || counter.Number < 0) throw new InvalidDataException("Invalid music project counter.");
        var number = checked(counter.Number + 1); Write(path, new Counter(1, number));
        return new(Guid.NewGuid().ToString("N"), number, DateTimeOffset.Now);
    }
    public MusicProject Load(string id)
    {
        var value = Read<MusicProject>(PathFor(id)); Validate(value);
        if (value.Id != id || !value.Persistent) throw new InvalidDataException("Music project identity mismatch.");
        return value;
    }
    public IReadOnlyList<MusicProject> List() => !Directory.Exists(directory) ? []
        : Directory.EnumerateFiles(directory, "*.project.json").Select(path => Load(Path.GetFileName(path)[..^13]))
            .OrderByDescending(p => p.Number).ToArray();
    public MusicProject Fix(MusicProject project, MusicProjectSnapshot snapshot)
    {
        snapshot.Validate(); using var gate = Lock();
        var current = project.Persistent ? Load(project.Id) : project;
        var saved = current with { Persistent = true, Manual = true, Saved = snapshot.Snapshot() };
        Save(saved); return saved;
    }
    public MusicProject Rename(MusicProject project, string name)
    {
        name = name.Trim(); if (name.Length is < 1 or > 200) throw new InvalidDataException("Invalid music project name.");
        using var gate = Lock(); var current = project.Persistent ? Load(project.Id) : project;
        var saved = current with { Name = name }; if (saved.Persistent) Save(saved); return saved;
    }
    public MusicProject AddRequest(MusicProject project, string jobId, MusicProjectSnapshot snapshot)
    {
        snapshot.Validate(); if (!Guid.TryParseExact(jobId, "N", out _)) throw new InvalidDataException("Invalid music job identifier.");
        using var gate = Lock(); var current = project.Persistent ? Load(project.Id) : project;
        // Recovery/retry of an existing job must not append a second history step.
        if (current.Steps.Any(s => s.JobId == jobId)) return current;
        var step = new MusicProjectStep(current.Steps.Length + 1, jobId, DateTimeOffset.Now, snapshot.Snapshot());
        var saved = current with { Persistent = true, Saved = snapshot.Snapshot(), Steps = [.. current.Steps, step] };
        Save(saved); return saved;
    }
    public MusicProject SetOutcome(string id, string jobId, MusicProjectOutcome outcome, string message = "")
    {
        using var gate = Lock(); var project = Load(id);
        if (!Enum.IsDefined(outcome) || !project.Steps.Any(s => s.JobId == jobId)) throw new InvalidDataException("Unknown music project step.");
        var saved = project with { Steps = project.Steps.Select(s => s.JobId == jobId ? s with { Outcome = outcome, Message = message } : s).ToArray() };
        Save(saved); return saved;
    }
    public MusicProject SaveWorkspace(MusicProject project, MusicProjectSnapshot snapshot)
    {
        snapshot.Validate(); if (!project.Persistent) return project;
        using var gate = Lock(); var current = Load(project.Id);
        var updated = current with { Saved = snapshot.Snapshot() }; Save(updated); return updated;
    }
    private void Save(MusicProject project) {
        Validate(project); var path = PathFor(project.Id);
        if (File.Exists(path) && Read<MusicProject>(path).Schema == 1 && !File.Exists(path + ".schema1.bak")) File.Copy(path, path + ".schema1.bak");
        Write(path, project with { Schema = 2 });
    }
    private static void Validate(MusicProject project)
    {
        if (project.Schema is not (1 or 2) || !Guid.TryParseExact(project.Id, "N", out _) || project.Number < 1
            || project.Name is null || project.Name.Length > 200 || project.Saved is null || project.Steps is null
            || project.Steps.Length > 10000 || project.Manual && !project.Persistent)
            throw new InvalidDataException("Unsupported music project.");
        project.Saved.Validate();
        for (var i = 0; i < project.Steps.Length; i++) {
            var step = project.Steps[i];
            if (step is null || step.Number != i + 1 || !Guid.TryParseExact(step.JobId, "N", out _) || !Enum.IsDefined(step.Outcome)
                || step.Snapshot is null || step.Message is null) throw new InvalidDataException("Invalid music project history.");
            step.Snapshot.Validate();
        }
        if (project.Steps.Select(s => s.JobId).Distinct().Count() != project.Steps.Length) throw new InvalidDataException("Duplicate music project request.");
    }
    private FileStream Lock()
    {
        Directory.CreateDirectory(directory); var deadline = DateTime.UtcNow.AddSeconds(2);
        while (true) {
            try { return new(Path.Combine(directory, "write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(20); }
        }
    }
    private static T Read<T>(string path)
    {
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("Music project document is too large.");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new InvalidDataException("Missing music project data.");
    }
    private static void Write<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, value);
                if (stream.Length > 64 * 1024 * 1024) throw new InvalidDataException("Music project document is too large.");
                stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
