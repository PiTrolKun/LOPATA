using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHub.Models;
using AIHub.Services.VideoEditor;

namespace AIHub.Services.SongClip;

internal static partial class InfiniteTalkRunner
{
    internal const string ModelId="infinitetalk",Kind="song.clip.infinitetalk";
    private const string Scenario="song-to-clip/infinitetalk";
    internal static void ValidateSettings(SongClipAttempt attempt)
    {
        SongClipReferences.ValidateNewAttempt(attempt);
        var references=SongClipReferences.For(attempt);
        if(references.Length!=1||references[0].Kind!="performer")throw new InvalidDataException("SongClip.InfiniteReferenceRequired");
        var s=attempt.Settings;var n=s.InfiniteTalk??new();
        if(attempt.Model!=ModelId||attempt.PlanningVersion!=1||s.Fps!=24||s.Width is <256 or >3840||s.Height is <256 or >2160
            ||s.Width%2!=0||s.Height%2!=0||s.Quality is <1 or >100||s.Seed is <0 or >int.MaxValue
            ||n.Width is <256 or >1024||n.Height is <256 or >1024||n.Width%32!=0||n.Height%32!=0||n.Steps is <20 or >50
            ||n.FrameWindow!=81||n.MotionFrames!=9||!double.IsFinite(n.AudioCfg)||n.AudioCfg is <2 or >5
            ||!double.IsFinite(n.VramReserveGb)||n.VramReserveGb is <0 or >8)
            throw new InvalidDataException("SongClip.InvalidSettings");
    }
    internal static async Task<SongClipResult> RunAsync(SongClipAttempt attempt,SongClipStore store,
        Action<double,string> progress,CancellationToken token)
    {
        if(SongClipVariantCatalog.IsRetiredModel(ModelId))throw new InvalidOperationException(SongClipVariantCatalog.RetiredMessageKey);
        ValidateSettings(attempt);
        using var audit=ComponentAudit.CaptureOperation();using var auditLog=ComponentAudit.CaptureLog(m=>progress(0,m));
        var job=store.Resolve(attempt.ProjectId,"attempts/"+Guid.ParseExact(attempt.Id,"N").ToString("N"));Directory.CreateDirectory(job);
        var cards=InfiniteTalkComponents.Cards(attempt.ModelsRoot);
        await InfiniteTalkBinding.EnsureAsync(attempt,store,job,cards,token);
        var clock=Path.Combine(job,"elapsed.json");
        var previous=File.Exists(clock)?JsonSerializer.Deserialize<double>(await File.ReadAllTextAsync(clock,token)):0;
        if(!double.IsFinite(previous)||previous<0)throw new InvalidDataException("Invalid generation clock.");
        var watch=Stopwatch.StartNew();double saved=0;
        double Elapsed()=>previous+watch.Elapsed.TotalSeconds;
        void Report(double fraction,string message) {
            if(watch.Elapsed.TotalSeconds-saved>=2){SongClipStore.Atomic(clock,Elapsed());saved=watch.Elapsed.TotalSeconds;}
            progress(fraction,message);
        }
        try {return await Render(attempt,store,job,Report,token,Elapsed);}
        finally {SongClipStore.Atomic(clock,Elapsed());}
    }
    private static async Task<SongClipResult> Render(SongClipAttempt attempt,SongClipStore store,string job,
        Action<double,string> progress,CancellationToken token,Func<double> elapsed)
    {
        progress(0,"SongClip.Preparing");await ApplicationBackgroundOperations.RetireModelsAsync();
        await VideoRuntime.PrepareAsync(token);
        var env=await InfiniteTalkComponents.PrepareAsync(attempt.ModelsRoot,attempt.Connections,token,m=>progress(0,m));
        await RuntimeFileAudit.VerifyAsync("SongClip.InfiniteTalk.Adapter",Path.Combine(AppContext.BaseDirectory,"Tools"),
            ApplicationBuildVersion.Value,InfiniteTalkBinding.AdapterFiles(),token);
        var request=Request(attempt,store,job,env);
        var analyze=Path.Combine(job,"infinitetalk-analysis-request.json");SongClipStore.Atomic(analyze,request);
        await RunWorkerAsync(env.Python.Python,analyze,job,"--analyze",progress,token);
        using var analysis=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(job,"infinitetalk-analysis.json"),token));
        var slots=ValidateAnalysis(attempt,analysis.RootElement);
        var plan=await SongClipPlanning.EnsureAsync(attempt,store,job,progress,token,slots)
            ??throw new InvalidDataException(SongClipVisualProtocol.InvalidReply);
        request["visualPlan"]=JsonSerializer.SerializeToNode(plan);
        var input=Path.Combine(job,"request.json");
        if(File.Exists(input)) {
            if(!JsonNode.DeepEquals(JsonNode.Parse(await File.ReadAllTextAsync(input,token)),request))
                throw new InvalidDataException("SongClip.VisualPlanMismatch");
        }else {token.ThrowIfCancellationRequested();SongClipStore.Atomic(input,request);}
        await ApplicationBackgroundOperations.RetireModelsAsync();
        await RunWorkerAsync(env.Python.Python,input,job,File.Exists(Path.Combine(job,"worker-result.json"))?"--verify":"--run",progress,token);
        using var receipt=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(job,"worker-result.json"),token));
        var result=receipt.RootElement;
        if(result.GetProperty("schema").GetInt32()!=1||result.GetProperty("modelId").GetString()!=ModelId
            ||result.GetProperty("sourceRevision").GetString()!=InfiniteTalkComponents.Revision
            ||result.GetProperty("requestSha256").GetString()!=await H3MontageBinding.HashAsync(input,token)
            ||!result.GetProperty("referenceUsed").GetBoolean()
            ||result.GetProperty("fps").GetInt32()!=25
            ||result.GetProperty("frames").GetInt32()!=(int)Math.Ceiling(analysis.RootElement.GetProperty("durationSeconds").GetDouble()*25))
            throw new InvalidDataException("SongClip.InfiniteResultInvalid");
        var raw=RuntimeFileAudit.Contained(job,Path.GetRelativePath(job,result.GetProperty("output").GetString()!));
        if(result.GetProperty("sha256").GetString()!=await H3MontageBinding.HashAsync(raw,token))
            throw new InvalidDataException("SongClip.InfiniteResultInvalid");
        var facts=new OutputProvenance {Scenario=Scenario,CreatedUtc=DateTimeOffset.UtcNow,GenerationSeconds=elapsed(),
            ModelHistory="recorded-inference; audio-driven performer and connected windows",Models=[..Facts(env.Cards),..plan.Assistants]};
        var final=Path.Combine(job,"clip.mp4");
        if(!File.Exists(final)) {
            var staging=Path.Combine(job,"clip.installing.mp4");progress(.99,"SongClip.Finalizing");
            await VideoRuntime.RunAsync(false,["-nostdin","-v","error","-y","-i",raw,"-map","0:v:0","-map","0:a:0","-c","copy",
                "-metadata","encoded_by=LOPATA "+facts.ProgramVersion,"-metadata","LOPATA_VIDEO="+facts.Json(),
                "-metadata","LOPATA_OPERATION="+attempt.Id,"-metadata","creation_time="+facts.ExportedUtc.ToString("O"),
                "-movflags","+faststart+use_metadata_tags",staging],token);
            await Check(staging);token.ThrowIfCancellationRequested();File.Move(staging,final,true);
        }
        return await Check(final);
        async Task<SongClipResult> Check(string path) {
            using var output=await VideoRuntime.ProbeAsync(path,token);
            using var source=await VideoRuntime.ProbeAsync(store.Resolve(attempt.ProjectId,attempt.Song),token);
            double seconds;
            try {seconds=H3MusicResultValidation.Validate(output.RootElement,source.RootElement,attempt.Settings,attempt.Id,facts.Models,Scenario);}
            catch(InvalidDataException e){throw new InvalidDataException("SongClip.InfiniteResultInvalid",e);}
            return new(attempt.Id,"attempts/"+attempt.Id+"/clip.mp4",ModelId,seconds,DateTimeOffset.UtcNow);
        }
    }
    internal static SongClipVisualScene[] ValidateAnalysis(SongClipAttempt attempt,JsonElement value)
    {
        static InvalidDataException Invalid()=>new("SongClip.InfiniteResultInvalid");
        if(value.ValueKind!=JsonValueKind.Object||!value.TryGetProperty("schema",out var schema)
            ||schema.ValueKind!=JsonValueKind.Number||!schema.TryGetInt32(out var version)||version!=1
            ||!value.TryGetProperty("durationSeconds",out var d)||d.ValueKind!=JsonValueKind.Number||!d.TryGetDouble(out var duration)
            ||!double.IsFinite(duration)||duration is <=0 or >7200
            ||!value.TryGetProperty("slots",out var rows)||rows.ValueKind!=JsonValueKind.Array)throw Invalid();
        var slots=SongClipVisualProtocol.Slots(attempt,duration);var actual=rows.EnumerateArray().ToArray();
        if(actual.Length!=slots.Length)throw Invalid();
        for(var i=0;i<slots.Length;i++) {
            var row=actual[i];
            if(row.ValueKind!=JsonValueKind.Object||!row.TryGetProperty("index",out var index)||index.ValueKind!=JsonValueKind.Number
                ||!index.TryGetInt32(out var number)||number!=slots[i].Index)throw Invalid();
            foreach(var (key,expected) in new[]{("startSeconds",slots[i].StartSeconds),("endSeconds",slots[i].EndSeconds)})
                if(!row.TryGetProperty(key,out var n)||n.ValueKind!=JsonValueKind.Number||!n.TryGetDouble(out var time)
                    ||!double.IsFinite(time)||Math.Abs(time-expected)>.0001)throw Invalid();
        }
        return slots;
    }
    internal static OutputModelUse[] Facts(IReadOnlyList<ManagedModelArtifactCard> cards)=>cards
        .Select(c=>new OutputModelUse(c.ModelArtifactId,c.DisplayName,c.Revision,c.ModelArtifactId==InfiniteTalkComponents.SourceId
            ?"audio-driven continuation nodes":c.Files.Any(f=>f.RelativePath.EndsWith(".gguf",StringComparison.OrdinalIgnoreCase))
                ?"video diffusion and audio conditioning":c.ModelArtifactId=="video-h3-runtime-libraries"?"runtime":"encoder, decoder or runtime")).ToArray();
}
