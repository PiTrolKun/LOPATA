using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Services.SongClip;

namespace AIHub.Services.Shorts;

internal sealed record ShortsProjectStep(string JobId, DateTimeOffset CreatedAt, ShortsDraft Snapshot);

internal sealed record ShortsProject(string Id, DateTimeOffset CreatedAt, ShortsDraft Workspace)
{
    public int Schema { get; init; } = 1;
    public string Name { get; init; } = "";
    public bool Manual { get; init; }
    public bool Persistent { get; init; }
    public ShortsProjectStep[] Steps { get; init; } = [];
}

internal sealed class ShortsProjects(string root)
{
    internal static ShortsProjects Default { get; } = new(ShortsJob.Root);
    private string DirectoryPath => Path.Combine(root, "Projects");
    private string DraftPath => Path.Combine(root, "draft.json");

    internal ShortsDraft LoadDraft()
    {
        try { return Read<ShortsDraft>(DraftPath); }
        catch (Exception error) when (ExpectedReadError(error)) { return new(); }
    }

    internal void WriteDraft(ShortsDraft draft) => SongClipStore.Atomic(DraftPath, draft);

    internal ShortsProject New(ShortsDraft? draft = null) =>
        CreateWorkspace(Guid.NewGuid().ToString("N"), draft ?? new());

    internal ShortsProject Restore(ShortsDraft draft)
    {
        if (Guid.TryParseExact(draft.ProjectId, "N", out _))
            return File.Exists(ProjectPath(draft.ProjectId)) ? Load(draft.ProjectId) : CreateWorkspace(draft.ProjectId, draft);

        // A pre-project draft with a submitted job can be imported again after a crash
        // between the project write and the active draft write without adding a duplicate.
        if (Guid.TryParseExact(draft.JobId, "N", out _))
        {
            var existing = FindOwner(null, draft.JobId);
            if (existing is not null) return existing;
            var standalone = CreateWorkspace(draft.JobId, draft);
            return standalone with { Steps = [new(draft.JobId, standalone.CreatedAt, standalone.Workspace)] };
        }

        if (!HasContent(draft)) return New(draft);
        var hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(draft));
        var legacyId = new Guid(hash.AsSpan(0, 16)).ToString("N");
        return File.Exists(ProjectPath(legacyId)) ? Load(legacyId) : CreateWorkspace(legacyId, draft);
    }

    private static ShortsProject CreateWorkspace(string id, ShortsDraft draft) =>
        new(id, DateTimeOffset.Now, draft with { ProjectId = id });

    internal ShortsProject Load(string id)
    {
        var project = Read<ShortsProject>(ProjectPath(id));
        if (project.Schema != 1 || project.Id != id || !project.Persistent || project.Workspace is null ||
            project.Workspace.ProjectId != id || project.Name is null || project.Steps is null ||
            project.Steps.Any(step => step is null || !Guid.TryParseExact(step.JobId, "N", out _) || step.Snapshot is null) ||
            project.Steps.Select(step => step.JobId).Distinct().Count() != project.Steps.Length)
            throw new InvalidDataException("Shorts.Projects.ReadError");
        return project;
    }

    internal IReadOnlyList<ShortsProject> List()
    {
        if (!Directory.Exists(DirectoryPath)) return [];
        var projects = new List<ShortsProject>();
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            try { projects.Add(Load(Path.GetFileNameWithoutExtension(file))); }
            catch (Exception error) when (ExpectedReadError(error))
            { OwnedProcessRegistry.Log("shorts_project_read_failed", "Shorts", detail: error.Message); }
        }
        return projects.OrderByDescending(project => project.CreatedAt).ToArray();
    }

    internal ShortsProject? FindOwner(string? owner, string jobId)
    {
        if (owner is null) return List().SingleOrDefault(project => project.Steps.Any(step => step.JobId == jobId));
        if (!File.Exists(ProjectPath(owner))) return null;
        var project = Load(owner);
        return project.Steps.Any(step => step.JobId == jobId) ? project : null;
    }

    internal ShortsProject SaveWorkspace(ShortsProject project, ShortsDraft draft, bool manual = false) =>
        Update(project, current => current with
        {
            Persistent = true,
            Manual = manual || current.Manual,
            Workspace = draft with { ProjectId = current.Id }
        });

    internal ShortsProject Rename(ShortsProject project, string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 200) throw new InvalidDataException("Shorts.Projects.InvalidName");
        if (!project.Persistent) return project with { Name = name };
        return Update(project, current => current with { Name = name });
    }

    internal ShortsProject Record(ShortsProject project, ShortsDraft draft, string jobId, DateTimeOffset createdAt)
    {
        if (!Guid.TryParseExact(jobId, "N", out _)) throw new InvalidDataException("Shorts.InvalidJob");
        return Update(project, current =>
        {
            var snapshot = draft with { ProjectId = current.Id, JobId = jobId };
            return current with
            {
                Persistent = true,
                Workspace = snapshot,
                Steps = current.Steps.Any(step => step.JobId == jobId) ? current.Steps : [.. current.Steps, new(jobId, createdAt, snapshot)]
            };
        });
    }

    internal static bool HasContent(ShortsDraft draft) =>
        (draft with { ProjectId = null, References = draft.Images.Length == 0 ? null : draft.References }) != new ShortsDraft();

    private ShortsProject Update(ShortsProject project, Func<ShortsProject, ShortsProject> update)
    {
        using var gate = Lock();
        var file = ProjectPath(project.Id);
        var next = update(File.Exists(file) ? Load(project.Id) : project);
        SongClipStore.Atomic(file, next);
        return next;
    }

    private string ProjectPath(string id) => Guid.TryParseExact(id, "N", out _)
        ? Path.Combine(DirectoryPath, id + ".json") : throw new InvalidDataException("Shorts.Projects.ReadError");

    private FileStream Lock()
    {
        Directory.CreateDirectory(DirectoryPath);
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (true)
        {
            try { return new(Path.Combine(DirectoryPath, "write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(20); }
        }
    }

    private static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new InvalidDataException("Shorts.Projects.ReadError");

    internal static bool ExpectedReadError(Exception error) =>
        error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException;
}
