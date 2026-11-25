using System.Text.Json.Serialization;

namespace AIHub.Services.SongClip;

internal sealed record SongClipDenoSettings(int Width = 1920, int Height = 1080)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Accelerate { get; init; }
}

internal static class SongClipDeno
{
    internal const string Mode = "deno";
    internal const string Title = "Deno RTX";
    internal const string Worker = "song_clip_deno.py";
    internal const string AcceleratedWorker = "song_clip_deno_sol.py";
    internal const string FinishWorker = "song_clip_deno_finish.py";
    internal static readonly SongClipDenoSettings[] Sizes = [new(1920, 1080), new(3840, 2160), new(1080, 1920), new(2160, 3840)];

    internal static SongClipDenoSettings Output(SongClipSettings settings) => settings.Deno ?? new();
    internal static bool Accelerated(SongClipAttempt attempt) =>
        attempt.Model == H3MusicRunner.ModelId && SongClipModes.IsDeno(attempt) && attempt.Settings.Deno?.Accelerate == true;
    internal static SongClipDenoSettings Intermediate(SongClipSettings settings) =>
        Output(settings) is { Width: var width, Height: var height } && width < height ? new(1080, 1920) : new();

    internal static SongClipMontageSettings Generation(SongClipSettings settings) =>
        Intermediate(settings).Width == 1080
            ? new() { Width = 576, Height = 1024, Steps = 30 }
            : new() { Width = 1024, Height = 576, Steps = 30 };
}
