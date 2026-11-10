using AIHub.Services;
using System.Text.Json.Serialization;

namespace AIHub.Models.OneC;

internal static class OneCIds
{
    internal static string New() => Guid.NewGuid().ToString("N");
}

internal sealed class OneCProject
{
    public int Schema { get; set; } = 1;
    public string Id { get; set; } = OneCIds.New();
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public long Sequence { get; set; }
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;
    public string ModelId { get; set; } = "";
    public List<OneCFile> Files { get; set; } = [];
    public List<OneCTask> Tasks { get; set; } = [new()];
    public List<OneCProposal> Proposals { get; set; } = [];
    public string ActiveTaskId { get; set; } = "";
    public string ActiveFileId { get; set; } = "";
    public List<string> OpenFileIds { get; set; } = [];
    public bool SidebarVisible { get; set; } = true;
    public string ActiveProposalId { get; set; } = "";
    public OneCJob? Pending { get; set; }
    public List<OneCOperation> History { get; set; } = [];
    [JsonIgnore] public OneCTask Task => Tasks.Single(t => t.Id == ActiveTaskId);
}

internal sealed class OneCFile
{
    public string Id { get; set; } = OneCIds.New();
    public string Name { get; set; } = "";
    public string Origin { get; set; } = "material";
    public string Reference { get; set; } = "";
    public string RecoveryCode { get; set; } = "";
    public string CreatedOperationId { get; set; } = "";
    public string Code { get; set; } = "";
    public string Encoding { get; set; } = "utf-8";
    public string DiskPath { get; set; } = "";
    public string DiskHash { get; set; } = "";
    public string SavedCodeHash { get; set; } = "";
    public string SourceProposalId { get; set; } = "";
    public OutputModelUse[] Models { get; set; } = [];
    public int Caret { get; set; }
    public int SelectionLength { get; set; }
    public double Scroll { get; set; }
    public OneCCheck? Check { get; set; }
    [JsonIgnore] public bool OwnershipUnavailable { get; set; }
}

internal sealed class OneCTask
{
    public string Id { get; set; } = OneCIds.New();
    public string Mode { get; set; } = "build";
    public string Prompt { get; set; } = "";
    public string Context { get; set; } = "unknown";
    public string PlatformVersion { get; set; } = "";
    public string SourceFileId { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public int Start { get; set; }
    public int Length { get; set; }
    public bool WholeFile { get; set; } = true;
    public List<OneCUserFeedback> Feedback { get; set; } = [];
}

internal sealed record OneCUserFeedback(string Id, string ProposalId, string Error, string TriedCode, DateTimeOffset Created,
    string CodeHash = "", string CodeSnapshot = "");
internal sealed record OneCOperation(string Id, string Kind, string SubjectId, DateTimeOffset Created);
internal sealed record OneCBinding(string FileId, string SourceHash, string FullSource, int Start, int Length, bool WholeFile);
internal sealed record OneCCheck(string CodeHash, string Tool, string Version, string State,
    OneCDiagnostic[] Diagnostics, DateTimeOffset Created, string Detail = "");
internal sealed record OneCDiagnostic(string Id, string Severity, string Message, int Line, int Column, int EndLine, int EndColumn);

internal sealed class OneCProposal
{
    public string Id { get; set; } = OneCIds.New();
    public string TaskId { get; set; } = "";
    public string ParentProposalId { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string RequestInput { get; set; } = "";
    public string SystemPrompt { get; set; } = "";
    public OneCBinding Binding { get; set; } = new("", "", "", 0, 0, true);
    public string Code { get; set; } = "";
    public string OriginalCode { get; set; } = "";
    public string Explanation { get; set; } = "";
    public string InsertLocation { get; set; } = "";
    public string SuggestedName { get; set; } = "Результат.bsl";
    public string[] MissingContext { get; set; } = [];
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public OutputModelUse[] Models { get; set; } = [];
    public OneCCheck? Check { get; set; }
    public string AppliedFileId { get; set; } = "";
    public string State { get; set; } = "completed";
    public string RawResponse { get; set; } = "";
}

internal sealed class OneCJob
{
    public string Id { get; set; } = OneCIds.New();
    public string ProjectId { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string ProposalId { get; set; } = OneCIds.New();
    public string ParentProposalId { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string Instructions { get; set; } = "";
    public string Input { get; set; } = "";
    public string OriginalInput { get; set; } = "";
    public string Mode { get; set; } = "build";
    public string Language { get; set; } = "ru";
    public OneCBinding Binding { get; set; } = new("", "", "", 0, 0, true);
    public string State { get; set; } = "pending";
    public string Error { get; set; } = "";
    public int RepairAttempt { get; set; }
}
