namespace AIHub.Services;

/// <summary>Duration ceilings for the selected inference contract, in seconds.</summary>
public static class MusicDurationPolicy
{
    public const int FallbackSeconds = 600;
    internal const int LegacySeconds = 360;
    internal const int FramesPerSecond = 25;
    private const int HeartMuLaFrames = 8192;
    private const int HeartMuLaFrameMilliseconds = 80;

    public static int MaximumManualSeconds(string variation, string? studioRevision = null) => variation switch
    {
        MusicStudioRuntime.Variation when (studioRevision ?? MusicStudioRuntime.SelectedProfile.Revision) == MusicStudioRuntime.Current.Revision
            => FallbackSeconds,
        MusicAceCatalog.Variation or MusicDiffRhythmCatalog.Variation => FallbackSeconds,
        MusicHeartMuLaCatalog.Variation => (HeartMuLaFrames - 1) * HeartMuLaFrameMilliseconds / 1000,
        _ => LegacySeconds
    };

    // For engines with EOS or a duration predictor this is a reservation, not a target length.
    public static int AutomaticSeconds(string variation, string? studioRevision = null) =>
        MaximumManualSeconds(variation, studioRevision);

    internal static int LegacyMaximumSeconds(string variation) =>
        MusicDiffRhythmCatalog.IsDiff(variation) ? MusicDiffRhythmCatalog.MaximumDuration : LegacySeconds;

    internal static int SequenceTokens(MusicYueRequest request)
    {
        if (!request.ExpandsStudioBudget ||
            request.EffectiveExpert.Integer("semantic_sampling.max_tokens") != MusicExpertCatalog.DefaultSequenceTokens)
            return request.EffectiveExpert.SequenceLimit(request.DurationSeconds);

        return checked(request.DurationSeconds * FramesPerSecond);
    }

    internal static int ScoreTokens(MusicYueRequest request)
    {
        var configured = request.Expert?.PlanLimit ?? request.PlanTokenLimit;
        return request.ExpandsStudioBudget && configured == MusicExpertCatalog.DefaultScoreTokens
            ? Math.Max(configured, checked(request.DurationSeconds * 15)) : configured;
    }
}

public sealed class MusicDurationContextException(int availableSeconds)
    : System.IO.IOException($"The lyrics and score leave room for {availableSeconds} seconds of music.")
{
    public int AvailableSeconds { get; } = availableSeconds;
}
