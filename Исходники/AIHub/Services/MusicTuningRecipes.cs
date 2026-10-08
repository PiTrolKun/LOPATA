namespace AIHub.Services;

public sealed record MusicTuningRecipe(string Id, string Source, IReadOnlyDictionary<string, double> Values, string Adaptation)
{
    public string? Variation { get; init; }
    public IReadOnlyDictionary<string, string> TextValues { get; init; } = new Dictionary<string, string>();
    public string[] Fields => [.. Values.Keys, .. TextValues.Keys];
    public bool Matches(MusicExpertSettings settings) => Values.All(p => settings.Get(p.Key) == p.Value) &&
        TextValues.All(p => settings.TextValues[p.Key] == p.Value);
    public MusicExpertSettings Apply(MusicExpertSettings current)
    {
        if ((Variation is null && MusicAceCatalog.IsAce(current.Variation)) || (Variation is not null && Variation != current.Variation))
            throw new System.IO.InvalidDataException("Recipe belongs to another model variation.");
        var next = current.Snapshot(); foreach (var pair in Values) next.Values[pair.Key] = pair.Value;
        foreach (var pair in TextValues) next.TextValues[pair.Key] = pair.Value;
        var state = MusicTuningProfile.State(next);
        next = next with { Tuning = state with { X = null, Y = null, Pins = state.Pins.Except(Values.Keys).ToArray(),
            Sound = Fields.Any(k => k.StartsWith("dcw_", StringComparison.Ordinal)) ? null : state.Sound,
            SimplePreset = Id, SimpleModified = false, ExpertModified = true } };
        next.Validate(); return next;
    }
    public ModelPresetRecipe Metadata => new(Id, Source, "Experimental parameter adaptation", Adaptation, Fields);
}

public static class MusicTuningRecipes
{
    private const string Studio = "https://github.com/vrgamegirl19/Yue2_Studio/blob/15f382047321165e51a626d75deba1483080021e/docs/settings.md";
    private const string Covers = "https://www.reddit.com/r/StableDiffusion/comments/1wkvpb1/creative_covers_with_yue2_examples_and_configs/";
    public static IReadOnlyList<MusicTuningRecipe> All { get; } = [
        Group("S1", "abc_sampling", .55, .82, 16), Group("S2", "abc_sampling", 1, .97, 64),
        Group("S3", "semantic_sampling", .8, .88, 50), Group("S4", "semantic_sampling", 1.2, .99, 200),
        Cover("R1", .8, 1.2), Cover("R2", .9, 2), Cover("R3", .95, 1.9) ];
    public static IReadOnlyList<MusicTuningRecipe> For(string variation) => MusicAceCatalog.IsAce(variation) ? MusicAceRecipes.All : All;
    public static MusicExpertSettings Ordinary(MusicExpertSettings current)
    {
        var next = MusicTuningProfile.ResetCircle(current);
        if (!MusicAceCatalog.IsAce(current.Variation) && !MusicTuningProfile.State(next).Pins.Contains("cfg_scale")) next.Values["cfg_scale"] = -1;
        return next with { Tuning = MusicTuningProfile.State(next) with { SimplePreset = "Ordinary", SimpleModified = false } };
    }
    private static MusicTuningRecipe Group(string id, string prefix, double t, double p, int k) => new(id, Studio,
        new Dictionary<string, double> { [prefix + ".temperature"] = t, [prefix + ".top_p"] = p, [prefix + ".top_k"] = k },
        "Studio Python sampling only; not acoustically validated on native Q8.");
    private static MusicTuningRecipe Cover(string id, double temperature, double guidance) => new(id, Covers,
        new Dictionary<string, double> { ["semantic_sampling.temperature"] = temperature, ["semantic_sampling.top_p"] = .95,
            ["semantic_sampling.top_k"] = 100, ["semantic_sampling.repetition_penalty"] = 1.2, ["cfg_scale"] = guidance },
        "English full-plan covers. Original: acoustic CFG 1, 64 DPM2/sgm_uniform steps and LoRA. Native applies semantic fields only; current synthesis, seeds and plan mode are preserved.");
}
