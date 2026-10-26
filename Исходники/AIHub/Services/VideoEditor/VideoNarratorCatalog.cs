using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.VideoEditor;

internal sealed record VideoNarratorModel(string Id, string Name, string[] Languages, string[] Speakers,
    string DefaultSpeaker, string DescriptionKey, string License, string Source, string? Blocked = null,
    bool Rate = false, bool Style = false, bool Steps = false, bool Preset = false);
internal sealed record VideoNarratorPin(string Repository, string Revision, List<ManagedModelArtifactFile> Files, OutputModelUse[]? AdditionalModels = null);

internal static class VideoNarratorCatalog
{
    private static readonly string[] QwenSpeakers = ["Vivian", "Serena", "Uncle_Fu", "Dylan", "Eric", "Ryan", "Aiden", "Ono_Anna", "Sohee"];
    internal static IReadOnlyList<VideoNarratorModel> Models { get; } = [
        new("kokoro-sveta", "Kokoro · Света", ["ru"], ["sveta"], "sveta", "KokoroRu", "Apache-2.0; voice terms", "https://huggingface.co/zaakirio/kokoro-ru", Rate: true),
        new("kokoro-heart", "Kokoro · af_heart", ["en"], ["af_heart"], "af_heart", "KokoroEn", "Apache-2.0", "https://huggingface.co/hexgrad/Kokoro-82M", Rate: true),
        new("kitten2", "KittenTTS 2", ["ru", "en"], KittenVoices(), "Bruno", "Kitten", "Stellon Labs Community License; Apache-2.0/MIT code", "https://github.com/KittenML/KittenTTS", Preset: true),
        new("vosk010", "Vosk-TTS 0.10 multi", ["ru"], Enumerable.Range(0, 57).Select(i => i.ToString()).ToArray(), "0", "Vosk", "Apache-2.0", "https://github.com/alphacep/vosk-tts", Rate: true),
        new("supertonic3", "Supertonic 3", ["ar", "bg", "cs", "da", "de", "el", "en", "es", "et", "fi", "fr", "hi", "hr", "hu", "id", "it", "ja", "ko", "lt", "lv", "nl", "pl", "pt", "ro", "ru", "sk", "sl", "sv", "tr", "uk", "vi"],
            ["M1", "M2", "M3", "M4", "M5", "F1", "F2", "F3", "F4", "F5"], "M1", "Supertonic", "BigScience Open RAIL-M; MIT code", "https://huggingface.co/jiangzhuo9357/supertonic-3", Rate: true, Steps: true),
        new("qwen3-17b", "Qwen3-TTS CustomVoice 1.7B", ["ru", "en", "zh", "ja", "ko", "de", "fr", "it", "es", "pt"], QwenSpeakers, "Ryan", "Qwen", "Apache-2.0", "https://huggingface.co/Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice", Style: true),
        new("qwen3-06b", "Qwen3-TTS CustomVoice 0.6B", ["ru", "en", "zh", "ja", "ko", "de", "fr", "it", "es", "pt"], QwenSpeakers, "Ryan", "QwenSmall", "Apache-2.0", "https://huggingface.co/Qwen/Qwen3-TTS-12Hz-0.6B-CustomVoice"),
        new("orpheus3b", "Orpheus 3B English · Q4_K_M", ["en"], ["tara", "leah", "jess", "leo", "dan", "mia", "zac", "zoe"], "tara", "Orpheus", "Apache-2.0; MIT SNAC/code", "https://github.com/canopyai/Orpheus-TTS/tree/main/additional_inference_options/no_gpu"),
        new("silero56", "Silero v5.6 RU", ["ru"], ["aidar", "baya", "kseniya", "xenia", "eugene"], "xenia", "Silero", "CC-BY-NC-4.0", "https://github.com/snakers4/silero-models"),
        new("cosyvoice300", "CosyVoice-300M-SFT", ["en", "zh", "ja", "ko", "yue"], ["中文女", "中文男", "日语男", "粤语女", "英文女", "英文男", "韩语女"], "英文女", "CosyVoice", "Apache-2.0; MIT dependencies", "https://github.com/QwenAudio/CosyVoice", Rate: true)
    ];
    internal static VideoNarratorModel Get(string id) => Models.SingleOrDefault(m => m.Id == id) ?? throw new InvalidDataException("VideoEditor.VoiceUnavailable");
    internal static bool MatchesPin(ManagedModelArtifactCard? installed, ManagedModelArtifactCard expected) =>
        installed is not null && installed.ModelArtifactId == expected.ModelArtifactId && installed.Revision == expected.Revision
        && installed.Files.Count == expected.Files.Count
        && expected.Files.All(file => installed.Files.Any(actual => actual.RelativePath == file.RelativePath
            && actual.SizeBytes == file.SizeBytes && string.Equals(actual.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase)));
    private static string[] KittenVoices() => JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Tools", "VideoNarrators", "kitten-voices.json")))!;
    internal static string ArtifactId(string id) => id switch {
        "kokoro-sveta" => ManagedModelCatalog.KokoroRussianArtifactId,
        "kokoro-heart" => ManagedModelCatalog.KokoroEnglishArtifactId,
        _ => "video-narrator-" + Get(id).Id
    };
    internal static void Validate(string id, string language, int rate, VideoVoiceOptions? options)
    {
        var model = Get(id); VideoVoiceProfiles.ValidateStored(options);
        if (!model.Languages.Contains(language) || rate is < 50 or > 200 || !model.Rate && rate != 100
            || options!.Speaker.Length > 0 && !model.Speakers.Contains(options.Speaker)
            || !model.Style && options.Style.Length > 0 || !model.Steps && options.Steps != 8
            || options.Device is not ("auto" or "cpu" or "gpu") || options.Steps is < 1 or > 50 || id == "supertonic3" && rate < 70
            || options.Preset is not ("stable" or "expressive") || !model.Preset && options.Preset != "stable")
            throw new InvalidDataException("VideoEditor.VoiceUnavailable");
    }
    internal static VideoNarratorPin Pin(string id)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Tools", "VideoNarrators", "models.json");
        return JsonSerializer.Deserialize<Dictionary<string, VideoNarratorPin>>(File.ReadAllText(path))!.GetValueOrDefault(id)
            ?? throw new InvalidDataException("VideoEditor.VoiceUnavailable");
    }
    internal static ManagedModelArtifactCard Card(string root, string id)
    {
        if (id == "kokoro-sveta") return ManagedModelCatalog.CreateKokoroRussian(root);
        if (id == "kokoro-heart") return ManagedModelCatalog.CreateKokoroEnglish(root);
        var model = Get(id); var pin = Pin(id);
        return new() { ModelArtifactId = ArtifactId(id), DisplayName = model.Name, Family = model.Name, Role = ManagedModelRoles.Speech,
            Provider = id == "vosk010" ? "Alpha Cephei" : id == "silero56" ? "Silero" : "Hugging Face", RepositoryId = pin.Repository, Revision = pin.Revision, License = model.License,
            Format = id switch { "supertonic3" => "ONNX", "vosk010" => "ZIP / ONNX", "silero56" => "Torch package", "orpheus3b" => "GGUF / SNAC ONNX", "kitten2" => "GGUF CPU / Safetensors GPU", "cosyvoice300" => "PyTorch", _ => "Safetensors" }, SourcePage = model.Source,
            IsManaged = true, CanRemoveFiles = true, ModelsRoot = root, InstallDirectory = string.IsNullOrWhiteSpace(root) ? "" : Path.Combine(root, "Speech", id, pin.Revision[..12]),
            Origin = ManagedModelOrigins.PredefinedScenario, RuntimeBackend = "upstream Python / LOPATA voice adapter",
            Consumers = [new() { Id = ScenarioNavigationCatalog.VideoEditor, DisplayName = "Видеоредактор / Video editor", Kind = "scenario" }], Files = pin.Files };
    }
    internal static IReadOnlyList<ManagedModelArtifactCard> Cards(string root) => Models
        .Where(m => m.Blocked is null)
        .SelectMany(m => m.Id.StartsWith("kokoro-", StringComparison.Ordinal) ? new[] { VideoNarratorRuntime.Card(root, m.Id) }
            : new[] { Card(root, m.Id), VideoNarratorRuntime.Card(root, m.Id) }).DistinctBy(c => c.ModelArtifactId).ToArray();
}
