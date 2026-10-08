using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace AIHub.Services;

public static class MusicSongMetadata
{
    public static Dictionary<string, string> Create(MusicGenerationJob job, MusicGenerationVariant variant, int index)
    {
        var culture = CultureInfo.InvariantCulture;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["title"] = string.IsNullOrWhiteSpace(job.Title) ? System.IO.Path.GetFileNameWithoutExtension(variant.ResultPath) : job.Title,
            ["artist"] = job.Artist, ["comment"] = job.Comment,
            ["lyrics"] = job.Lyrics, ["LOPATA_STYLE"] = job.Style,
            ["date"] = job.CreatedAt.ToString("O", culture), ["track"] = (index + 1).ToString(culture),
            ["LOPATA_JOB"] = job.Id, ["LOPATA_MODEL"] = "YuE2 3B Q8_0",
            ["LOPATA_MODEL_REVISION"] = job.ModelRevision, ["LOPATA_RUNTIME_REVISION"] = job.RuntimeRevision,
            ["LOPATA_RUNTIME_PACK"] = variant.UsedRuntimePack ?? job.RuntimePack, ["LOPATA_VERSION"] = job.AppVersion,
            ["LOPATA_GGML_REVISION"] = MusicYueRuntime.GgmlRevision,
            ["LOPATA_AUDIO_RUNTIME"] = MusicAudioRuntime.Revision,
            ["LOPATA_LM_SEED"] = variant.LanguageSeed.ToString(culture),
            ["LOPATA_SEED"] = variant.SoundSeed.ToString(culture),
            ["LOPATA_DURATION_REQUEST_SECONDS"] = job.DurationSeconds.ToString(culture),
            ["LOPATA_PARAMETERS"] = string.Join('\n', job.Expert.Values.OrderBy(p => p.Key).Select(p => p.Key + "=" +
                (p.Key == "lm_seed" ? variant.LanguageSeed : p.Key == "seed" ? variant.SoundSeed : p.Value).ToString("R", culture))) +
                "\ncot_mode=" + job.Expert.Cot + "\neffective_cfg=" + job.Expert.Guidance.ToString("R", culture) +
                "\nsemantic_output_limit=" + job.Expert.SequenceLimit(job.DurationSeconds).ToString(culture) +
                "\neffective_semantic_min_tokens=" + Math.Min(job.Expert.Integer("semantic_sampling.min_tokens"), job.Expert.SequenceLimit(job.DurationSeconds)).ToString(culture)
        };
        if (job.Wishes is { } wishes) {
            result["LOPATA_WISHES"] = JsonSerializer.Serialize(wishes);
            if (wishes.Selections.TryGetValue("genres", out var genres)) result["genre"] = string.Join("; ", genres);
        }
        if (job.Expert.Tuning is { } tuning) result["LOPATA_TUNING"] = JsonSerializer.Serialize(tuning);
        if (job.Output is { } output) result["LOPATA_OUTPUT"] = JsonSerializer.Serialize(output);
        if (job.ProjectId is { } project) {
            result["LOPATA_PROJECT"] = project;
            result["LOPATA_PROJECT_NAME"] = job.ProjectName ?? "";
            result["LOPATA_PROJECT_STEP"] = job.ProjectStep!.Value.ToString(culture);
        }
        if (variant.Hardware is { Length: > 0 }) result["LOPATA_HARDWARE"] = variant.Hardware;
        if (variant.PlanHardware is { Length: > 0 }) result["LOPATA_PLAN_HARDWARE"] = variant.PlanHardware;
        if (variant.GenerationSeconds is { } seconds) result["LOPATA_GENERATION_SECONDS"] = seconds.ToString("0.###", culture);
        return result.Where(p => p.Value.Length > 0).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
    }
    public static string AppVersion => Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion.Split('+')[0] ?? "unknown";
    public static string FfMetadata(IReadOnlyDictionary<string, string> values)
    {
        var text = new StringBuilder(";FFMETADATA1\n");
        foreach (var item in values) text.Append(Escape(item.Key)).Append('=').Append(Escape(item.Value)).Append('\n');
        return text.ToString();
    }
    private static string Escape(string text)
    {
        if (text.Contains('\0')) throw new System.IO.InvalidDataException("Audio tags cannot contain NUL characters.");
        return text.Replace("\\", "\\\\").Replace("=", "\\=").Replace(";", "\\;").Replace("#", "\\#").Replace("\r", "\\\r").Replace("\n", "\\\n");
    }
}
