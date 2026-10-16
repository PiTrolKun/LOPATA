namespace AIHub.Services;

/// <summary>Editable analysis budgets and fixed structural contracts, with their original defaults.</summary>
public static class MusicRhymeAnalysisSettings
{
    public static IReadOnlyList<MusicRhymeSetting> Items { get; } = [
        MusicRhymeSetting.Int("analysis.max_fragments", "analysis", 3, 1, 16),
        MusicRhymeSetting.Number("analysis.line_share", "analysis", .2, .01, 1),
        MusicRhymeSetting.Int("analysis.minimum_lines", "analysis", 2, 1, 64),
        MusicRhymeSetting.Int("analysis.max_fragment_lines", "analysis", 4, 2, 4),
        MusicRhymeSetting.Int("analysis.variation_fragments", "analysis", 1, 1, 16),
        MusicRhymeSetting.Toggle("analysis.first_over_budget", "analysis", true),
        MusicRhymeSetting.Int("analysis.length_difference", "analysis", 3, 1, 16),
        MusicRhymeSetting.Int("analysis.reference_lines", "analysis", 3, 2, 32),
        MusicRhymeSetting.Number("analysis.reference_share", "analysis", .6, .1, 1),
        MusicRhymeSetting.Int("analysis.alternation_tolerance", "analysis", 1, 0, 8),
        MusicRhymeSetting.Int("analysis.alternation_min_lines", "analysis", 4, 4, 64),
        MusicRhymeSetting.Int("analysis.strong_key_units", "analysis", 2, 1, 8),
        MusicRhymeSetting.Info("analysis.scheme_block_lines", "analysis", 4),
        MusicRhymeSetting.Info("analysis.rhyme_schemes", "analysis", 3),
        MusicRhymeSetting.Info("analysis.fragment_integrity", "analysis", 1)
    ];
}
