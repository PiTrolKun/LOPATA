using System.Text.Json;

namespace AIHub.Services.LiteraryImport;

public sealed record ImportUnit(string Id, string Conversation, string Message, string Parent, string Type,
    int Fragment, int Offset, string Text, bool Technical);
public sealed record ImportConversation(string Id, string Title, int Units);
public sealed record ImportEntry(string Name, long Bytes, string Status);
public sealed record ImportInput(List<ImportConversation> Conversations, List<ImportUnit> Units, List<ImportEntry> Inventory,
    List<string> Warnings);
public sealed record ImportDecision(string Id, string Kind, string Project, string Chapter, string Reason);
public sealed record ImportArtifact(string Id, string Step, string File, long Bytes, string Sha256, string Status,
    DateTimeOffset Created, string[] Parents);
public sealed class ImportSessionState
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceHash { get; set; } = "";
    public string SourceExtension { get; set; } = "";
    public string Stage { get; set; } = "input";
    public string LastError { get; set; } = "";
    public string[] Conversations { get; set; } = [];
    public string SelectedProject { get; set; } = "";
    public string ProjectId { get; set; } = Guid.NewGuid().ToString("N");
    public string ProjectPath { get; set; } = "";
    public string PlannedPath { get; set; } = "";
    public string Genre { get; set; } = "";
    public string RagStatus { get; set; } = "pending";
    public string MemoryStatus { get; set; } = "pending";
    public string ReviewRevision { get; set; } = "";
    public string RuntimeFingerprint { get; set; } = "";
    public List<ImportArtifact> Artifacts { get; set; } = [];
}
public sealed record ImportProgress(string Stage, int Done, int Total);
public static class ImportJson
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, MaxDepth = 128,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
