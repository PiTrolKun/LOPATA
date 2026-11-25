using System.IO;
using System.Text.Json;

namespace AIHub.Services.OfficeEditor;

internal sealed record EditorDraftInfo(string Id, string? Source, string? Target, DateTimeOffset UpdatedUtc, bool Closed,
    string? TargetHash = null, bool ProtectSource = false, string? SourceHash = null);

internal sealed class EditorDraft
{
    internal static string Root => Path.Combine(AppDataPaths.BaseDirectory, "OfficeEditor", "Drafts");
    internal string Id { get; }
    internal string DirectoryPath { get; }
    internal string Payload => Path.Combine(DirectoryPath, "document.odt");
    internal string ProjectPath => Path.Combine(DirectoryPath, "work.LOPATAdoxAI");
    internal string? Source { get; }
    internal string? Target { get; private set; }
    internal string? TargetHash { get; private set; }
    internal string? SourceHash { get; private set; }
    internal bool ProtectSource { get; set; }
    private FileStream? _lease;

    internal EditorDraft(string? source, string? id = null)
    {
        Id = id ?? Guid.NewGuid().ToString("N");
        if (!Guid.TryParseExact(Id, "N", out _)) throw new InvalidDataException("OfficeEditor.InvalidPackage");
        DirectoryPath = ManagedWorkStorage.Current.Job("OfficeEditor/Drafts", Id, temporary: true);
        Directory.CreateDirectory(DirectoryPath);
        _lease = new FileStream(Path.Combine(DirectoryPath, "lease"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var metadata = Path.Combine(DirectoryPath, "draft.json");
            var prior = id is null || !File.Exists(metadata) ? null
                : JsonSerializer.Deserialize<EditorDraftInfo>(File.ReadAllText(metadata));
            Source = prior?.Source ?? source;
            Target = prior?.Target;
            TargetHash = prior?.TargetHash;
            SourceHash = prior?.SourceHash;
            ProtectSource = prior?.ProtectSource == true;
            SaveInfo(false);
        }
        catch { _lease.Dispose(); throw; }
    }
    internal void SetTarget(string value, string? hash = null)
    {
        Target = value;
        TargetHash = hash;
        if (Source != null && string.Equals(Path.GetFullPath(Source), Path.GetFullPath(value), StringComparison.OrdinalIgnoreCase))
            SourceHash = hash;
        SaveInfo(false);
    }
    internal void SaveInfo(bool closed)
    {
        var target = Path.Combine(DirectoryPath, "draft.json"); var temporary = target + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new EditorDraftInfo(Id, Source, Target, DateTimeOffset.UtcNow, closed, TargetHash, ProtectSource, SourceHash)));
        File.Move(temporary, target, true);
    }
    internal void Close()
    {
        try { SaveInfo(true); }
        finally { _lease?.Dispose(); _lease = null; }
    }

    internal void Discard()
    {
        // Drop the document while we still hold its recovery lease. A simultaneous
        // recovery must not claim this directory between releasing the lease and deletion.
        foreach (var file in new[] { ProjectPath, ProjectPath + ".bak", Payload, Path.Combine(DirectoryPath, "previous.odt") })
            File.Delete(file);
        Close();
        Directory.Delete(DirectoryPath, true);
    }

    internal static IReadOnlyList<EditorDraftInfo> Recoverable()
    {
        var drafts = new List<EditorDraftInfo>();
        foreach (var id in ManagedWorkStorage.Current.Jobs("OfficeEditor/Drafts"))
        {
            try
            {
                var directory = ManagedWorkStorage.Current.Job("OfficeEditor/Drafts", id, temporary: true);
                if (!Directory.Exists(directory)) continue;
                using var lease = new FileStream(Path.Combine(directory, "lease"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var info = JsonSerializer.Deserialize<EditorDraftInfo>(File.ReadAllText(Path.Combine(directory, "draft.json")));
                // Unsaved new documents remain available even after a normal close.
                if (info?.Id == Path.GetFileName(directory) &&
                    (File.Exists(Path.Combine(directory, "document.odt")) || File.Exists(Path.Combine(directory, "work.LOPATAdoxAI")))) drafts.Add(info);
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return drafts.OrderByDescending(d => d.UpdatedUtc).ToArray();
    }
}
