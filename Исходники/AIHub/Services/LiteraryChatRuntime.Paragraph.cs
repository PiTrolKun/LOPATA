using AIHub.Models;

namespace AIHub.Services;

public sealed partial class LiteraryChatRuntime
{
    public async Task<ParagraphReply> ParagraphAsync(ParagraphRequest request, Func<string,string> localize,
        Action<ParagraphReceipt> receipt, Action<int> budget, IProgress<ModelStreamChunk>? stream, CancellationToken ct, Action? attemptStarting = null)
    {
        ParagraphEvidence? evidence=null; LiteraryParagraphCatalog? catalog=null; string? stamp=null;
        var raw=await StructuredAnalysisAsync([],budget,ct,request.Role==LiteraryChatProfile.Advisor && !request.Discuss?LiteraryParagraphPrompts.Schema:null,
            "Paragraph"+request.Role,
            request,prepareMessages:async token=>
            {
                var project=LiteraryProjectStore.ReadProject(request.Editor.Directory);
                stamp=LiteraryParagraphRevision.Capture(request.Editor);
                catalog=new(project,request.Editor,localize);
                evidence=await new LiteraryParagraphSources(project,request.Editor,catalog,localize).ReadAsync(request.Task,request.Selection,
                    r=>{ _diagnostics?.Write("mandatory_source",r); receipt(r); },token);
                _diagnostics?.Write("paragraph_evidence",evidence);
                if(stamp!=LiteraryParagraphRevision.Capture(request.Editor)) throw new System.IO.IOException("Project changed during source reading.");
                return LiteraryParagraphPrompts.Build(request,evidence,catalog);
            }, profile:request.Role,streamProgress:stream,attemptStarting:attemptStarting,
            grammar:request.Role==LiteraryChatProfile.Writer?LiteraryParagraphPrompts.SingleParagraphGrammar:null);
        if(stamp!=LiteraryParagraphRevision.Capture(request.Editor)) throw new System.IO.IOException("Project changed during generation; response not applied.");
        if(request.Role==LiteraryChatProfile.Writer || request.Discuss) return new(raw,[],evidence!);
        var parsed=LiteraryParagraphPrompts.ParseAdvisor(raw,catalog!);
        return new(parsed.Task,parsed.Recommendations,evidence!);
    }
}
