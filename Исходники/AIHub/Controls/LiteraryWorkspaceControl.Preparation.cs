using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class LiteraryWorkspaceControl
{
    private async Task<bool> PrepareMaterialsAsync(IProgress<LiteraryPreparationProgress> progress,CancellationToken ct)
    {
        LiteraryPendingPart[] pending=[];
        var scan=await LiteraryPreparationRetry.RunAsync(_entry.ProjectPath,"Preparation",async ()=>
        { pending=await Task.Run(()=>LiteraryPreparationPlan.Read(_entry.ProjectPath),ct);return true; },ct);
        if(scan.Report is { } scanReport) return Failed(scanReport);
        var modes=pending.Length==0?new LiteraryPreparationModes("auto","auto")
            : LiteraryPreparationDialog.Choose(this,_l,pending);
        if(modes is null) return false;
        ct.ThrowIfCancellationRequested();
        if(modes.Rag=="manual" && pending.Any(p=>p.Rag) && !LiteraryPreparationDialog.ReviewRag(this,_l,pending)) return false;
        var rag=await LiteraryPreparationRetry.RunAsync(_entry.ProjectPath,"RAG",async ()=>
        {
            await LiteraryStorageMigration.MigrateAsync(_entry.ProjectPath,progress,ct);
            await new LiteraryWorkIndex(new(_entry.ProjectPath)).PrepareAsync(progress,ct);return true;
        },ct);
        if(rag.Report is { } ragReport) return Failed(ragReport);
        var jelly=await LiteraryPreparationRetry.RunAsync(_entry.ProjectPath,"Jelly",
            ()=>PrepareJellyAsync(progress,ct,modes.Jelly),ct);
        if(jelly.Report is { } jellyReport) return Failed(jellyReport);
        return jelly.Completed;
    }
    private bool Failed(string report)
    {
        LiteraryPreparationDialog.Report(this,_l,report);
        throw new System.IO.IOException(_l("Literary.Preparation.Failed"));
    }
}
