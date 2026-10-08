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
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ModelCircleState? Sound { get; init; }
    public ModelTuningState Snapshot() => this with { Pins = [.. Pins] };
    public void Validate()
    {
        if (Profile is not (MusicTuningProfile.Id or MusicTuningProfile.Bf16Id or MusicTuningProfile.StudioId or MusicAceTuningProfile.Id) || Version != 1 || X.HasValue != Y.HasValue ||
            X is { } x && (!double.IsFinite(x) || Math.Abs(x) > 1) ||
            Y is { } y && (!double.IsFinite(y) || Math.Abs(y) > 1))
            throw new InvalidDataException("Unsupported tuning profile or position.");
        var parameters = Profile == MusicAceTuningProfile.Id ? MusicAceCatalog.Parameters : MusicExpertCatalog.Parameters;
        if (Pins is null || Pins.Length > parameters.Count ||
            Pins.Distinct(StringComparer.Ordinal).Count() != Pins.Length ||
            Pins.Except(parameters.Select(p => p.Key)).Any())
            throw new InvalidDataException("Invalid manual parameter pins.");
        if (Sound is not null) {
            if (Profile != MusicAceTuningProfile.Id) throw new InvalidDataException("Sound circle belongs to ACE.");
            Sound.Validate();
        }
        foreach (var name in new[] { SimplePreset, ExpertPreset })
            if (name is not null && (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl)))
                throw new InvalidDataException("Invalid tuning preset reference.");
    }
}

public sealed record ModelCircleState
{
    public int Version { get; init; } = 1;
    public double X { get; init; }
    public double Y { get; init; }
    public void Validate()
    {
        if (Version != 1 || !double.IsFinite(X) || !double.IsFinite(Y) || Math.Abs(X) > 1 || Math.Abs(Y) > 1)
            throw new InvalidDataException("Invalid secondary circle state.");
    }
}

public sealed record ModelPresetRecipe(string Id, string Source, string Task, string Adaptation, string[] Fields)
{
    public int Version { get; init; } = 1;
    public bool Experimental { get; init; } = true;
    public void Validate(string variation = MusicComponentCatalog.ModelId)
    {
        var allowed = MusicAceCatalog.IsAce(variation) ? MusicAceCatalog.Parameters.Select(p => p.Key)
            .Concat(MusicAceCatalog.TextParameters.Select(p => p.Key)) : MusicExpertCatalog.Parameters.Select(p => p.Key);
        if (Version != 1 || new[] { Id, Source, Task, Adaptation }.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 2048) ||
            Fields is null || Fields.Length > 20 || Fields.Distinct().Count() != Fields.Length ||
            Fields.Except(allowed).Any())
            throw new InvalidDataException("Invalid recipe metadata.");
    }
}
