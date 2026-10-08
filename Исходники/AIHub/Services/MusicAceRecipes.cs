namespace AIHub.Services;

/// <summary>Partial, model-scoped experiments. Never replace musical inputs or hardware policy.</summary>
public static class MusicAceRecipes
{
    private const string Docs = "https://github.com/ace-step/ACE-Step-1.5/blob/" + MusicAceCatalog.SourceRevision + "/docs/en/";
    public static IReadOnlyList<MusicTuningRecipe> All { get; } = [
        Recipe("ACE.Restrained", Docs + "INFERENCE.md", new() { ["lm_temperature"] = .70 },
            "Lower end of the official LM temperature range. Only temperature changes; not acoustically validated in LOPATA."),
        Recipe("ACE.Free", Docs + "INFERENCE.md", new() { ["lm_temperature"] = 1, ["lm_top_p"] = .95 },
            "LOPATA selection within the documented exploration ranges; not a complete published recipe or validated quality improvement."),
        Recipe("ACE.Preserve", Docs + "INFERENCE.md", new() { ["use_cot_caption"] = 0, ["use_cot_language"] = 0,
            ["thinking"] = 1, ["use_cot_metas"] = 1 },
            "LOPATA composition of documented controls. Preserve caption and selected language; keep LM audio planning and metadata completion."),
        Recipe("ACE.DcwThink", Docs + "DCW.md", new() { ["thinking"] = 1, ["dcw_enabled"] = 1,
            ["dcw_scaler"] = .02, ["dcw_high_scaler"] = .06 },
            "Official Gradio Think-mode DCW strengths. Explicitly enable double/haar; musical benefit in LOPATA remains untested.",
            new() { ["dcw_mode"] = "double", ["dcw_wavelet"] = "haar" }),
        Recipe("ACE.DcwLow", Docs + "DCW.md", new() { ["dcw_enabled"] = 1, ["dcw_scaler"] = .02 },
            "Official conservative low-band candidate. High scaler is inactive in low mode and remains unchanged.",
            new() { ["dcw_mode"] = "low", ["dcw_wavelet"] = "haar" }),
        Recipe("ACE.Heun", "https://github.com/FurkanGozukara/ACE-Step_Premium/blob/main/README.md",
            new() { ["infer_method"] = 0, ["sampler_mode"] = 1, ["inference_steps"] = 8 },
            "Community XL integration selects Heun. LOPATA adaptation uses ODE and 8 steps, clearing custom timesteps; speed and quality claims are not transferred.",
            new() { ["timesteps"] = "" }),
        Recipe("ACE.Community", "https://www.reddit.com/r/StableDiffusion/comments/1qvufdf/how_to_turn_acestep_15_into_a_suno_45_killer/",
            new() { ["lm_temperature"] = .80, ["lm_cfg_scale"] = 2.5, ["thinking"] = 1 },
            "Partial adaptation of UnfortunateHurricane's ordinary Turbo audio example (18 steps, explicit musical conditions). Only LM temperature, CFG and Think transfer; preserve XL synthesis and user inputs. Not validated on XL.") ];

    private static MusicTuningRecipe Recipe(string id, string source, Dictionary<string, double> values, string adaptation,
        Dictionary<string, string>? text = null) => new(id, source, values, adaptation) {
            Variation = MusicAceCatalog.Variation, TextValues = text ?? new() };
}
