using System.IO;
using System.Text.RegularExpressions;

namespace AIHub.Services.MusicLora;

public sealed record MusicLoraChoice(string Id, double Strength) { public bool Enabled { get; init; } = true; }
public sealed record MusicLoraUse(string Id, string Revision, double Strength);
public sealed record MusicLoraSelection
{
    public bool Enabled { get; init; } = true;
    public Dictionary<string, MusicLoraChoice[]> ByModel { get; init; } = [];
    public MusicLoraChoice[] Choices(string variation) => (ByModel.GetValueOrDefault(variation) ?? []).Where(c => c.Enabled).ToArray();
    public MusicLoraSelection Snapshot() => this with { ByModel = ByModel.ToDictionary(p => p.Key, p => p.Value.ToArray()) };
    public void Validate()
    {
        if (ByModel is null || ByModel.Count > 30) throw new InvalidDataException("Music.Lora.Invalid");
        foreach (var (model, choices) in ByModel)
            if (model.Length > 100 || choices is null || choices.Length > 32 || choices.Any(c => c is null
                || !Regex.IsMatch(c.Id, @"\A[a-z0-9][a-z0-9-]{0,79}\z") || !double.IsFinite(c.Strength) || c.Strength is < 0 or > 4)
                || choices.DistinctBy(c => c.Id).Count() != choices.Length) throw new InvalidDataException("Music.Lora.Invalid");
    }
}
