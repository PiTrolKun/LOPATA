namespace AIHub.Services;

/// <summary>Discovery cards, not a download manifest. Only explicitly connected variants may launch.</summary>
public sealed record MusicModelVariant(string Id, string LabelKey, bool Connected = false);
public sealed record MusicModelCandidate(string Id, string Name, string DescriptionKey, IReadOnlyList<MusicModelVariant> Variants);

public static class MusicModelSelectionCatalog
{
    public static IReadOnlyList<MusicModelCandidate> All { get; } = Array.AsReadOnly(new[]
    {
        Model("yue2", "YuE2", V("studio-q8", "StudioQ8", true)),
        Model("ace-step", "ACE-Step 1.5", V("xl-turbo", "AceXLTurbo4B", true)),
        Model("diffrhythm", "DiffRhythm 2", V("diff2", "DiffRhythm2", true)),
        Model("heartmula", "HeartMuLa", V("3b", "3B")),
        Model("songgeneration", "SongGeneration 2 / LeVo 2", V("base", "Base"), V("hybrid", "Hybrid")),
        Model("minimax", "MiniMax Music 3", V("base", "Base"), V("bf16", "BF16"), V("community", "Community"), V("hybrid", "Hybrid")),
        Model("mulacover", "MuLaCover", V("base", "Base")),
        Model("yingmusic", "YingMusic-Singer-Plus", V("base", "Base")),
        Model("vibe", "VIBE", V("base", "Base")),
        Model("midasheng", "MiDashengLM-Gen", V("base", "Base"))
    });

    public static bool CanOpen(string modelId, string variantId) => All.Any(model => model.Id == modelId &&
        model.Variants.Any(variant => variant.Id == variantId && variant.Connected));
    private static MusicModelVariant V(string id, string key, bool connected = false) => new(id, key, connected);
    private static MusicModelCandidate Model(string id, string name, params MusicModelVariant[] variants) =>
        new(id, name, "Music.Models.Description." + id, Array.AsReadOnly(variants));
}
