using System.IO;
using System.Text.Json;

namespace AIHub.Services;

public enum MusicAudioTaskMode { NewSong, Cover, ReplaceVoice, ReplaceMusic, Repaint, Restore }
public enum MusicReferenceRole { Sound, Voice }
public sealed record MusicAudioInput(string Path)
{
    public string Name { get; init; } = "";
    public double Start { get; init; }
    public double? End { get; init; }
    public MusicReferenceRole Role { get; init; }
    public bool Enabled { get; init; } = true;
    public string? Sha256 { get; init; }
    public string? OriginSha256 { get; init; }
    public double? OriginStart { get; init; }
    public double? OriginEnd { get; init; }
    public void Validate(bool staged = false)
    {
        if (string.IsNullOrWhiteSpace(Path) || !System.IO.Path.IsPathFullyQualified(Path) || !double.IsFinite(Start) || Start < 0
            || End is { } end && (!double.IsFinite(end) || end <= Start) || !Enum.IsDefined(Role)
            || staged && (Sha256?.Length != 64 || !Sha256.All(Uri.IsHexDigit)))
            throw new InvalidDataException("Music.Transform.InvalidInput");
    }
}

/// <summary>Project intention; inputs in a job are immutable, hashed, owned copies.</summary>
public sealed record MusicAudioTask
{
    public bool Enabled { get; init; }
    public MusicAudioTaskMode Mode { get; init; }
    public MusicAudioInput? Source { get; init; }
    public MusicAudioInput[] References { get; init; } = [];
    public bool KeepHarmony { get; init; }
    public double SourceStrength { get; init; } = .65;
    public double EditStart { get; init; }
    public double EditEnd { get; init; } = 10;
    public double Crossfade { get; init; } = .08;
    public bool ReduceNoise { get; init; } = true;
    public bool Normalize { get; init; }
    public string SourceLyrics { get; init; } = "";
    public bool NeedsSource => Mode != MusicAudioTaskMode.NewSong;
    public bool UsesGenerator => Mode != MusicAudioTaskMode.Restore;
    public bool NeedsSeparation => Mode is MusicAudioTaskMode.ReplaceVoice or MusicAudioTaskMode.ReplaceMusic;
    public MusicAudioTask Snapshot() => this with { References = References.Select(r => r with { }).ToArray(), Source = Source is null ? null : Source with { } };
    public void Validate(string? variation = null, bool staged = false)
    {
        if (SourceLyrics is null || !Enum.IsDefined(Mode) || References is null || References.Any(r => r is null)
            || !double.IsFinite(SourceStrength) || SourceStrength is < 0 or > 1
            || !double.IsFinite(EditStart) || !double.IsFinite(EditEnd) || EditStart < 0 || EditEnd <= EditStart
            || !double.IsFinite(Crossfade) || Crossfade is < 0 or > 2) throw new InvalidDataException("Music.Transform.InvalidInput");
        if (!Enabled) return;
        if (NeedsSource && Source is null || Mode == MusicAudioTaskMode.NewSong && !References.Any(r => r.Enabled))
            throw new InvalidDataException("Music.Transform.MissingInput");
        if (NeedsSource) Source?.Validate(staged);
        var references = variation is null ? References.Where(r => UsesGenerator && r.Enabled) : MusicAudioInputPolicy.ActiveReferences(this, variation);
        foreach (var item in references) item.Validate(staged);
        if (variation == MusicStudioRuntime.Variation && references.Any(r => r.Role == MusicReferenceRole.Voice))
            throw new InvalidDataException("Music.Transform.StudioVoiceReference");
        if (Mode != MusicAudioTaskMode.Restore && variation is not null && variation != MusicAceCatalog.Variation && variation != MusicStudioRuntime.Variation)
            throw new InvalidDataException("Music.Transform.Unsupported");
    }
    public object PublicReceipt() => new { operation = Mode.ToString(), SourceStrength, KeepHarmony, EditStart, EditEnd, Crossfade,
        ReduceNoise, Normalize, source = Source is null ? null : Identity(Source), references = References.Where(r => r.Enabled).Select(Identity).ToArray() };
    private static object Identity(MusicAudioInput input) => new { Name = System.IO.Path.GetFileName(input.Name), input.Sha256, input.OriginSha256, input.OriginStart, input.OriginEnd, input.Start, input.End, role = input.Role.ToString() };
}

public sealed record MusicAudioTaskReceipt
{
    public string? ScorePath { get; init; }
    public string? ScoreHash { get; init; }
    public string? RenderPath { get; init; }
    public string? RenderHash { get; init; }
    public string? OutputPath { get; init; }
    public string? OutputHash { get; init; }
    public string? Detail { get; init; }
    public MusicStemReceipt? OriginalStems { get; init; }
    public MusicStemReceipt? RenderedStems { get; init; }
    public string? FittedLayerPath { get; init; }
    public string? FittedLayerHash { get; init; }
    public MusicLayerFitSummary? Alignment { get; init; }
}
