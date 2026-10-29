using System.IO;

namespace AIHub.Services.VideoPreparation;

public sealed record VideoH3Artifact(string RelativePath, string Role, long Size, string Sha256);
public sealed record VideoH3Take(string Id, string ItemId, string SceneId, string Video, long Seed, double Seconds, string Parent);
public sealed record VideoH3Result(string Id, string Kind, string VideoRole, double Seconds, DateTimeOffset CreatedUtc, OutputModelUse[] Models);
public sealed class VideoH3Run
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string InputHash { get; set; } = "";
    public string? SourceRunId { get; set; }
    public bool Complete { get; set; }
    public double? GenerationSeconds { get; set; }
    public int RetryAttempts { get; set; }
    public List<VideoH3Artifact> Files { get; set; } = [];
    public List<VideoH3Take> Takes { get; set; } = [];
    public List<VideoH3Result> Results { get; set; } = [];
    internal static bool SafeRelative(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 240) return false;
        try { _ = PythonWheelExtractor.RelativeDestination(name, wheelLayout: false); return true; }
        catch (InvalidDataException) { return false; }
    }
    internal static void Validate(VideoPreparationProject project, IReadOnlyList<VideoH3Run> runs)
    {
        if (runs is null || runs.Count > 1000 || runs.Any(r => r is null) || runs.Select(r => r.Id).Distinct().Count() != runs.Count) Invalid();
        foreach (var run in runs)
        {
            if (!Guid.TryParseExact(run.Id, "N", out _) || run.InputHash is null || run.InputHash.Length != 64 || !run.InputHash.All(char.IsAsciiHexDigit)
                || run.SourceRunId is { } source && (!Guid.TryParseExact(source, "N", out _) || source == run.Id || !runs.Any(r => r.Id == source))
                || run.GenerationSeconds is { } elapsed && (!double.IsFinite(elapsed) || elapsed < 0)
                || run.RetryAttempts is < 0 or > 5 || run.Files is null || run.Takes is null || run.Results is null
                || run.Files.Any(f => f is null) || run.Takes.Any(t => t is null) || run.Results.Any(r => r is null)
                || run.Files.Count > 100000 || run.Takes.Count > 20000 || run.Results.Count > 20000
                || run.Files.Select(f => f.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != run.Files.Count
                || run.Files.Select(f => f.Role).Distinct().Count() != run.Files.Count
                || run.Takes.Select(t => t.Id).Distinct().Count() != run.Takes.Count
                || run.Results.Select(r => r.Id).Distinct().Count() != run.Results.Count) Invalid();
            foreach (var file in run.Files)
                if (!SafeRelative(file.RelativePath) || file.Role is null || !file.Role.StartsWith("h3-run/" + run.Id + "/", StringComparison.Ordinal)
                    || file.Size <= 0 || file.Sha256 is null || file.Sha256.Length != 64 || !file.Sha256.All(char.IsAsciiHexDigit)
                    || project.Attachments.Count(a => a.Role == file.Role) != 1) Invalid();
            foreach (var take in run.Takes)
                if (string.IsNullOrWhiteSpace(take.Id) || !SafeRelative(take.Video) || !double.IsFinite(take.Seconds) || take.Seconds <= 0
                    || !run.Files.Any(f => f.RelativePath == take.Video)) Invalid();
            foreach (var result in run.Results)
                if (!Guid.TryParseExact(result.Id, "N", out _) || result.Kind is not ("fragment" or "whole")
                    || !double.IsFinite(result.Seconds) || result.Seconds <= 0 || result.CreatedUtc == default
                    || !run.Files.Any(f => f.Role == result.VideoRole) || result.Models is null || result.Models.Length == 0
                    || result.Models.Any(m => m is null)) Invalid();
        }
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn] private static void Invalid() => throw new InvalidDataException("VideoPrep.InvalidArchive");
}
