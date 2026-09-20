using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIHub.Services.LiteraryImport;

/// <summary>Mandatory immutable records, independent of optional diagnostic logging.</summary>
public sealed class ImportSession : IDisposable
{
    private readonly FileStream _lease;
    private readonly object _sync = new();
    public string Root { get; }
    public ImportSessionState State { get; private set; }
    public string Source => Path.Combine(Root, "source" + State.SourceExtension);
    private ImportSession(string root, ImportSessionState state)
    {
        Root = Path.GetFullPath(root); LiteraryProjectLayout.CheckTreePath(Root);
        _lease = new FileStream(Path.Combine(Root, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        State = state;
    }
    public static ImportSession Create(string folder, string source, CancellationToken token)
    {
        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension is not (".zip" or ".json")) throw new InvalidDataException("Literary.Import.Unsupported");
        if (new FileInfo(source).Length > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Literary.Import.Limit");
        ImportDisk.Require(folder, checked(new FileInfo(source).Length * 4));
        LiteraryProjectLayout.CheckTreePath(folder);
        var root = Path.Combine(folder, "Импорт DeepSeek " + DateTime.Now.ToString("dd.MM HH-mm-ss") + "-" + Guid.NewGuid().ToString("N")[..4]); Directory.CreateDirectory(root);
        var session = new ImportSession(root, new() { SourceExtension = extension });
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(session.Source, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[65536]; int read;
                while ((read = input.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); }
                output.Flush(true);
            }
            session.State.SourceHash = HashFile(session.Source);
            session.Save(); return session;
        }
        catch { session.Dispose(); throw; }
    }
    public ImportSession ForkForAssembly(CancellationToken ct)
    {
        var copy = Create(Path.GetDirectoryName(Root)!, Source, ct);
        try
        {
            foreach (var artifact in State.Artifacts)
            {
                ct.ThrowIfCancellationRequested();
                File.Copy(ArtifactPath(artifact),Path.Combine(copy.Root,artifact.File));
                copy.State.Artifacts.Add(artifact);
            }
            copy.State.Conversations=State.Conversations.ToArray(); copy.State.Genre=State.Genre;
            copy.State.Stage="project-choice"; copy.State.RuntimeFingerprint=State.RuntimeFingerprint;
            copy.AddJson("reassembly-origin",new { sourceSession=State.Id, policy="new project; original project preserved" });
            copy.Save(); return copy;
        }
        catch { copy.Dispose(); throw; }
    }
    public static ImportSession Open(string root)
    {
        LiteraryProjectLayout.CheckTreePath(root);
        var state = JsonSerializer.Deserialize<ImportSessionState>(File.ReadAllText(Path.Combine(root, "session.json")), ImportJson.Options)
            ?? throw new InvalidDataException("Literary.Import.Corrupt");
        if (state.Version != 1 || state.SourceExtension is not (".json" or ".zip")) throw new InvalidDataException("Literary.Import.Corrupt");
        var session = new ImportSession(root, state);
        try
        {
            if (HashFile(session.Source) != state.SourceHash) throw new InvalidDataException("Literary.Import.Corrupt");
            if ((File.GetAttributes(session.Source) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Literary.Import.Corrupt");
            foreach (var artifact in state.Artifacts)
                if (HashFile(session.ArtifactPath(artifact)) != artifact.Sha256) throw new InvalidDataException("Literary.Import.Corrupt");
            foreach (var pending in Directory.EnumerateFiles(session.Root, "*.pending"))
            {
                var record = JsonSerializer.Deserialize<ImportPending>(File.ReadAllText(pending), ImportJson.Options)!;
                ValidateArtifactName(record.File);
                if (!state.Artifacts.Any(a => a.File == record.File) && File.Exists(Path.Combine(session.Root, record.File)))
                    session.Register(record.Step, record.File, "interrupted", record.Parents);
                File.Delete(pending);
            }
            foreach (var orphan in Directory.EnumerateFiles(session.Root, "*.data").Where(p => !state.Artifacts.Any(a => a.File == Path.GetFileName(p))))
                session.Register("interrupted-uncommitted", Path.GetFileName(orphan), "interrupted");
            return session;
        }
        catch { session.Dispose(); throw; }
    }
    public static string HashFile(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public string ArtifactPath(ImportArtifact artifact)
    {
        ValidateArtifactName(artifact.File);
        var path = Path.Combine(Root, artifact.File);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Literary.Import.Corrupt");
        return path;
    }
    public ImportArtifact Add(string step, string text, string status = "complete", params string[] parents)
        => AddBytes(step, Encoding.UTF8.GetBytes(text), status, parents);
    public ImportArtifact AddJson<T>(string step, T value, params string[] parents)
        => Add(step, JsonSerializer.Serialize(value, ImportJson.Options), "complete", parents);
    public ImportArtifact AddBytes(string step, byte[] bytes, string status = "complete", params string[] parents)
    {
        lock (_sync)
        {
            ImportDisk.Require(Root, bytes.LongLength * 2);
            var name = Guid.NewGuid().ToString("N") + ".data";
            using (var file = new FileStream(Path.Combine(Root, name), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(true); }
            return Register(step, name, status, parents);
        }
    }
    public (string Name, FileStream Stream) BeginRaw(string step = "source-json", params string[] parents)
    {
        var name = Guid.NewGuid().ToString("N") + ".data";
        ImportDisk.Require(Root, 16L * 1024 * 1024);
        LiteraryChapterFiles.Write(Path.Combine(Root, name + ".pending"), JsonSerializer.Serialize(new ImportPending(name, step, parents)));
        return (name, new FileStream(Path.Combine(Root, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough));
    }
    public ImportArtifact Register(string step, string name, string status, params string[] parents)
    {
        lock (_sync)
        {
            ValidateArtifactName(name);
            var path = Path.Combine(Root, name);
            var artifact = new ImportArtifact(Guid.NewGuid().ToString("N"), step, name, new FileInfo(path).Length,
                HashFile(path), status, DateTimeOffset.UtcNow, parents);
            State.Artifacts.Add(artifact); Save();
            File.Delete(path + ".pending"); return artifact;
        }
    }
    public T? ReadLast<T>(string step)
    {
        var artifact = State.Artifacts.LastOrDefault(a => a.Step == step && a.Status == "complete");
        return artifact is null ? default : JsonSerializer.Deserialize<T>(File.ReadAllText(ArtifactPath(artifact)), ImportJson.Options);
    }
    private static void ValidateArtifactName(string name)
    {
        if (Path.GetFileName(name) != name || Path.GetExtension(name) != ".data"
            || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "N", out _))
            throw new InvalidDataException("Literary.Import.Corrupt");
    }
    public void Save()
    {
        lock (_sync)
        {
            LiteraryProjectLayout.CheckTreePath(Root);
            LiteraryChapterFiles.Write(Path.Combine(Root, "session.json"), JsonSerializer.Serialize(State, ImportJson.Options));
        }
    }
    public void Dispose() => _lease.Dispose();
    private sealed record ImportPending(string File, string Step, string[] Parents);
}
