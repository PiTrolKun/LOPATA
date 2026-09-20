using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.LiteraryImport;

public sealed record ImportProjectStatus(int Parts,int Indexed,int Excluded,int MissingIndexes,int Prepared,int Confirmed,int Pending,int MissingMemory,int Facts)
{
    public static ImportProjectStatus Read(string root)
    {
        var layout=new LiteraryProjectLayout(root); var store=new LiteraryChapterStore(root); store.Open();
        var snapshot=LiteraryEditorSnapshot.Capture(layout.ProjectId,root,store.Index,store.Load(),false);
        var memory=new LiteraryJellyStore(layout);
        int parts=0,indexed=0,excluded=0,missing=0,prepared=0,confirmed=0,pending=0,missingMemory=0;
        var revisions=new Dictionary<string,string>();
        foreach(var source in snapshot.Sources.Where(p=>p.Finished && p.Id!=snapshot.ActiveId))
        {
            var text=LiteraryChapterFiles.Read(Path.Combine(root,"chapters",source.FileName));
            if(string.IsNullOrWhiteSpace(text)) continue;
            parts++; var revision=LiteraryWorkIndex.Revision(text); revisions[source.Id]=revision;
            var doubts=ImportEligibility.Review(root,source.Id)?.Doubts.Length>0;
            if(doubts) excluded++;
            if(ImportEligibility.AllowedSpans(root,source.Id,text).Count>0)
            { if(LiteraryWorkIndex.Current(layout,source,text) is not null) indexed++; else missing++; }
            if(doubts) continue;
            var batch=memory.Find(source.Id,revision);
            if(batch is null) { missingMemory++; continue; }
            prepared++; if(batch.Status=="confirmed") confirmed++; else pending++;
        }
        var facts=memory.Read().Count(f=>revisions.GetValueOrDefault(f.PartId)==f.Revision);
        return new(parts,indexed,excluded,missing,prepared,confirmed,pending,missingMemory,facts);
    }
    public string Describe(Func<string,string> l)=>string.Format(l("Literary.Import.PreparationSummary"),Parts,Indexed,Excluded,MissingIndexes,Confirmed,Prepared,Pending,MissingMemory,Facts);
    public static void ExportCurrent(string root,string language,CancellationToken ct)
    {
        using var origin=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"Import","origin.json")));
        var folder=origin.RootElement.GetProperty("sessionFolder").GetString()!;
        if(!Directory.Exists(folder)) throw new IOException("Literary.Import.SessionMissing");
        using var session=ImportSession.Open(folder);
        var project=LiteraryProjectStore.ReadProject(root);
        if(session.State.Id!=origin.RootElement.GetProperty("Id").GetString() || session.State.ProjectId!=project.Id)
            throw new InvalidDataException("Literary.Import.Corrupt");
        ImportCompletion.Export(session,new LiteraryProjectEntry(project.Id,project.ProjectName,root),language,ct);
    }
}
