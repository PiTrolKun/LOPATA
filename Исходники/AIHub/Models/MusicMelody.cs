using System.IO;

namespace AIHub.Models;

public sealed record MusicMelodyNote(int Pitch, int Start, int Length, int Velocity = 90)
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
}

/// <summary>One explicitly chosen monophonic melody; quarter note = 480 ticks.</summary>
public sealed record MusicMelody
{
    public const int Ticks = 480, Quantum = 60;
    public bool Enabled { get; init; }
    public int Bpm { get; init; } = 120;
    public int Numerator { get; init; } = 4;
    public int Denominator { get; init; } = 4;
    public MusicMelodyNote[] Notes { get; init; } = [];
    public string SourceName { get; init; } = "";
    public string[] ImportWarnings { get; init; } = [];
    public int EndTick => Notes.Length == 0 ? 0 : Notes.Max(n => n.Start + n.Length);
    public double Seconds => EndTick / (double)Ticks * 60 / Bpm;
    public MusicMelody Snapshot() => this with { Notes = Notes.Select(n => n with { }).ToArray(), ImportWarnings = ImportWarnings.ToArray() };
    public void Validate()
    {
        if (Notes is null || Notes.Length > 4096 || Notes.Any(n => n is null) || Bpm is < 40 or > 240 || Numerator is < 1 or > 12 || Denominator is not (2 or 4 or 8)
            || SourceName is null || SourceName.Length > 512 || ImportWarnings is null || ImportWarnings.Length > 32
            || ImportWarnings.Any(w => w is not ("ImportDetails" or "TempoFlattened" or "PolyphonyFlattened")))
            throw new InvalidDataException("Music.Midi.Invalid");
        var end = 0;
        foreach (var n in Notes.OrderBy(n => n.Start)) {
            if (n is null || !Guid.TryParseExact(n.Id, "N", out _) || n.Pitch is < 0 or > 127 || n.Velocity is < 1 or > 127
                || n.Start < end || n.Start % Quantum != 0 || n.Length < Quantum || n.Length % Quantum != 0 || n.Start > 1_000_000 || n.Length > 1_000_000)
                throw new InvalidDataException("Music.Midi.Overlap");
            end = checked(n.Start + n.Length);
        }
        if (Notes.Select(n => n.Id).Distinct().Count() != Notes.Length || Seconds > 360.001) throw new InvalidDataException("Music.Midi.Limit");
        if (Enabled && Notes.Length == 0) throw new InvalidDataException("Music.Midi.Empty");
    }
}
