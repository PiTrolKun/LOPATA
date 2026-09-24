using System.IO;
using AIHub.Services.LiteraryImport;

namespace AIHub.Services;

public sealed record LiteraryPendingPart(string Id, string Label, string Text, bool Rag, bool Jelly);
public sealed record LiteraryPreparationModes(string Rag, string Jelly);

public static class LiteraryPreparationPlan
{
    public static LiteraryPendingPart[] Read(string root)
    {
        var layout=new LiteraryProjectLayout(root); var chapters=new LiteraryChapterStore(root); chapters.Open();
        var editor=LiteraryEditorSnapshot.Capture(layout.ProjectId,root,chapters.Index,chapters.Load(),false);
        var memory=new LiteraryJellyStore(layout); var result=new List<LiteraryPendingPart>();
        foreach(var source in editor.Sources.Where(s=>s.Id!=editor.ActiveId))
        {
            var text=LiteraryChapterFiles.Read(Path.Combine(root,"chapters",source.FileName));
            if(string.IsNullOrWhiteSpace(text)) continue;
            var rag=LiteraryWorkIndex.Current(layout,source,text) is null && ImportEligibility.AllowedSpans(root,source.Id,text).Count>0;
            var jelly=ImportEligibility.Review(root,source.Id)?.Doubts.Length is not >0
                && memory.Find(source.Id,LiteraryWorkIndex.Revision(text))?.Status!="confirmed";
            if(rag||jelly) result.Add(new(source.Id,source.Number+" · "+source.Title,text,rag,jelly));
        }
        return result.ToArray();
    }
}

public sealed record LiteraryPreparationAttempt(bool Completed, string? Report = null);

public static class LiteraryPreparationRetry
{
    public static async Task<LiteraryPreparationAttempt> RunAsync(string root,string stage,Func<Task<bool>> action,CancellationToken ct)
    {
        var errors=new List<object>();
        for(var attempt=1;attempt<=4;attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return new(await action()); }
            catch(OperationCanceledException) { throw; }
            catch(Exception ex) when(ex is not OutOfMemoryException)
            { errors.Add(new { attempt, type=ex.GetType().Name, ex.HResult, time=DateTimeOffset.UtcNow }); }
        }
        var folder=new LiteraryProjectLayout(root).EnsureFolder(Path.Combine("Diagnostics/PreparationReports",Guid.NewGuid().ToString("N")));
        var report=Path.Combine(folder,"report.json");
        LiteraryChapterFiles.Write(report,ParagraphJson.Encode(new { version=1,stage,attempts=errors,
            appVersion=typeof(LiteraryPreparationRetry).Assembly.GetName().Version?.ToString(),issueUrl=ImportRagPreparation.IssuesUrl }));
        return new(false,report);
    }
}
