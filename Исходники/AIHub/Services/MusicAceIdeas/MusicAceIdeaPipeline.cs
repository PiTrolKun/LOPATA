using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.MusicLora;

namespace AIHub.Services.MusicAceIdeas;

public static class MusicAceIdeaPipeline
{
    public static void Validate(MusicAceIdeaWorkspace state,string variation,MusicAudioTask? audio,MusicMelody? melody) {
        state.Validate();if(!state.Enabled)return;
        if(!MusicAceCatalog.IsAce(variation))throw new InvalidDataException("Music.AceIdeas.Unsupported");
        if(audio?.Enabled==true||melody?.Enabled==true)throw new InvalidDataException("Music.AceIdeas.Conflict");
    }
    public static MusicGenerationJob Capture(MusicGenerationJob job,MusicAceIdeaWorkspace state,bool preview=false) {
        Validate(state,job.Variation,job.AudioTask,job.Melody);if(!state.Enabled)return job;
        var binding=MusicAceIdea.Bind(job.Style,job.Lyrics,job.Expert,job.Loras,job.DurationAutomatic?null:job.DurationSeconds);
        var ideas=state.Selected.Select(id=>state.Ideas.Single(i=>i.Id==id)).ToArray();
        if(ideas.Any(i=>i.InputHash!=binding))throw new InvalidDataException("Music.AceIdeas.InputChanged");
        return job with {Variants=job.Variants.Select((v,index)=>{
            var idea=ideas[index%ideas.Length];var use=new MusicAceIdeaUse(idea,state.Retake?RandomNumberGenerator.GetInt32(1,int.MaxValue):null,state.Retake?state.Variance:0,preview);
            var soundSeed=state.Retake?(idea.BaseSoundSeed??idea.Seed):v.SoundSeed;
            return v with {AceIdea=use,LanguageSeed=soundSeed,SoundSeed=soundSeed};
        }).ToArray()};
    }
    public static void Apply(Dictionary<string,object?> parameters,MusicAceIdeaUse use) {
        use.Validate();var data=use.EffectiveData;
        parameters["audio_codes"]=data.Codes;parameters["caption"]=data.Caption;parameters["lyrics"]=data.Lyrics;
        parameters["bpm"]=data.Bpm;parameters["keyscale"]=data.Keyscale;parameters["timesignature"]=data.Timesignature;
        parameters["vocal_language"]=data.VocalLanguage;parameters["duration"]=data.Duration;
        foreach(var key in new[]{"thinking","use_cot_metas","use_cot_caption","use_cot_language","use_cot_lyrics"})parameters[key]=false;
        parameters["retake_seed"]=use.RetakeSeed;parameters["retake_variance"]=use.Variance;
    }
    public static void Confirm(MusicAceIdeaUse? use,string? receipt,int? expectedSeed=null) {
        if(use is null)return;use.Validate();
        using var document=JsonDocument.Parse(receipt??"{}");
        if(!document.RootElement.TryGetProperty("effective",out var effective)||!effective.TryGetProperty("audio_codes",out var codes)
            ||codes.GetString()!=use.EffectiveData.Codes)throw new InvalidDataException("Music.AceIdeas.ReceiptMissing");
        if(use.Variance>0&&(!effective.TryGetProperty("retake_seed",out var seed)||seed.ToString()!=use.RetakeSeed!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            throw new InvalidDataException("Music.AceIdeas.ReceiptMissing");
        if(!effective.TryGetProperty("retake_variance",out var variance)||!variance.TryGetDouble(out var actual)||Math.Abs(actual-use.Variance)>1e-8
            ||expectedSeed is {} expected&&(!effective.TryGetProperty("seed",out var sound)||sound.ToString()!=expected.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            throw new InvalidDataException("Music.AceIdeas.ReceiptMissing");
        if(use.Variance>0&&(!document.RootElement.TryGetProperty("retake_adapter",out var adapter)
            ||!adapter.TryGetProperty("applied",out var applied)||applied.ValueKind!=JsonValueKind.True))
            throw new InvalidDataException("Music.AceIdeas.RetakeUnsupported");
    }
    public static MusicAceIdea FromSong(MusicGenerationJob job,int index) {
        if(!MusicAceCatalog.IsAce(job.Variation)||job.AudioTask?.Enabled==true||job.Melody?.Enabled==true)throw new InvalidDataException("Music.AceIdeas.NoSong");
        var variant=job.Variants[index];using var document=JsonDocument.Parse(variant.ExecutionReceipt??"{}");
        if(!document.RootElement.TryGetProperty("effective",out var e)||!e.TryGetProperty("audio_codes",out var codes)||string.IsNullOrWhiteSpace(codes.GetString()))
            throw new InvalidDataException("Music.AceIdeas.NoSong");
        string Text(string name,string fallback="")=>e.TryGetProperty(name,out var p)&&p.ValueKind==JsonValueKind.String?p.GetString()??fallback:fallback;
        string ResolvedText(string name,string fallback="") {
            var value=Text(name);return string.IsNullOrWhiteSpace(value)?Text("cot_"+name,fallback):value;
        }
        double? Number(string name)=>e.TryGetProperty(name,out var p)&&p.ValueKind==JsonValueKind.Number&&p.TryGetDouble(out var value)&&double.IsFinite(value)?value:null;
        var bpm=Number("bpm") is >0 and <=300?Number("bpm"):Number("cot_bpm");
        var duration=Number("duration") is >0?Number("duration"):Number("cot_duration");
        var snapshot=MusicProjectSnapshot.FromJob(job) with {AceIdeas=new(),Scores=new()};
        var data=new MusicAceIdeaData(codes.GetString()!,ResolvedText("caption",MusicLoraCatalog.EffectiveStyle(job.Style,job.Loras)),
            ResolvedText("lyrics",job.Lyrics),bpm is >0 and <=300?(int)bpm.Value:null,ResolvedText("keyscale"),ResolvedText("timesignature"),ResolvedText("vocal_language","unknown"),
            duration is >0?duration.Value:
                System.Text.RegularExpressions.Regex.Matches(codes.GetString()!,@"<\|audio_code_\d+\|>").Count/5d);
        var idea=new MusicAceIdea {Name=string.IsNullOrWhiteSpace(job.Title)?Path.GetFileNameWithoutExtension(variant.ResultPath):job.Title,Origin="song",Input=snapshot,Style=job.Style,
            Data=data,BaseSoundSeed=variant.SoundSeed,SourceJob=job.Id,SourceVariant=index,Hash=MusicAceIdea.Stamp(data.Codes),
            InputHash=MusicAceIdea.Bind(job.Style,job.Lyrics,job.Expert,job.Loras,job.DurationAutomatic?null:job.DurationSeconds),SourceRevision=job.RuntimeRevision,ModelRevision=job.ModelRevision,
            Seed=variant.AceIdea?.Idea.Seed??variant.LanguageSeed};
        idea.Validate();return idea;
    }
}
