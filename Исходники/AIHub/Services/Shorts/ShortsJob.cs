using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AIHub.Models;
using AIHub.Services.SongClip;
using AIHub.Services.VideoEditor;
using AIHub.Services.VideoPreparation;

namespace AIHub.Services.Shorts;

internal sealed record ShortsReference(string Path, string Description = "");
internal sealed record ShortsDraft(bool FromSong = false, int Seconds = 15, string Prompt = "", string Song = "",
    double Start = 0, ShortsReference[]? References = null, string? JobId = null, string? OutputPath = null,
    int? ExcerptFrames = null, bool FadeEnding = false, string? ProjectId = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UseDeno { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AccelerateDeno { get; init; }
    internal ShortsReference[] Images => References ?? [];
    internal double SongSeconds => (ExcerptFrames ?? Seconds * ShortsJob.Fps) / (double)ShortsJob.Fps;
}

internal sealed record ShortsDlssCheckpoint(string SourceSha256, string MediaSha256, OutputModelUse Model);

internal sealed record ShortsJob(string Id, bool FromSong, int Seconds, string ModelsRoot, int Connections,
    string Output, DateTimeOffset CreatedUtc, SongClipAttempt? Song, VideoH3Job? Description, string? SourceSha256 = null,
    int? ExcerptFrames = null, bool FadeEnding = false, ShortsDlssCheckpoint? DlssCheckpoint = null, string? ProjectId = null)
{
    internal const string Kind = "video.shorts";
    internal const int NativeWidth = 768, NativeHeight = 1344;
    internal const int Width = 1080, Height = 1920, Fps = 24;
    internal const int MaximumSeconds = 179;
    internal static IReadOnlyList<int> DurationPresets { get; } = Array.AsReadOnly(new[] { 15, 30, 60, 90, 120, 150, MaximumSeconds });
    internal const int FadeSeconds = 1;
    internal int OutputFrames => (FromSong ? ExcerptFrames : null) ?? Seconds * Fps;
    internal double OutputSeconds => OutputFrames / (double)Fps;
    private const int FastPassSeconds = 5, FastSteps = 8;
    // VideoH3's native runner accepts at most four image references.
    internal const int DescriptionImageLimit = 4;
    internal static string Root => Path.Combine(AppDataPaths.BaseDirectory, "Shorts");
    internal string SourcePath()
    {
        if (Song is { } song) return Assets.Resolve(Id, $"attempts/{song.Id}/clip.mp4");
        var run = Description?.Run ?? throw new InvalidDataException("Shorts.InvalidJob");
        var result = run.Results.LastOrDefault(item => item.Kind == "whole")
            ?? throw new InvalidDataException("Shorts.InvalidResult");
        var artifact = run.Files.Single(file => file.Role == result.VideoRole);
        return RuntimeFileAudit.Contained(Path.Combine(VideoH3Runner.DirectoryFor(run.Id), "output"), artifact.RelativePath);
    }
    internal static SongClipStore Assets => new(family: "Shorts/Jobs");
    private static string JobFile(string id) => Assets.Resolve(id, "shorts.json");

    internal static ShortsJob Load(string id) =>
        JsonSerializer.Deserialize<ShortsJob>(File.ReadAllText(JobFile(id))) is { } job && job.Id == id
            ? job : throw new InvalidDataException("Shorts.InvalidJob");

    internal void Save() => SongClipStore.Atomic(JobFile(Id), this);

    internal static async Task<ShortsJob> CreateAsync(ShortsDraft draft, string modelsRoot, int connections, CancellationToken token)
    {
        Validate(draft);
        var profile = ShortsGenerationProfile.FromDraft(draft);
        var id = Guid.NewGuid().ToString("N");
        var output = ValidateOutputPath(draft.OutputPath ?? SuggestOutputPath());
        _ = Assets.Folder(id, Path.GetDirectoryName(output));
        var owned = new List<SongClipReference>();
        foreach (var image in draft.Images)
        {
            var file = await Assets.ImportAsync(id, image.Path, token);
            if (owned.Any(reference => reference.File == file)) throw new InvalidDataException("Shorts.DuplicateImage");
            owned.Add(new(file, "performer", image.Description.Trim()));
        }

        SongClipAttempt? song = null;
        VideoH3Job? description = null;
        if (draft.FromSong)
        {
            await VideoRuntime.PrepareAsync(token);
            using var probe = await VideoRuntime.ProbeAsync(draft.Song, token);
            ValidateRange(draft.Start, draft.SongSeconds, SongDuration(probe.RootElement));
            var (_, timings) = SongClipSourceMetadata.Parse(probe.RootElement);
            var (lyrics, sliceTimings) = SliceTimings(timings, draft.Start, draft.SongSeconds);
            var excerpt = Assets.Resolve(id, "song.wav");
            await CutSongAsync(draft.Song, excerpt, draft.Start, draft.SongSeconds, token);
            song = new(id, id, "song.wav", owned.FirstOrDefault()?.File ?? "", Path.GetFileNameWithoutExtension(draft.Song),
                lyrics, sliceTimings, new SongClipSettings
                {
                    Width = Width, Height = Height, Fps = Fps,
                    Prompt = "Create a vertical music video for this excerpt. Cut to the rhythm. Each supplied image represents the named song performer.",
                    Motion = new() { Mode = SongClipDeno.Mode },
                    Deno = new(Width, Height) { Accelerate = true }
                }, modelsRoot, connections)
            { Model = H3MusicRunner.ModelId, PlanningVersion = SongClipVisualProtocol.Version, References = [.. owned] };
            Assets.EnsureAttemptSnapshot(song);
        }
        else
        {
            var deno = profile.Deno ? new VideoH3DenoProfile(profile.Accelerate) : null;
            var settings = new VideoH3Settings
            {
                Aspect = "9:16", Width = deno?.Width ?? NativeWidth, Height = deno?.Height ?? NativeHeight,
                ClipSeconds = FastPassSeconds, Steps = deno?.Steps ?? FastSteps, VramReserveGb = profile.Deno ? 4 : 2,
                Continuation = "extend", AudioMode = "native", AudioContextFrames = 39
            };
            VideoH3WorkspaceState.Validate(new() { Settings = settings });
            // One item lets the existing worker continue its action using the preceding
            // take's video and audio context. It keeps the model loaded between passes.
            var images = owned.Select(x => Assets.Resolve(id, x.File)).ToArray();
            var item = new VideoH3Item(id, id, Describe(draft.Prompt, images.Length), draft.Seconds, -1, images, [], [], "extend");
            description = VideoH3Job.CreateForItems(new() { Id = id }, JobFile(id), modelsRoot, connections,
                VideoH3Catalog.ModelId, settings, [item], deno);
        }
        var job = new ShortsJob(id, draft.FromSong, draft.Seconds, modelsRoot, connections, output, DateTimeOffset.UtcNow, song, description,
            ExcerptFrames: draft.FromSong ? draft.ExcerptFrames : null, FadeEnding: draft.FadeEnding, ProjectId: draft.ProjectId);
        job.Save();
        return job;
    }

    internal static string SuggestOutputPath()
    {
        var resultRoot = new StorageSettingsStore().LoadOrCreate().Results.Locations
            .Select(x => x.Path?.Trim()).FirstOrDefault(path => path is not null && Path.IsPathFullyQualified(path)) ?? Path.Combine(Root, "Results");
        return Path.Combine(resultRoot, "Шортсы", $"Shorts_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}.mp4");
    }

    internal static string ValidateOutputPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Shorts.OutputInvalid");
        path = Path.GetFullPath(path);
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Shorts.OutputExists");
        Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        return path;
    }

    internal static string Describe(string prompt, int references) => references == 0 ? prompt.Trim() :
        prompt.Trim() + "\n\n" + string.Join("\n", Enumerable.Range(1, references)
            .Select(index => $"Reference image {index} depicts character {index} in this video. Keep the appearance and clothing of this character consistent."));

    internal static void Validate(ShortsDraft draft)
    {
        if (!DurationPresets.Contains(draft.Seconds)) throw new InvalidDataException("Shorts.InvalidDuration");
        if (draft.FromSong && draft.ExcerptFrames is { } frames && (frames < FadeSeconds * Fps || frames > draft.Seconds * Fps))
            throw new InvalidDataException("Shorts.SongRange");
        if (!draft.FromSong && string.IsNullOrWhiteSpace(draft.Prompt)) throw new InvalidDataException("Shorts.PromptRequired");
        if (draft.FromSong && !File.Exists(draft.Song)) throw new InvalidDataException("Shorts.SongRequired");
        if (!draft.FromSong && draft.Images.Length > DescriptionImageLimit) throw new InvalidDataException("Shorts.ImageLimit");
        if (draft.FromSong && draft.Images.Any(image => string.IsNullOrWhiteSpace(image.Description)))
            throw new InvalidDataException("SongClip.ReferenceRoleRequired");
        if (draft.Images.Any(image => !File.Exists(image.Path))) throw new InvalidDataException("Shorts.ImageMissing");
    }

    internal static void ValidateRange(double start, double seconds, double duration)
    {
        if (!double.IsFinite(start) || !double.IsFinite(seconds) || !double.IsFinite(duration) ||
            start < 0 || seconds < FadeSeconds || start + seconds > duration + .001)
            throw new InvalidDataException("Shorts.SongRange");
    }

    internal static double SongDuration(JsonElement probe)
    {
        var audio = probe.GetProperty("streams").EnumerateArray().FirstOrDefault(stream => stream.GetProperty("codec_type").GetString() == "audio");
        if (audio.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Shorts.SongRequired");
        var duration = audio.TryGetProperty("duration", out var field) && double.TryParse(field.GetString(), CultureInfo.InvariantCulture, out var value)
            ? value : double.Parse(probe.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        return double.IsFinite(duration) && duration > 0 ? duration : throw new InvalidDataException("Shorts.SongRange");
    }

    internal static string AudioFade(double seconds) =>
        FormattableString.Invariant($"afade=t=out:st={seconds - FadeSeconds:0.#########}:d={FadeSeconds}");

    internal static async Task CutSongAsync(string source, string output, double start, double seconds, CancellationToken token,
        bool fadeEnding = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        List<string> args = ["-nostdin", "-v", "error", "-y", "-ss", start.ToString("R", CultureInfo.InvariantCulture),
            "-i", source, "-map", "0:a:0", "-map_metadata", "-1", "-t", seconds.ToString("0.#########", CultureInfo.InvariantCulture)];
        if (fadeEnding) args.AddRange(["-af", AudioFade(seconds)]);
        args.AddRange(["-c:a", "pcm_s16le", output]);
        await VideoRuntime.RunAsync(false, args, token);
    }

    internal static (string Lyrics, string Timings) SliceTimings(string lrc, double start, double duration)
    {
        var lines = new List<(double Time, string Text)>();
        foreach (var line in lrc.Split('\n'))
        {
            var timestamps = Regex.Matches(line, @"\[(\d+):(\d{2}(?:\.\d+)?)\]");
            if (timestamps.Count == 0) continue;
            var tail = timestamps[^1];
            var text = line[(tail.Index + tail.Length)..].Trim();
            foreach (Match stamp in timestamps)
                if (double.TryParse(stamp.Groups[1].Value, CultureInfo.InvariantCulture, out var minutes)
                    && double.TryParse(stamp.Groups[2].Value, CultureInfo.InvariantCulture, out var seconds))
                    lines.Add((minutes * 60 + seconds, text));
        }
        var ordered = lines.OrderBy(line => line.Time).ToArray();
        var selected = ordered.Where((line, i) => line.Time < start + duration && (line.Time >= start ||
            i + 1 < ordered.Length && ordered[i + 1].Time > start)).ToArray();
        return (string.Join("\n", selected.Select(line => line.Text)), string.Join("\n", selected.Select(line =>
        {
            var offset = Math.Max(0, line.Time - start);
            var centiseconds = (int)Math.Round(offset * 100);
            return FormattableString.Invariant($"[{centiseconds / 6000:00}:{centiseconds / 100 % 60:00}.{centiseconds % 100:00}]{line.Text}");
        })));
    }
}
