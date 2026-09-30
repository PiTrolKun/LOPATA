using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class LiteraryWorkspaceControl
{
    private async Task<bool> PrepareMaterialsAsync(IProgress<LiteraryPreparationProgress> progress,CancellationToken ct)
    {
        LiteraryPendingPart[] pending=[];
        var scan=await LiteraryPreparationRetry.RunAsync(_entry.ProjectPath,"Preparation",async ()=>
        { pending=await Task.Run(()=>LiteraryPreparationPlan.Read(_entry.ProjectPath),ct);return true; },ct);
        if(scan.Report is { } scanReport) { await WaitForReadinessAsync(ct); return Failed(scanReport); }
        if(pending.Length>0) await WaitForReadinessAsync(ct);
        var modes=ChooseMemoryModes(pending,ct);
        if(modes is null) return false;
        ct.ThrowIfCancellationRequested();
        var rag=await LiteraryPreparationRetry.RunAsync(_entry.ProjectPath,"RAG",async ()=>
        {
            await LiteraryStorageMigration.MigrateAsync(_entry.ProjectPath,progress,ct);
            await new LiteraryWorkIndex(new(_entry.ProjectPath)).PrepareAsync(progress,ct);return true;
        },ct);
        if(rag.Report is { } ragReport) { await WaitForReadinessAsync(ct); return Failed(ragReport); }
        var jelly=await LiteraryPreparationRetry.RunAsync(_entry.ProjectPath,"Jelly",
            ()=>PrepareJellyAsync(progress,ct,modes.Jelly),ct);
        if(jelly.Report is { } jellyReport) { await WaitForReadinessAsync(ct); return Failed(jellyReport); }
        return jelly.Completed;
    }
    private bool Failed(string report)
    {
        LiteraryPreparationDialog.Report(this,_l,report);
        throw new System.IO.IOException(_l("Literary.Preparation.Failed"));
    }
}
