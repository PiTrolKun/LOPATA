using System.IO;

namespace AIHub.Services.VideoPreparation;

/// <summary>Requested H3 settings only. This document is never an inference receipt.</summary>
public sealed class VideoH3Settings
{
    public string RunMode { get; set; } = "automatic";
    public int RetryCount { get; set; } = 3;
    public int OutputHeight { get; set; }
    public double TargetSeconds { get; set; }
    public double ClipSeconds { get; set; } = 5;
    public string Aspect { get; set; } = "16:9";
    public int Width { get; set; } = 1344;
    public int Height { get; set; } = 768;
    public int Steps { get; set; } = 20;
    public long Seed { get; set; } = -1;
    public string AudioMode { get; set; } = "native";
    public string ReferenceSize { get; set; } = "match";
    public string Continuation { get; set; } = "auto";
    public int ContextFrames { get; set; } = 39;
    public int AudioContextFrames { get; set; }
    public string PinMode { get; set; } = "masked";
    public double MaskHold { get; set; }
    public int MaskRampFrames { get; set; }
    public string Sampler { get; set; } = "res_multistep";
    public string Scheduler { get; set; } = "simple";
    public int VideoShift { get; set; } = 12;
    public int AudioShift { get; set; } = 3;
    public string Attention { get; set; } = "pytorch";
    public bool Spectrum { get; set; }
    public string Turbo { get; set; } = "off";
    public string TurboFile { get; set; } = "";
    public double TurboStrength { get; set; } = 1;
    public string ContentLoraFile { get; set; } = "";
    public double ContentLoraStrength { get; set; } = 1;
    public string MemoryMode { get; set; } = "auto";
    public double VramReserveGb { get; set; } = 2;
    public double WindowSeconds { get; set; } = 1;
    public double OverlapSeconds { get; set; } = .25;
    public double DecodeSeconds { get; set; } = 5;
    public bool Upscale { get; set; }
    public double UpscaleMegapixels { get; set; } = 2;
    public double RefineAmount { get; set; } = .2;
    public int RefineSteps { get; set; } = 10;
    public bool PreserveAudio { get; set; } = true;
    public int Crf { get; set; } = 19;
    public string ExportName { get; set; } = "video";
}
public sealed class VideoH3PlanSettings
{
    public string? Prompt { get; set; }
    public double Seconds { get; set; }
    public long? Seed { get; set; }
    public string Operation { get; set; } = "auto";
    public List<string>? ReferenceIds { get; set; }
    public List<string> VideoReferenceRoles { get; set; } = [];
    public List<string> AudioReferenceRoles { get; set; } = [];
}
public sealed class VideoH3WorkspaceState
{
    public List<VideoH3Run> Runs { get; set; } = [];
    public VideoH3Settings Settings { get; set; } = new();
    public string SelectedShotId { get; set; } = "";
    public Dictionary<string, VideoH3PlanSettings> Plans { get; set; } = [];
    public static VideoH3WorkspaceState Ensure(VideoPreparationProject p) => (p.Generator ??= new()).H3 ??= new();
    public static VideoH3WorkspaceState FromProject(VideoPreparationProject p)
    {
        if (p.Generator?.H3 is { } existing) return VideoShots.Copy(existing);
        var state = new VideoH3WorkspaceState();
        if (VideoGeneratorPlan.Selected(p)?.Settings is { ModelId: "minimax-h3" } selected)
        {
            state.Settings.Width = (selected.Width + 31) / 32 * 32;
            state.Settings.Height = (selected.Height + 31) / 32 * 32;
            state.Settings.Seed = selected.Seed;
            state.Settings.Aspect = Aspects.MinBy(a =>
            { var ratio = a.Split(':').Select(double.Parse).ToArray(); return Math.Abs(ratio[0] / ratio[1] - selected.Width / (double)selected.Height); })!;
        }
        return state;
    }
    public static readonly string[] Operations = ["auto", "new", "extend", "prepend", "bridge", "loop"];
    public static readonly string[] Aspects = ["16:9", "9:16", "1:1", "4:3", "3:4", "21:9"];
    public static int RawFrames(double seconds) => Math.Max(5, (int)Math.Ceiling((seconds * 24 - 5) / 17) * 17 + 5);
    public static void Resolution(VideoH3Settings s, double megapixels)
    {
        var ratio = s.Aspect.Split(':').Select(double.Parse).ToArray();
        s.Width = Math.Max(32, (int)Math.Round(Math.Sqrt(megapixels * 1024 * 1024 * ratio[0] / ratio[1]) / 32) * 32);
        s.Height = Math.Max(32, (int)Math.Round(s.Width * ratio[1] / ratio[0] / 32) * 32);
    }
    public static bool FitsInputs(VideoH3Settings s) =>
        s.Width % 32 == 0 && s.Height % 32 == 0 && s.OverlapSeconds < s.WindowSeconds
        && (s.PinMode == "guide" || s.ContextFrames is 39 or 90 or 141);
    public static void Validate(VideoH3WorkspaceState? state)
    {
        if (state is null) return;
        if (state.Settings is not { } s || state.Plans is null || state.Plans.Count > 16384 || !OptionalId(state.SelectedShotId)) Invalid();
        s = state.Settings!;
        if (!One(s.RunMode, "review", "automatic") || !Range(s.TargetSeconds, 0, 86400) || !Range(s.ClipSeconds, 1, 15)
            || !Aspects.Contains(s.Aspect) || s.Width is < 128 or > 8192 || s.Height is < 128 or > 8192 || !FitsInputs(s)
            || s.Steps is < 1 or > 100 || s.Seed < -1 || !One(s.AudioMode, "native", "mute") || !One(s.ReferenceSize, "match", "max")
            || !Operations.Contains(s.Continuation) || s.ContextFrames is not (5 or 22 or 39 or 56 or 90 or 141)
            || s.AudioContextFrames is < 0 or > 240 || !One(s.PinMode, "masked", "guide", "both") || !Range(s.MaskHold, 0, 1)
            || s.MaskRampFrames is < 0 or > 256 || !One(s.Sampler, "res_multistep", "euler") || !One(s.Scheduler, "simple", "beta")
            || s.VideoShift is < 1 or > 40 || s.AudioShift is < 1 or > 40 || !One(s.Attention, "pytorch", "sage")
            || !One(s.Turbo, "off", "normal", "larryvrh") || !FileName(s.TurboFile) || !Range(s.TurboStrength, 0, 1)
            || !FileName(s.ContentLoraFile) || !Range(s.ContentLoraStrength, 0, 2) || !One(s.MemoryMode, "auto", "gpu", "offload")
            || !Range(s.VramReserveGb, 0, 64) || !Range(s.WindowSeconds, 1, 15) || !Range(s.OverlapSeconds, 0, 14)
            || !Range(s.DecodeSeconds, 1, 15) || !Range(s.UpscaleMegapixels, .1, 8) || !Range(s.RefineAmount, 0, 1)
            || s.RefineSteps is < 1 or > 100 || s.Crf is < 0 or > 51 || s.RetryCount is < 1 or > 5
            || s.OutputHeight is not (0 or 720 or 1080 or 2160) || !FileName(s.ExportName) || s.ExportName.Length == 0) Invalid();
        foreach (var (id, v) in state.Plans!)
            if (!Guid.TryParseExact(id, "N", out _) || v is null || v.Prompt?.Length > 100000 || !Range(v.Seconds, 0, 86400)
                || v.Seed is < -1 || !Operations.Contains(v.Operation) || v.ReferenceIds is { } refs && (refs.Count > 512
                    || refs.Distinct().Count() != refs.Count || refs.Any(r => !Guid.TryParseExact(r, "N", out _)))
                || !Roles(v.VideoReferenceRoles) || !Roles(v.AudioReferenceRoles)) Invalid();
    }
    private static bool FileName(string? s) => s is not null && s.Length <= 180 && s.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && s is not "." and not "..";
    private static bool OptionalId(string? id) => id == "" || Guid.TryParseExact(id, "N", out _);
    private static bool Roles(List<string>? roles) => roles is not null && roles.Count <= 3 && roles.Distinct().Count() == roles.Count
        && roles.All(r => r is not null && r.Length <= 256 && (r.StartsWith("h3-reference/", StringComparison.Ordinal) || r.StartsWith("video-board/audio/", StringComparison.Ordinal)));
    private static bool One(string? s, params string[] options) => options.Contains(s);
    private static bool Range(double n, double min, double max) => double.IsFinite(n) && n >= min && n <= max;
    [System.Diagnostics.CodeAnalysis.DoesNotReturn] private static void Invalid() => throw new InvalidDataException("VideoPrep.H3SettingsInvalid");
}
