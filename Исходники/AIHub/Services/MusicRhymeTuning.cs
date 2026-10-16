using System.IO;

namespace AIHub.Services;

public enum MusicRhymeSettingKind { Integer, Number, Toggle, Information }

/// <summary>One bounded setting shared by persistence, the editor and execution.</summary>
public sealed record MusicRhymeSetting(string Id, string Group, double Default, double Minimum, double Maximum,
    MusicRhymeSettingKind Kind = MusicRhymeSettingKind.Integer, double Step = 1)
{
    public string LabelKey => "Music.Rhyme.Settings." + Id;
    public string HintKey => LabelKey + ".Hint";
    public static MusicRhymeSetting Int(string id, string group, int value, int minimum, int maximum) =>
        new(id, group, value, minimum, maximum);
    public static MusicRhymeSetting Number(string id, string group, double value, double minimum, double maximum, double step = .01) =>
        new(id, group, value, minimum, maximum, MusicRhymeSettingKind.Number, step);
    public static MusicRhymeSetting Toggle(string id, string group, bool value) =>
        new(id, group, value ? 1 : 0, 0, 1, MusicRhymeSettingKind.Toggle);
    public static MusicRhymeSetting Info(string id, string group, double value) =>
        new(id, group, value, value, value, MusicRhymeSettingKind.Information);
    public bool Accepts(double value) => double.IsFinite(value) && value >= Minimum && value <= Maximum
        && (Kind == MusicRhymeSettingKind.Number || value == Math.Truncate(value));
}

/// <summary>A request owns its snapshot; changing preferences cannot alter a running task.</summary>
public sealed record MusicRhymeTuning
{
    public Dictionary<string, double> Values { get; init; } = new(StringComparer.Ordinal);
    public double Number(string id) => Values.TryGetValue(id, out var value) ? value : MusicRhymeSettingCatalog.Find(id).Default;
    public int Integer(string id) => checked((int)Number(id));
    public bool Enabled(string id) => Number(id) != 0;
    public MusicRhymeTuning Snapshot() => new() { Values = new(Values, StringComparer.Ordinal) };
    public bool Equals(MusicRhymeTuning? other) => other is not null
        && MusicRhymeSettingCatalog.All.All(item => Number(item.Id) == other.Number(item.Id));
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in MusicRhymeSettingCatalog.All) hash.Add(Number(item.Id));
        return hash.ToHashCode();
    }
    public void Validate()
    {
        if (Values is null || Values.Count > 256) throw new InvalidDataException("Music.Rhyme.Settings.Invalid");
        foreach (var (id, value) in Values)
            if (!MusicRhymeSettingCatalog.Find(id).Accepts(value)) throw new InvalidDataException("Music.Rhyme.Settings.Invalid");
    }
}

public static class MusicRhymeSettingCatalog
{
    public static IReadOnlyList<MusicRhymeSetting> All { get; } = MusicRhymeAnalysisSettings.Items
        .Concat(MusicRhymeProgramSettings.Items).Concat(MusicRhymeModelSettings.Items).Concat(MusicRhymeViewSettings.Items).ToArray();
    public static MusicRhymeSetting Find(string id) => All.FirstOrDefault(item => item.Id == id)
        ?? throw new InvalidDataException("Music.Rhyme.Settings.Invalid");
}
