namespace AIHub.Services;

public static class MusicRhymeViewSettings
{
    public static IReadOnlyList<MusicRhymeSetting> Items { get; } = [
        MusicRhymeSetting.Int("base.source_limit", "base", 200000, 100, 200000),
        MusicRhymeSetting.Int("history.steps_limit", "history", 500, 1, 500),
        MusicRhymeSetting.Int("history.column_limit", "history", 400000, 100, 400000),
        MusicRhymeSetting.Int("history.undo_limit", "history", 1000, 1, 10000),
        MusicRhymeSetting.Int("history.summary_limit", "history", 3, 1, 32),
        MusicRhymeSetting.Int("history.summary_height", "history", 100, 40, 600),
        MusicRhymeSetting.Int("history.details_height", "history", 180, 80, 1000),
        MusicRhymeSetting.Int("history.reanalysis_delay", "history", 650, 100, 5000),
        MusicRhymeSetting.Int("history.pulse_delay", "history", 600, 100, 5000),
        MusicRhymeSetting.Info("history.saved_candidates", "history", 1000),
        MusicRhymeSetting.Info("history.saved_changes", "history", 32)
    ];
}
