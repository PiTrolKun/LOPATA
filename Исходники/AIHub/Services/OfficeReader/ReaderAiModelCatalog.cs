using System.Globalization;
using System.IO;
using AIHub.Models;
using AIHub.Services.VideoEditor;

namespace AIHub.Services.OfficeReader;

internal sealed record ReaderAiModel(string Id, string Name, ManagedModelArtifactCard Card, bool Downloaded, bool Default, bool Experimental);

/// <summary>Projects the shared inventory, not an Office-specific list of model names.</summary>
internal static class ReaderAiModelCatalog
{
    internal static IReadOnlyList<ReaderAiModel> TextModels(StorageSettings storage, IEnumerable<ManagedModelArtifactCard> inventory)
    {
        var cards = inventory.ToArray();
        var combined = ManagedModelCatalog.CreatePredefined(storage).Select(predefined =>
            cards.FirstOrDefault(c => c.ModelArtifactId == predefined.ModelArtifactId) ?? predefined)
            .Concat(cards).DistinctBy(c => c.ModelArtifactId).Where(SupportsText);
        return Order(combined.Select(c => new ReaderAiModel(c.ModelArtifactId, c.DisplayName, c, Downloaded(c),
            c.ModelArtifactId == ManagedModelCatalog.CoreArtifactId, c.ModelArtifactId != ManagedModelCatalog.CoreArtifactId)))
            .DistinctBy(m => PrimaryPath(m.Card), StringComparer.OrdinalIgnoreCase).ToArray();
    }
    internal static IReadOnlyList<ReaderAiModel> SpeechModels(string root, IEnumerable<ManagedModelArtifactCard> inventory)
    {
        var cards = inventory.ToArray();
        return Order(VideoNarratorCatalog.Models.Select(m => {
            var expected = VideoNarratorCatalog.Card(root, m.Id);
            var c = cards.FirstOrDefault(c => c.ModelArtifactId == expected.ModelArtifactId) ?? expected;
            return new ReaderAiModel(m.Id, m.Name, c, Downloaded(c), m.Id == ReaderAiSettings.DefaultSpeechModelId, false);
        })).ToArray();
    }
    internal static IOrderedEnumerable<ReaderAiModel> Order(IEnumerable<ReaderAiModel> models) => models
        .OrderByDescending(m => m.Default).ThenByDescending(m => m.Downloaded).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase);
    internal static bool SupportsText(ManagedModelArtifactCard card)
    {
        if (card.Role is ManagedModelRoles.Speech or ManagedModelRoles.Embedding or ManagedModelRoles.Reranker) return false;
        if (card.RuntimeBackend.Contains("diffusion", StringComparison.OrdinalIgnoreCase)
            || card.Family is "ImageGeneration" or "YuE2") return false;
        var architecture = Architecture(card);
        var identity = (card.ModelArtifactId + " " + card.DisplayName).ToLowerInvariant();
        if (new[] { "mmproj", "projector", "rerank", "embedding", "whisper", "tts", "kokoro", "orpheus", "snac" }.Any(identity.Contains)) return false;
        if (new[] { "bert", "t5", "clip", "florence", "flux", "diffusion", "vae", "wavtokenizer", "whisper", "ace_step", "musicgen" }.Any(architecture.Contains)) return false;
        if (GgufMetadataReader.IsKnownUnsupportedArchitecture(architecture)) return false;
        if (!card.Files.Any(f => f.Purpose is "main_model" or "executor_model" || f.RelativePath.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)
            || f.RelativePath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
            && !f.RelativePath.Contains("mmproj", StringComparison.OrdinalIgnoreCase))) return false;
        if (card.Role is ManagedModelRoles.Core or ManagedModelRoles.Executor or ManagedModelRoles.Localizer) return true;
        return card.Format.Equals("GGUF", StringComparison.OrdinalIgnoreCase)
            || card.RuntimeBackend.Contains("chatllm", StringComparison.OrdinalIgnoreCase)
            || new[] { "qwen", "gemma", "llama", "mistral", "phi", "gpt", "deepseek", "internlm", "kimi", "smollm", "falcon", "bloom" }.Any(architecture.Contains);
    }
    private static string Architecture(ManagedModelArtifactCard card)
    {
        if (!string.IsNullOrEmpty(card.Architecture)) return card.Architecture.ToLowerInvariant();
        var file = card.Files.FirstOrDefault(f => f.RelativePath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase));
        if (file is null || !Path.IsPathFullyQualified(card.InstallDirectory)) return "";
        try
        {
            using var stream = File.OpenRead(Path.Combine(card.InstallDirectory, file.RelativePath));
            var data = new byte[(int)Math.Min(stream.Length, 1024 * 1024)];
            stream.ReadExactly(data); return GgufMetadataReader.Read(data).Architecture.ToLowerInvariant();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return ""; }
    }
    internal static bool Downloaded(ManagedModelArtifactCard card)
    {
        if (!Path.IsPathFullyQualified(card.InstallDirectory)) return false;
        var required = card.Files.Where(f => f.IsRequired).ToArray();
        return required.Length > 0 && required.All(f => {
            try { var file = new FileInfo(Path.Combine(card.InstallDirectory, f.RelativePath)); return file.Exists && (f.SizeBytes <= 0 || file.Length == f.SizeBytes); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
        });
    }
    private static string PrimaryPath(ManagedModelArtifactCard c) => c.Files.FirstOrDefault(f => f.Purpose is "main_model" or "executor_model") is { } file
        ? Path.Combine(c.InstallDirectory, file.RelativePath) : c.ModelArtifactId;
    internal static string Size(long bytes) => bytes <= 0 ? "?" : bytes < 1_000_000 ? bytes.ToString("N0", CultureInfo.CurrentCulture) + " B" : bytes >= 1_000_000_000
        ? (bytes / 1_000_000_000d).ToString("0.##", CultureInfo.CurrentCulture) + " GB"
        : (bytes / 1_000_000d).ToString("0.##", CultureInfo.CurrentCulture) + " MB";
}
