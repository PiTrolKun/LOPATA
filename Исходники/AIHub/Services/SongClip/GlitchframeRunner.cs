using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIHub.Services.VideoEditor;

namespace AIHub.Services.SongClip;

internal static class GlitchframeRunner
{
    internal static async Task<SongClipResult> RunAsync(SongClipAttempt attempt, SongClipStore store,
        Action<double,string> progress, CancellationToken token)
    {
        using var audit = ComponentAudit.CaptureOperation();
        await VideoRuntime.PrepareAsync(token);
        var folder=store.Resolve(attempt.ProjectId,"attempts/"+Guid.ParseExact(attempt.Id,"N").ToString("N"));Directory.CreateDirectory(folder);
        var final=Path.Combine(folder,"clip.mp4");
        if(File.Exists(final)) {
            var plan=await SongClipPlanning.ReadCompletedAsync(attempt,store,folder,token);
            return await CheckResult(final,attempt,store,token,plan?.Assistants);
        }
        var clockFile=Path.Combine(folder,"elapsed.json");
        var prior=File.Exists(clockFile)?JsonSerializer.Deserialize<double>(await File.ReadAllTextAsync(clockFile,token)):0;
        if(!double.IsFinite(prior)||prior<0)throw new InvalidDataException("Invalid generation clock.");
        var watch=Stopwatch.StartNew();double saved=0;double Elapsed()=>prior+watch.Elapsed.TotalSeconds;
        try {
            return await RunAttemptAsync(attempt,store,(p,m)=>{if(watch.Elapsed.TotalSeconds-saved>=2){SongClipStore.Atomic(clockFile,Elapsed());saved=watch.Elapsed.TotalSeconds;}progress(p,m);},token,Elapsed);
        } finally {SongClipStore.Atomic(clockFile,Elapsed());}
    }
    private static async Task<SongClipResult> RunAttemptAsync(SongClipAttempt attempt, SongClipStore store,
        Action<double,string> progress, CancellationToken token,Func<double> elapsed)
    {
        using var audit = ComponentAudit.CaptureOperation();
        if(attempt.Settings.Width is < 256 or > 3840 || attempt.Settings.Height is < 256 or > 2160
            || attempt.Settings.Fps is < 12 or > 60 || attempt.Settings.Steps is < 8 or > 50
            || attempt.Settings.Interval is < 2 or > 60 || !double.IsFinite(attempt.Settings.Intensity)
            || attempt.Settings.Intensity is < 0 or > 2 || attempt.PlanningVersion is < 0 or > 1) throw new InvalidDataException("SongClip.InvalidSettings");
        var job=store.Resolve(attempt.ProjectId,"attempts/"+Guid.ParseExact(attempt.Id,"N").ToString("N"));
        Directory.CreateDirectory(job);
        var final=Path.Combine(job,"clip.mp4");
        if(File.Exists(final)) {
            var completedPlan=await SongClipPlanning.ReadCompletedAsync(attempt,store,job,token);
            return await CheckResult(final,attempt,store,token,completedPlan?.Assistants);
        }
        progress(0,"SongClip.Preparing");
        await ApplicationBackgroundOperations.RetireModelsAsync();
        var plan=await SongClipPlanning.EnsureAsync(attempt,store,job,progress,token);
        if(attempt.PlanningVersion==1&&plan?.Assistants is not {Length:>0})throw new InvalidDataException(SongClipVisualProtocol.InvalidReply);
        var environment=await GlitchframeComponents.PrepareAsync(attempt.ModelsRoot,attempt.Connections,token,m=>progress(0,m));
        await VideoRuntime.PrepareAsync(token);
        using(var script=typeof(GlitchframeRunner).Assembly.GetManifestResourceStream("AIHub.SongClip.Worker")!) {
            await RuntimeFileAudit.VerifyAsync("SongClip.Adapter",Path.Combine(AppContext.BaseDirectory,"Tools"),ApplicationBuildVersion.Value,
                [new("song_clip_glitch.py",script.Length,Convert.ToHexString(SHA256.HashData(script)))],token);
        }
        var receipt=Path.Combine(job,"worker-result.json");
        if(!File.Exists(receipt)) {
            var payload=new {
                libraries=environment.Libraries,source=environment.Source,model=environment.Model,job,
                song=store.Resolve(attempt.ProjectId,attempt.Song),reference=attempt.Reference.Length==0?"":store.Resolve(attempt.ProjectId,attempt.Reference),
                ffmpeg=Path.Combine(VideoRuntime.DirectoryPath,"ffmpeg.exe"),ffprobe=Path.Combine(VideoRuntime.DirectoryPath,"ffprobe.exe"),
                width=attempt.Settings.Width,height=attempt.Settings.Height,fps=attempt.Settings.Fps,
                // SDXL drawing size is independent of the output's aspect-preserving upscale.
                genWidth=attempt.Settings.Width>attempt.Settings.Height?1024:768,
                genHeight=attempt.Settings.Width>attempt.Settings.Height?768:1024,
                steps=attempt.Settings.Steps,interval=attempt.Settings.Interval,seed=attempt.Settings.Seed,
                prompt=attempt.Settings.Prompt,visualPlan=plan,shader=attempt.Settings.Shader,intensity=attempt.Settings.Intensity,
                title="",words=ParseTimings(attempt.Timings),device=environment.Python.Device
            };
            var request=Path.Combine(job,"request.json"); SongClipWorkerRequest.Write(request,payload,plan);
            var info=new ProcessStartInfo(environment.Python.Python) {UseShellExecute=false,CreateNoWindow=true,
                RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
            ManagedPythonLaunch.ScriptArguments(info,[Path.Combine(AppContext.BaseDirectory,"Tools","song_clip_glitch.py"),request]);
            using var process=OwnedProcessRegistry.Shared.Start(info,"SongClip.Glitchframe");
            using var cancel=token.Register(()=>{try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}});
            var diagnostics=new StringBuilder();
            async Task Read(StreamReader reader,bool events) {
                while(await reader.ReadLineAsync() is {} line) {
                    await File.AppendAllTextAsync(Path.Combine(job,events?"progress.jsonl":"stderr.log"),line+Environment.NewLine,CancellationToken.None);
                    if(events) {
                        try {using var message=JsonDocument.Parse(line);var e=message.RootElement;progress(e.GetProperty("fraction").GetDouble(),e.GetProperty("message").GetString()!);}
                        catch(JsonException){progress(0,line);}
                    } else {diagnostics.AppendLine(line);if(diagnostics.Length>16000)diagnostics.Remove(0,8000);}
                }
            }
            var reads=Task.WhenAll(Read(process.StandardOutput,true),Read(process.StandardError,false));
            await process.WaitForExitAsync(CancellationToken.None);await reads;token.ThrowIfCancellationRequested();
            if(process.ExitCode!=0) throw new IOException("Glitchframe: "+diagnostics);
        }
        using var rendered=JsonDocument.Parse(await File.ReadAllTextAsync(receipt,token));
        var raw=rendered.RootElement.GetProperty("output").GetString()!;
        if(!Path.GetFullPath(raw).StartsWith(job+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid worker output path.");
        var facts=new OutputProvenance {Scenario="song-to-clip/glitchframe",CreatedUtc=DateTimeOffset.UtcNow,
            GenerationSeconds=elapsed(),ModelHistory="recorded-inference",
            Models=[new("stabilityai/stable-diffusion-xl-base-1.0","SDXL 1.0 FP16","462165984030d82259a11f4367a4eed129e94a7b","background-image-generation"),
                new("OlaProeis/Glitchframe","Glitchframe","a9660508fdbbfbc6243f570d71bbd2af8ee5b111","music-video-composition"),
                ..(plan?.Assistants??[])]};
        var staging=Path.Combine(job,"clip.installing.mp4");
        progress(.99,"SongClip.Finalizing");
        // Copy video/audio streams. Output provenance is mandatory, never a sidecar-only claim.
        await VideoRuntime.RunAsync(false,["-nostdin","-v","error","-y","-i",raw,"-map","0:v:0","-map","0:a:0","-c","copy",
            "-metadata","encoded_by=LOPATA "+facts.ProgramVersion,"-metadata","LOPATA_VIDEO="+facts.Json(),
            "-metadata","creation_time="+facts.ExportedUtc.ToString("O"),"-movflags","+faststart+use_metadata_tags",staging],token);
        _=await CheckResult(staging,attempt,store,token,plan?.Assistants);token.ThrowIfCancellationRequested();File.Move(staging,final,true);
        return await CheckResult(final,attempt,store,token,plan?.Assistants);
    }
    private static async Task<SongClipResult> CheckResult(string file,SongClipAttempt attempt,SongClipStore store,CancellationToken token,
        IReadOnlyList<OutputModelUse>? assistants=null)
    {
        if(attempt.PlanningVersion==1&&assistants is not {Count:>0})throw new InvalidDataException(SongClipVisualProtocol.InvalidReply);
        using var probe=await VideoRuntime.ProbeAsync(file,token);
        var source=store.Resolve(attempt.ProjectId,attempt.Song);
        using var input=await VideoRuntime.ProbeAsync(source,token);
        var seconds=ValidateResult(probe.RootElement,input.RootElement,attempt.Settings,assistants);
        return new(attempt.Id,"attempts/"+attempt.Id+"/clip.mp4","glitchframe",seconds,DateTimeOffset.UtcNow);
    }
    internal static double ValidateResult(JsonElement output,JsonElement source,SongClipSettings settings,
        IReadOnlyList<OutputModelUse>? assistants=null)
    {
        var streams=output.GetProperty("streams").EnumerateArray().ToArray();
        var video=streams.SingleOrDefault(s=>s.GetProperty("codec_type").GetString()=="video");
        var audio=streams.SingleOrDefault(s=>s.GetProperty("codec_type").GetString()=="audio");
        var input=source.GetProperty("streams").EnumerateArray().FirstOrDefault(s=>s.GetProperty("codec_type").GetString()=="audio");
        if(video.ValueKind==JsonValueKind.Undefined||audio.ValueKind==JsonValueKind.Undefined||input.ValueKind==JsonValueKind.Undefined
            ||video.GetProperty("width").GetInt32()!=settings.Width||video.GetProperty("height").GetInt32()!=settings.Height)throw new InvalidDataException("Invalid Glitchframe streams.");
        static double Duration(JsonElement stream,JsonElement document) {
            var value=stream.TryGetProperty("duration",out var d)?d:document.GetProperty("format").GetProperty("duration");
            if(!double.TryParse(value.GetString(),NumberStyles.Float,CultureInfo.InvariantCulture,out var seconds)||!double.IsFinite(seconds)||seconds<=0)throw new InvalidDataException("Invalid media duration.");
            return seconds;
        }
        var expected=Duration(input,source);var tolerance=Math.Max(.1,1d/settings.Fps);
        if(Math.Abs(Duration(video,output)-expected)>tolerance||Math.Abs(Duration(audio,output)-expected)>tolerance)throw new InvalidDataException("The Glitchframe result does not cover the whole song.");
        var tag=output.GetProperty("format").GetProperty("tags").EnumerateObject().SingleOrDefault(t=>t.Name.Equals("LOPATA_VIDEO",StringComparison.OrdinalIgnoreCase));
        var facts=tag.Value.ValueKind==JsonValueKind.String?JsonSerializer.Deserialize<OutputProvenance>(tag.Value.GetString()!):null;
        if(facts is null||facts.Program!="LOPATA"||facts.Scenario!="song-to-clip/glitchframe"||facts.GenerationSeconds is not >=0||facts.Models is null
            ||!facts.Models.Any(m=>m.Id=="stabilityai/stable-diffusion-xl-base-1.0"&&m.Revision=="462165984030d82259a11f4367a4eed129e94a7b")
            ||!facts.Models.Any(m=>m.Id=="OlaProeis/Glitchframe"&&m.Revision=="a9660508fdbbfbc6243f570d71bbd2af8ee5b111")
            ||assistants?.Any(expected=>!facts.Models.Any(model=>model.Id==expected.Id&&model.Revision==expected.Revision&&model.Role==expected.Role))==true)
            throw new InvalidDataException("Missing Glitchframe provenance.");
        _=facts.Json();return Duration(video,output);
    }
    internal static object[] ParseTimings(string lrc)
    {
        var lines=lrc.Split('\n').SelectMany(line=>Regex.Matches(line,@"\[(\d+):(\d+(?:\.\d+)?)\]")
            .Select(m=>(Time:int.Parse(m.Groups[1].Value,CultureInfo.InvariantCulture)*60+double.Parse(m.Groups[2].Value,CultureInfo.InvariantCulture),
                Text:Regex.Replace(line,@"\[[^\]]*\]","").Trim()))).OrderBy(x=>x.Time).ToArray();
        if(lines.Any(l=>!double.IsFinite(l.Time))) throw new InvalidDataException("Invalid lyric timing.");
        // LRC records whole lines. Preserve their supplied timing; never invent word alignment.
        return lines.Select((line,i)=>(object)new {word=line.Text,line_idx=i,t_start=line.Time,t_end=i+1<lines.Length?lines[i+1].Time:line.Time+3}).ToArray();
    }
}
