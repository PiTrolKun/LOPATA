using System.IO;
using System.Text.Json;
using Lopata.Updates;

namespace AIHub.Services.CharacterKnowledge;

internal sealed class KnowledgeStore(string directory)
{
    private const string FileSuffix = ".knowledge.json";
    private const string PreviousSuffix = ".previous";
    private readonly string _directory = Path.GetFullPath(directory);
    private string RecentPath => Path.Combine(_directory, "last-base.json");

    internal KnowledgeStore() : this(Path.Combine(AppDataPaths.BaseDirectory, "CharacterKnowledge")) { }

    internal IReadOnlyList<KnowledgeBase> List()
    {
        if (!Directory.Exists(_directory)) return [];
        SafeUpdatePath.RejectLinks(_directory);

        var bases = new List<KnowledgeBase>();
        var ids = new HashSet<Guid>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*" + FileSuffix + "*"))
        {
            var name = Path.GetFileName(path);
            if (name.EndsWith(PreviousSuffix, StringComparison.Ordinal)) name = name[..^PreviousSuffix.Length];
            if (!name.EndsWith(FileSuffix, StringComparison.Ordinal) ||
                !Guid.TryParseExact(name[..^FileSuffix.Length], "N", out var id) || !ids.Add(id)) continue;
            try { bases.Add(Load(id).Base); }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
            {
                // Other bases can still be opened when one was damaged outside the editor.
            }
        }

        return bases.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.BaseId).ToArray();
    }

    internal Guid? LastOpened()
    {
        if (!File.Exists(RecentPath)) return null;
        try { return Read<RecentBase>(RecentPath).BaseId; }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    internal void Remember(Guid id)
    {
        using var lease = AcquireWriteLease();
        Write(RecentPath, stream => JsonSerializer.Serialize(stream, new RecentBase(id), KnowledgeFormat.Json), keepPrevious: false);
    }

    internal KnowledgeProject Create(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new KnowledgeProblem("BaseNameRequired");
        return Add(new(new(Guid.NewGuid(), name.Trim(), DateTimeOffset.UtcNow), new([], []), Guid.Empty));
    }

    internal KnowledgeProject Import(string path) => Add(KnowledgeArchive.Read(path));

    internal KnowledgeProject Load(Guid id) => ReadLatest(id, out _);

    private KnowledgeProject ReadLatest(Guid id, out bool recovered)
    {
        var path = ProjectPath(id);
        recovered = false;
        try { return ReadProject(path, id); }
        catch (Exception error) when ((error is IOException or JsonException) &&
            error is not KnowledgeProblem { Key: "UnsupportedVersion" } && File.Exists(path + PreviousSuffix))
        {
            recovered = true;
            return ReadProject(path + PreviousSuffix, id);
        }
    }

    internal KnowledgeProject Save(KnowledgeProject project)
    {
        using var lease = AcquireWriteLease();
        if (ReadLatest(project.Base.BaseId, out var recovered).Revision != project.Revision)
            throw new KnowledgeProblem("ChangedElsewhere");
        return WriteProject(project, keepPrevious: !recovered);
    }

    private KnowledgeProject Add(KnowledgeProject project)
    {
        using var lease = AcquireWriteLease();
        var id = project.Base.BaseId;
        while (File.Exists(ProjectPath(id)) || File.Exists(ProjectPath(id) + PreviousSuffix))
            id = Guid.NewGuid();
        return WriteProject(project with { Base = project.Base with { BaseId = id } });
    }

    private KnowledgeProject WriteProject(KnowledgeProject project, bool keepPrevious = true)
    {
        var updated = project with { Revision = Guid.NewGuid() };
        Write(ProjectPath(project.Base.BaseId), stream => JsonSerializer.Serialize(stream, updated, KnowledgeFormat.Json),
            keepPrevious);
        return updated;
    }

    private KnowledgeProject ReadProject(string path, Guid id)
    {
        var project = Read<KnowledgeProject>(path);
        if (project.Base.BaseId != id || project.Revision == Guid.Empty) throw new KnowledgeProblem("InvalidData");
        KnowledgeFormat.Validate(project);
        return project;
    }

    private string ProjectPath(Guid id) => Path.Combine(_directory, id.ToString("N") + FileSuffix);

    private FileStream AcquireWriteLease()
    {
        SafeUpdatePath.RejectLinks(_directory);
        Directory.CreateDirectory(_directory);
        var lockPath = Path.Combine(_directory, "write.lock");
        SafeUpdatePath.RejectLinks(lockPath);
        return new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static T Read<T>(string path) where T : class
    {
        SafeUpdatePath.RejectLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return KnowledgeFormat.Read<T>(stream);
    }

    internal static void Write(string path, Action<Stream> write, bool keepPrevious)
    {
        var destination = Path.GetFullPath(path);
        SafeUpdatePath.RejectLinks(destination);
        if (keepPrevious) SafeUpdatePath.RejectLinks(destination + PreviousSuffix);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        var created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            if (keepPrevious && File.Exists(destination))
                File.Replace(temporary, destination, destination + PreviousSuffix, ignoreMetadataErrors: true);
            else
                File.Move(temporary, destination, overwrite: !keepPrevious);
        }
        finally
        {
            if (created)
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private sealed record RecentBase(Guid BaseId);
}
