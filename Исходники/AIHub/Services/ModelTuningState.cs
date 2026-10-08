using System.IO;

namespace AIHub.Services;

/// <summary>Display intent and provenance; effective numbers remain in the engine settings.</summary>
public sealed record ModelTuningState
{
    public string Profile { get; init; } = MusicTuningProfile.Id;
    public int Version { get; init; } = 1;
    public double? X { get; init; }
    public double? Y { get; init; }
    public string[] Pins { get; init; } = [];
    public string? SimplePreset { get; init; }
    public string? ExpertPreset { get; init; }
    public bool SimpleModified { get; init; }
    public bool ExpertModified { get; init; }
    public ModelTuningState Snapshot() => this with { Pins = [.. Pins] };
    public void Validate()
    {
        if (Profile is not (MusicTuningProfile.Id or MusicTuningProfile.Bf16Id or MusicTuningProfile.StudioId) || Version != 1 || X.HasValue != Y.HasValue ||
            X is { } x && (!double.IsFinite(x) || Math.Abs(x) > 1) ||
            Y is { } y && (!double.IsFinite(y) || Math.Abs(y) > 1))
            throw new InvalidDataException("Unsupported tuning profile or position.");
        if (Pins is null || Pins.Length > MusicExpertCatalog.Parameters.Count ||
            Pins.Distinct(StringComparer.Ordinal).Count() != Pins.Length ||
            Pins.Except(MusicExpertCatalog.Parameters.Select(p => p.Key)).Any())
            throw new InvalidDataException("Invalid manual parameter pins.");
        foreach (var name in new[] { SimplePreset, ExpertPreset })
            if (name is not null && (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl)))
                throw new InvalidDataException("Invalid tuning preset reference.");
    }
}

public sealed record ModelPresetRecipe(string Id, string Source, string Task, string Adaptation, string[] Fields)
{
    public int Version { get; init; } = 1;
    public bool Experimental { get; init; } = true;
    public void Validate()
    {
        if (Version != 1 || new[] { Id, Source, Task, Adaptation }.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 2048) ||
            Fields is null || Fields.Length > 20 || Fields.Distinct().Count() != Fields.Length ||
            Fields.Except(MusicExpertCatalog.Parameters.Select(p => p.Key)).Any())
            throw new InvalidDataException("Invalid recipe metadata.");
    }
}
