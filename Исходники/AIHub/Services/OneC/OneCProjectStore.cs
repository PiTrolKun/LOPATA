using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHub.Models.OneC;
using Lopata.Updates;

namespace AIHub.Services.OneC;

internal sealed partial class OneCProjectStore(string? root = null)
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal string Root { get; } = root ?? Path.Combine(AppDataPaths.BaseDirectory, "OneC", "Projects");
    internal IReadOnlyList<string> UnavailableProjects { get; private set; } = [];
    private object Gate(string id) => Gates.GetOrAdd(Path.GetFullPath(DirectoryFor(id)), _ => new());
    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    internal static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    internal static void Id(string id) { if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("OneC.InvalidProject"); }
    internal string DirectoryFor(string id) { Id(id); return Path.Combine(Root, id); }
    internal string FileFor(string id, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':')
            || relative.Split('/', '\\').Any(p => p is ".." or "." or "")) throw new InvalidDataException("OneC.InvalidPath");
        var directory = Path.GetFullPath(DirectoryFor(id)) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(directory, relative));
        if (!path.StartsWith(directory, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("OneC.InvalidPath");
        SafeUpdatePath.RejectLinks(path); return path;
    }
    internal OneCProject Create(string name)
    {
        var project = new OneCProject { Name = Name(name), Number = List().Select(p => p.Number).DefaultIfEmpty().Max() + 1 };
        project.ActiveTaskId = project.Tasks[0].Id;
        Save(project); return project;
    }
    internal IReadOnlyList<OneCProject> List()
    {
        if (!Directory.Exists(Root)) return [];
        var result = new List<OneCProject>(); var unavailable = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(Root).Take(10000))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
            try { result.Add(Load(Path.GetFileName(directory))); }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or InvalidDataException)
            { unavailable.Add(Path.GetFileName(directory)); OwnedProcessRegistry.Log("onec_project_unavailable", "OneC", detail: error.GetType().Name); }
        }
        UnavailableProjects = unavailable;
        return result.OrderByDescending(p => p.Updated).ToArray();
    }
    internal OneCProject Load(string id)
    {
        lock (Gate(id))
        {
            var path = FileFor(id, "project.json");
            try { return Read(path); }
            catch (Exception error) when (error is IOException or JsonException or InvalidDataException or DecoderFallbackException)
            {
                var recovered = Read(path + ".bak");
                if (File.Exists(path)) File.Move(path, path + ".damaged-" + OneCIds.New());
                Atomic(path, JsonSerializer.Serialize(recovered, Json)); return recovered;
            }
            OneCProject Read(string file)
            {
                SafeUpdatePath.RejectLinks(file);
                if (new FileInfo(file).Length > 24_000_000) throw new InvalidDataException("OneC.InvalidProject");
                var p = JsonSerializer.Deserialize<OneCProject>(File.ReadAllText(file, new UTF8Encoding(false, true)), Json)
                    ?? throw new InvalidDataException("OneC.InvalidProject");
                if (p.Id != id) throw new InvalidDataException("OneC.InvalidProject");
                Validate(p, allowReadOnlyRecovery: true); return p;
            }
        }
    }
    internal OneCProject Update(string id, Action<OneCProject> change)
    { lock (Gate(id)) { var p = Load(id); change(p); Save(p); return p; } }
    internal void Save(OneCProject project)
    {
        lock (Gate(project.Id))
        {
            Validate(project);
            var path = FileFor(project.Id, "project.json");
            if (File.Exists(path))
            {
                using var existing = JsonDocument.Parse(File.ReadAllText(path));
                if (existing.RootElement.GetProperty("Sequence").GetInt64() != project.Sequence)
                    throw new InvalidOperationException("OneC.ProjectChanged");
            }
            foreach (var file in project.Files)
            {
                if (file.Origin != "created") continue;
                if (file.OwnershipUnavailable && Hash(file.Code) == file.SavedCodeHash) continue;
                RequireOwned(project, file);
                if (file.SavedCodeHash != Hash(file.Code))
                {
                    if (!string.IsNullOrEmpty(file.DiskPath)) VerifyDisk(project, file);
                    SaveOwnedRevision(project, file);
                }
            }
            var oldSequence = project.Sequence; project.Sequence++; project.Updated = DateTimeOffset.UtcNow;
            try
            {
                var text = JsonSerializer.Serialize(project, Json);
                if (Encoding.UTF8.GetByteCount(text) > 24_000_000) throw new InvalidDataException("OneC.ProjectLimit");
                Atomic(path, text);
            }
            catch { project.Sequence = oldSequence; throw; }
        }
    }
    internal void Validate(OneCProject project, bool allowReadOnlyRecovery = false)
    {
        ValidateMembers(project);
        Id(project.Id);
        if (project.Schema != 1 || project.Sequence < 0 || project.Number < 0 || project.Name is null || project.Name.Length > 100
            || project.Files is null || project.Tasks is null || project.Proposals is null || project.History is null
            || project.Files.Count > 1000 || project.Tasks.Count is < 1 or > 1000 || project.Proposals.Count > 2000
            || project.Files.Select(f => f.Id).Distinct().Count() != project.Files.Count
            || project.Tasks.Select(t => t.Id).Distinct().Count() != project.Tasks.Count
            || project.Proposals.Select(p => p.Id).Distinct().Count() != project.Proposals.Count
            || !project.Tasks.Any(t => t.Id == project.ActiveTaskId)) throw new InvalidDataException("OneC.InvalidProject");
        long size = 0;
        foreach (var file in project.Files)
        {
            Id(file.Id); _ = Name(file.Name);
            if (file.Code is null || file.Code.Length > 2_000_000 || file.Origin is not ("material" or "created"))
                throw new InvalidDataException("OneC.InvalidProject");
            size += file.Code.Length + file.RecoveryCode.Length;
            if (file.Origin == "material" && file.SavedCodeHash != Hash(file.Code)) throw new InvalidDataException("OneC.ReadOnly");
            if (file.DiskPath.Length > 0) _ = FileFor(project.Id, file.DiskPath);
            if (file.Origin == "created")
            {
                try { RequireOwned(project, file); }
                catch (InvalidDataException) when (allowReadOnlyRecovery || file.OwnershipUnavailable && Hash(file.Code) == file.SavedCodeHash)
                { file.OwnershipUnavailable = true; }
            }
        }
        foreach (var task in project.Tasks)
        {
            Id(task.Id);
            if (task.Mode is not ("build" or "learn") || task.Prompt is null || task.Prompt.Length > 20000
                || task.Feedback is null || task.Feedback.Count > 1000 || task.Start < 0 || task.Length < 0
                || task.SourceFileId.Length > 0 && !project.Files.Any(f => f.Id == task.SourceFileId))
                throw new InvalidDataException("OneC.InvalidProject");
        }
        foreach (var proposal in project.Proposals)
        {
            Id(proposal.Id);
            if (!project.Tasks.Any(t => t.Id == proposal.TaskId) || proposal.Code is null || proposal.Code.Length > 1_000_000
                || proposal.Binding is null || proposal.Binding.Start < 0 || proposal.Binding.Length < 0
                || proposal.Binding.Start > proposal.Binding.FullSource.Length - proposal.Binding.Length)
                throw new InvalidDataException("OneC.InvalidProject");
            size += proposal.Code.Length + proposal.OriginalCode.Length + proposal.Binding.FullSource.Length + proposal.RawResponse.Length
                + proposal.RequestInput.Length + proposal.SystemPrompt.Length + proposal.Explanation.Length;
        }
        if (size > 8_000_000) throw new InvalidDataException("OneC.ProjectLimit");
        if (project.Pending is { } job) { Id(job.Id); if (job.ProjectId != project.Id || !project.Tasks.Any(t => t.Id == job.TaskId)) throw new InvalidDataException("OneC.InvalidProject"); }
    }
    internal static string Name(string value)
    {
        if (value is null) throw new InvalidDataException("OneC.InvalidName");
        var name = value.Trim();
        if (name.Length is < 1 or > 100 || name.TrimEnd('.') != name || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name is "." or ".." || System.Text.RegularExpressions.Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new InvalidDataException("OneC.InvalidName");
        return name;
    }
    internal static void Atomic(string path, string text)
    {
        SafeUpdatePath.RejectLinks(path); SafeUpdatePath.RejectLinks(path + ".bak");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + OneCIds.New() + ".tmp";
        var created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { created = true; stream.Write(Encoding.UTF8.GetBytes(text)); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", true); else File.Move(temporary, path);
        }
        finally { if (created && File.Exists(temporary)) File.Delete(temporary); }
    }
}
