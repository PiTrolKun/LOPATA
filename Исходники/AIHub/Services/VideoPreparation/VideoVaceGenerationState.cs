using System.IO;

namespace AIHub.Services.VideoPreparation;

public sealed record VideoVaceArtifact(string RelativePath, string Role, long Size, string Sha256);
public sealed record VideoVaceTake(string Id, string ItemId, string Video, long Seed, int Frames, int PrefixFrames, string Parent)
{
    // Video retains the complete native output, including the conditioned prefix. Assembly omits that prefix.
    public double Seconds => DeliveredSeconds;
    public double DeliveredSeconds => (Frames - PrefixFrames) / (double)VideoVaceWorkspaceState.Fps;
}
public sealed record VideoVaceResult(string Id, string VideoRole, double Seconds, DateTimeOffset CreatedUtc, OutputModelUse[] Models);

public sealed class VideoVaceRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string InputHash { get; set; } = "";
    public bool Complete { get; set; }
    public double GenerationSeconds { get; set; }
    public int RetryAttempts { get; set; }
    public List<VideoVaceArtifact> Files { get; set; } = [];
    public List<VideoVaceTake> Takes { get; set; } = [];
    public List<VideoVaceResult> Results { get; set; } = [];

    internal static bool SafeRelative(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 240) return false;
        try { _ = PythonWheelExtractor.RelativeDestination(name, wheelLayout: false); return true; }
        catch (InvalidDataException) { return false; }
    }

    internal static void Validate(IReadOnlyList<VideoVaceRun> runs)
    {
        if (runs is null || runs.Count > 1000 || runs.Any(r => r is null)
            || runs.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != runs.Count) Invalid();
        var resultIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var run in runs)
        {
            if (!Guid.TryParseExact(run.Id, "N", out _) || !Hash(run.InputHash) || !double.IsFinite(run.GenerationSeconds) || run.GenerationSeconds < 0
                || run.RetryAttempts is < 0 or > 5 || run.Files is null || run.Takes is null || run.Results is null
                || run.Files.Count > 100000 || run.Takes.Count > 20000 || run.Results.Count > 20000
                || run.Files.Any(f => f is null) || run.Takes.Any(t => t is null) || run.Results.Any(r => r is null)) Invalid();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var roles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in run.Files)
                if (!SafeRelative(file.RelativePath) || !paths.Add(file.RelativePath) || !SafeRelative(file.Role)
                    || !file.Role.StartsWith("vace-run/" + run.Id + "/", StringComparison.Ordinal) || !roles.Add(file.Role)
                    || file.Size <= 0 || !Hash(file.Sha256)) Invalid();
            var takes = new HashSet<string>(StringComparer.Ordinal); var videos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            VideoVaceTake? previous = null;
            foreach (var take in run.Takes)
            {
                if (!Token(take.Id) || !takes.Add(take.Id) || !Token(take.ItemId) || !SafeRelative(take.Video) || !videos.Add(take.Video)
                    || !paths.Contains(take.Video) || take.Seed < 0 || !VideoVaceWorkspaceState.FrameGrid(take.Frames)
                    || take.Parent != (previous?.Id ?? "") || (previous is null ? take.PrefixFrames != 0
                        : take.PrefixFrames < 5 || take.PrefixFrames > take.Frames - 4 || take.PrefixFrames > previous.Frames || (take.PrefixFrames - 1) % 4 != 0)
                    || !paths.Contains(Path.ChangeExtension(take.Video, ".lineage.json").Replace('\\', '/'))) Invalid();
                previous = take;
            }
            foreach (var result in run.Results)
            {
                if (!Guid.TryParseExact(result.Id, "N", out _) || !resultIds.Add(result.Id) || !roles.Contains(result.VideoRole)
                    || !double.IsFinite(result.Seconds) || result.Seconds <= 0 || result.CreatedUtc == default
                    || result.Models is null || result.Models.Length is < 1 or > 128 || result.Models.Any(m => m is null)) Invalid();
                foreach (var model in result.Models)
                    foreach (var text in new[] { model.Id, model.Name, model.Revision, model.Role })
                        if (string.IsNullOrWhiteSpace(text) || text.Length > 1024 || Path.IsPathFullyQualified(text) || text.Any(char.IsControl)) Invalid();
            }
            if (run.Complete && (run.Takes.Count == 0 || run.Results.Count == 0)) Invalid();
        }
    }

    internal static void Validate(VideoPreparationProject project, IReadOnlyList<VideoVaceRun> runs)
    {
        Validate(runs);
        if (project.Attachments is null || project.Attachments.Any(a => a is null || a.Role is null)) Invalid();
        var attachments = project.Attachments.GroupBy(a => a.Role, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var file in runs.SelectMany(r => r.Files))
            if (!attachments.TryGetValue(file.Role, out var count) || count != 1) Invalid();
        // Archive validation runs before extraction. Actual size/SHA are checked by runner accept/restore and archive streaming.
    }

    private static bool Hash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static bool Token(string? value) => value is { Length: > 0 and <= 128 } && value is not "." and not ".."
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
    [System.Diagnostics.CodeAnalysis.DoesNotReturn] private static void Invalid() => throw new InvalidDataException("VideoPrep.InvalidArchive");
}
