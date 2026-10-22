using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.MusicScore;

public sealed record MusicScoreCandidate(int Seed)
{
    public MusicScoreVersion? Version { get; init; }
    public string? Error { get; init; }
}
public sealed record MusicScorePlanRequest(string Id,string ProjectId,string ModelsRoot,MusicProjectSnapshot Input,
    string Style,MusicLora.MusicLoraUse[] Loras,string RuntimeRevision,string RuntimePack,MusicScoreCandidate[] Candidates);

/// <summary>Durable queue; a completed plan is never represented as completed audio.</summary>
public static class MusicScorePlanWork
{
    public const string Kind="music.score.plan";
    private static readonly MusicScorePlanStore Default=new(Path.Combine(AppDataPaths.BaseDirectory,"Music","Scores"));
    public static void Save(MusicScorePlanRequest work)=>Default.Save(work);
    public static MusicScorePlanRequest Load(string id)=>Default.Load(id);
    internal static void Validate(MusicScorePlanRequest work) {
        if(work.Candidates.Length is <1 or >8 || !Guid.TryParseExact(work.ProjectId,"N",out _) || work.Input.Variation!=MusicStudioRuntime.Variation
            || !MusicStudioRuntime.IsSupported(work.RuntimeRevision,work.RuntimePack)) throw new InvalidDataException("Music.Score.Invalid");
        work.Input.Validate();foreach(var c in work.Candidates) {if(c.Seed<0)throw new InvalidDataException("Music.Score.Invalid");c.Version?.Validate();}
    }
    public static async Task RunAsync(MusicScorePlanRequest work,IMusicYueWorker worker,Action<MusicScorePlanRequest> changed,
        CancellationToken token,BackgroundOperationState? restored=null,Func<bool>? stopAfterCurrent=null,MusicScorePlanStore? storage=null) {
        var store=storage??Default;store.Save(work);
        await ApplicationBackgroundOperations.RunAsync(Kind,"YuE2 Studio · ABC",work.ProjectId,new {work.Id},async attempt=> {
            await ApplicationBackgroundOperations.RetireModelsAsync();
            for(var index=0;index<work.Candidates.Length;index++) {
                attempt.ThrowIfCancellationRequested();var candidate=work.Candidates[index];
                if(candidate.Version is not null || candidate.Error is not null)continue;
                if(stopAfterCurrent?.Invoke()==true) {
                    work=work with {Candidates=work.Candidates.Select(c=>c.Version is null&&c.Error is null?c with {Error="Music.Score.Skipped"}:c).ToArray()};
                    store.Save(work);changed(work);break;
                }
                var plan=Path.Combine(store.Folder(work.Id),Guid.NewGuid().ToString("N")+".abc");
                try {
                    var request=new MusicYueRequest(work.Style,work.Input.Lyrics,candidate.Seed,candidate.Seed,8) {
                        Expert=work.Input.Expert.Snapshot(),Wishes=work.Input.Wishes.Snapshot(),Loras=work.Loras.ToArray(),
                        StudioRuntimeRevision=work.RuntimeRevision,StudioRuntimePack=work.RuntimePack,
                        FollowScoreSections=work.Input.Scores.FollowSections };
                    await worker.PlanAsync(MusicModelVariants.Artifact(work.ModelsRoot,MusicStudioRuntime.Variation,MusicComponentCatalog.ModelId),
                        request,Path.ChangeExtension(plan,".json"),plan,attempt);
                    var abc=await File.ReadAllTextAsync(plan,attempt);
                    _=await MusicScoreNative.InspectAsync(abc,attempt);
                    abc=await MusicScoreNative.MarkAsync(abc,MusicLora.MusicLoraCatalog.EffectiveStyle(work.Style,work.Loras),work.Input.Lyrics,work.Input.Expert.Cot,false,attempt);
                    var version=new MusicScoreVersion {Name=(index+1).ToString(),Origin="generated",Abc=abc,Hash=MusicScoreVersion.Stamp(abc),
                        InputHash=MusicScoreVersion.Bind(work.Style,work.Input.Lyrics,work.Input.Expert.Cot,work.Loras),Cot=work.Input.Expert.Cot,
                        RuntimeRevision=work.RuntimeRevision,RuntimePack=work.RuntimePack,ModelRevision=work.Input.ModelRevision,Seed=candidate.Seed,WorkId=work.Id,WorkIndex=index};
                    candidate=candidate with {Version=version};
                } catch(OperationCanceledException) {throw;}
                catch(Exception error) {candidate=candidate with {Error=error.Message};}
                var candidates=work.Candidates.ToArray();candidates[index]=candidate;work=work with {Candidates=candidates};
                store.Save(work);changed(work);
                ApplicationBackgroundOperations.Current?.SaveCheckpoint(new {work.Id,Finished=work.Candidates.Count(c=>c.Version is not null||c.Error is not null)});
            }
            if(work.Candidates.All(c=>c.Version is null))throw new InvalidDataException("Music.Score.AllPlansFailed");
            return work.Id;
        },token,restored);
    }
}
