using System.IO;
using AIHub.Models.OneC;

namespace AIHub.Services.OneC;

internal sealed partial class OneCProjectStore
{
    private static void Required(object? value) { if (value is null) throw new InvalidDataException("OneC.InvalidProject"); }
    private static void ValidateMembers(OneCProject p)
    {
        Required(p.Files); Required(p.Tasks); Required(p.Proposals); Required(p.History); Required(p.OpenFileIds);
        Required(p.ModelId); Required(p.ActiveTaskId); Required(p.ActiveFileId); Required(p.ActiveProposalId);
        if (p.History.Count > 20000 || p.OpenFileIds.Any(id => !p.Files.Any(f => f?.Id == id))) throw new InvalidDataException("OneC.InvalidProject");
        foreach (var operation in p.History) { Required(operation); Required(operation.Id); Required(operation.Kind); Required(operation.SubjectId); }
        foreach (var f in p.Files)
        {
            Required(f); Required(f.Name); Required(f.Code); Required(f.Encoding); Required(f.DiskPath); Required(f.DiskHash);
            Required(f.Reference);
            Required(f.RecoveryCode);
            if (f.RecoveryCode.Length > 2_000_000) throw new InvalidDataException("OneC.InputLimit");
            if (f.Reference.Length > 500 || f.Reference.Any(char.IsControl)) throw new InvalidDataException("OneC.InvalidProject");
            Required(f.SavedCodeHash); Required(f.Models); Required(f.CreatedOperationId); Required(f.SourceProposalId);
            if (f.Caret < 0 || f.SelectionLength < 0 || !double.IsFinite(f.Scroll)) throw new InvalidDataException("OneC.InvalidProject");
            Check(f.Check);
        }
        foreach (var t in p.Tasks)
        {
            Required(t); Required(t.Prompt); Required(t.Feedback); Required(t.SourceFileId); Required(t.SourceHash); Required(t.Context); Required(t.PlatformVersion);
            foreach (var feedback in t.Feedback)
            { Required(feedback); Required(feedback.Error); Required(feedback.TriedCode); Required(feedback.CodeHash); Required(feedback.CodeSnapshot); }
        }
        foreach (var v in p.Proposals)
        {
            Required(v); Required(v.Code); Required(v.OriginalCode); Required(v.Explanation); Required(v.InsertLocation); Required(v.SuggestedName);
            Required(v.Models); Required(v.MissingContext); Required(v.State); Required(v.AppliedFileId); Required(v.RawResponse); Required(v.RequestHash);
            Required(v.RequestInput); Required(v.SystemPrompt);
            if (v.RequestInput.Length > 200000 || v.SystemPrompt.Length > 20000 || v.RawResponse.Length > 120000)
                throw new InvalidDataException("OneC.InputLimit");
            Binding(v.Binding); Check(v.Check);
        }
        if (p.Pending is { } job)
        {
            Required(job.Input); Required(job.OriginalInput); Required(job.Instructions); Required(job.ModelId); Required(job.Error);
            Binding(job.Binding);
            if (job.RepairAttempt is < 0 or > 2 || job.Input.Length > 200000 || job.Instructions.Length > 20000)
                throw new InvalidDataException("OneC.InvalidProject");
        }
        static void Binding(OneCBinding b)
        {
            Required(b); Required(b.FullSource); Required(b.FileId); Required(b.SourceHash);
            if (b.FileId.Length > 0 && Hash(b.FullSource) != b.SourceHash) throw new InvalidDataException("OneC.InvalidProject");
        }
        static void Check(OneCCheck? c)
        {
            if (c is null) return;
            Required(c.Diagnostics); Required(c.State); Required(c.CodeHash); Required(c.Detail);
            if (c.Diagnostics.Length > 20000) throw new InvalidDataException("OneC.InvalidProject");
            foreach (var d in c.Diagnostics) { Required(d); Required(d.Message); Required(d.Severity); }
        }
    }
}
