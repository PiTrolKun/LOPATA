using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIHub.Models;
using AIHub.Services.MusicLora;

namespace AIHub.Services.MusicAceIdeas;

public sealed record MusicAceIdeaData(string Codes,string Caption,string Lyrics,int? Bpm,string Keyscale,
    string Timesignature,string VocalLanguage,double Duration)
{
    private static readonly Regex Token=new(@"<\|audio_code_(\d{1,5})\|>",RegexOptions.CultureInvariant);
    public string[] Tokens() {
        if(Codes is null||Codes.Length>100000)throw new InvalidDataException("Music.AceIdeas.Invalid");
        var matches=Token.Matches(Codes);
        if(matches.Count is <5 or >1800||Token.Replace(Codes,"").Trim().Length!=0
            ||matches.Any(m=>int.Parse(m.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture)>63999))
            throw new InvalidDataException("Music.AceIdeas.Invalid");
        return matches.Select(m=>m.Value).ToArray();
    }
    public void Validate() {
        _=Tokens();
        if(Caption is null||Caption.Length>20000||Lyrics is null||Lyrics.Length>100000||Keyscale is null||Timesignature is null||VocalLanguage is null
            ||!double.IsFinite(Duration)||Duration is <1 or >360||Bpm is <1 or >300)
            throw new InvalidDataException("Music.AceIdeas.Invalid");
    }
}
public sealed record MusicAceIdea
{
    public string Id {get;init;}=Guid.NewGuid().ToString("N");
    public string Name {get;init;}="";
    public string Origin {get;init;}="generated";
    public DateTimeOffset Created {get;init;}=DateTimeOffset.Now;
    public int Seed {get;init;}
    public int? BaseSoundSeed {get;init;}
    public string? SourceJob {get;init;}
    public int? SourceVariant {get;init;}
    public string Style {get;init;}="";
    public MusicProjectSnapshot Input {get;init;}=new();
    public MusicAceIdeaData Data {get;init;}=new("","","",null,"","","",1);
    public string Hash {get;init;}="";
    public string InputHash {get;init;}="";
    public string SourceRevision {get;init;}=MusicAceCatalog.SourceRevision;
    public string ModelRevision {get;init;}=MusicAceCatalog.ModelRevision;
    public string RuntimeRevision {get;init;}=MusicAceCatalog.RuntimeRevision;
    public static string Stamp(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string Bind(string style,string lyrics,MusicExpertSettings expert,MusicLoraUse[] loras,int? duration)=>Stamp(JsonSerializer.Serialize(new {
        style,lyrics,duration,values=expert.Values.Where(p=>p.Key is not ("seed" or "retake_seed" or "retake_variance")).OrderBy(p=>p.Key,StringComparer.Ordinal).ToArray(),
        text=expert.TextValues.OrderBy(p=>p.Key,StringComparer.Ordinal).ToArray(),loras=loras.OrderBy(l=>l.Id,StringComparer.Ordinal).ToArray()
    }));
    public void Validate() {
        if(!Guid.TryParseExact(Id,"N",out _)||Name is null||Name.Length>300||Style is null||Input is null
            ||Input.AceIdeas is null||Input.AceIdeas.Ideas.Length!=0||Input.Variation!=MusicAceCatalog.Variation
            ||Seed<0||BaseSoundSeed<0||SourceRevision!=MusicAceCatalog.SourceRevision||ModelRevision!=MusicAceCatalog.ModelRevision
            ||RuntimeRevision!=MusicAceCatalog.RuntimeRevision||Data is null||Hash!=Stamp(Data.Codes)||InputHash is null||InputHash.Length!=64)
            throw new InvalidDataException("Music.AceIdeas.Invalid");
        Data.Validate();Input.Validate();
    }
}
public sealed record MusicAceIdeaUse(MusicAceIdea Idea,int? RetakeSeed=null,double Variance=0,bool Preview=false)
{
    public MusicAceIdeaData EffectiveData=>Preview?Idea.Data with {Codes=string.Concat(Idea.Data.Tokens().Take(100)),Duration=Math.Min(20,Idea.Data.Tokens().Length/5d)}:Idea.Data;
    public void Validate() {Idea.Validate();if(RetakeSeed<0||!double.IsFinite(Variance)||Variance is <0 or >1||Variance>0&&RetakeSeed is null)throw new InvalidDataException("Music.AceIdeas.Invalid");EffectiveData.Validate();}
}
public sealed record MusicAceIdeaWorkspace
{
    public MusicAceIdea[] Ideas {get;init;}=[];
    public string[] Selected {get;init;}=[];
    public bool Enabled {get;init;}
    public bool Retake {get;init;}
    public double Variance {get;init;}=.2;
    public string? PendingWorkId {get;init;}
    public string[] Errors {get;init;}=[];
    public MusicAceIdeaWorkspace Snapshot()=>this with {Ideas=Ideas.ToArray(),Selected=Selected.ToArray(),Errors=Errors.ToArray()};
    public void Validate() {
        if(Ideas is null||Selected is null||Errors is null||Errors.Length>8||Errors.Any(e=>e is null||e.Length>4096)||Ideas.Length>128||Selected.Length>8||Ideas.Any(i=>i is null)
            ||Ideas.Select(i=>i.Id).Distinct().Count()!=Ideas.Length||Selected.Distinct().Count()!=Selected.Length
            ||Selected.Any(id=>Ideas.All(i=>i.Id!=id))||Enabled&&Selected.Length==0||!double.IsFinite(Variance)||Variance is <0 or >1
            ||PendingWorkId is not null&&!Guid.TryParseExact(PendingWorkId,"N",out _))throw new InvalidDataException("Music.AceIdeas.Invalid");
        foreach(var idea in Ideas)idea.Validate();
    }
}
