using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.MusicLora;

namespace AIHub.Models;

public sealed record MusicScoreVersion
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string? Parent { get; init; }
    public string Name { get; init; } = "";
    public string Origin { get; init; } = "import";
    public string Abc { get; init; } = "";
    public string Hash { get; init; } = "";
    public string InputHash { get; init; } = "";
    public string Cot { get; init; } = "full";
    public DateTimeOffset Created { get; init; } = DateTimeOffset.Now;
    public int? Seed { get; init; }
    public string? RuntimeRevision { get; init; }
    public string? RuntimePack { get; init; }
    public string? ModelRevision {get;init;}
    public string? WorkId { get; init; }
    public int? WorkIndex { get; init; }
    public string Draft { get; init; } = "";
    public string? Error {get;init;}
    public void Validate() {
        if (!Guid.TryParseExact(Id,"N",out _) || Parent is not null && !Guid.TryParseExact(Parent,"N",out _)
            || Name is null || Name.Length > 300 || Abc is null || Encoding.UTF8.GetByteCount(Abc) > 200000
            || Hash != Stamp(Abc) || InputHash.Length != 64 || Cot is not ("full" or "melody")
            || Draft is null || Encoding.UTF8.GetByteCount(Draft) > 200000 || Error?.Length>4096) throw new InvalidDataException("Music.Score.Invalid");
    }
    public static string Stamp(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string Bind(string style, string lyrics, string cot, MusicLoraUse[] loras) => Stamp(JsonSerializer.Serialize(new {
        Style = MusicLoraCatalog.EffectiveStyle(style,loras), Lyrics = lyrics, Cot = cot,
        Loras = loras.OrderBy(l=>l.Id,StringComparer.Ordinal).ToArray() }));
}
public sealed record MusicScoreWorkspace
{
    public MusicScoreVersion[] Versions { get; init; } = [];
    public string[] Selected { get; init; } = [];
    public bool Enabled { get; init; }
    public bool Keep { get; init; }
    public bool FollowSections { get; init; }
    public bool LyricTiming { get; init; }
    public string? PendingWorkId { get; init; }
    public MusicScoreWorkspace Snapshot() => this with { Versions = Versions.ToArray(), Selected = Selected.ToArray() };
    public void Validate() {
        if (Versions is null || Selected is null || Versions.Any(v=>v is null) || Versions.Length > 128 || Selected.Length > 8
            || Versions.Select(v=>v.Id).Distinct().Count()!=Versions.Length || Selected.Distinct().Count()!=Selected.Length)
            throw new InvalidDataException("Music.Score.Invalid");
        foreach(var version in Versions) version.Validate();
        if(PendingWorkId is not null&&!Guid.TryParseExact(PendingWorkId,"N",out _))throw new InvalidDataException("Music.Score.Invalid");
        if(Selected.Any(id=>Versions.All(v=>v.Id!=id)) || Enabled && Selected.Length==0) throw new InvalidDataException("Music.Score.Invalid");
    }
}

public sealed record MusicScoreNote(long Start, long Length, int Pitch);
public sealed record MusicScoreBar(long Start,long Length,string Meter,string Key,string Section,bool Editable);
public sealed record MusicScoreSection(string Name,int Bar,int Bars);
public sealed record MusicScoreChord(long Start,string Name);
public sealed record MusicScoreVoices(MusicScoreNote[] Vocal,MusicScoreNote[] Ins);
public sealed record MusicScoreSheet(long Unit,long PerQuarter,int Bpm,double Seconds,long Total,
    MusicScoreBar[] Bars,MusicScoreSection[] Sections,MusicScoreVoices Notes,MusicScoreChord[] Chords);
public sealed record MusicScoreInspection(string Abc,MusicScoreSheet Sheet);
