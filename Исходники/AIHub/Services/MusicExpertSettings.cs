using System.IO;
using System.Text.Json.Serialization;

namespace AIHub.Services;

public sealed record ExpertParameter(string Key, string Group, double Default, double Minimum, double Maximum,
    double Step = 1, string[]? Choices = null);

/// <summary>Versioned capabilities for an engine; no executable commands or filesystem values.</summary>
public static class MusicExpertCatalog
{
    public const string Model = "YuE2";
    public const int ContractVersion = 1;
    public static IReadOnlyList<ExpertParameter> Parameters { get; } = Build();
    private static ExpertParameter[] Build()
    {
        var result = new List<ExpertParameter> {
            new("cot", "Plan", 0, 0, 2, Choices: ["Full", "Melody", "Off"]),
            new("steps", "Sound", 32, 1, 256),
            new("cfg_scale", "Sound", -1, -1, 20, .01),
            new("peak_clip", "Sound", 10, 0, 999),
            new("lm_seed", "Random", -1, -1, int.MaxValue),
            new("seed", "Random", -1, -1, int.MaxValue) };
        foreach (var (prefix, group, temperature, probability, candidates, penalty, window, minimum, maximum) in new[] {
            ("abc_sampling", "PlanSampling", .7, .9, 30, 1.005, 100, 32, 4096),
            ("semantic_sampling", "Sequence", 1.0, .95, 100, 1.2, 50, 200, 9000) })
        {
            result.Add(new(prefix + ".temperature", group, temperature, 0, 5, .01));
            result.Add(new(prefix + ".top_p", group, probability, .001, 1, .001));
            result.Add(new(prefix + ".top_k", group, candidates, 1, 10000));
            result.Add(new(prefix + ".repetition_penalty", group, penalty, .001, 5, .001));
            result.Add(new(prefix + ".penalty_window", group, window, 1, 100));
            result.Add(new(prefix + ".min_tokens", group, minimum, 0, MusicTextBudget.FillLimit));
            result.Add(new(prefix + ".max_tokens", group, maximum, 1, MusicTextBudget.FillLimit));
        }
        return result.ToArray();
    }
}

public sealed record MusicExpertSettings
{
    [JsonRequired] public Dictionary<string, double> Values { get; init; } = MusicExpertCatalog.Parameters.ToDictionary(p => p.Key, p => p.Default);
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ModelTuningState? Tuning { get; init; }
    public double Get(string key) => Values[key];
    public int Integer(string key) => checked((int)Get(key));
    [JsonIgnore] public string Cot => new[] { "full", "melody", "off" }[Integer("cot")];
    [JsonIgnore] public int PlanLimit => Cot == "off" ? 0 : Integer("abc_sampling.max_tokens");
    public int SequenceLimit(int duration) => Math.Min(Integer("semantic_sampling.max_tokens"), checked(duration * 25));
    [JsonIgnore] public double Guidance => Get("cfg_scale") < 0 ? Cot == "off" ? 1.01 : 1 : Get("cfg_scale");
    [JsonIgnore] public string Instruction => Cot switch {
        "melody" => "Generate a melody-only ABC transcription without chord symbols, then generate music with codec tokens from the given conditions.",
        "off" => "Generate music with codec tokens from the given conditions.",
        _ => MusicTextBudget.DefaultInstruction };
    public MusicExpertSettings Snapshot() => this with { Values = new(Values, StringComparer.Ordinal), Tuning = Tuning?.Snapshot() };
    public bool SameAs(MusicExpertSettings other) => Values.Count == other.Values.Count && Values.All(p => other.Values.TryGetValue(p.Key, out var v) && v == p.Value);
    public void Validate()
    {
        Tuning?.Validate();
        if (Values is null) throw new InvalidDataException("Missing parameter values.");
        var unknown = Values.Keys.Except(MusicExpertCatalog.Parameters.Select(p => p.Key)).ToArray();
        if (unknown.Length != 0) throw new InvalidDataException("Unknown parameters: " + string.Join(", ", unknown));
        foreach (var p in MusicExpertCatalog.Parameters)
        {
            if (!Values.TryGetValue(p.Key, out var v)) throw new InvalidDataException("Missing parameter: " + p.Key);
            if (!double.IsFinite(v) || v < p.Minimum || v > p.Maximum || p.Step == 1 && v != Math.Truncate(v))
                throw new InvalidDataException($"Invalid {p.Key}: {v}; range {p.Minimum}…{p.Maximum}.");
        }
        foreach (var prefix in new[] { "abc_sampling", "semantic_sampling" })
            if (Get(prefix + ".min_tokens") > Get(prefix + ".max_tokens"))
                throw new InvalidDataException(prefix + ": min_tokens exceeds max_tokens.");
        // -1 is automatic. Fractional negative guidance values have no distinct native meaning.
        if (Get("cfg_scale") < 0 && Get("cfg_scale") != -1) throw new InvalidDataException("cfg_scale: use -1 for automatic.");
    }
    public object Sampling(string prefix, int maximum) => new {
        temperature = Get(prefix + ".temperature"), top_p = Get(prefix + ".top_p"), top_k = Integer(prefix + ".top_k"),
        repetition_penalty = Get(prefix + ".repetition_penalty"), penalty_window = Integer(prefix + ".penalty_window"),
        min_tokens = Math.Min(Integer(prefix + ".min_tokens"), maximum), max_tokens = maximum };
}
