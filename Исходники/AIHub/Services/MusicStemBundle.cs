using System.IO;

namespace AIHub.Services;

public sealed record MusicStemReceipt(string VoicePcm, string MusicPcm, string VoiceHash, string MusicHash,
    string SourceHash, string Provider, string Device, double Gain)
{
    public string ModelRevision { get; init; } = MusicStemsCatalog.ModelRevision;
    public string ModelHash { get; init; } = MusicStemsCatalog.ModelHash;
    public string Runtime { get; init; } = "1.23.2";
}

public sealed record MusicStemBundle
{
    public MusicStemReceipt? Receipt { get; init; }
    public string? MusicHash { get; init; }
    public string? AdditionalMusicHash { get; init; }
    public string? TextHash { get; init; }
    public string? SourceTimingHash { get; init; }
    public bool Published { get; init; }
    public string? Error { get; init; }
    public static string MusicPath(MusicGenerationVariant variant, bool additional = false) =>
        Path.Combine(Path.GetDirectoryName(variant.ResultPath)!, "music" + Path.GetExtension(additional ? variant.AdditionalPath! : variant.ResultPath));
    public static string TextPath(MusicGenerationVariant variant) => Path.Combine(Path.GetDirectoryName(variant.ResultPath)!, "song.txt");
}

public sealed class MusicStemsException(Exception inner) : IOException("Music.Stems.Failed", inner);
