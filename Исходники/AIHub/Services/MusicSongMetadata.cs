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
        var studioProfile = job.Variation == MusicStudioRuntime.Variation
            ? MusicStudioRuntime.ResolveProfile(job.RuntimeRevision, job.RuntimePack) : null;
        var engineSetId = studioProfile?.EngineSetId ?? job.EngineSetId;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["title"] = string.IsNullOrWhiteSpace(job.Title) ? System.IO.Path.GetFileNameWithoutExtension(variant.ResultPath) : job.Title,
            ["artist"] = job.Artist, ["comment"] = job.Comment,
            ["lyrics"] = job.Lyrics, ["LOPATA_STYLE"] = job.Style,
            ["date"] = job.CreatedAt.ToString("O", culture), ["track"] = (index + 1).ToString(culture),
            ["LOPATA_JOB"] = job.Id, ["LOPATA_MODEL"] = MusicModelVariants.Name(job.Variation),
            [MusicCoverProvenance.ModelTag] = MusicCoverProvenance.Model(job.Cover),
            [MusicCoverProvenance.SourceTag] = job.Cover is null ? "standard" : "generated",
            ["LOPATA_VARIATION"] = job.Variation, ["LOPATA_DECODER_REVISION"] = job.DecoderRevision,
            ["LOPATA_MODEL_REVISION"] = job.ModelRevision, ["LOPATA_RUNTIME_REVISION"] = job.RuntimeRevision,
            ["LOPATA_RUNTIME_PACK"] = variant.UsedRuntimePack ?? job.RuntimePack, ["LOPATA_VERSION"] = job.AppVersion,
            ["producer"] = EngineSetMetadataIdentity.Producer(job.AppVersion, engineSetId),
            ["LOPATA_GGML_REVISION"] = job.Variation is MusicModelVariants.Bf16 or MusicStudioRuntime.Variation or MusicAceCatalog.Variation or MusicDiffRhythmCatalog.Variation or MusicHeartMuLaCatalog.Variation ? "" : MusicYueRuntime.GgmlRevision,
            ["LOPATA_AUDIO_RUNTIME"] = MusicAudioRuntime.Revision,
            ["LOPATA_LM_SEED"] = variant.LanguageSeed.ToString(culture),
            ["LOPATA_SEED"] = variant.SoundSeed.ToString(culture),
            ["LOPATA_DURATION_REQUEST_SECONDS"] = job.DurationAutomatic ? "auto" : job.DurationSeconds.ToString(culture),
            ["LOPATA_PARAMETERS"] = string.Join('\n', job.Expert.Values.Where(p => MusicModelVariants.SupportsParameter(job.Variation, p.Key)).OrderBy(p => p.Key).Select(p => p.Key + "=" +
                (p.Key == "lm_seed" ? variant.LanguageSeed : p.Key == "seed" ? variant.SoundSeed : p.Value).ToString("R", culture))) +
                (MusicModelVariants.ExternalPipeline(job.Variation) ? "\n" + string.Join('\n', job.Expert.TextValues.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value)) : "\ncot_mode=" + job.Expert.Cot + "\neffective_cfg=" + job.Expert.Guidance.ToString("R", culture) +
                "\nsemantic_output_limit=" + job.Expert.SequenceLimit(job.DurationSeconds).ToString(culture) +
                "\neffective_semantic_min_tokens=" + Math.Min(job.Expert.Integer("semantic_sampling.min_tokens"), job.Expert.SequenceLimit(job.DurationSeconds)).ToString(culture))
        };
        EngineSetMetadataIdentity.AddTags(result, engineSetId);
        if (job.Loras.Length > 0) result["LOPATA_LORAS"] = JsonSerializer.Serialize(job.Loras.Select(u => {
            var d = MusicLora.MusicLoraCatalog.Find(u.Id); return new { u.Id, u.Revision, u.Strength, d.Repository,
                slots = d.Slots, Format = d.Variation == MusicAceCatalog.Variation ? d.Format : "native-engine",
                files = d.Files.Select(f => new { f.RelativePath, f.Sha256 }) };
        }));
        if (job.Loras.Length > 0 && job.Variation == MusicStudioRuntime.Variation)
            result["LOPATA_LORA_RUNTIME"] = MusicLora.MusicLoraStudioRuntime.Revision + "; sha256=" + MusicLora.MusicLoraStudioRuntime.ServerHash;
        result["LOPATA_ENGINE_SET_HISTORY"] = studioProfile is not null
            ? job.EngineSetId is not null && job.EngineSetId != engineSetId ? "runtime-profile; conflicting-job-snapshot" : "runtime-profile"
            : engineSetId is null ? "unknown" : "job-snapshot";
        if (job.Wishes is { } wishes) {
            result["LOPATA_WISHES"] = JsonSerializer.Serialize(wishes);
            if (wishes.Selections.TryGetValue("genres", out var genres)) result["genre"] = string.Join("; ", genres);
        }
        if (MusicHeartMuLaCatalog.IsHeart(job.Variation)) {
            result.Remove("LOPATA_LM_SEED");
            result["LOPATA_ENGINE_REVISION"] = MusicHeartMuLaCatalog.SourceRevision;
            result["LOPATA_HEARTMULA_REQUEST"] = variant.ExecutionReceipt ?? "";
            result["LOPATA_ADAPTER"] = "HeartMuLa author API; Windows eager execution and device-aware release";
        }
        if (MusicDiffRhythmCatalog.IsDiff(job.Variation)) {
            result.Remove("LOPATA_LM_SEED");
            result["LOPATA_ENGINE_REVISION"] = MusicDiffRhythmCatalog.SourceRevision;
            result["LOPATA_DIFFRHYTHM_REQUEST"] = variant.ExecutionReceipt ?? "";
            result["LOPATA_ADAPTER"] = "DiffRhythm2 author pipeline; " +
                (job.Expert.Integer("experimental_ru") == 1 ? "experimental Russian eSpeak frontend v1 enabled" : "author EN/ZH frontend");
        }
        if (MusicAceCatalog.IsAce(job.Variation)) {
            result.Remove("LOPATA_LM_SEED");
            result["LOPATA_ACE_REQUEST"] = PublicAceReceipt(variant.ExecutionReceipt);
            result["LOPATA_ADAPTER"] = "official Python API; LM=1.7B/PyTorch; task=text2music; batch=1; no lyric rewrite";
        }
        if (job.Variation == MusicModelVariants.Bf16) {
            result.Remove("LOPATA_LM_SEED");
            result["LOPATA_PRECISION"] = "generator=bfloat16;vae=float32;quantization=none";
            result["LOPATA_ADAPTER"] = "yue2-infer-0.1.5;torch-eager;offload_ar=true;ode_method=midpoint;context=24576";
        }
        if (job.Variation == MusicStudioRuntime.Variation) {
            var profile = studioProfile!;
            result["LOPATA_ENGINE_REVISION"] = profile.EngineRevision;
            result["LOPATA_STUDIO_SOURCE"] = profile.SourceRevision;
            result["LOPATA_PRECISION"] = "generator=Q8_0;vae=F32";
            result["LOPATA_ADAPTER"] = "nar_lora_joint_v9.safetensors;revision=" + MusicStudioRuntime.CompanionRevision;
            result["LOPATA_STUDIO_POLICY"] = profile.OutputPolicy;
            result["LOPATA_STUDIO_SOLVER"] = job.Expert.StudioSolver;
            if (variant.ExecutionReceipt is { Length: > 0 } receipt) result["LOPATA_STUDIO_REQUEST"] = receipt;
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
    private static string PublicAceReceipt(string? receipt)
    {
        if (string.IsNullOrEmpty(receipt)) return "";
        var node = System.Text.Json.Nodes.JsonNode.Parse(receipt);
        if (node?["initialization"] is System.Text.Json.Nodes.JsonObject init) { init.Remove("project_root"); init.Remove("config_path"); }
        foreach (var bank in new[] { node?["loras"], node?["request"]?["loras"] }.OfType<System.Text.Json.Nodes.JsonArray>())
            foreach (var item in bank.OfType<System.Text.Json.Nodes.JsonObject>()) item.Remove("directory");
        return node?.ToJsonString() ?? "";
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
