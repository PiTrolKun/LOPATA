using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.MusicLora;

namespace AIHub.Services.MusicAceIdeas;

public sealed record MusicAceIdeaCandidate(int Seed,MusicAceIdea? Idea=null,string? Error=null);
public sealed record MusicAceIdeaWorkRequest(string Id,string ProjectId,string ModelsRoot,string Style,
    MusicProjectSnapshot Input,MusicAceIdeaCandidate[] Candidates);

public sealed class MusicAceIdeaStore(string root)
{
    public static MusicAceIdeaStore Default {get;}=new(Path.Combine(AppDataPaths.BaseDirectory,"Music","AceIdeas"));
    public string Folder(string id)=>Guid.TryParseExact(id,"N",out _)?Path.Combine(root,id):throw new InvalidDataException("Music.AceIdeas.Invalid");
    public void Save(MusicAceIdeaWorkRequest work) {
        Validate(work);var folder=Folder(work.Id);Directory.CreateDirectory(folder);var temp=Path.Combine(folder,Guid.NewGuid().ToString("N")+".tmp");
        try {using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(file,work);file.Flush(true);}
            File.Move(temp,Path.Combine(folder,"work.json"),true);
        }finally{if(File.Exists(temp))File.Delete(temp);}
    }
    public MusicAceIdeaWorkRequest Load(string id) {
        var file=Path.Combine(Folder(id),"work.json");if(new FileInfo(file).Length>16000000)throw new InvalidDataException("Music.AceIdeas.Invalid");
        var work=JsonSerializer.Deserialize<MusicAceIdeaWorkRequest>(File.ReadAllText(file))??throw new InvalidDataException("Music.AceIdeas.Invalid");
        if(work.Id!=id)throw new InvalidDataException("Music.AceIdeas.Invalid");Validate(work);return work;
    }
    private static void Validate(MusicAceIdeaWorkRequest work) {
        if(!Guid.TryParseExact(work.Id,"N",out _)||!Guid.TryParseExact(work.ProjectId,"N",out _)||work.Input.Variation!=MusicAceCatalog.Variation
            ||work.Input.AceIdeas.Ideas.Length!=0||work.Candidates.Length is <1 or >8||work.Candidates.Any(c=>c.Seed<0||c.Error?.Length>4096))
            throw new InvalidDataException("Music.AceIdeas.Invalid");
        work.Input.Validate();foreach(var candidate in work.Candidates)candidate.Idea?.Validate();
    }
}
public static class MusicAceIdeaWork
{
    public const string Kind="music.ace.ideas";
    public static async Task RunAsync(MusicAceIdeaWorkRequest work,IMusicYueWorker worker,Action<MusicAceIdeaWorkRequest> changed,
        CancellationToken token,BackgroundOperationState? restored=null,MusicAceIdeaStore? storage=null) {
        var store=storage??MusicAceIdeaStore.Default;store.Save(work);
        await ApplicationBackgroundOperations.RunAsync(Kind,"ACE · music ideas",work.ProjectId,new{work.Id},async attempt=>{
            await ApplicationBackgroundOperations.RetireModelsAsync();
            var loras=MusicLoraCatalog.Capture(work.Input.Loras,MusicAceCatalog.Variation);
            for(var index=0;index<work.Candidates.Length;index++) {
                attempt.ThrowIfCancellationRequested();var candidate=work.Candidates[index];
                if(candidate.Idea is not null||candidate.Error is not null)continue;
                try {
                    var plan=Path.Combine(store.Folder(work.Id),Guid.NewGuid().ToString("N")+".plan.json");
                    var request=new MusicYueRequest(work.Style,work.Input.Lyrics,candidate.Seed,candidate.Seed,work.Input.DurationSeconds??360) {
                        DurationAutomatic=work.Input.DurationSeconds is null,Expert=work.Input.Expert.Snapshot(),Wishes=work.Input.Wishes.Snapshot(),Loras=loras};
                    await worker.PlanAsync(MusicModelVariants.Artifact(work.ModelsRoot,MusicAceCatalog.Variation,MusicAceCatalog.Variation),
                        request,Path.ChangeExtension(plan,".request.json"),plan,attempt);
                    if(new FileInfo(plan).Length>200000)throw new InvalidDataException("Music.AceIdeas.Invalid");
                    var data=JsonSerializer.Deserialize<MusicAceIdeaData>(await File.ReadAllTextAsync(plan,attempt))??throw new InvalidDataException("Music.AceIdeas.Invalid");data.Validate();
                    var idea=new MusicAceIdea {Name=(index+1).ToString(),Seed=candidate.Seed,Data=data,Input=work.Input.Snapshot(),Style=work.Style,
                        Hash=MusicAceIdea.Stamp(data.Codes),InputHash=MusicAceIdea.Bind(work.Style,work.Input.Lyrics,work.Input.Expert,loras,work.Input.DurationSeconds)};
                    idea.Validate();candidate=candidate with {Idea=idea};
                }catch(OperationCanceledException){throw;}
                catch(Exception error){candidate=candidate with {Error=error.Message[..Math.Min(4096,error.Message.Length)]};}
                var candidates=work.Candidates.ToArray();candidates[index]=candidate;work=work with {Candidates=candidates};
                store.Save(work);changed(work);ApplicationBackgroundOperations.Current?.SaveCheckpoint(new{work.Id,Finished=candidates.Count(c=>c.Idea is not null||c.Error is not null)});
            }
            if(work.Candidates.All(c=>c.Idea is null))throw new InvalidDataException("Music.AceIdeas.AllFailed");
            return work.Id;
        },token,restored);
    }
}
