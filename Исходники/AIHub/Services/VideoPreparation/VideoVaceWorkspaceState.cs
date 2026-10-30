using System.IO;

namespace AIHub.Services.VideoPreparation;

public sealed class VideoVaceSettings
{
    public int Width { get; set; } = 832;
    public int Height { get; set; } = 480;
    public int Steps { get; set; } = 20;
    public long Seed { get; set; } = -1;
    public double TargetSeconds { get; set; }
    public int Frames { get; set; } = 81;
    public int ContextFrames { get; set; } = 9;
    public int RetryCount { get; set; } = 3;
    public string MemoryMode { get; set; } = "auto";
    public double VramReserveGb { get; set; } = 2;
    public int Crf { get; set; } = 19;
    public int OutputHeight { get; set; }
    public string ExportName { get; set; } = "video";
}

/// <summary>VACE has its own 16 fps settings and history; an H3 selection never becomes VACE state.</summary>
public sealed class VideoVaceWorkspaceState
{
    public const int Fps = 16;
    public const double Cfg = 5;
    public const double Shift = 16;
    public VideoVaceSettings Settings { get; set; } = new();
    public List<VideoVaceRun> Runs { get; set; } = [];
    public string SelectedResultId { get; set; } = "";

    public static VideoVaceWorkspaceState Ensure(VideoPreparationProject p) => (p.Generator ??= new()).Vace ??= new();
    public static VideoVaceWorkspaceState FromProject(VideoPreparationProject p) =>
        p.Generator?.Vace is { } existing ? VideoShots.Copy(existing) : new();

    public static void Validate(VideoVaceWorkspaceState? state)
    {
        if (state is null) return;
        if (state.Settings is not { } s || state.Runs is null || state.SelectedResultId is null) Invalid();
        s = state.Settings!;
        // The UI offers the two 480p orientations. Smaller multiples of 16 are reserved for physical probes.
        if (s.Width is < 128 or > 832 || s.Height is < 128 or > 832 || s.Width % 16 != 0 || s.Height % 16 != 0
            || s.Steps is < 1 or > 100 || s.Seed < -1 || !Range(s.TargetSeconds, 0, 86400)
            || !FrameGrid(s.Frames) || s.ContextFrames < 5 || s.ContextFrames > s.Frames - 4 || (s.ContextFrames - 1) % 4 != 0
            || s.RetryCount is < 1 or > 5 || s.MemoryMode is not ("auto" or "gpu" or "offload" or "cpu")
            || !Range(s.VramReserveGb, 0, 64) || s.Crf is < 0 or > 51 || s.OutputHeight is not (0 or 720 or 1080)
            || string.IsNullOrWhiteSpace(s.ExportName) || s.ExportName.Length > 180 || s.ExportName is "." or ".."
            || s.ExportName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || s.ExportName.Any(char.IsControl)) Invalid();
        VideoVaceRun.Validate(state.Runs!);
        if (state.SelectedResultId.Length > 0 && (!Guid.TryParseExact(state.SelectedResultId, "N", out _)
            || !state.Runs!.Any(r => r.Results.Any(result => result.Id == state.SelectedResultId)))) Invalid();
    }

    internal static bool FrameGrid(int frames) => frames is >= 5 and <= 81 && (frames - 1) % 4 == 0;
    private static bool Range(double value, double minimum, double maximum) => double.IsFinite(value) && value >= minimum && value <= maximum;
    [System.Diagnostics.CodeAnalysis.DoesNotReturn] private static void Invalid() => throw new InvalidDataException("VideoPrep.VaceSettingsInvalid");
}
