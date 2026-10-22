using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIHub.Models;

namespace AIHub.Services.MusicScore;

public static class MusicScorePipeline
{
    public static bool Supported(string variation) => variation==MusicStudioRuntime.Variation && EngineSetSelection.ActiveId=="002";
    public static void Validate(MusicScoreWorkspace? scores,string variation,MusicExpertSettings expert,MusicAudioTask? audio,MusicMelody? melody,string lyrics) {
        if(scores is null)return;scores.Validate();
        if(!scores.Enabled&&!scores.FollowSections&&!scores.LyricTiming)return;
        if(variation!=MusicStudioRuntime.Variation)throw new InvalidDataException("Music.Score.Unsupported");
        if(scores.Enabled&&(audio?.Enabled==true||melody?.Enabled==true))throw new InvalidDataException("Music.Score.Conflict");
        if(expert.Cot=="off")throw new InvalidDataException("Music.Score.NeedCot");
        if(scores.Enabled&&scores.Selected.Any(id=>scores.Versions.Single(v=>v.Id==id).Error is not null))throw new InvalidDataException("Music.Score.Invalid");
        if(scores.Enabled&&scores.Selected.Select(id=>scores.Versions.Single(v=>v.Id==id)).Any(v=>v.Cot!=expert.Cot))
            throw new InvalidDataException("Music.Score.CotMismatch");
        if(scores.FollowSections&&(expert.Cot!="full" || Regex.Matches(lyrics,@"(?m)^\s*\[[^\]\r\n]+\]\s*$").Count is <1 or >64))
            throw new InvalidDataException("Music.Score.NeedSections");
        if(scores.LyricTiming&&(expert.Get("cfg_scale")!=1 || string.IsNullOrWhiteSpace(lyrics)))throw new InvalidDataException("Music.Score.TimingUnavailable");
    }
    public static async Task<MusicGenerationJob> CaptureAsync(MusicGenerationJobs jobs,MusicGenerationJob job,MusicScoreWorkspace source,CancellationToken token) {
        Validate(source,job.Variation,job.Expert,job.AudioTask,job.Melody,job.Lyrics);
        if(!source.Enabled&&!source.FollowSections&&!source.LyricTiming)return job;
        if(job.EngineSetId!="002")throw new InvalidDataException("Music.Score.Unsupported");
        var versions=source.Enabled ? source.Selected.Select(id=>source.Versions.Single(v=>v.Id==id)).ToArray() : [];
        var binding=MusicScoreVersion.Bind(job.Style,job.Lyrics,job.Expert.Cot,job.Loras);
        if(!source.Keep&&versions.Any(v=>v.InputHash!=binding))throw new InvalidDataException("Music.Score.InputChanged");
        var variants=job.Variants.ToArray();
        for(var i=0;source.Enabled&&i<variants.Length;i++) {
            var version=versions[i%versions.Length];
            _=await MusicScoreNative.InspectAsync(version.Abc,token);
            var abc=await MusicScoreNative.MarkAsync(version.Abc,MusicLora.MusicLoraCatalog.EffectiveStyle(job.Style,job.Loras),job.Lyrics,job.Expert.Cot,source.Keep,token);
            var path=jobs.StagePath(job.Id,".abc");await File.WriteAllTextAsync(path,abc,token);
            variants[i]=variants[i] with {PlanFile=path,PlanHash=MusicScoreVersion.Stamp(abc),ScoreVersionId=version.Id,PlanHardware="saved ABC"};
        }
        job=job with {Scores=source with {Versions=versions,Selected=versions.Select(v=>v.Id).ToArray()},Variants=variants};
        jobs.Save(job);return job;
    }
    public static async Task<MusicGenerationVariant> SaveEffectiveAsync(MusicGenerationJobs jobs,MusicGenerationJob job,MusicGenerationVariant variant,string? receipt,CancellationToken token) {
        if(job.Scores is null)return variant;
        if(string.IsNullOrWhiteSpace(receipt))throw new InvalidDataException("Music.Score.ReceiptMissing");
        using var document=JsonDocument.Parse(receipt);var settings=document.RootElement;
        if(settings.TryGetProperty("generation_settings",out var nested))settings=nested;
        if(!settings.TryGetProperty("abc",out var abc)||string.IsNullOrWhiteSpace(abc.GetString()))throw new InvalidDataException("Music.Score.ReceiptMissing");
        if(job.Scores.LyricTiming&&!settings.TryGetProperty("lyric_schedule",out _))throw new InvalidDataException("Music.Score.TimingUnavailable");
        var text=abc.GetString()!;var path=jobs.StagePath(job.Id,".effective.abc");await File.WriteAllTextAsync(path,text,token);
        return variant with {EffectivePlanFile=path,EffectivePlanHash=MusicScoreVersion.Stamp(text)};
    }
    public static async Task CheckSchedulingAsync(MusicYueRequest request,CancellationToken token) {
        if(request.ScoreLyricTiming&&!await MusicScoreNative.CanScheduleAsync(request.Abc,request.Lyrics,token))
            throw new InvalidDataException("Music.Score.TimingUnavailable");
    }
}
