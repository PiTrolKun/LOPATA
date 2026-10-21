namespace AIHub.Services.MusicLora;

/// <summary>Non-blocking author constraints and shared-stage warnings; never rewrites a request.</summary>
internal static class MusicLoraUsageHints
{
    // Author documents these two immutable files as the same training in F32/BF16.
    // https://huggingface.co/Mothersuperior/YuE2-instrumental-cot-full-loras/tree/947f2f4b28978b2b6c3e316e6a87925c76bf3c4b
    private static readonly string[] InstrumentalWeights = [
        "2252d7071fbff0fde77c48369a8d4f3484f65a3bba01e5ee614411e22c426a96",
        "e408fd3148b75b1165f7ddbf63db575d83bb6402a0b5f876fcb767dbcb2c5414"];
    internal static string? Family(MusicLoraDefinition d) => d.Variation == MusicStudioRuntime.Variation
        && d.Files.Length == 1 && InstrumentalWeights.Contains(d.Files[0].Sha256, StringComparer.Ordinal) ? "instrumental-v3" : null;
    internal static IEnumerable<string> EquivalentWeights(MusicLoraDefinition d) => Family(d) is not null ? InstrumentalWeights : [];
    internal static string? AuthorHint(MusicLoraDefinition d) => Family(d) is not null ? "Music.Lora.InstrumentalHint" : null;
    internal static string[] Warnings(IReadOnlyList<MusicLoraUse> uses) => Warnings(uses.Select(u => (MusicLoraCatalog.Find(u.Id), u.Strength)));
    internal static string[] Warnings(IEnumerable<(MusicLoraDefinition Definition, double Strength)> uses)
    {
        var active = uses.Where(u => u.Strength > 0).ToArray(); var warnings = new List<string>();
        if (active.Count(u => u.Definition.Slots.GetValueOrDefault("ar") > 0) > 1
            && active.Sum(u => u.Definition.Slots.GetValueOrDefault("ar") * u.Strength) > 1)
            warnings.Add("Music.Lora.PlannerCombinationWarning");
        warnings.AddRange(active.Select(u => AuthorHint(u.Definition)).OfType<string>().Distinct());
        return warnings.ToArray();
    }
    internal static bool InvalidScore(string message) => message.Contains("the score the model wrote", StringComparison.OrdinalIgnoreCase)
        && message.Contains("cannot be sung", StringComparison.OrdinalIgnoreCase);
}
